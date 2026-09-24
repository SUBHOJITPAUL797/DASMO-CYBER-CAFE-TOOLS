using System.IO;
using System.Drawing;
using System.Windows;
using System.Windows.Controls;
using Hardcodet.Wpf.TaskbarNotification;
using Serilog;

namespace SmartSaver.Services;

/// <summary>
/// Manages the system tray (notification area) icon using Hardcodet.NotifyIcon.Wpf.
/// Provides a context menu with Open Settings, Pause/Resume, View Log, and Exit
/// actions, and exposes corresponding events for the host application to handle.
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private TaskbarIcon? _taskbarIcon;
    private MenuItem? _pauseMenuItem;
    private bool _isPaused;
    private bool _disposed;

    /// <summary>Raised when the user selects "Open DASMO Cyber Center" from the tray menu.</summary>
    public event EventHandler? OnOpenDashboard;

    /// <summary>Raised when the user selects "Open Settings" from the tray menu.</summary>
    public event EventHandler? OnOpenSettings;

    /// <summary>Raised when the user selects "A4 Document Stacker" from the tray menu.</summary>
    public event EventHandler? OnOpenStacker;

    /// <summary>Raised when the user selects "Ration / Aadhaar A4 Print" from the tray menu.</summary>
    public event EventHandler? OnOpenGovtCardPrint;

    /// <summary>Raised when the user selects "Merge PDF Files" from the tray menu.</summary>
    public event EventHandler? OnOpenMerge;

    /// <summary>Raised when the user selects "Extract / Split PDF" from the tray menu.</summary>
    public event EventHandler? OnOpenSplit;

    /// <summary>Raised when the user selects "Convert Images to PDF" from the tray menu.</summary>
    public event EventHandler? OnOpenImg2Pdf;

    /// <summary>Raised when the user selects "Convert PDF to Images" from the tray menu.</summary>
    public event EventHandler? OnOpenPdf2Img;

    /// <summary>Raised when the user selects "Enhance Phone Scan" from the tray menu.</summary>
    public event EventHandler? OnOpenEnhance;

    /// <summary>Raised when the user selects "Add Name & Date Stamp" from the tray menu.</summary>
    public event EventHandler? OnOpenStamp;

    /// <summary>Raised when the user selects "Resize Signature" from the tray menu.</summary>
    public event EventHandler? OnOpenSignatureResize;

    /// <summary>Raised when the user selects "PDF Editor Studio" from the tray menu.</summary>
    public event EventHandler? OnOpenPdfEditor;

    /// <summary>Raised when the user selects "Today's Output History" from the tray menu.</summary>
    public event EventHandler? OnOpenHistory;

    /// <summary>Raised when the user selects "Print Counter & Billing Studio" from the tray menu.</summary>
    public event EventHandler? OnOpenPrintCounter;

    /// <summary>Raised when the user toggles the Pause/Resume state.</summary>
    public event EventHandler<bool>? OnPauseToggle;

    /// <summary>Raised when the user selects "View Log" from the tray menu.</summary>
    public event EventHandler? OnViewLog;

    /// <summary>Raised when the user selects "Exit" from the tray menu.</summary>
    public event EventHandler? OnExit;

    /// <summary>Gets whether the service is currently in paused state.</summary>
    public bool IsPaused => _isPaused;

    /// <summary>
    /// Initializes and displays the system tray icon with context menu.
    /// Must be called from the UI (STA) thread.
    /// </summary>
    public void Initialize()
    {
        if (_taskbarIcon is not null)
        {
            Log.Warning("TrayIconService is already initialized");
            return;
        }

        try
        {
            _taskbarIcon = new TaskbarIcon
            {
                ToolTipText = "DASMO CYBER CAFE TOOLS",
                ContextMenu = BuildContextMenu(),
                Visibility = Visibility.Visible
            };

            // Use embedded application icon or a default
            SetIcon(paused: false);

            _taskbarIcon.TrayMouseDoubleClick += (_, _) => OnOpenDashboard?.Invoke(this, EventArgs.Empty);

            Log.Information("System tray icon initialized");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to initialize system tray icon");
        }
    }

    /// <summary>
    /// Toggles the paused state and updates the tray icon appearance.
    /// </summary>
    public void TogglePause()
    {
        _isPaused = !_isPaused;
        UpdatePauseState();
        OnPauseToggle?.Invoke(this, _isPaused);
    }

    /// <summary>
    /// Sets the paused state explicitly and updates the tray icon.
    /// </summary>
    /// <param name="paused">Whether the service should be paused.</param>
    public void SetPaused(bool paused)
    {
        if (_isPaused == paused) return;

        _isPaused = paused;
        UpdatePauseState();
        OnPauseToggle?.Invoke(this, _isPaused);
    }

    public void SetVisible(bool visible)
    {
        if (_taskbarIcon != null)
        {
            _taskbarIcon.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    /// <summary>
    /// Shows a balloon tip notification from the tray icon.
    /// </summary>
    /// <param name="title">Balloon title.</param>
    /// <param name="message">Balloon message body.</param>
    /// <param name="icon">Balloon icon type.</param>
    public void ShowBalloon(string title, string message, BalloonIcon icon = BalloonIcon.Info)
    {
        try
        {
            _taskbarIcon?.ShowBalloonTip(title, message, icon);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to show balloon tip");
        }
    }

    private ContextMenu BuildContextMenu()
    {
        // --- Color palette (dark theme, always visible regardless of Windows accent/theme) ---
        var bgBrush       = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(28, 28, 40));   // deep navy
        var fgBrush       = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.White);
        var hoverBrush    = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(60, 60, 90));   // lighter on hover
        var separatorBrush= new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(60, 60, 80));

        var menu = new ContextMenu
        {
            Background = bgBrush,
            BorderBrush = separatorBrush,
            BorderThickness = new System.Windows.Thickness(1),
            Padding = new System.Windows.Thickness(0, 4, 0, 4)
        };

        MenuItem MakeItem(string header, RoutedEventHandler onClick)
        {
            var item = new MenuItem
            {
                Header = header,
                Background = bgBrush,
                Foreground = fgBrush,
                Padding = new System.Windows.Thickness(12, 6, 16, 6),
                FontSize = 13,
                FontFamily = new System.Windows.Media.FontFamily("Segoe UI")
            };
            // Keep hover visible
            item.MouseEnter += (_, _) => item.Background = hoverBrush;
            item.MouseLeave += (_, _) => item.Background = bgBrush;
            item.Click += onClick;
            return item;
        }

        Separator MakeSep()
        {
            return new Separator
            {
                Background = separatorBrush,
                Margin = new System.Windows.Thickness(6, 2, 6, 2),
                Height = 1
            };
        }

        // Open DASMO CYBER CAFE TOOLS (All Tools)
        var openDashboardItem = MakeItem("⚡   Open DASMO CYBER CAFE TOOLS", (_, _) =>
        {
            Log.Information("Tray menu: Open Dashboard");
            OnOpenDashboard?.Invoke(this, EventArgs.Empty);
        });
        menu.Items.Add(openDashboardItem);

        menu.Items.Add(MakeSep());

        // Print Counter & Billing Studio
        var openPrintCounterItem = MakeItem("🖨️   Print Counter & Billing Studio...", (_, _) =>
        {
            Log.Information("Tray menu: Open Print Counter & Billing Studio");
            OnOpenPrintCounter?.Invoke(this, EventArgs.Empty);
        });
        menu.Items.Add(openPrintCounterItem);

        // A4 Document Stacker
        var openStackerItem = MakeItem("🖨   A4 Document Stacker", (_, _) =>
        {
            Log.Information("Tray menu: Open Stacker");
            OnOpenStacker?.Invoke(this, EventArgs.Empty);
        });
        menu.Items.Add(openStackerItem);

        // Ration / Aadhaar A4 Print
        var openCardPrintItem = MakeItem("💳   Ration / Aadhaar A4 Print...", (_, _) =>
        {
            Log.Information("Tray menu: Open Ration / Aadhaar A4 Print");
            OnOpenGovtCardPrint?.Invoke(this, EventArgs.Empty);
        });
        menu.Items.Add(openCardPrintItem);

        // Merge PDF Files
        var openMergeItem = MakeItem("📑   Merge PDF Files...", (_, _) =>
        {
            Log.Information("Tray menu: Open Merge PDFs");
            OnOpenMerge?.Invoke(this, EventArgs.Empty);
        });
        menu.Items.Add(openMergeItem);

        // Split / Extract PDF
        var openSplitItem = MakeItem("✂️   Split / Extract PDF...", (_, _) =>
        {
            Log.Information("Tray menu: Open Split PDF");
            OnOpenSplit?.Invoke(this, EventArgs.Empty);
        });
        menu.Items.Add(openSplitItem);

        // Convert Images to PDF (A4)
        var openImg2PdfItem = MakeItem("🖼️   Convert Images to PDF (A4)...", (_, _) =>
        {
            Log.Information("Tray menu: Open Images to PDF");
            OnOpenImg2Pdf?.Invoke(this, EventArgs.Empty);
        });
        menu.Items.Add(openImg2PdfItem);

        // Convert PDF to Images
        var openPdf2ImgItem = MakeItem("📄   Convert PDF to JPG Images...", (_, _) =>
        {
            Log.Information("Tray menu: Open PDF to Images");
            OnOpenPdf2Img?.Invoke(this, EventArgs.Empty);
        });
        menu.Items.Add(openPdf2ImgItem);

        // Enhance Document Scan
        var openEnhanceItem = MakeItem("✨   Enhance Phone Scan (Magic White)...", (_, _) =>
        {
            Log.Information("Tray menu: Open Scan Enhancer");
            OnOpenEnhance?.Invoke(this, EventArgs.Empty);
        });
        menu.Items.Add(openEnhanceItem);

        // Candidate Photo Stamp
        var openStampItem = MakeItem("🏷️   Add Name & Date Stamp (SSC Photo)...", (_, _) =>
        {
            Log.Information("Tray menu: Open Photo Stamp");
            OnOpenStamp?.Invoke(this, EventArgs.Empty);
        });
        menu.Items.Add(openStampItem);

        // Resize Signature & Photo (Pi7 Resizer)
        var openSignatureItem = MakeItem("✍️   Resize Signature & Photo (Exact px/cm)...", (_, _) =>
        {
            Log.Information("Tray menu: Open Resize Signature");
            OnOpenSignatureResize?.Invoke(this, EventArgs.Empty);
        });
        menu.Items.Add(openSignatureItem);

        // PDF Editor Studio (In-Place Vector Modification)
        var openPdfEditorItem = MakeItem("📝   PDF Editor Studio (In-Place Edit)...", (_, _) =>
        {
            Log.Information("Tray menu: Open PDF Editor Studio");
            OnOpenPdfEditor?.Invoke(this, EventArgs.Empty);
        });
        menu.Items.Add(openPdfEditorItem);

        // Today's Output History Hub
        var openHistoryItem = MakeItem("🕒   Today's Output History...", (_, _) =>
        {
            Log.Information("Tray menu: Open Output History");
            OnOpenHistory?.Invoke(this, EventArgs.Empty);
        });
        menu.Items.Add(openHistoryItem);

        menu.Items.Add(MakeSep());

        // Open Settings
        var openSettingsItem = MakeItem("⚙   Open Settings", (_, _) =>
        {
            Log.Information("Tray menu: Open Settings");
            OnOpenSettings?.Invoke(this, EventArgs.Empty);
        });
        menu.Items.Add(openSettingsItem);

        menu.Items.Add(MakeSep());

        // Pause / Resume
        _pauseMenuItem = MakeItem("||   Pause", (_, _) =>
        {
            TogglePause();
            Log.Debug("Tray menu: {Action}", _isPaused ? "Paused" : "Resumed");
        });
        menu.Items.Add(_pauseMenuItem);

        menu.Items.Add(MakeSep());

        // View Log
        var viewLogItem = MakeItem("=   View Log", (_, _) =>
        {
            Log.Debug("Tray menu: View Log");
            OnViewLog?.Invoke(this, EventArgs.Empty);
        });
        menu.Items.Add(viewLogItem);

        menu.Items.Add(MakeSep());

        // Exit
        var exitItem = MakeItem("X   Exit", (_, _) =>
        {
            Log.Debug("Tray menu: Exit");
            OnExit?.Invoke(this, EventArgs.Empty);
        });
        menu.Items.Add(exitItem);

        return menu;
    }

    private void UpdatePauseState()
    {
        if (_pauseMenuItem is not null)
        {
            _pauseMenuItem.Header = _isPaused ? "▶   Resume" : "⏸   Pause";
        }

        if (_taskbarIcon is not null)
        {
            _taskbarIcon.ToolTipText = _isPaused
                ? "DASMO CYBER COMPRESSOR — PAUSED"
                : "DASMO CYBER COMPRESSOR — Auto File Compressor";
        }

        SetIcon(_isPaused);
        Log.Information("DASMO CYBER COMPRESSOR is now {State}", _isPaused ? "paused" : "active");
    }

    private System.Drawing.Icon? _currentTrayIcon;

    private void SetIcon(bool paused)
    {
        if (_taskbarIcon == null) return;

        try
        {
            string iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "tray_icon.ico");
            System.Drawing.Icon? loadedIcon = null;

            if (File.Exists(iconPath))
            {
                loadedIcon = new System.Drawing.Icon(iconPath, 32, 32);
            }
            else
            {
                var uri = new Uri("pack://application:,,,/Resources/tray_icon.ico", UriKind.Absolute);
                var streamInfo = System.Windows.Application.GetResourceStream(uri);
                if (streamInfo != null)
                {
                    using var stream = streamInfo.Stream;
                    loadedIcon = new System.Drawing.Icon(stream, 32, 32);
                }
            }

            if (loadedIcon != null)
            {
                var oldIcon = _currentTrayIcon;
                _currentTrayIcon = loadedIcon;
                _taskbarIcon.Icon = loadedIcon;
                oldIcon?.Dispose();
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to set custom tray icon");
        }
    }

    /// <summary>Removes the tray icon and releases resources.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            if (_taskbarIcon is not null)
            {
                _taskbarIcon.Visibility = Visibility.Collapsed;
                _taskbarIcon.Dispose();
                _taskbarIcon = null;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Error disposing tray icon");
        }

        GC.SuppressFinalize(this);
    }
}
