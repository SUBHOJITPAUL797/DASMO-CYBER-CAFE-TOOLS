using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Serilog;
using SmartSaver.Models;
using SmartSaver.Services;
using SmartSaver.Views;

namespace SmartSaver.ViewModels;

public enum DashboardTab
{
    Home = 0,
    MergePdf = 1,
    SplitPdf = 2,
    Img2Pdf = 3,
    Pdf2Img = 4,
    ScannerEnhance = 5,
    PhotoStamp = 6,
    History = 7,
    Settings = 8,
    GovtCardTab = 9,
    ResizeSignature = 10,
    UpdatesAbout = 11,
    PrintCounter = 12
}

public class MainViewModel : ViewModelBase
{
    private int _selectedTabIndex = 0;
    public int SelectedTabIndex
    {
        get => _selectedTabIndex;
        set
        {
            if (SetProperty(ref _selectedTabIndex, value))
            {
                if (value == 7)
                {
                    HistoryVm.RefreshList();
                }
                else if (value == 11)
                {
                    _ = CheckForUpdatesAsync(silent: true);
                }

                OnPropertyChanged(nameof(IsHomeTab));
                OnPropertyChanged(nameof(IsMergeTab));
                OnPropertyChanged(nameof(IsSplitTab));
                OnPropertyChanged(nameof(IsImg2PdfTab));
                OnPropertyChanged(nameof(IsPdf2ImgTab));
                OnPropertyChanged(nameof(IsEnhanceTab));
                OnPropertyChanged(nameof(IsStampTab));
                OnPropertyChanged(nameof(IsHistoryTab));
                OnPropertyChanged(nameof(IsSettingsTab));
                OnPropertyChanged(nameof(IsGovtCardTab));
                OnPropertyChanged(nameof(IsResizeSignatureTab));
                OnPropertyChanged(nameof(IsUpdatesAboutTab));
                OnPropertyChanged(nameof(IsPrintCounterTab));
                OnPropertyChanged(nameof(CurrentHeaderTitle));
            }
        }
    }

    public bool IsHomeTab => SelectedTabIndex == 0;
    public bool IsMergeTab => SelectedTabIndex == 1;
    public bool IsSplitTab => SelectedTabIndex == 2;
    public bool IsImg2PdfTab => SelectedTabIndex == 3;
    public bool IsPdf2ImgTab => SelectedTabIndex == 4;
    public bool IsEnhanceTab => SelectedTabIndex == 5;
    public bool IsStampTab => SelectedTabIndex == 6;
    public bool IsHistoryTab => SelectedTabIndex == 7;
    public bool IsSettingsTab => SelectedTabIndex == 8;
    public bool IsGovtCardTab => SelectedTabIndex == 9;
    public bool IsResizeSignatureTab => SelectedTabIndex == 10;
    public bool IsUpdatesAboutTab => SelectedTabIndex == 11;
    public bool IsPrintCounterTab => SelectedTabIndex == 12;

    public string CurrentHeaderTitle => SelectedTabIndex switch
    {
        1 => "📑 PDF Merger & Multi-File Combiner",
        2 => "✂️ PDF Page Splitter & Extractor",
        3 => "🖼️ Convert Images to A4 PDF",
        4 => "📄 Convert PDF to High-Res JPG Images",
        5 => "✨ Document Scanner Enhancer (Magic White)",
        6 => "🏷️ Candidate Photo Name & Date Stamp (SSC/Govt)",
        7 => "🕒 Today's Output History & Activity Hub",
        8 => "⚙️ Application Settings & Preferences",
        9 => "💳 e-Ration / Aadhaar / PAN A4 Multi-Card Print Sheet Generator",
        10 => "✍️ Resize Signature & Photo (Exact px/cm & KB for Govt Portals)",
        11 => "🚀 Application Updates & About Developer",
        12 => "🖨️ Cyber Cafe Print Counter & Rush-Hour Billing Studio",
        _ => "⚡ DASMO CYBER COMPRESSOR — All-In-One Cyber Cafe Suite"
    };

    private bool _isSidebarCollapsed;
    public bool IsSidebarCollapsed
    {
        get => _isSidebarCollapsed;
        set
        {
            if (SetProperty(ref _isSidebarCollapsed, value))
            {
                OnPropertyChanged(nameof(IsSidebarExpanded));
                OnPropertyChanged(nameof(SidebarWidth));
                OnPropertyChanged(nameof(SidebarGridWidth));
                OnPropertyChanged(nameof(SidebarToggleIcon));
                OnPropertyChanged(nameof(SidebarToggleTooltip));
            }
        }
    }

