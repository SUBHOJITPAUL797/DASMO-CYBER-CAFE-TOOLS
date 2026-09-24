using System.IO;
using System.Windows;
using SmartSaver.Helpers;
using SmartSaver.Services;
using SmartSaver.ViewModels;
using SmartSaver.Views;
using Serilog;
using Serilog.Events;
using MessageBox = System.Windows.MessageBox;

namespace SmartSaver;

/// <summary>
/// Main application class — manages lifecycle, services, and single-instance enforcement
/// </summary>
public partial class App : System.Windows.Application
{
    public static App? CurrentApp => System.Windows.Application.Current as App;

    private MainWindow? _mainWindow;
    private TrayIconService? _trayIconService;
    private FileWatcherService? _fileWatcherService;
    private ExplorerKeyboardHook? _explorerKeyboardHook;
    private bool _isPaused;

    public bool EnsureCloudLicenseApproved()
    {
        if (FirebaseCloudAuthService.BypassForTests)
            return true;

        if (FirebaseCloudAuthService.Instance.CurrentStatus == CloudAuthStatus.UpdateRequired)
        {
            var updateWin = new MandatoryUpdateWindow();
            updateWin.ShowDialog();
            return false;
        }

        if (FirebaseCloudAuthService.Instance.CurrentStatus == CloudAuthStatus.Approved)
            return true;

        try
        {
            var status = Task.Run(() => FirebaseCloudAuthService.Instance.InitializeAndCheckAuthAsync()).GetAwaiter().GetResult();
            if (status == CloudAuthStatus.UpdateRequired)
            {
                var updateWin = new MandatoryUpdateWindow();
                updateWin.ShowDialog();
                return false;
            }
            if (status == CloudAuthStatus.Approved)
                return true;
        }
        catch { }

        var gate = new AuthGateWindow();
        bool? res = gate.ShowDialog();
        return res == true && gate.IsApprovedAndReady;
    }

    public void ShowMainWindow(DashboardTab? tab = null)
    {
        Dispatcher.Invoke(() =>
        {
            try
            {
                if (!EnsureCloudLicenseApproved())
                {
                    Log.Warning("Access blocked by Cloud License Gatekeeper");
                    return;
                }

                if (_mainWindow == null || !_mainWindow.IsLoaded)
                {
                    _mainWindow = new MainWindow();
                    _mainWindow.Closed += (s, e) => _mainWindow = null;
                    MainWindow = _mainWindow;
                }

                if (tab.HasValue)
                {
                    _mainWindow.ViewModel.SwitchToTab(tab.Value);
                }

                _mainWindow.Show();
                _mainWindow.WindowState = WindowState.Normal;
                _mainWindow.Activate();
                _mainWindow.Focus();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to show MainWindow");
            }
        });
    }

    public void OpenGovtCardPrintWithFile(string filePath)
    {
        Dispatcher.Invoke(() =>
        {
            ShowMainWindow(DashboardTab.GovtCardTab);
            if (File.Exists(filePath) && _mainWindow != null)
            {
                _ = _mainWindow.ViewModel.GovtCardVm.AddSourceFilesAsync(new[] { filePath });
            }
        });
    }