    public bool IsSidebarExpanded => !IsSidebarCollapsed;
    public double SidebarWidth => IsSidebarCollapsed ? 64 : 230;
    public System.Windows.GridLength SidebarGridWidth => IsSidebarCollapsed ? new System.Windows.GridLength(64) : new System.Windows.GridLength(230);
    public string SidebarToggleIcon => IsSidebarCollapsed ? "▶" : "◀";
    public string SidebarToggleTooltip => IsSidebarCollapsed ? "Expand Sidebar Menu (Ctrl+B)" : "Collapse Sidebar to Focus (Ctrl+B)";

    // Sub-ViewModels
    public MergePdfViewModel MergePdfVm { get; }
    public SplitPdfViewModel SplitPdfVm { get; }
    public ImageToPdfViewModel Img2PdfVm { get; }
    public PdfToImageViewModel Pdf2ImgVm { get; }
    public ScanEnhancerViewModel EnhancerVm { get; }
    public PhotoStampViewModel PhotoStampVm { get; }
    public TodayHistoryViewModel HistoryVm { get; }
    public SettingsViewModel SettingsVm { get; }
    public GovtCardExtractorViewModel GovtCardVm { get; }
    public SignatureResizeViewModel SignatureResizeVm { get; }
    public PrintTrackerViewModel PrintTrackerVm { get; }

    // Navigation Commands
    public ICommand NavigateCommand { get; }
    public ICommand OpenStackerCommand { get; }
    public ICommand OpenPassportStudioCommand { get; }
    public ICommand OpenPdfEditorCommand { get; }
    public ICommand OpenPrintTrackerStudioCommand { get; }
    public ICommand OpenCashDrawerCommand { get; }
    public ICommand OpenCompressDialogCommand { get; }
    public ICommand SelectToolFileCommand { get; }
    public ICommand ToggleSidebarCommand { get; }

    // Updates & About Developer Properties
    private CancellationTokenSource? _updateCts;
    private string _cachedDownloadUrl = string.Empty;

    public string CurrentVersionDisplay => $"v{AppUpdateService.CurrentVersion}";
    public string HardwareIdDisplay => HardwareIdService.GetHardwareId();
    public string DeveloperName => "Subhojit Paul";
    public string DeveloperBio => "Founder & Lead Software Architect of DASMO CYBER CAFE TOOLS. Engineering high-performance, rock-solid desktop automation utilities tailored for Indian cyber cafes, digital printing centers, and CSC centres.";
    public string DeveloperPhone => "+91 8927408840";
    public string DeveloperEmail => "subhojitpaul26042004@gmail.com";

    private bool _isCheckingForUpdate;
    public bool IsCheckingForUpdate
    {
        get => _isCheckingForUpdate;
        set => SetProperty(ref _isCheckingForUpdate, value);
    }

    private string _updateStatusText = "App is running the latest build.";
    public string UpdateStatusText
    {
        get => _updateStatusText;
        set => SetProperty(ref _updateStatusText, value);
    }

    private bool _hasUpdateAvailable;
    public bool HasUpdateAvailable
    {
        get => _hasUpdateAvailable;
        set => SetProperty(ref _hasUpdateAvailable, value);
    }

    private string _latestVersionText = $"v{AppUpdateService.CurrentVersion}";
    public string LatestVersionText
    {
        get => _latestVersionText;
        set => SetProperty(ref _latestVersionText, value);
    }

    private string _updateChangelogText = "All features and security patches are up to date.";
    public string UpdateChangelogText
    {
        get => _updateChangelogText;
        set => SetProperty(ref _updateChangelogText, value);
    }

    private static string? _lastNotifiedUpdateVersion;
    private readonly System.Windows.Threading.DispatcherTimer _updateCheckTimer;

    private string _autoDetectMode = "prompt";
    public string AutoDetectMode
    {
        get => _autoDetectMode;
        set
        {
            if (SetProperty(ref _autoDetectMode, value))
            {
                OnPropertyChanged(nameof(AutoDetectStatusText));
                OnPropertyChanged(nameof(AutoDetectStatusIcon));
                OnPropertyChanged(nameof(AutoDetectButtonBackground));
                OnPropertyChanged(nameof(AutoDetectTooltip));
                OnPropertyChanged(nameof(IsPromptMode));
                OnPropertyChanged(nameof(IsSilentMode));
                OnPropertyChanged(nameof(IsOffMode));
            }
        }
    }

    public bool IsPromptMode => AutoDetectMode == "prompt";
    public bool IsSilentMode => AutoDetectMode == "silent";
    public bool IsOffMode => AutoDetectMode == "off";

    private bool _isSilentSizePopupOpen;
    public bool IsSilentSizePopupOpen
    {
        get => _isSilentSizePopupOpen;
        set => SetProperty(ref _isSilentSizePopupOpen, value);
    }

    public int SilentTargetSizeKB
    {
        get => SettingsManager.Instance.Current.AutoCompress.TargetSizeKB;
        set
        {
            SettingsManager.Instance.Update(s => s.AutoCompress.TargetSizeKB = value);
            (System.Windows.Application.Current as App)?.RestartFileWatcher();
            if (SettingsVm != null)
            {
                SettingsVm.TargetSize = value;
            }
            OnPropertyChanged(nameof(SilentTargetSizeKB));
            OnPropertyChanged(nameof(AutoDetectStatusText));
            OnPropertyChanged(nameof(AutoDetectTooltip));
            OnPropertyChanged(nameof(Is50KbSelected));
            OnPropertyChanged(nameof(Is100KbSelected));
            OnPropertyChanged(nameof(Is150KbSelected));
            OnPropertyChanged(nameof(Is200KbSelected));
            OnPropertyChanged(nameof(Is300KbSelected));
            OnPropertyChanged(nameof(Is500KbSelected));
        }
    }

    public bool Is50KbSelected => SilentTargetSizeKB == 50;
    public bool Is100KbSelected => SilentTargetSizeKB == 100;
    public bool Is150KbSelected => SilentTargetSizeKB == 150;
    public bool Is200KbSelected => SilentTargetSizeKB == 200;
    public bool Is300KbSelected => SilentTargetSizeKB == 300;
    public bool Is500KbSelected => SilentTargetSizeKB == 500;

    private string _customSilentSizeText = string.Empty;
    public string CustomSilentSizeText
    {
        get => _customSilentSizeText;
        set => SetProperty(ref _customSilentSizeText, value);
    }

    public string AutoDetectStatusText => AutoDetectMode switch
    {
        "silent" => $"Auto: SILENT ({SettingsManager.Instance.Current.AutoCompress.TargetSizeKB} KB)",
        "prompt" => "Auto: PROMPT",
        _ => "Auto: OFF"
    };

    public string AutoDetectStatusIcon => AutoDetectMode switch
    {
        "silent" => "🤖",
        "prompt" => "⚡",
        _ => "⏸️"
    };

    public string AutoDetectTooltip => AutoDetectMode switch
    {
        "silent" => $"Auto-Detection: SILENT mode (Auto-compresses new downloads directly to {SettingsManager.Instance.Current.AutoCompress.TargetSizeKB} KB in background). Click to switch mode or change size.",
        "prompt" => "Auto-Detection: PROMPT mode (Shows interactive popup on new downloads). Click to switch mode.",
        _ => "Auto-Detection: OFF (Disabled). Click to switch mode."
    };

    public System.Windows.Media.Brush AutoDetectButtonBackground => AutoDetectMode switch
    {
        "silent" => new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#0284C7")),
        "prompt" => new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#10B981")),
        _ => new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#64748B"))
    };

    public ICommand ToggleAutoDetectCommand => new RelayCommand(_ =>
    {
        // Cycle: prompt -> silent -> off -> prompt
        string nextMode = AutoDetectMode switch
        {
            "prompt" => "silent",
            "silent" => "off",
            _ => "prompt"
        };

        SetAutoDetectModeInternal(nextMode);

        if (nextMode == "silent")
        {
            // When user activates silent mode, immediately open size selector popup so they can choose their target file size!
            CustomSilentSizeText = SilentTargetSizeKB.ToString();
            IsSilentSizePopupOpen = true;
        }
        else
        {
            IsSilentSizePopupOpen = false;
        }

        Log.Information("User toggled Auto-Detect mode to {Mode}", nextMode);
    });

    public ICommand OpenSilentSizePopupCommand => new RelayCommand(_ =>
    {
        CustomSilentSizeText = SilentTargetSizeKB.ToString();
        IsSilentSizePopupOpen = !IsSilentSizePopupOpen;
    });

    public ICommand CloseSilentSizePopupCommand => new RelayCommand(_ =>
    {
        IsSilentSizePopupOpen = false;
    });

    public ICommand SetSilentTargetSizeCommand => new RelayCommand(param =>
    {
        if (param != null && int.TryParse(param.ToString(), out int size) && size > 0)
        {
            SilentTargetSizeKB = size;
            CustomSilentSizeText = size.ToString();
            IsSilentSizePopupOpen = false;
            Log.Information("User selected silent auto-compress target size {Size} KB", size);
        }
    });