    public void ShowStackerWindow()
    {
        Dispatcher.Invoke(() =>
        {
            try
            {
                if (!EnsureCloudLicenseApproved())
                {
                    Log.Warning("Access blocked by Cloud License Gatekeeper");
                    return;
                }

                var existingWindow = Windows.OfType<DocumentStackerWindow>().FirstOrDefault();
                if (existingWindow != null)
                {
                    existingWindow.Activate();
                    return;
                }

                var stackerWindow = new DocumentStackerWindow();
                stackerWindow.Show();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to show StackerWindow");
            }
        });
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        try
        {
            base.OnStartup(e);

            // Stamp process and Windows registry with DASMO CYBER CAFE TOOLS AUMID & official logo
            NotificationService.EnsureNotificationBranding();

            // Register Global Exception Handlers to log all errors and prevent silent application crashes
            DispatcherUnhandledException += (sender, args) =>
            {
                Log.Error(args.Exception, "Unhandled WPF Dispatcher Exception in DASMO CYBER CAFE TOOLS");
                args.Handled = true; // Prevent WPF framework from closing the app!
            };

            AppDomain.CurrentDomain.UnhandledException += (sender, args) =>
            {
                if (args.ExceptionObject is Exception ex)
                {
                    Log.Error(ex, "Unhandled AppDomain Exception (Terminating={IsTerminating})", args.IsTerminating);
                }
            };

            TaskScheduler.UnobservedTaskException += (sender, args) =>
            {
                Log.Error(args.Exception, "Unobserved Task Exception in DASMO CYBER CAFE TOOLS");
                args.SetObserved();
            };

            // Handle toast notification activations — when user clicks a toast,
            // Windows COM re-launches the process. We must handle this gracefully
            // to prevent the app from crashing or spawning duplicate instances.
            Microsoft.Toolkit.Uwp.Notifications.ToastNotificationManagerCompat.OnActivated += toastArgs =>
            {
                // Toast was clicked — just bring existing app to foreground if needed
                Dispatcher.Invoke(() =>
                {
                    try
                    {
                        Log.Debug("Toast notification activated: {Args}", toastArgs.Argument);
                    }
                    catch { }
                });
            };

            // Subscribe to IPC events before acquiring single instance to prevent race condition
            SingleInstanceHelper.MessageReceived += OnIpcMessageReceived;

            // Enforce single instance
            if (!SingleInstanceHelper.TryAcquire())
            {
                // Another instance is running — check if we have resize/compress/peek arguments to forward
                if (e.Args.Length >= 2 && e.Args[0] == "--resize")
                {
                    string filePath = e.Args[1];
                    SingleInstanceHelper.SignalExistingInstance($"RESIZE|{filePath}");
                }
                else if (e.Args.Length >= 2 && e.Args[0] == "--peek")
                {
                    string filePath = e.Args[1];
                    SingleInstanceHelper.SignalExistingInstance($"PEEK|{filePath}");
                }
                else if (e.Args.Length >= 1 && (e.Args[0] == "--edit-pdf" || e.Args[0] == "--editpdf"))
                {
                    if (e.Args.Length >= 2)
                    {
                        string filePath = e.Args[1];
                        SingleInstanceHelper.SignalExistingInstance($"EDIT_PDF|{filePath}");
                    }
                    else
                    {
                        SingleInstanceHelper.SignalExistingInstance("SHOW_PDF_EDITOR");
                    }
                }
                else if (e.Args.Length >= 2 && e.Args[0] == "--compress")
                {
                    var files = e.Args.Skip(1).Where(File.Exists).ToList();
                    if (files.Count > 1)
                        SingleInstanceHelper.SignalExistingInstance($"COMPRESS_BATCH|{string.Join("|", files)}");
                    else if (files.Count == 1)
                        SingleInstanceHelper.SignalExistingInstance($"COMPRESS|{files[0]}");
                }
                else if (e.Args.Length >= 1 && e.Args[0] == "--merge")
                {
                    var selected = ExplorerSelectionHelper.GetSelectedFilePathsInExplorer()
                        .Where(f => File.Exists(f) && Path.GetExtension(f).Equals(".pdf", StringComparison.OrdinalIgnoreCase)).ToList();

                    var files = e.Args.Skip(1).Where(File.Exists).ToList();
                    foreach (var s in selected)
                    {
                        if (!files.Contains(s, StringComparer.OrdinalIgnoreCase)) files.Add(s);
                    }
                    SingleInstanceHelper.SignalExistingInstance($"MERGE|{string.Join("|", files)}");
                }
                else if (e.Args.Length >= 2 && e.Args[0] == "--split")
                {
                    string filePath = e.Args[1];
                    SingleInstanceHelper.SignalExistingInstance($"SPLIT|{filePath}");
                }
                else if (e.Args.Length >= 1 && e.Args[0] == "--img2pdf")
                {
                    var selected = ExplorerSelectionHelper.GetSelectedFilePathsInExplorer()
                        .Where(f => File.Exists(f) && RegistryHelper.ImageExtensions.Contains(Path.GetExtension(f).ToLowerInvariant())).ToList();
                    var files = e.Args.Skip(1).Where(File.Exists).ToList();
                    foreach (var s in selected) if (!files.Contains(s, StringComparer.OrdinalIgnoreCase)) files.Add(s);
                    SingleInstanceHelper.SignalExistingInstance($"IMG2PDF|{string.Join("|", files)}");
                }
                else if (e.Args.Length >= 2 && e.Args[0] == "--pdf2img")
                {
                    string filePath = e.Args[1];
                    SingleInstanceHelper.SignalExistingInstance($"PDF2IMG|{filePath}");
                }
                else if (e.Args.Length >= 2 && e.Args[0] == "--enhance")
                {
                    string filePath = e.Args[1];
                    SingleInstanceHelper.SignalExistingInstance($"ENHANCE|{filePath}");
                }
                else if (e.Args.Length >= 2 && e.Args[0] == "--stamp")
                {
                    string filePath = e.Args[1];
                    SingleInstanceHelper.SignalExistingInstance($"STAMP|{filePath}");
                }
                else if (e.Args.Length >= 2 && (e.Args[0] == "--signature" || e.Args[0] == "--resizesignature"))
                {
                    string filePath = e.Args[1];
                    SingleInstanceHelper.SignalExistingInstance($"SIGNATURE|{filePath}");
                }
                else if (e.Args.Length >= 1 && (e.Args[0] == "--cardprint" || e.Args[0] == "--rationcard"))
                {
                    var files = e.Args.Skip(1).Where(File.Exists).ToList();
                    SingleInstanceHelper.SignalExistingInstance($"CARDPRINT|{string.Join("|", files)}");
                }
                else if (e.Args.Contains("--history"))
                {
                    SingleInstanceHelper.SignalExistingInstance("SHOW_HISTORY");
                }
                else if (e.Args.Contains("--stack"))
                {
                    SingleInstanceHelper.SignalExistingInstance("SHOW_STACKER");
                }
                else if (e.Args.Contains("--print-counter") || e.Args.Contains("--printcounter") || e.Args.Contains("--printbilling"))
                {
                    SingleInstanceHelper.SignalExistingInstance("SHOW_PRINT_COUNTER");
                }
                else if (e.Args.Contains("--test-toast"))
                {
                    SingleInstanceHelper.SignalExistingInstance("TEST_TOAST");
                }
                else if (e.Args.Contains("--dashboard"))
                {
                    SingleInstanceHelper.SignalExistingInstance("SHOW_DASHBOARD");
                }
                else
                {
                    SingleInstanceHelper.SignalExistingInstance("SHOW_DASHBOARD");
                }
                Shutdown(0);
                return;
            }

        // Ensure background system tray application stays alive when dialog windows close
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // Cloud License Gatekeeper: Strict hardware lock & admin approval verification
        if (!EnsureCloudLicenseApproved())
        {
            Log.Warning("Access denied by Cloud License Gatekeeper. Application shutting down.");
            Shutdown(0);
            return;
        }

        // Live revocation listener: if admin bans, unapproves, or deletes the user while app is running, immediately block them
        FirebaseCloudAuthService.Instance.OnAuthStateChanged += (status, user) =>
        {
            if (status == CloudAuthStatus.UpdateRequired)
            {
                Dispatcher.Invoke(() =>
                {
                    StopFileWatcher();
                    if (Windows.OfType<MandatoryUpdateWindow>().Any()) return;
                    foreach (Window win in Windows.Cast<Window>().ToList())
                    {
                        if (win is not MandatoryUpdateWindow)
                        {
                            try { win.Close(); } catch { }
                        }
                    }
                    var updateWin = new MandatoryUpdateWindow();
                    updateWin.ShowDialog();
                });
                return;
            }

            if (status == CloudAuthStatus.Banned || 
                status == CloudAuthStatus.PendingApproval || 
                status == CloudAuthStatus.DeviceMismatch || 
                status == CloudAuthStatus.Expired || 
                status == CloudAuthStatus.NotLoggedIn)
            {
                Dispatcher.Invoke(() =>
                {
                    StopFileWatcher();
                    if (Windows.OfType<AuthGateWindow>().Any()) return;
                    foreach (Window win in Windows.Cast<Window>().ToList())
                    {
                        if (win is not AuthGateWindow)
                        {
                            try { win.Close(); } catch { }
                        }
                    }
                    var gate = new AuthGateWindow();
                    gate.ShowDialog();
                });
            }
        };

        // Start background heartbeat to re-verify license with cloud periodically
        FirebaseCloudAuthService.Instance.StartLicenseHeartbeat();

        // Initialize settings
        var settings = SettingsManager.Instance.Current;

        // Configure logging
        ConfigureLogging(settings.General.LogLevel);
        Log.Information("DASMO CYBER CAFE TOOLS v1.1 starting up...");

        // Start tray icon
        InitializeTrayIcon();

        // Auto-configure Windows Explorer PDF Preview Handler and disable security warnings
        RegistryHelper.FixExplorerPdfPreviewHandler();
        RegistryHelper.RegisterContextMenu();

        // Start file watcher if auto-compress is enabled
        if (settings.AutoCompress.Enabled)
        {
            StartFileWatcher();
        }

        // Start automatic print queue monitor for Brother DCP-T530DW & cyber cafe billing
        try
        {
            PrintTrackerService.Instance.Start();
            Log.Information("PrintTrackerService background spooler monitor initialized");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to start PrintTrackerService");
        }

        // Start Explorer global Spacebar Quick Peek hook
        _explorerKeyboardHook = new ExplorerKeyboardHook();
        _explorerKeyboardHook.OnSpacebarPeekTriggered += filePath =>
        {
            ShowQuickPeekDialog(filePath);
        };
        _explorerKeyboardHook.Start();

        // Register startup if needed
        if (settings.General.StartWithWindows)
        {
            RegistryHelper.SetStartupEnabled(true);
        }

        // Ensure Ghostscript is installed (runs silently in background — only on first launch)
        // Once installed, all PDF compressions automatically use the professional GS engine
        _ = GhostscriptSetupService.EnsureAvailableAsync(
            onProgress: msg => Log.Information("[GS Setup] {Msg}", msg),
            onComplete: success =>
            {
                if (success)
                {
                    Log.Information("Ghostscript ready at: {Path}", GhostscriptService.FindExecutable());
                    // Must dispatch to UI thread — callback arrives from background Task.Run
                    Dispatcher.Invoke(() =>
                    {
                        _trayIconService?.ShowBalloon(
                            "PDF Engine Upgraded",
                            "Ghostscript installed — PDF compression is now professional-grade!",
                            Hardcodet.Wpf.TaskbarNotification.BalloonIcon.Info);
                    });
                }
            });

        // Handle command-line requests
        if (e.Args.Length >= 2 && e.Args[0] == "--resize")
        {
            string filePath = e.Args[1];
            Log.Information("Right-click resize requested for: {FilePath}", filePath);
            ShowResizeDialog(filePath);
        }
        else if (e.Args.Length >= 2 && e.Args[0] == "--peek")
        {
            string filePath = e.Args[1];
            Log.Information("Right-click peek requested for: {FilePath}", filePath);
            ShowQuickPeekDialog(filePath);
        }
        else if (e.Args.Length >= 2 && e.Args[0] == "--compress")
        {
            var files = e.Args.Skip(1).Where(File.Exists).ToList();
            if (files.Count > 1)
            {
                Log.Information("Right-click batch compress requested for {Count} files", files.Count);
                ShowBatchCompressDialog(files);
            }
            else if (files.Count == 1)
            {
                Log.Information("Right-click compress requested for: {FilePath}", files[0]);
                ShowCompressDialog(files[0]);
            }
        }
        else if (e.Args.Length >= 1 && e.Args[0] == "--merge")
        {
            var selected = ExplorerSelectionHelper.GetSelectedFilePathsInExplorer()
                .Where(f => File.Exists(f) && Path.GetExtension(f).Equals(".pdf", StringComparison.OrdinalIgnoreCase)).ToList();

            var files = e.Args.Skip(1).Where(File.Exists).ToList();
            foreach (var s in selected)
            {
                if (!files.Contains(s, StringComparer.OrdinalIgnoreCase)) files.Add(s);
            }
            Log.Information("Merge PDFs requested for {Count} files", files.Count);
            ShowMergePdfDialog(files);
        }
        else if (e.Args.Length >= 2 && e.Args[0] == "--split")
        {
            string filePath = e.Args[1];
            Log.Information("Split PDF requested for: {FilePath}", filePath);
            ShowSplitPdfDialog(filePath);
        }
        else if (e.Args.Length >= 1 && e.Args[0] == "--img2pdf")
        {
            var selected = ExplorerSelectionHelper.GetSelectedFilePathsInExplorer()
                .Where(f => File.Exists(f) && RegistryHelper.ImageExtensions.Contains(Path.GetExtension(f).ToLowerInvariant())).ToList();
            var files = e.Args.Skip(1).Where(File.Exists).ToList();
            foreach (var s in selected) if (!files.Contains(s, StringComparer.OrdinalIgnoreCase)) files.Add(s);
            Log.Information("Images to PDF requested for {Count} files", files.Count);
            ShowImageToPdfDialog(files);
        }
        else if (e.Args.Length >= 1 && (e.Args[0] == "--edit-pdf" || e.Args[0] == "--editpdf"))
        {
            string? filePath = e.Args.Length >= 2 ? e.Args[1] : null;
            Log.Information("PDF Editor requested for: {FilePath}", filePath ?? "<empty>");
            ShowPdfEditor(filePath);
        }
        else if (e.Args.Length >= 2 && e.Args[0] == "--pdf2img")
        {
            string filePath = e.Args[1];
            Log.Information("PDF to Images requested for: {FilePath}", filePath);
            ShowPdfToImageDialog(filePath);
        }
        else if (e.Args.Length >= 2 && e.Args[0] == "--enhance")
        {
            string filePath = e.Args[1];
            Log.Information("Scan Enhancer requested for: {FilePath}", filePath);
            ShowScanEnhancerDialog(filePath);
        }
        else if (e.Args.Length >= 2 && e.Args[0] == "--stamp")
        {
            string filePath = e.Args[1];
            Log.Information("Photo Stamp requested for: {FilePath}", filePath);
            ShowPhotoStampDialog(filePath);
        }
        else if (e.Args.Length >= 2 && (e.Args[0] == "--signature" || e.Args[0] == "--resizesignature"))
        {
            string filePath = e.Args[1];
            Log.Information("Resize Signature requested for: {FilePath}", filePath);
            ShowSignatureResizeDialog(filePath);
        }
        else if (e.Args.Contains("--history"))
        {
            Log.Information("Output History requested");
            ShowTodayHistoryDialog();
        }
        else if (e.Args.Contains("--stack"))
        {
            Log.Information("Command-line stack requested");
            OnOpenStacker(this, EventArgs.Empty);
        }
        else if (e.Args.Contains("--print-counter") || e.Args.Contains("--printcounter") || e.Args.Contains("--printbilling"))
        {
            Log.Information("Command-line print counter studio requested");
            ShowPrintTrackerStudio();
        }
        else if (e.Args.Contains("--test-toast"))
        {
            Log.Information("Test toast requested via command-line");
            NotificationService.NotifyUpdateAvailable("1.5.9", "Full Cyber Cafe Suite with Auto Spooler & Duplex Accounting");
            Shutdown(0);
            return;
        }
        else if (!e.Args.Contains("--background"))
        {
            Log.Information("Manual launch detected - opening Unified Dashboard Window");
            ShowMainWindow();
        }

        Log.Information("DASMO CYBER CAFE TOOLS started successfully");
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.ToString(), "Startup Error", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(-1);
        }
    }