    public ICommand ApplyCustomSilentSizeCommand => new RelayCommand(_ =>
    {
        if (int.TryParse(CustomSilentSizeText, out int size) && size >= 10 && size <= 50000)
        {
            SilentTargetSizeKB = size;
            IsSilentSizePopupOpen = false;
            Log.Information("User applied custom silent auto-compress target size {Size} KB", size);
        }
        else
        {
            System.Windows.MessageBox.Show("Please enter a valid target file size between 10 KB and 50,000 KB.", "Invalid Size", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }
    });

    public ICommand SetModeCommand => new RelayCommand(param =>
    {
        string mode = param?.ToString() ?? "prompt";
        SetAutoDetectModeInternal(mode);
        if (mode == "silent")
        {
            CustomSilentSizeText = SilentTargetSizeKB.ToString();
            IsSilentSizePopupOpen = true;
        }
        else
        {
            IsSilentSizePopupOpen = false;
        }
    });

    public void SetAutoDetectModeInternal(string mode)
    {
        SettingsManager.Instance.Update(s =>
        {
            if (mode == "off")
            {
                s.AutoCompress.Enabled = false;
                s.AutoCompress.ActionOnNewFile = "off";
            }
            else
            {
                s.AutoCompress.Enabled = true;
                s.AutoCompress.ActionOnNewFile = mode;
            }
        });

        (System.Windows.Application.Current as App)?.RestartFileWatcher();
        RefreshAutoDetectStatus();
        if (SettingsVm != null)
        {
            SettingsVm.ActionOnNewFile = mode;
            SettingsVm.AutoCompressEnabled = (mode != "off");
        }
    }

    public void RefreshAutoDetectStatus()
    {
        var settings = SettingsManager.Instance.Current;
        if (!settings.AutoCompress.Enabled || settings.AutoCompress.ActionOnNewFile == "off" || settings.AutoCompress.ActionOnNewFile == "disabled")
        {
            AutoDetectMode = "off";
        }
        else
        {
            AutoDetectMode = settings.AutoCompress.ActionOnNewFile == "silent" ? "silent" : "prompt";
        }
        CustomSilentSizeText = settings.AutoCompress.TargetSizeKB.ToString();
        OnPropertyChanged(nameof(IsPromptMode));
        OnPropertyChanged(nameof(IsSilentMode));
        OnPropertyChanged(nameof(IsOffMode));
        OnPropertyChanged(nameof(SilentTargetSizeKB));
        OnPropertyChanged(nameof(Is50KbSelected));
        OnPropertyChanged(nameof(Is100KbSelected));
        OnPropertyChanged(nameof(Is150KbSelected));
        OnPropertyChanged(nameof(Is200KbSelected));
        OnPropertyChanged(nameof(Is300KbSelected));
        OnPropertyChanged(nameof(Is500KbSelected));
    }

    public ICommand OpenUpdateDialogCommand => new RelayCommand(_ =>
    {
        SwitchToTab(DashboardTab.UpdatesAbout);
        var ask = System.Windows.MessageBox.Show(
            $"🚀 A new update ({LatestVersionText}) is available for DASMO CYBER CAFE TOOLS!\n\nRelease Notes:\n{(string.IsNullOrWhiteSpace(UpdateChangelogText) ? "Bug fixes and performance improvements." : UpdateChangelogText)}\n\nWould you like to download and install this update now?",
            "Update Available",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Information);
        if (ask == System.Windows.MessageBoxResult.Yes)
        {
            _ = DownloadAndInstallUpdateAsync();
        }
    });

    private string _customAdminNoticeText = string.Empty;
    public string CustomAdminNoticeText
    {
        get => _customAdminNoticeText;
        set
        {
            if (SetProperty(ref _customAdminNoticeText, value))
            {
                OnPropertyChanged(nameof(HasCustomAdminNotice));
            }
        }
    }

    public bool HasCustomAdminNotice => !string.IsNullOrWhiteSpace(CustomAdminNoticeText);

    private bool _isDownloadingUpdate;
    public bool IsDownloadingUpdate
    {
        get => _isDownloadingUpdate;
        set => SetProperty(ref _isDownloadingUpdate, value);
    }

    private double _downloadProgressValue;
    public double DownloadProgressValue
    {
        get => _downloadProgressValue;
        set => SetProperty(ref _downloadProgressValue, value);
    }

    private string _downloadProgressText = "0%";
    public string DownloadProgressText
    {
        get => _downloadProgressText;
        set => SetProperty(ref _downloadProgressText, value);
    }

    private string _downloadStatusText = string.Empty;
    public string DownloadStatusText
    {
        get => _downloadStatusText;
        set => SetProperty(ref _downloadStatusText, value);
    }

    // Update Commands
    public ICommand CheckForUpdatesCommand { get; }
    public ICommand DownloadUpdateCommand { get; }
    public ICommand CancelUpdateDownloadCommand { get; }
    public ICommand CopyHardwareIdCommand { get; }
    public ICommand OpenWhatsAppCommand { get; }
    public ICommand OpenEmailCommand { get; }
    public ICommand OpenGitHubReleasesCommand { get; }

    public Action? RequestOpenStacker { get; set; }
    public Action? RequestOpenCompressDialog { get; set; }
    public Action? RequestClose { get; set; }

    public MainViewModel()
    {
        MergePdfVm = new MergePdfViewModel();
        SplitPdfVm = new SplitPdfViewModel(string.Empty);
        Img2PdfVm = new ImageToPdfViewModel();
        Pdf2ImgVm = new PdfToImageViewModel(string.Empty);
        EnhancerVm = new ScanEnhancerViewModel(string.Empty);
        PhotoStampVm = new PhotoStampViewModel(string.Empty);
        HistoryVm = new TodayHistoryViewModel();
        SettingsVm = new SettingsViewModel();
        GovtCardVm = new GovtCardExtractorViewModel();
        SignatureResizeVm = new SignatureResizeViewModel(string.Empty);
        PrintTrackerVm = new PrintTrackerViewModel();

        ToggleSidebarCommand = new RelayCommand(_ => IsSidebarCollapsed = !IsSidebarCollapsed);

        RefreshAutoDetectStatus();
        SettingsVm.RequestClose = () => SwitchToTab(0);
        SettingsVm.RequestSave = (s, e) =>
        {
            RefreshAutoDetectStatus();
            SwitchToTab(0);
        };

        _updateCheckTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMinutes(30)
        };
        _updateCheckTimer.Tick += async (s, e) =>
        {
            try
            {
                await CheckForUpdatesAsync(silent: true);
            }
            catch { }
        };
        _updateCheckTimer.Start();

        NavigateCommand = new RelayCommand(param =>
        {
            if (param is int index)
            {
                SelectedTabIndex = index;
            }
            else if (param is string s && int.TryParse(s, out int idx))
            {
                SelectedTabIndex = idx;
            }
        });

        OpenStackerCommand = new RelayCommand(_ => RequestOpenStacker?.Invoke());
        OpenPassportStudioCommand = new RelayCommand(_ => PassportStudioDialog.ShowStudio());
        OpenPdfEditorCommand = new RelayCommand(p => ShowPdfEditor(p?.ToString()));
        OpenPrintTrackerStudioCommand = new RelayCommand(_ => PrintTrackerStudioWindow.ShowStudio());
        OpenCashDrawerCommand = new RelayCommand(_ => CashDrawerWindow.ShowCashDrawer());
        OpenCompressDialogCommand = new RelayCommand(_ => RequestOpenCompressDialog?.Invoke());
        SelectToolFileCommand = new RelayCommand(param => BrowseFileForCurrentTool(param?.ToString()));

        CheckForUpdatesCommand = new RelayCommand(async _ => await CheckForUpdatesAsync(silent: false));
        DownloadUpdateCommand = new RelayCommand(async _ => await DownloadAndInstallUpdateAsync());
        CancelUpdateDownloadCommand = new RelayCommand(_ => CancelUpdateDownload());
        CopyHardwareIdCommand = new RelayCommand(_ =>
        {
            try
            {
                System.Windows.Clipboard.SetText(HardwareIdDisplay);
                System.Windows.MessageBox.Show(
                    $"Hardware ID copied to clipboard:\n\n{HardwareIdDisplay}\n\nSend this to Subhojit Paul on WhatsApp (+91 8927408840) to activate your PC.",
                    "Hardware ID Copied", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to copy Hardware ID");
            }
        });
        OpenWhatsAppCommand = new RelayCommand(_ => OpenUrl("https://wa.me/918927408840"));
        OpenEmailCommand = new RelayCommand(_ => OpenUrl("mailto:subhojitpaul26042004@gmail.com"));
        OpenGitHubReleasesCommand = new RelayCommand(_ => OpenUrl("https://github.com/SUBHOJITPAUL797/DASMO-CYBER-CAFE-TOOLS/releases"));

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(2500);
                await CheckForUpdatesAsync(silent: true);
            }
            catch { }
        });
    }

    public static void ShowPdfEditor(string? path = null)
    {
        if (App.CurrentApp != null)
        {
            App.CurrentApp.ShowPdfEditor(path);
            return;
        }

        try
        {
            var existing = System.Windows.Application.Current?.Windows.OfType<Views.PdfEditorWindow>().FirstOrDefault();
            if (existing != null)
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    _ = existing.Vm.LoadDocumentAsync(path);
                }
                if (existing.WindowState == System.Windows.WindowState.Minimized)
                {
                    existing.WindowState = System.Windows.WindowState.Normal;
                }
                existing.Activate();
                existing.Focus();
                return;
            }

            var win = new Views.PdfEditorWindow(path ?? string.Empty);
            // DO NOT set win.Owner = Current?.MainWindow!
            // 1. If Current.MainWindow is null, WPF sets Current.MainWindow = win, causing "Cannot set Owner property to itself" crash.
            // 2. PdfEditorWindow is an independent Studio workspace window (like DocumentStackerWindow) and should not be hidden/minimized with MainWindow.
            win.Show();
            win.Activate();
            win.Focus();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to launch PDF Editor Window");
            System.Windows.MessageBox.Show($"Could not open PDF Editor Studio:\n{ex.Message}", "PDF Editor Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
    }

    public void SwitchToTab(DashboardTab tab)
    {
        SelectedTabIndex = (int)tab;
    }

    public void LoadFileIntoTool(string filePath, DashboardTab targetTab)
    {
        if (!File.Exists(filePath)) return;

        SelectedTabIndex = (int)targetTab;
        switch (targetTab)
        {
            case DashboardTab.MergePdf:
                MergePdfVm.AddFile(filePath);
                break;
            case DashboardTab.SplitPdf:
                SplitPdfVm.LoadFile(filePath);
                break;
            case DashboardTab.Img2Pdf:
                Img2PdfVm.AddFile(filePath);
                break;
            case DashboardTab.Pdf2Img:
                Pdf2ImgVm.LoadFile(filePath);
                break;
            case DashboardTab.ScannerEnhance:
                EnhancerVm.LoadFile(filePath);
                break;
            case DashboardTab.PhotoStamp:
                PhotoStampVm.LoadFile(filePath);
                break;
            case DashboardTab.GovtCardTab:
                _ = GovtCardVm.AddSourceFilesAsync(new[] { filePath });
                break;
            case DashboardTab.ResizeSignature:
                SignatureResizeVm.LoadFile(filePath);
                break;
            case DashboardTab.PrintCounter:
                PrintTrackerStudioWindow.ShowStudio();
                break;
        }
    }

    private void BrowseFileForCurrentTool(string? toolName)
    {
        try
        {
            var dlg = new Microsoft.Win32.OpenFileDialog();
            switch (toolName?.ToLowerInvariant() ?? string.Empty)
            {
                case "split":
                    dlg.Title = "Select PDF File to Split / Extract";
                    dlg.Filter = "PDF Files (*.pdf)|*.pdf";
                    if (dlg.ShowDialog() == true) SplitPdfVm.LoadFile(dlg.FileName);
                    break;

                case "pdf2img":
                    dlg.Title = "Select PDF File to Convert to JPG";
                    dlg.Filter = "PDF Files (*.pdf)|*.pdf";
                    if (dlg.ShowDialog() == true) Pdf2ImgVm.LoadFile(dlg.FileName);
                    break;

                case "enhance":
                    dlg.Title = "Select Phone Scanned Document Image";
                    dlg.Filter = "All Supported Images (*.jpg;*.jpeg;*.jpe;*.jfif;*.png;*.webp;*.bmp;*.gif;*.tiff;*.tif)|*.jpg;*.jpeg;*.jpe;*.jfif;*.png;*.webp;*.bmp;*.gif;*.tiff;*.tif|JPEG Images (*.jpg;*.jpeg;*.jfif;*.jpe)|*.jpg;*.jpeg;*.jfif;*.jpe|PNG Images (*.png)|*.png|WebP Images (*.webp)|*.webp|BMP Images (*.bmp)|*.bmp|TIFF Images (*.tiff;*.tif)|*.tiff;*.tif|All Files (*.*)|*.*";
                    if (dlg.ShowDialog() == true) EnhancerVm.LoadFile(dlg.FileName);
                    break;

                case "stamp":
                    dlg.Title = "Select Candidate Photo";
                    dlg.Filter = "All Supported Images (*.jpg;*.jpeg;*.jpe;*.jfif;*.png;*.webp;*.bmp;*.gif;*.tiff;*.tif)|*.jpg;*.jpeg;*.jpe;*.jfif;*.png;*.webp;*.bmp;*.gif;*.tiff;*.tif|JPEG Images (*.jpg;*.jpeg;*.jfif;*.jpe)|*.jpg;*.jpeg;*.jfif;*.jpe|PNG Images (*.png)|*.png|WebP Images (*.webp)|*.webp|BMP Images (*.bmp)|*.bmp|TIFF Images (*.tiff;*.tif)|*.tiff;*.tif|All Files (*.*)|*.*";
                    if (dlg.ShowDialog() == true) PhotoStampVm.LoadFile(dlg.FileName);
                    break;

                case "passport":
                case "passportstudio":
                    PassportStudioDialog.ShowStudio();
                    break;

                case "signature":
                case "resizesignature":
                    dlg.Title = "Select Candidate Signature or Photo";
                    dlg.Filter = "All Supported Images (*.jpg;*.jpeg;*.jpe;*.jfif;*.png;*.webp;*.bmp;*.gif;*.tiff;*.tif)|*.jpg;*.jpeg;*.jpe;*.jfif;*.png;*.webp;*.bmp;*.gif;*.tiff;*.tif|JPEG Images (*.jpg;*.jpeg;*.jfif;*.jpe)|*.jpg;*.jpeg;*.jfif;*.jpe|PNG Images (*.png)|*.png|WebP Images (*.webp)|*.webp|BMP Images (*.bmp)|*.bmp|TIFF Images (*.tiff;*.tif)|*.tiff;*.tif|All Files (*.*)|*.*";
                    if (dlg.ShowDialog() == true) SignatureResizeVm.LoadFile(dlg.FileName);
                    break;

                case "img2pdf":
                    dlg.Title = "Select Images to Convert to PDF";
                    dlg.Filter = "All Supported Images (*.jpg;*.jpeg;*.jpe;*.jfif;*.png;*.webp;*.bmp;*.gif;*.tiff;*.tif)|*.jpg;*.jpeg;*.jpe;*.jfif;*.png;*.webp;*.bmp;*.gif;*.tiff;*.tif|JPEG Images (*.jpg;*.jpeg;*.jfif;*.jpe)|*.jpg;*.jpeg;*.jfif;*.jpe|PNG Images (*.png)|*.png|WebP Images (*.webp)|*.webp|BMP Images (*.bmp)|*.bmp|TIFF Images (*.tiff;*.tif)|*.tiff;*.tif|All Files (*.*)|*.*";
                    dlg.Multiselect = true;
                    if (dlg.ShowDialog() == true && dlg.FileNames != null)
                    {
                        foreach (var f in dlg.FileNames) Img2PdfVm.AddFile(f);
                    }
                    break;

                case "merge":
                    dlg.Title = "Select PDF Files to Merge";
                    dlg.Filter = "PDF Files (*.pdf)|*.pdf";
                    dlg.Multiselect = true;
                    if (dlg.ShowDialog() == true && dlg.FileNames != null)
                    {
                        MergePdfVm.AddFiles(dlg.FileNames);
                    }
                    break;

                case "govtcard":
                case "rationcard":
                    dlg.Title = "Select e-Ration / Aadhaar / PAN Card PDFs";
                    dlg.Filter = "PDF & Image Files (*.pdf;*.jpg;*.png)|*.pdf;*.jpg;*.jpeg;*.png";
                    dlg.Multiselect = true;
                    if (dlg.ShowDialog() == true && dlg.FileNames != null)
                    {
                        _ = GovtCardVm.AddSourceFilesAsync(dlg.FileNames);
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error browsing file for tool {Tool}", toolName);
        }
    }

    public async Task CheckForUpdatesAsync(bool silent = false)
    {
        if (IsCheckingForUpdate || IsDownloadingUpdate) return;

        IsCheckingForUpdate = true;
        UpdateStatusText = "Checking cloud licensing and GitHub releases...";

        try
        {
            var result = await AppUpdateService.Instance.CheckForUpdateAsync().ConfigureAwait(true);
            LatestVersionText = $"v{result.LatestVersion}";
            _cachedDownloadUrl = result.DownloadUrl;
            CustomAdminNoticeText = result.CustomAdminMessage;

            if (!string.IsNullOrWhiteSpace(result.ReleaseNotes))
            {
                UpdateChangelogText = result.ReleaseNotes;
            }

            if (result.HasUpdate)
            {
                HasUpdateAvailable = true;
                UpdateStatusText = $"New version v{result.LatestVersion} is available for installation!";

                // Always send Windows Toast notification if not previously notified for this version in this session
                if (_lastNotifiedUpdateVersion != result.LatestVersion)
                {
                    _lastNotifiedUpdateVersion = result.LatestVersion;
                    NotificationService.NotifyUpdateAvailable(result.LatestVersion, result.ReleaseNotes);
                }

                if (result.IsMandatory)
                {
                    System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                    {
                        var win = new Views.MandatoryUpdateWindow(result);
                        win.ShowDialog();
                    });
                }
                else if (!silent)
                {
                    var ask = System.Windows.MessageBox.Show(
                        $"🚀 A new update (v{result.LatestVersion}) is available for DASMO CYBER CAFE TOOLS!\n\nRelease Highlights:\n{(string.IsNullOrWhiteSpace(result.ReleaseNotes) ? "Bug fixes and improvements" : result.ReleaseNotes)}\n\nWould you like to download and install this update now?",
                        "Update Available", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Information);
                    if (ask == System.Windows.MessageBoxResult.Yes)
                    {
                        _ = DownloadAndInstallUpdateAsync();
                    }
                }
            }
            else
            {
                HasUpdateAvailable = false;
                UpdateStatusText = $"You are running the latest version (v{AppUpdateService.CurrentVersion}).";
                if (!silent)
                {
                    System.Windows.MessageBox.Show(
                        $"You are using the latest version of DASMO CYBER CAFE TOOLS (v{AppUpdateService.CurrentVersion}).\nNo updates are needed at this time.",
                        "Up to Date", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to check for updates");
            UpdateStatusText = "Failed to connect to update server.";
            if (!silent)
            {
                System.Windows.MessageBox.Show(
                    $"Could not check for updates: {ex.Message}",
                    "Update Check Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            }
        }
        finally
        {
            IsCheckingForUpdate = false;
        }
    }

    public async Task DownloadAndInstallUpdateAsync()
    {
        if (IsDownloadingUpdate) return;
        if (string.IsNullOrWhiteSpace(_cachedDownloadUrl))
        {
            await CheckForUpdatesAsync(silent: true);
            if (string.IsNullOrWhiteSpace(_cachedDownloadUrl))
            {
                System.Windows.MessageBox.Show("No download URL found for this update. Please visit the official GitHub repository or contact developer support.", "Update Notice", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                return;
            }
        }

        IsDownloadingUpdate = true;
        DownloadProgressValue = 0;
        DownloadProgressText = "Connecting...";
        DownloadStatusText = "Initiating secure download...";
        _updateCts = new CancellationTokenSource();

        try
        {
            string? downloadedPath = await AppUpdateService.Instance.DownloadUpdateAsync(
                _cachedDownloadUrl,
                progress =>
                {
                    System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                    {
                        DownloadProgressValue = progress.Percentage;
                        DownloadProgressText = $"{progress.FormattedDownloaded} / {progress.FormattedTotal} ({progress.Percentage}%)";
                        DownloadStatusText = $"{progress.FormattedSpeed} • {progress.StatusMessage}";
                    });
                },
                _updateCts.Token).ConfigureAwait(true);

            if (!string.IsNullOrEmpty(downloadedPath) && File.Exists(downloadedPath))
            {
                DownloadStatusText = "Download complete. Ready to install.";
                var confirm = System.Windows.MessageBox.Show(
                    "Update download complete!\n\nDASMO CYBER CAFE TOOLS will now close and launch the installer.\nDo you want to proceed?",
                    "Install Update", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question);

                if (confirm == System.Windows.MessageBoxResult.Yes)
                {
                    AppUpdateService.Instance.LaunchInstallerAndExit(downloadedPath);
                }
                else
                {
                    DownloadStatusText = "Installer saved at: " + downloadedPath;
                }
            }
        }
        catch (OperationCanceledException)
        {
            DownloadStatusText = "Download was canceled.";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Download update failed");
            DownloadStatusText = "Download failed: " + ex.Message;
            System.Windows.MessageBox.Show($"Failed to download update: {ex.Message}", "Update Download Failed", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
        finally
        {
            IsDownloadingUpdate = false;
            _updateCts?.Dispose();
            _updateCts = null;
        }
    }

    public void CancelUpdateDownload()
    {
        if (_updateCts != null && !_updateCts.IsCancellationRequested)
        {
            _updateCts.Cancel();
        }
    }

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to open URL: {Url}", url);
        }
    }
}