    private void OnIpcMessageReceived(string message)
    {
        Dispatcher.Invoke(() =>
        {
            try
            {
                if (message == "SHOW_DASHBOARD" || message == "SHOW_SETTINGS" || message == "SHOW")
                {
                    Log.Information("IPC Request: Open Dashboard");
                    ShowMainWindow();
                }
                else if (message == "SHOW_STACKER")
                {
                    Log.Information("IPC Request: Open Stacker");
                    OnOpenStacker(this, EventArgs.Empty);
                }
                else if (message == "SHOW_MERGE" || message.StartsWith("MERGE|"))
                {
                    var files = message.StartsWith("MERGE|")
                        ? message.Substring("MERGE|".Length).Split('|', StringSplitOptions.RemoveEmptyEntries).Where(File.Exists).ToList()
                        : new List<string>();
                    Log.Information("IPC Request: Merge PDFs ({Count} files)", files.Count);
                    ShowMergePdfDialog(files);
                }
                else if (message.StartsWith("SPLIT|"))
                {
                    string filePath = message.Substring("SPLIT|".Length);
                    Log.Information("IPC Request: Split PDF {FilePath}", filePath);
                    ShowSplitPdfDialog(filePath);
                }
                else if (message.StartsWith("IMG2PDF|"))
                {
                    var files = message.Substring("IMG2PDF|".Length).Split('|', StringSplitOptions.RemoveEmptyEntries).Where(File.Exists).ToList();
                    Log.Information("IPC Request: Images to PDF ({Count} files)", files.Count);
                    ShowImageToPdfDialog(files);
                }
                else if (message == "SHOW_PDF_EDITOR" || message == "SHOW_EDIT_PDF" || message.StartsWith("EDIT_PDF|"))
                {
                    string? filePath = message.StartsWith("EDIT_PDF|") ? message.Substring("EDIT_PDF|".Length) : null;
                    Log.Information("IPC Request: Edit PDF {FilePath}", filePath ?? "<new studio>");
                    ShowPdfEditor(filePath);
                }
                else if (message.StartsWith("PDF2IMG|"))
                {
                    string filePath = message.Substring("PDF2IMG|".Length);
                    Log.Information("IPC Request: PDF to Images {FilePath}", filePath);
                    ShowPdfToImageDialog(filePath);
                }
                else if (message.StartsWith("ENHANCE|"))
                {
                    string filePath = message.Substring("ENHANCE|".Length);
                    Log.Information("IPC Request: Scan Enhance {FilePath}", filePath);
                    ShowScanEnhancerDialog(filePath);
                }
                else if (message.StartsWith("STAMP|"))
                {
                    string filePath = message.Substring("STAMP|".Length);
                    Log.Information("IPC Request: Photo Stamp {FilePath}", filePath);
                    ShowPhotoStampDialog(filePath);
                }
                else if (message.StartsWith("SIGNATURE|"))
                {
                    string filePath = message.Substring("SIGNATURE|".Length);
                    Log.Information("IPC Request: Resize Signature {FilePath}", filePath);
                    ShowSignatureResizeDialog(filePath);
                }
                else if (message.StartsWith("CARDPRINT|"))
                {
                    var files = message.Substring("CARDPRINT|".Length)
                        .Split('|', StringSplitOptions.RemoveEmptyEntries)
                        .Where(File.Exists)
                        .ToList();
                    Log.Information("IPC Request: Ration / Aadhaar Card Print ({Count} files)", files.Count);
                    ShowMainWindow(DashboardTab.GovtCardTab);
                    if (files.Count > 0 && _mainWindow != null)
                    {
                        _ = _mainWindow.ViewModel.GovtCardVm.AddSourceFilesAsync(files);
                    }
                }
                else if (message == "SHOW_HISTORY")
                {
                    Log.Information("IPC Request: Show Output History");
                    ShowTodayHistoryDialog();
                }
                else if (message == "SHOW_PRINT_COUNTER" || message == "SHOW_PRINT_TRACKER")
                {
                    Log.Information("IPC Request: Show Print Counter & Billing Studio");
                    ShowPrintTrackerStudio();
                }
                else if (message == "TEST_TOAST")
                {
                    Log.Information("IPC Request: Test toast");
                    NotificationService.NotifyUpdateAvailable("1.5.9", "Full Cyber Cafe Suite with Auto Spooler & Duplex Accounting");
                }
                else if (message.StartsWith("RESIZE|"))
                {
                    string filePath = message.Substring("RESIZE|".Length);
                    Log.Information("IPC Request: Resize file {FilePath}", filePath);
                    ShowResizeDialog(filePath);
                }
                else if (message.StartsWith("PEEK|"))
                {
                    string filePath = message.Substring("PEEK|".Length);
                    Log.Information("IPC Request: Peek file {FilePath}", filePath);
                    ShowQuickPeekDialog(filePath);
                }
                else if (message.StartsWith("COMPRESS_BATCH|"))
                {
                    var files = message.Substring("COMPRESS_BATCH|".Length)
                        .Split('|', StringSplitOptions.RemoveEmptyEntries)
                        .Where(File.Exists)
                        .ToList();
                    Log.Information("IPC Request: Batch compress {Count} files", files.Count);
                    if (files.Count > 1)
                        ShowBatchCompressDialog(files);
                    else if (files.Count == 1)
                        ShowCompressDialog(files[0]);
                }
                else if (message.StartsWith("COMPRESS|"))
                {
                    string filePath = message.Substring("COMPRESS|".Length);
                    Log.Information("IPC Request: Compress file {FilePath}", filePath);
                    ShowCompressDialog(filePath);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to process IPC message: {Message}", message);
            }
        });
    }

    private void ConfigureLogging(string logLevel)
    {
        string logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DASMO CYBER CAFE TOOLS", "logs");
        Directory.CreateDirectory(logDir);

        var minLevel = logLevel.ToLower() switch
        {
            "debug" => LogEventLevel.Debug,
            "off" => LogEventLevel.Fatal,
            _ => LogEventLevel.Information
        };

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Is(minLevel)
            .WriteTo.Console()
            .WriteTo.File(
                Path.Combine(logDir, "dasmo_cyber_cafe_tools_.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 30,
                outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff}] [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
    }

    private void InitializeTrayIcon()
    {
        _trayIconService = new TrayIconService();
        _trayIconService.OnOpenDashboard += (_, _) => ShowMainWindow();
        _trayIconService.OnOpenSettings += OnOpenSettings;
        _trayIconService.OnOpenStacker += OnOpenStacker;
        _trayIconService.OnOpenGovtCardPrint += (_, _) => ShowMainWindow(DashboardTab.GovtCardTab);
        _trayIconService.OnOpenMerge += OnOpenMerge;
        _trayIconService.OnOpenSplit += OnOpenSplit;
        _trayIconService.OnOpenImg2Pdf += OnOpenImg2Pdf;
        _trayIconService.OnOpenPdf2Img += OnOpenPdf2Img;
        _trayIconService.OnOpenEnhance += OnOpenEnhance;
        _trayIconService.OnOpenStamp += OnOpenStamp;
        _trayIconService.OnOpenSignatureResize += OnOpenSignatureResize;
        _trayIconService.OnOpenPdfEditor += (_, _) => ShowPdfEditor();
        _trayIconService.OnOpenPrintCounter += (_, _) => ShowPrintTrackerStudio();
        _trayIconService.OnOpenHistory += OnOpenHistory;
        _trayIconService.OnPauseToggle += OnPauseToggle;
        _trayIconService.OnViewLog += OnViewLog;
        _trayIconService.OnExit += OnExit;
        _trayIconService.Initialize();
    }

    private void StartFileWatcher()
    {
        try
        {
            _fileWatcherService?.Dispose();
            
            var imageCompressor = new ImageCompressor();
            var pdfCompressor = new PdfCompressor();
            var officeCompressor = new OfficeCompressor(imageCompressor);
            var compressionEngine = new CompressionEngine(imageCompressor, pdfCompressor, officeCompressor);
            var notificationService = new NotificationService();
            
            _fileWatcherService = new FileWatcherService(compressionEngine, notificationService);
            _fileWatcherService.Start();
            Log.Information("File watcher service started");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to start file watcher service");
        }
    }

    private void StopFileWatcher()
    {
        _fileWatcherService?.Stop();
        Log.Information("File watcher service stopped");
    }

    /// <summary>
    /// Restarts or stops the background file watcher service depending on current settings.
    /// </summary>
    public void RestartFileWatcher()
    {
        try
        {
            var settings = SettingsManager.Instance.Current;
            if (!settings.AutoCompress.Enabled || settings.AutoCompress.ActionOnNewFile == "off" || settings.AutoCompress.ActionOnNewFile == "disabled")
            {
                StopFileWatcher();
            }
            else
            {
                if (_fileWatcherService == null)
                {
                    StartFileWatcher();
                }
                else
                {
                    _fileWatcherService.Restart();
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to dynamically restart file watcher service");
        }
    }

    /// <summary>
    /// Checks whether the background file watcher service is currently active and monitoring.
    /// </summary>
    public bool IsFileWatcherActive => _fileWatcherService != null &&
                                       !_fileWatcherService.IsPaused &&
                                       SettingsManager.Instance.Current.AutoCompress.Enabled &&
                                       SettingsManager.Instance.Current.AutoCompress.ActionOnNewFile != "off" &&
                                       SettingsManager.Instance.Current.AutoCompress.ActionOnNewFile != "disabled";

    private void OnOpenStacker(object? sender, EventArgs e)
    {
        ShowStackerWindow();
    }

    private void OnOpenMerge(object? sender, EventArgs e)
    {
        Dispatcher.Invoke(() => ShowMergePdfDialog());
    }

    private void OnOpenSplit(object? sender, EventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            try
            {
                var dlg = new Microsoft.Win32.OpenFileDialog
                {
                    Title = "Select PDF File to Extract / Split Pages",
                    Filter = "PDF Files (*.pdf)|*.pdf"
                };
                if (dlg.ShowDialog() == true && !string.IsNullOrEmpty(dlg.FileName))
                {
                    ShowSplitPdfDialog(dlg.FileName);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error in OnOpenSplit");
            }
        });
    }

    private void OnOpenImg2Pdf(object? sender, EventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            try
            {
                var dlg = new Microsoft.Win32.OpenFileDialog
                {
                    Title = "Select Image Files to Convert to PDF",
                    Filter = "Image Files (*.jpg;*.jpeg;*.png;*.webp;*.bmp;*.tiff)|*.jpg;*.jpeg;*.png;*.webp;*.bmp;*.tiff|All Files (*.*)|*.*",
                    Multiselect = true
                };
                if (dlg.ShowDialog() == true && dlg.FileNames != null && dlg.FileNames.Length > 0)
                {
                    ShowImageToPdfDialog(dlg.FileNames);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error in OnOpenImg2Pdf");
            }
        });
    }

    private void OnOpenPdf2Img(object? sender, EventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            try
            {
                var dlg = new Microsoft.Win32.OpenFileDialog
                {
                    Title = "Select PDF File to Convert to Images",
                    Filter = "PDF Files (*.pdf)|*.pdf"
                };
                if (dlg.ShowDialog() == true && !string.IsNullOrEmpty(dlg.FileName))
                {
                    ShowPdfToImageDialog(dlg.FileName);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error in OnOpenPdf2Img");
            }
        });
    }

    private void OnOpenEnhance(object? sender, EventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            try
            {
                var dlg = new Microsoft.Win32.OpenFileDialog
                {
                    Title = "Select Phone Scanned Document Image",
                    Filter = "All Supported Images (*.jpg;*.jpeg;*.jpe;*.jfif;*.png;*.webp;*.bmp;*.gif;*.tiff;*.tif)|*.jpg;*.jpeg;*.jpe;*.jfif;*.png;*.webp;*.bmp;*.gif;*.tiff;*.tif|JPEG Images (*.jpg;*.jpeg;*.jfif;*.jpe)|*.jpg;*.jpeg;*.jfif;*.jpe|PNG Images (*.png)|*.png|WebP Images (*.webp)|*.webp|BMP Images (*.bmp)|*.bmp|TIFF Images (*.tiff;*.tif)|*.tiff;*.tif|All Files (*.*)|*.*"
                };
                if (dlg.ShowDialog() == true && !string.IsNullOrEmpty(dlg.FileName))
                {
                    ShowScanEnhancerDialog(dlg.FileName);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error in OnOpenEnhance");
            }
        });
    }

    private void OnOpenStamp(object? sender, EventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            try
            {
                var dlg = new Microsoft.Win32.OpenFileDialog
                {
                    Title = "Select Passport Photo to Add Name & Date Stamp",
                    Filter = "All Supported Images (*.jpg;*.jpeg;*.jpe;*.jfif;*.png;*.webp;*.bmp;*.gif;*.tiff;*.tif)|*.jpg;*.jpeg;*.jpe;*.jfif;*.png;*.webp;*.bmp;*.gif;*.tiff;*.tif|JPEG Images (*.jpg;*.jpeg;*.jfif;*.jpe)|*.jpg;*.jpeg;*.jfif;*.jpe|PNG Images (*.png)|*.png|WebP Images (*.webp)|*.webp|BMP Images (*.bmp)|*.bmp|TIFF Images (*.tiff;*.tif)|*.tiff;*.tif|All Files (*.*)|*.*"
                };
                if (dlg.ShowDialog() == true && !string.IsNullOrEmpty(dlg.FileName))
                {
                    ShowPhotoStampDialog(dlg.FileName);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error in OnOpenStamp");
            }
        });
    }

    private void OnOpenSignatureResize(object? sender, EventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            try
            {
                var dlg = new Microsoft.Win32.OpenFileDialog
                {
                    Title = "Select Candidate Signature or Photo to Resize",
                    Filter = "All Supported Images (*.jpg;*.jpeg;*.jpe;*.jfif;*.png;*.webp;*.bmp;*.gif;*.tiff;*.tif)|*.jpg;*.jpeg;*.jpe;*.jfif;*.png;*.webp;*.bmp;*.gif;*.tiff;*.tif|JPEG Images (*.jpg;*.jpeg;*.jfif;*.jpe)|*.jpg;*.jpeg;*.jfif;*.jpe|PNG Images (*.png)|*.png|WebP Images (*.webp)|*.webp|BMP Images (*.bmp)|*.bmp|TIFF Images (*.tiff;*.tif)|*.tiff;*.tif|All Files (*.*)|*.*"
                };
                if (dlg.ShowDialog() == true && !string.IsNullOrEmpty(dlg.FileName))
                {
                    ShowSignatureResizeDialog(dlg.FileName);
                }
                else
                {
                    ShowMainWindow(DashboardTab.ResizeSignature);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error in OnOpenSignatureResize");
            }
        });
    }

    private void OnOpenHistory(object? sender, EventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            try
            {
                ShowTodayHistoryDialog();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error in OnOpenHistory");
            }
        });
    }

    private void OnOpenSettings(object? sender, EventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            if (!EnsureCloudLicenseApproved()) return;
            var existingWindow = Windows.OfType<SettingsWindow>().FirstOrDefault();
            if (existingWindow != null)
            {
                existingWindow.Activate();
                return;
            }

            var settingsWindow = new SettingsWindow();
            settingsWindow.SettingsSaved += OnSettingsSaved;
            settingsWindow.Show();
        });
    }

    private void OnSettingsSaved(object? sender, EventArgs e)
    {
        Log.Information("Settings saved — reloading services");
        var settings = SettingsManager.Instance.Current;

        // Update logging level
        ConfigureLogging(settings.General.LogLevel);

        // Update startup registration
        RegistryHelper.SetStartupEnabled(settings.General.StartWithWindows);

        // Restart file watcher with new settings
        StopFileWatcher();
        if (settings.AutoCompress.Enabled && !_isPaused)
        {
            StartFileWatcher();
        }

        // Update tray icon visibility
        if (_trayIconService != null)
        {
            _trayIconService.SetVisible(settings.General.ShowTrayIcon);
        }
    }

    private void OnPauseToggle(object? sender, bool isPaused)
    {
        _isPaused = isPaused;
        if (_isPaused)
        {
            StopFileWatcher();
            _trayIconService?.SetPaused(true);
            Log.Information("Auto-compress paused by user");
        }
        else
        {
            var settings = SettingsManager.Instance.Current;
            if (settings.AutoCompress.Enabled)
            {
                StartFileWatcher();
            }
            _trayIconService?.SetPaused(false);
            Log.Information("Auto-compress resumed by user");
        }
    }

    private void OnViewLog(object? sender, EventArgs e)
    {
        string logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DASMO CYBER CAFE TOOLS", "logs");
        Directory.CreateDirectory(logDir);

        // Find the most recent log file
        var latestLog = Directory.GetFiles(logDir, "dasmo_cyber_cafe_tools_*.log")
            .Concat(Directory.GetFiles(logDir, "smartsaver_*.log"))
            .OrderByDescending(f => f)
            .FirstOrDefault();

        if (latestLog != null)
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = latestLog,
                UseShellExecute = true
            });
        }
        else
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = logDir,
                UseShellExecute = true
            });
        }
    }

    private void OnExit(object? sender, EventArgs e)
    {
        Log.Information("DASMO CYBER CAFE TOOLS exiting...");
        _fileWatcherService?.Dispose();
        _trayIconService?.Dispose();
        try { Microsoft.Toolkit.Uwp.Notifications.ToastNotificationManagerCompat.History.Clear(); } catch { }
        Log.CloseAndFlush();
        Shutdown(0);
    }

    public void ShowResizeDialog(string filePath)
    {
        if (!EnsureCloudLicenseApproved()) return;
        if (!File.Exists(filePath))
        {
            Log.Warning("Resize requested for non-existent file: {FilePath}", filePath);
            MessageBox.Show($"File not found: {filePath}", "DASMO CYBER CAFE TOOLS", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var dialog = new ImageResizeDialog(filePath);
        dialog.Show();
    }

    public void ShowCompressDialog(string filePath)
    {
        if (!EnsureCloudLicenseApproved()) return;
        if (!File.Exists(filePath))
        {
            Log.Warning("Compress requested for non-existent file: {FilePath}", filePath);
            MessageBox.Show($"File not found: {filePath}", "DASMO CYBER CAFE TOOLS", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var dialog = new Views.FileCompressDialog(filePath);
        dialog.Show();
    }

    public void ShowBatchCompressDialog(IEnumerable<string> filePaths)
    {
        if (!EnsureCloudLicenseApproved()) return;
        var files = filePaths.Where(File.Exists).ToList();
        if (files.Count == 0) return;

        var dialog = new BatchCompressDialog(files);
        dialog.Show();
    }

    public void ShowQuickPeekDialog(string filePath)
    {
        try
        {
            if (!EnsureCloudLicenseApproved()) return;
            if (!File.Exists(filePath)) return;

            var existingPeek = Windows.OfType<QuickPeekDialog>().FirstOrDefault();
            if (existingPeek != null)
            {
                bool isSameFile = string.Equals(existingPeek.FilePath, filePath, StringComparison.OrdinalIgnoreCase);
                existingPeek.Close();
                if (isSameFile)
                {
                    // Spacebar pressed on the currently previewed file toggles it off
                    return;
                }
            }

            var dialog = new QuickPeekDialog(filePath);
            dialog.Show();
            dialog.Activate();
            dialog.Focus();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to show QuickPeekDialog for {FilePath}", filePath);
        }
    }

    public void ShowMergePdfDialog(IEnumerable<string>? filePaths = null)
    {
        if (!EnsureCloudLicenseApproved()) return;
        var existingDialog = Windows.OfType<Views.MergePdfDialog>().FirstOrDefault();
        if (existingDialog != null)
        {
            existingDialog.Activate();
            if (filePaths != null && filePaths.Any())
            {
                if (existingDialog.DataContext is MergePdfViewModel vm)
                {
                    vm.AddFiles(filePaths);
                }
            }
            return;
        }

        var dialog = new Views.MergePdfDialog(filePaths);
        dialog.Show();
        dialog.Activate();
    }

    private void ShowSplitPdfDialog(string filePath)
    {
        try
        {
            if (!EnsureCloudLicenseApproved()) return;
            if (!File.Exists(filePath)) return;

            var existingDialog = Windows.OfType<Views.SplitPdfDialog>().FirstOrDefault();
            if (existingDialog != null)
            {
                if (existingDialog.DataContext is ViewModels.SplitPdfViewModel vm)
                {
                    vm.AppendPdfFile(filePath);
                }
                existingDialog.Activate();
                return;
            }

            var dialog = new Views.SplitPdfDialog(filePath);
            dialog.Show();
            dialog.Activate();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to show SplitPdfDialog for {Path}", filePath);
        }
    }

    private void ShowImageToPdfDialog(IEnumerable<string>? initialFiles = null)
    {
        try
        {
            if (!EnsureCloudLicenseApproved()) return;
            var existingDialog = Windows.OfType<Views.ImageToPdfDialog>().FirstOrDefault();
            if (existingDialog != null)
            {
                if (initialFiles != null && existingDialog.DataContext is ViewModels.ImageToPdfViewModel vm)
                {
                    foreach (var f in initialFiles) vm.AddFile(f);
                }
                existingDialog.Activate();
                return;
            }

            var dialog = new Views.ImageToPdfDialog(initialFiles);
            dialog.Show();
            dialog.Activate();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to show ImageToPdfDialog");
        }
    }

    private void ShowPdfToImageDialog(string filePath)
    {
        try
        {
            if (!EnsureCloudLicenseApproved()) return;
            if (!File.Exists(filePath)) return;

            var existingDialog = Windows.OfType<Views.PdfToImageDialog>().FirstOrDefault();
            if (existingDialog != null)
            {
                existingDialog.Activate();
                return;
            }

            var dialog = new Views.PdfToImageDialog(filePath);
            dialog.Show();
            dialog.Activate();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to show PdfToImageDialog for {Path}", filePath);
        }
    }

    private void ShowScanEnhancerDialog(string filePath)
    {
        try
        {
            if (!EnsureCloudLicenseApproved()) return;
            if (!File.Exists(filePath)) return;

            var existingDialog = Windows.OfType<Views.ScanEnhancerDialog>().FirstOrDefault();
            if (existingDialog != null)
            {
                existingDialog.Activate();
                return;
            }

            var dialog = new Views.ScanEnhancerDialog(filePath);
            dialog.Show();
            dialog.Activate();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to show ScanEnhancerDialog for {Path}", filePath);
        }
    }

    private void ShowPhotoStampDialog(string filePath)
    {
        try
        {
            if (!EnsureCloudLicenseApproved()) return;
            if (!File.Exists(filePath)) return;

            var existingDialog = Windows.OfType<Views.PhotoStampDialog>().FirstOrDefault();
            if (existingDialog != null)
            {
                existingDialog.Activate();
                return;
            }

            var dialog = new Views.PhotoStampDialog(filePath);
            dialog.Show();
            dialog.Activate();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to show PhotoStampDialog for {Path}", filePath);
        }
    }

    public void ShowSignatureResizeDialog(string filePath)
    {
        try
        {
            if (!EnsureCloudLicenseApproved()) return;
            var existingDialog = Windows.OfType<Views.SignatureResizeDialog>().FirstOrDefault();
            if (existingDialog != null)
            {
                if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath))
                {
                    if (existingDialog.DataContext is SignatureResizeViewModel vm)
                    {
                        vm.LoadFile(filePath);
                    }
                }
                existingDialog.Activate();
                return;
            }

            var dialog = new Views.SignatureResizeDialog(filePath);
            dialog.Show();
            dialog.Activate();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to show SignatureResizeDialog for {Path}", filePath);
        }
    }

    private void ShowTodayHistoryDialog()
    {
        try
        {
            if (!EnsureCloudLicenseApproved()) return;
            var existingDialog = Windows.OfType<Views.TodayHistoryDialog>().FirstOrDefault();
            if (existingDialog != null)
            {
                existingDialog.Activate();
                return;
            }

            var dialog = new Views.TodayHistoryDialog();
            dialog.Show();
            dialog.Activate();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to show TodayHistoryDialog");
        }
    }

    public void ShowPdfEditor(string? filePath = null)
    {
        Dispatcher.Invoke(() =>
        {
            try
            {
                if (!EnsureCloudLicenseApproved()) return;
                var existing = Windows.OfType<Views.PdfEditorWindow>().FirstOrDefault();
                if (existing != null)
                {
                    if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath))
                    {
                        _ = existing.Vm.LoadDocumentAsync(filePath);
                    }
                    if (existing.WindowState == WindowState.Minimized)
                    {
                        existing.WindowState = WindowState.Normal;
                    }
                    existing.Activate();
                    existing.Focus();
                    return;
                }

                var win = new Views.PdfEditorWindow(filePath ?? string.Empty);
                win.Show();
                win.Activate();
                win.Focus();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to show PdfEditorWindow in App");
                MessageBox.Show($"Could not open PDF Editor Studio:\n{ex.Message}", "PDF Editor Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        });
    }

    public void ShowPrintTrackerStudio()
    {
        Dispatcher.Invoke(() =>
        {
            try
            {
                if (!EnsureCloudLicenseApproved()) return;
                PrintTrackerStudioWindow.ShowStudio();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to show PrintTrackerStudioWindow");
            }
        });
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { PrintTrackerService.Instance.Stop(); } catch { }
        _explorerKeyboardHook?.Dispose();
        _fileWatcherService?.Dispose();
        _trayIconService?.Dispose();
        SingleInstanceHelper.Release();
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
