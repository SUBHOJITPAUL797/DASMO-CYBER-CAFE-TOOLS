using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Serilog;
using SmartSaver.Models;
using SmartSaver.Services;
using SmartSaver.Views;
using WpfClipboard = System.Windows.Clipboard;
using WpfMessageBox = System.Windows.MessageBox;

namespace SmartSaver.ViewModels;

public class DocumentStackerViewModel : ViewModelBase
{
    private readonly DocumentStackerService _stackerService;
    private readonly IDocumentEnhancerService _enhancerService;

    private BitmapSource? _frontRawBitmap;
    private string _frontRawPath = string.Empty;
    private int _frontRawPageIndex = -1;
    private int _frontPreviewVersion;

    private BitmapSource? _backRawBitmap;
    private string _backRawPath = string.Empty;
    private int _backRawPageIndex = -1;
    private int _backPreviewVersion;

    private string _frontPath = string.Empty;
    public string FrontPath
    {
        get => _frontPath;
        set
        {
            if (SetProperty(ref _frontPath, value))
            {
                _isSaved = false;
                OnPropertyChanged(nameof(FrontFileName));
                OnPropertyChanged(nameof(HasFront));
                OnPropertyChanged(nameof(HasUnsavedWork));
                OnPropertyChanged(nameof(IsFrontPdf));
                (StackCommand as RelayCommand)?.OnCanExecuteChanged();
                (PrintNowCommand as RelayCommand)?.OnCanExecuteChanged();
                (AutoCropFrontCommand as RelayCommand)?.OnCanExecuteChanged();
                (OpenCropFrontCommand as RelayCommand)?.OnCanExecuteChanged();
                (RotateFrontCommand as RelayCommand)?.OnCanExecuteChanged();
                LoadFrontPreview();
            }
        }
    }

    private string _backPath = string.Empty;
    public string BackPath
    {
        get => _backPath;
        set
        {
            if (SetProperty(ref _backPath, value))
            {
                _isSaved = false;
                OnPropertyChanged(nameof(BackFileName));
                OnPropertyChanged(nameof(HasBack));
                OnPropertyChanged(nameof(HasUnsavedWork));
                OnPropertyChanged(nameof(IsBackPdf));
                (StackCommand as RelayCommand)?.OnCanExecuteChanged();
                (PrintNowCommand as RelayCommand)?.OnCanExecuteChanged();
                (AutoCropBackCommand as RelayCommand)?.OnCanExecuteChanged();
                (OpenCropBackCommand as RelayCommand)?.OnCanExecuteChanged();
                (RotateBackCommand as RelayCommand)?.OnCanExecuteChanged();
                LoadBackPreview();
            }
        }
    }

    private string _frontOriginalPath = string.Empty;
    public string FrontOriginalPath
    {
        get => _frontOriginalPath;
        set => SetProperty(ref _frontOriginalPath, value);
    }

    private string _backOriginalPath = string.Empty;
    public string BackOriginalPath
    {
        get => _backOriginalPath;
        set => SetProperty(ref _backOriginalPath, value);
    }

    private bool _autoCropOnImport = true;
    public bool AutoCropOnImport
    {
        get => _autoCropOnImport;
        set => SetProperty(ref _autoCropOnImport, value);
    }

    public string FrontFileName => string.IsNullOrEmpty(FrontPath) ? "No front image/doc" : Path.GetFileName(FrontPath);
    public string BackFileName => string.IsNullOrEmpty(BackPath) ? "No back image/doc" : Path.GetFileName(BackPath);

    public bool HasFront => !string.IsNullOrEmpty(FrontPath) && File.Exists(FrontPath);
    public bool HasBack => !string.IsNullOrEmpty(BackPath) && File.Exists(BackPath);

    private bool _isSaved;
    public bool HasUnsavedWork => (HasFront || HasBack) && !_isSaved;

    public bool IsFrontPdf => Path.GetExtension(FrontPath).Equals(".pdf", StringComparison.OrdinalIgnoreCase);
    public bool IsBackPdf => Path.GetExtension(BackPath).Equals(".pdf", StringComparison.OrdinalIgnoreCase);

    private int _frontPageIndex = 0;
    public int FrontPageIndex
    {
        get => _frontPageIndex;
        set
        {
            if (SetProperty(ref _frontPageIndex, value))
            {
                LoadFrontPreview();
            }
        }
    }

    private int _backPageIndex = 1;
    public int BackPageIndex
    {
        get => _backPageIndex;
        set
        {
            if (SetProperty(ref _backPageIndex, value))
            {
                LoadBackPreview();
            }
        }
    }

    private bool _sameSizeBothCards = true;
    public bool SameSizeBothCards
    {
        get => _sameSizeBothCards;
        set
        {
            if (SetProperty(ref _sameSizeBothCards, value))
            {
                if (value && IsStandardIdCard)
                {
                    _backScale = _frontScale;
                    OnPropertyChanged(nameof(BackScale));
                    OnPropertyChanged(nameof(BackScaleFactor));
                }
            }
        }
    }

    private double _frontScale = 100.0;
    public double FrontScale
    {
        get => _frontScale;
        set
        {
            if (SetProperty(ref _frontScale, value))
            {
                OnPropertyChanged(nameof(FrontScaleFactor));
                OnPropertyChanged(nameof(FrontSlotWidth));
                OnPropertyChanged(nameof(FrontSlotHeight));
                if (SameSizeBothCards && IsStandardIdCard)
                {
                    _backScale = value;
                    OnPropertyChanged(nameof(BackScale));
                    OnPropertyChanged(nameof(BackScaleFactor));
                    OnPropertyChanged(nameof(BackSlotWidth));
                    OnPropertyChanged(nameof(BackSlotHeight));
                }
            }
        }
    }
    public double FrontScaleFactor => FrontScale / 100.0;

    private double _backScale = 100.0;
    public double BackScale
    {
        get => _backScale;
        set
        {
            if (SetProperty(ref _backScale, value))
            {
                OnPropertyChanged(nameof(BackScaleFactor));
                OnPropertyChanged(nameof(BackSlotWidth));
                OnPropertyChanged(nameof(BackSlotHeight));
                if (SameSizeBothCards && IsStandardIdCard)
                {
                    _frontScale = value;
                    OnPropertyChanged(nameof(FrontScale));
                    OnPropertyChanged(nameof(FrontScaleFactor));
                    OnPropertyChanged(nameof(FrontSlotWidth));
                    OnPropertyChanged(nameof(FrontSlotHeight));
                }
            }
        }
    }
    public double BackScaleFactor => BackScale / 100.0;

    private StackerLayoutMode _layoutMode = StackerLayoutMode.StandardIdCard;
    public StackerLayoutMode LayoutMode
    {
        get => _layoutMode;
        set
        {
            if (SetProperty(ref _layoutMode, value))
            {
                if (value == StackerLayoutMode.StandardIdCard && SameSizeBothCards)
                {
                    _backScale = _frontScale;
                    OnPropertyChanged(nameof(BackScale));
                    OnPropertyChanged(nameof(BackScaleFactor));
                }
                OnPropertyChanged(nameof(IsStandardIdCard));
                OnPropertyChanged(nameof(IsHalfA4));
                OnPropertyChanged(nameof(IsFullA4Sheet));
                OnPropertyChanged(nameof(IsFullA4Fit));
                OnPropertyChanged(nameof(IsBackSlotVisible));
                OnPropertyChanged(nameof(Slot1Label));
                OnPropertyChanged(nameof(FrontSlotWidth));
                OnPropertyChanged(nameof(FrontSlotHeight));
                OnPropertyChanged(nameof(BackSlotWidth));
                OnPropertyChanged(nameof(BackSlotHeight));
                OnPropertyChanged(nameof(FrontSlotBorderThickness));
                OnPropertyChanged(nameof(BackSlotBorderThickness));
                OnPropertyChanged(nameof(FrontSlotBorderBrush));
                OnPropertyChanged(nameof(BackSlotBorderBrush));
                OnPropertyChanged(nameof(PreviewModeBadgeText));
                (StackCommand as RelayCommand)?.OnCanExecuteChanged();
                (PrintNowCommand as RelayCommand)?.OnCanExecuteChanged();
            }
        }
    }

    public bool IsStandardIdCard
    {
        get => LayoutMode == StackerLayoutMode.StandardIdCard;
        set { if (value) LayoutMode = StackerLayoutMode.StandardIdCard; }
    }

    public bool IsHalfA4
    {
        get => LayoutMode == StackerLayoutMode.HalfA4;
        set { if (value) LayoutMode = StackerLayoutMode.HalfA4; }
    }

    public bool IsFullA4Sheet
    {
        get => LayoutMode == StackerLayoutMode.FullA4Sheet;
        set { if (value) LayoutMode = StackerLayoutMode.FullA4Sheet; }
    }

    public bool IsFullA4Fit
    {
        get => LayoutMode == StackerLayoutMode.FullA4Sheet;
        set { if (value) LayoutMode = StackerLayoutMode.FullA4Sheet; }
    }

    public bool IsBackSlotVisible => LayoutMode != StackerLayoutMode.FullA4Sheet;

    public string Slot1Label => LayoutMode == StackerLayoutMode.FullA4Sheet
        ? "📄 Document / Certificate (Full A4 Entire Page)"
        : "Front Side (Top)";

    public double FrontSlotWidth => IsStandardIdCard ? Math.Round(97.0 * FrontScaleFactor) : 216.0;
    public double FrontSlotHeight => IsStandardIdCard ? Math.Round(63.0 * FrontScaleFactor) : 145.0;
    public double BackSlotWidth => IsStandardIdCard ? Math.Round(97.0 * BackScaleFactor) : 216.0;
    public double BackSlotHeight => IsStandardIdCard ? Math.Round(63.0 * BackScaleFactor) : 145.0;

    public Thickness FrontSlotBorderThickness => IsStandardIdCard ? new Thickness(1) : new Thickness(0);
    public Thickness BackSlotBorderThickness => IsStandardIdCard ? new Thickness(1) : new Thickness(0);
    public System.Windows.Media.Brush FrontSlotBorderBrush => IsStandardIdCard ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 188, 212)) : System.Windows.Media.Brushes.Transparent;
    public System.Windows.Media.Brush BackSlotBorderBrush => IsStandardIdCard ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 188, 212)) : System.Windows.Media.Brushes.Transparent;

    public string PreviewModeBadgeText => LayoutMode switch
    {
        StackerLayoutMode.StandardIdCard => "🪪 Mode: Exact 8.5 × 5.5 cm Pocket ID (Wallet / Lamination Size)",
        StackerLayoutMode.HalfA4 => "📜 Mode: Large Half-A4 (Full Top/Bottom Xerox for Bank & Job Forms)",
        StackerLayoutMode.FullA4Sheet => "📄 Mode: Full A4 Page Document",
        _ => string.Empty
    };

    public ICommand SwitchToLargeHalfA4Command => new RelayCommand(_ => LayoutMode = StackerLayoutMode.HalfA4);
    public ICommand SwitchToPocketIdCommand => new RelayCommand(_ => LayoutMode = StackerLayoutMode.StandardIdCard);

    private bool _autoWhiten = true;
    public bool AutoWhiten
    {
        get => _autoWhiten;
        set
        {
            if (SetProperty(ref _autoWhiten, value))
            {
                OnPropertyChanged(nameof(EnableEnhancement));
                OnPropertyChanged(nameof(IsEnhancementActive));
                LoadFrontPreview();
                LoadBackPreview();
            }
        }
    }

    public bool EnableEnhancement
    {
        get => AutoWhiten;
        set => AutoWhiten = value;
    }

    private bool _pitchBlackText = false;
    public bool PitchBlackText
    {
        get => _pitchBlackText;
        set
        {
            if (SetProperty(ref _pitchBlackText, value))
            {
                OnPropertyChanged(nameof(IsEnhancementActive));
                LoadFrontPreview();
                LoadBackPreview();
            }
        }
    }

    public bool IsEnhancementActive => AutoWhiten || PitchBlackText;

    private EnhancementPreset _selectedPreset = EnhancementPreset.Balanced;
    public EnhancementPreset SelectedPreset
    {
        get => _selectedPreset;
        set
        {
            if (SetProperty(ref _selectedPreset, value))
            {
                OnPropertyChanged(nameof(IsPresetLight));
                OnPropertyChanged(nameof(IsPresetBalanced));
                OnPropertyChanged(nameof(IsPresetStrong));
                LoadFrontPreview();
                LoadBackPreview();
            }
        }
    }

    public bool IsPresetLight
    {
        get => SelectedPreset == EnhancementPreset.Light;
        set
        {
            if (value)
            {
                if (!AutoWhiten && !PitchBlackText) AutoWhiten = true;
                SelectedPreset = EnhancementPreset.Light;
            }
        }
    }

    public bool IsPresetBalanced
    {
        get => SelectedPreset == EnhancementPreset.Balanced;
        set
        {
            if (value)
            {
                if (!AutoWhiten && !PitchBlackText) AutoWhiten = true;
                SelectedPreset = EnhancementPreset.Balanced;
            }
        }
    }

    public bool IsPresetStrong
    {
        get => SelectedPreset == EnhancementPreset.Strong;
        set
        {
            if (value)
            {
                if (!AutoWhiten && !PitchBlackText) AutoWhiten = true;
                SelectedPreset = EnhancementPreset.Strong;
            }
        }
    }

    public ICommand SetPresetCommand => new RelayCommand(p =>
    {
        EnhancementPreset? newPreset = null;
        if (p is EnhancementPreset ep)
        {
            newPreset = ep;
        }
        else if (p is string s && Enum.TryParse<EnhancementPreset>(s, true, out var parsed))
        {
            newPreset = parsed;
        }

        if (newPreset.HasValue)
        {
            if (!AutoWhiten && !PitchBlackText) AutoWhiten = true;
            SelectedPreset = newPreset.Value;
        }
    });

    private string _selectedOutputFormat = "PDF";
    public string SelectedOutputFormat
    {
        get => _selectedOutputFormat;
        set => SetProperty(ref _selectedOutputFormat, value);
    }

    private int _targetSize = 200;
    public int TargetSize
    {
        get => _targetSize;
        set => SetProperty(ref _targetSize, value);
    }

    private string _targetSizeUnit = "KB";
    public string TargetSizeUnit
    {
        get => _targetSizeUnit;
        set => SetProperty(ref _targetSizeUnit, value);
    }

    private bool _isProcessing;
    public bool IsProcessing
    {
        get => _isProcessing;
        set
        {
            if (SetProperty(ref _isProcessing, value))
            {
                (StackCommand as RelayCommand)?.OnCanExecuteChanged();
                (PrintNowCommand as RelayCommand)?.OnCanExecuteChanged();
            }
        }
    }

    private string _progressText = string.Empty;
    public string ProgressText
    {
        get => _progressText;
        set => SetProperty(ref _progressText, value);
    }

    private string _resultText = string.Empty;
    public string ResultText
    {
        get => _resultText;
        set => SetProperty(ref _resultText, value);
    }

    private ImageSource? _frontImageSource;
    public ImageSource? FrontImageSource
    {
        get => _frontImageSource;
        set => SetProperty(ref _frontImageSource, value);
    }

    private ImageSource? _backImageSource;
    public ImageSource? BackImageSource
    {
        get => _backImageSource;
        set => SetProperty(ref _backImageSource, value);
    }

    private Visibility _frontPreviewVisibility = Visibility.Collapsed;
    public Visibility FrontPreviewVisibility
    {
        get => _frontPreviewVisibility;
        set => SetProperty(ref _frontPreviewVisibility, value);
    }

    private Visibility _backPreviewVisibility = Visibility.Collapsed;
    public Visibility BackPreviewVisibility
    {
        get => _backPreviewVisibility;
        set => SetProperty(ref _backPreviewVisibility, value);
    }

    private Visibility _frontPlaceholderVisibility = Visibility.Visible;
    public Visibility FrontPlaceholderVisibility
    {
        get => _frontPlaceholderVisibility;
        set => SetProperty(ref _frontPlaceholderVisibility, value);
    }

    private Visibility _backPlaceholderVisibility = Visibility.Visible;
    public Visibility BackPlaceholderVisibility
    {
        get => _backPlaceholderVisibility;
        set => SetProperty(ref _backPlaceholderVisibility, value);
    }

    public ICommand BrowseFrontCommand { get; }
    public ICommand BrowseBackCommand { get; }
    public ICommand PasteFrontClipboardCommand { get; }
    public ICommand PasteBackClipboardCommand { get; }
    public ICommand PasteAutoClipboardCommand { get; }
    public ICommand AutoCropFrontCommand { get; }
    public ICommand AutoCropBackCommand { get; }
    public ICommand OpenCropFrontCommand { get; }
    public ICommand OpenCropBackCommand { get; }
    public ICommand WhatsAppFixerFrontCommand { get; }
    public ICommand WhatsAppFixerBackCommand { get; }
    public ICommand RotateFrontCommand { get; }
    public ICommand RotateBackCommand { get; }
    public ICommand RevertFrontCommand { get; }
    public ICommand RevertBackCommand { get; }
    public ICommand SelectBothFilesCommand { get; }
    public ICommand SwapFilesCommand { get; }
    public ICommand ImportPdfCommand { get; }
    public ICommand ClearFrontCommand { get; }
    public ICommand ClearBackCommand { get; }
    public ICommand SetFrontScaleCommand { get; }
    public ICommand SetBackScaleCommand { get; }
    public ICommand StackCommand { get; }
    public ICommand PrintNowCommand { get; }
    public ICommand CloseCommand { get; }

    public Func<string?>? RequestBrowseFront { get; set; }
    public Func<string?>? RequestBrowseBack { get; set; }
    public Func<bool, string?>? RequestBrowseFile { get; set; }
    public Func<string[]?>? RequestBrowseBoth { get; set; }
    public Func<string?>? RequestImportPdf { get; set; }
    public Func<string, string, string?>? RequestSaveFile { get; set; }
    public Action? RequestClose { get; set; }

    public DocumentStackerViewModel(DocumentStackerService? stackerService = null, IDocumentEnhancerService? enhancerService = null)
    {
        _enhancerService = enhancerService ?? new DocumentEnhancerService();
        if (stackerService == null)
        {
            var imgComp = new ImageCompressor();
            var pdfComp = new PdfCompressor();
            var offComp = new OfficeCompressor(imgComp);
            var engine = new CompressionEngine(imgComp, pdfComp, offComp);
            _stackerService = new DocumentStackerService(engine, _enhancerService);
        }
        else
        {
            _stackerService = stackerService;
        }

        BrowseFrontCommand = new RelayCommand(_ => BrowseFile(true));
        BrowseBackCommand = new RelayCommand(_ => BrowseFile(false));
        PasteFrontClipboardCommand = new RelayCommand(_ => PasteFromClipboard(isFront: true));
        PasteBackClipboardCommand = new RelayCommand(_ => PasteFromClipboard(isFront: false));
        PasteAutoClipboardCommand = new RelayCommand(_ => PasteAuto());
        AutoCropFrontCommand = new RelayCommand(async _ => await AutoCropSlotAsync(true), _ => HasFront && !IsProcessing);
        AutoCropBackCommand = new RelayCommand(async _ => await AutoCropSlotAsync(false), _ => HasBack && !IsProcessing);
        OpenCropFrontCommand = new RelayCommand(_ => OpenCropDialog(true), _ => HasFront && !IsProcessing);
        OpenCropBackCommand = new RelayCommand(_ => OpenCropDialog(false), _ => HasBack && !IsProcessing);
        WhatsAppFixerFrontCommand = new RelayCommand(_ => OpenCropDialog(true, startInQuadMode: true), _ => HasFront && !IsProcessing);
        WhatsAppFixerBackCommand = new RelayCommand(_ => OpenCropDialog(false, startInQuadMode: true), _ => HasBack && !IsProcessing);
        RotateFrontCommand = new RelayCommand(async _ => await RotateSlotAsync(true), _ => HasFront && !IsProcessing);
        RotateBackCommand = new RelayCommand(async _ => await RotateSlotAsync(false), _ => HasBack && !IsProcessing);
        RevertFrontCommand = new RelayCommand(_ => RevertSlot(true), _ => !string.IsNullOrEmpty(FrontOriginalPath) && FrontOriginalPath != FrontPath);
        RevertBackCommand = new RelayCommand(_ => RevertSlot(false), _ => !string.IsNullOrEmpty(BackOriginalPath) && BackOriginalPath != BackPath);
        SelectBothFilesCommand = new RelayCommand(_ => SelectBothFiles());
        SwapFilesCommand = new RelayCommand(_ => SwapFrontAndBack());
        ImportPdfCommand = new RelayCommand(_ => Import2PagePdf());
        ClearFrontCommand = new RelayCommand(_ => { FrontPath = string.Empty; FrontOriginalPath = string.Empty; FrontImageSource = null; _frontRawBitmap = null; _frontRawPath = string.Empty; _frontRawPageIndex = -1; });
        ClearBackCommand = new RelayCommand(_ => { BackPath = string.Empty; BackOriginalPath = string.Empty; BackImageSource = null; _backRawBitmap = null; _backRawPath = string.Empty; _backRawPageIndex = -1; });
        SetFrontScaleCommand = new RelayCommand(p => { if (double.TryParse(p?.ToString(), out double s)) FrontScale = s; });
        SetBackScaleCommand = new RelayCommand(p => { if (double.TryParse(p?.ToString(), out double s)) BackScale = s; });

        StackCommand = new RelayCommand(async _ => await StackAsync(), _ => (!string.IsNullOrEmpty(FrontPath) || !string.IsNullOrEmpty(BackPath)) && !IsProcessing);
        PrintNowCommand = new RelayCommand(async _ => await PrintNowAsync(), _ => (!string.IsNullOrEmpty(FrontPath) || !string.IsNullOrEmpty(BackPath)) && !IsProcessing);
        CloseCommand = new RelayCommand(_ => RequestClose?.Invoke());
    }

    private void PasteAuto()
    {
        if (string.IsNullOrEmpty(FrontPath))
        {
            PasteFromClipboard(isFront: true);
        }
        else
        {
            PasteFromClipboard(isFront: false);
        }
    }

    private void PasteFromClipboard(bool isFront)
    {
        try
        {
            string? savedFile = ClipboardHelper.SaveClipboardImageToFile();
            if (!string.IsNullOrEmpty(savedFile) && File.Exists(savedFile))
            {
                Log.Information("Pasted image successfully from clipboard for {Slot}: {Path}", isFront ? "Front" : "Back", savedFile);
                SetSlotFile(isFront, savedFile, triggerAutoCrop: true);
                return;
            }

            WpfMessageBox.Show(
                "No image found in clipboard.\n\nPlease copy an image or photo (from Phone Link, WhatsApp, browser, or File Explorer) using Ctrl+C or Right-Click -> Copy, then try again.",
                "Clipboard Empty",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to paste from clipboard");
            WpfMessageBox.Show($"Could not paste image:\n{ex.Message}", "Paste Error", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    public async void SetSlotFile(bool isFront, string path, bool triggerAutoCrop = true)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;

        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is ".jpg" or ".jpeg" or ".png" or ".webp" or ".bmp")
        {
            // Auto-rotate vertical smartphone photos to standard horizontal landscape
            path = ImageCropService.EnsureLandscapeOrientation(path);
        }

        if (isFront)
        {
            FrontOriginalPath = path;
            FrontPath = path;
            FrontPageIndex = 0;
        }
        else
        {
            BackOriginalPath = path;
            BackPath = path;
            BackPageIndex = 0;
        }

        if (triggerAutoCrop && AutoCropOnImport)
        {
            if (ext is ".jpg" or ".jpeg" or ".png" or ".webp" or ".bmp")
            {
                await AutoCropSlotAsync(isFront);
            }
        }
    }

    private async void LoadFrontPreview()
    {
        int version = ++_frontPreviewVersion;
        try
        {
            if (string.IsNullOrEmpty(FrontPath) || !File.Exists(FrontPath))
            {
                _frontRawBitmap = null;
                _frontRawPath = string.Empty;
                _frontRawPageIndex = -1;
                FrontImageSource = null;
                FrontPreviewVisibility = Visibility.Collapsed;
                FrontPlaceholderVisibility = Visibility.Visible;
                return;
            }

            string currentPath = FrontPath;
            int currentPageIndex = FrontPageIndex;

            BitmapSource? rawBmp = null;
            if (_frontRawBitmap != null && _frontRawPath == currentPath && _frontRawPageIndex == currentPageIndex)
            {
                rawBmp = _frontRawBitmap;
            }
            else
            {
                string ext = Path.GetExtension(currentPath).ToLowerInvariant();
                if (ext is ".jpg" or ".jpeg" or ".png" or ".bmp" or ".webp")
                {
                    byte[] bytes = File.ReadAllBytes(currentPath);
                    using var ms = new MemoryStream(bytes);
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.StreamSource = ms;
                    bitmap.EndInit();
                    bitmap.Freeze();
                    rawBmp = bitmap;
                }
                else if (ext == ".pdf")
                {
                    var pdfBitmap = await PdfRendererService.RenderPdfPageAsync(currentPath, currentPageIndex);
                    if (pdfBitmap != null)
                    {
                        if (pdfBitmap.CanFreeze && !pdfBitmap.IsFrozen) pdfBitmap.Freeze();
                        rawBmp = pdfBitmap;
                    }
                }

                if (rawBmp != null)
                {
                    _frontRawBitmap = rawBmp;
                    _frontRawPath = currentPath;
                    _frontRawPageIndex = currentPageIndex;
                }
            }

            if (version != _frontPreviewVersion) return;

            if (rawBmp != null)
            {
                if (FrontPath != currentPath || FrontPageIndex != currentPageIndex)
                    return;

                if (AutoWhiten || PitchBlackText)
                {
                    var options = new EnhancementOptions(SelectedPreset)
                    {
                        WhitenBackground = AutoWhiten || PitchBlackText,
                        DarkenText = PitchBlackText,
                        PreserveColor = true
                    };
                    var enhanced = await Task.Run(() => _enhancerService.EnhanceBitmapSource(rawBmp, options));
                    if (version != _frontPreviewVersion) return;
                    if (FrontPath == currentPath && FrontPageIndex == currentPageIndex)
                    {
                        FrontImageSource = enhanced;
                        FrontPreviewVisibility = Visibility.Visible;
                        FrontPlaceholderVisibility = Visibility.Collapsed;
                    }
                }
                else
                {
                    FrontImageSource = rawBmp;
                    FrontPreviewVisibility = Visibility.Visible;
                    FrontPlaceholderVisibility = Visibility.Collapsed;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to load front preview for {Path}", FrontPath);
        }
    }

    private async void LoadBackPreview()
    {
        int version = ++_backPreviewVersion;
        try
        {
            if (string.IsNullOrEmpty(BackPath) || !File.Exists(BackPath))
            {
                _backRawBitmap = null;
                _backRawPath = string.Empty;
                _backRawPageIndex = -1;
                BackImageSource = null;
                BackPreviewVisibility = Visibility.Collapsed;
                BackPlaceholderVisibility = Visibility.Visible;
                return;
            }

            string currentPath = BackPath;
            int currentPageIndex = BackPageIndex;

            BitmapSource? rawBmp = null;
            if (_backRawBitmap != null && _backRawPath == currentPath && _backRawPageIndex == currentPageIndex)
            {
                rawBmp = _backRawBitmap;
            }
            else
            {
                string ext = Path.GetExtension(currentPath).ToLowerInvariant();
                if (ext is ".jpg" or ".jpeg" or ".png" or ".bmp" or ".webp")
                {
                    byte[] bytes = File.ReadAllBytes(currentPath);
                    using var ms = new MemoryStream(bytes);
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.StreamSource = ms;
                    bitmap.EndInit();
                    bitmap.Freeze();
                    rawBmp = bitmap;
                }
                else if (ext == ".pdf")
                {
                    var pdfBitmap = await PdfRendererService.RenderPdfPageAsync(currentPath, currentPageIndex);
                    if (pdfBitmap != null)
                    {
                        if (pdfBitmap.CanFreeze && !pdfBitmap.IsFrozen) pdfBitmap.Freeze();
                        rawBmp = pdfBitmap;
                    }
                }

                if (rawBmp != null)
                {
                    _backRawBitmap = rawBmp;
                    _backRawPath = currentPath;
                    _backRawPageIndex = currentPageIndex;
                }
            }

            if (version != _backPreviewVersion) return;

            if (rawBmp != null)
            {
                if (BackPath != currentPath || BackPageIndex != currentPageIndex)
                    return;

                if (AutoWhiten || PitchBlackText)
                {
                    var options = new EnhancementOptions(SelectedPreset)
                    {
                        WhitenBackground = AutoWhiten || PitchBlackText,
                        DarkenText = PitchBlackText,
                        PreserveColor = true
                    };
                    var enhanced = await Task.Run(() => _enhancerService.EnhanceBitmapSource(rawBmp, options));
                    if (version != _backPreviewVersion) return;
                    if (BackPath == currentPath && BackPageIndex == currentPageIndex)
                    {
                        BackImageSource = enhanced;
                        BackPreviewVisibility = Visibility.Visible;
                        BackPlaceholderVisibility = Visibility.Collapsed;
                    }
                }
                else
                {
                    BackImageSource = rawBmp;
                    BackPreviewVisibility = Visibility.Visible;
                    BackPlaceholderVisibility = Visibility.Collapsed;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to load back preview for {Path}", BackPath);
        }
    }

    private void BrowseFile(bool isFront)
    {
        string? path = isFront
            ? (RequestBrowseFront?.Invoke() ?? RequestBrowseFile?.Invoke(true))
            : (RequestBrowseBack?.Invoke() ?? RequestBrowseFile?.Invoke(false));
        if (!string.IsNullOrEmpty(path))
        {
            SetSlotFile(isFront, path, triggerAutoCrop: true);
        }
    }

    private void SelectBothFiles()
    {
        string[]? files = RequestBrowseBoth?.Invoke();
        if (files == null || files.Length == 0) return;

        if (files.Length >= 2)
        {
            SetSlotFile(true, files[0], triggerAutoCrop: true);
            SetSlotFile(false, files[1], triggerAutoCrop: true);
        }
        else
        {
            if (string.IsNullOrEmpty(FrontPath)) SetSlotFile(true, files[0], triggerAutoCrop: true);
            else SetSlotFile(false, files[0], triggerAutoCrop: true);
        }
    }

    private void SwapFrontAndBack()
    {
        var tempRawBitmap = _frontRawBitmap;
        var tempRawPath = _frontRawPath;
        var tempRawIndex = _frontRawPageIndex;

        _frontRawBitmap = _backRawBitmap;
        _frontRawPath = _backRawPath;
        _frontRawPageIndex = _backRawPageIndex;

        _backRawBitmap = tempRawBitmap;
        _backRawPath = tempRawPath;
        _backRawPageIndex = tempRawIndex;

        var tempPath = FrontPath;
        var tempIndex = FrontPageIndex;
        var tempScale = FrontScale;
        var tempOrig = FrontOriginalPath;

        FrontPath = BackPath;
        FrontPageIndex = BackPageIndex;
        FrontScale = BackScale;
        FrontOriginalPath = BackOriginalPath;

        BackPath = tempPath;
        BackPageIndex = tempIndex;
        BackScale = tempScale;
        BackOriginalPath = tempOrig;
    }

    public async Task AutoCropSlotAsync(bool isFront)
    {
        string source = isFront
            ? (!string.IsNullOrEmpty(FrontOriginalPath) ? FrontOriginalPath : FrontPath)
            : (!string.IsNullOrEmpty(BackOriginalPath) ? BackOriginalPath : BackPath);

        if (string.IsNullOrEmpty(source) || !File.Exists(source)) return;

        IsProcessing = true;
        ProgressText = $"🤖 Auto-detecting card in {(isFront ? "Front" : "Back")} photo...";

        try
        {
            string fileToCrop = source;
            string ext = Path.GetExtension(source).ToLowerInvariant();
            if (ext == ".pdf")
            {
                int pageIdx = isFront ? FrontPageIndex : BackPageIndex;
                var renderedBmp = await PdfRendererService.RenderPdfPageAsync(source, pageIdx);
                if (renderedBmp != null)
                {
                    string tempPng = Path.Combine(Path.GetTempPath(), $"dasmo_pdf_page_{Guid.NewGuid():N}.png");
                    var enc = new PngBitmapEncoder();
                    enc.Frames.Add(BitmapFrame.Create(renderedBmp));
                    using (var fs = File.OpenWrite(tempPng)) { enc.Save(fs); }
                    fileToCrop = tempPng;
                }
            }

            var croppedPath = await ImageCropService.Instance.AutoCropCardAsync(fileToCrop, AutoWhiten);
            if (!string.IsNullOrEmpty(croppedPath) && File.Exists(croppedPath))
            {
                if (isFront)
                {
                    if (string.IsNullOrEmpty(FrontOriginalPath)) FrontOriginalPath = source;
                    FrontPath = croppedPath;
                }
                else
                {
                    if (string.IsNullOrEmpty(BackOriginalPath)) BackOriginalPath = source;
                    BackPath = croppedPath;
                }
                ProgressText = $"✅ {(isFront ? "Front" : "Back")} card auto-cropped!";
            }
            else
            {
                ProgressText = "No clear card borders detected. Original preserved.";
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "AutoCropSlotAsync failed for {Slot}", isFront ? "Front" : "Back");
            ProgressText = "Auto-crop error. Original preserved.";
        }
        finally
        {
            IsProcessing = false;
        }
    }

    public async void OpenCropDialog(bool isFront, bool startInQuadMode = false)
    {
        string source = isFront
            ? (!string.IsNullOrEmpty(FrontOriginalPath) ? FrontOriginalPath : FrontPath)
            : (!string.IsNullOrEmpty(BackOriginalPath) ? BackOriginalPath : BackPath);

        if (string.IsNullOrEmpty(source) || !File.Exists(source)) return;

        try
        {
            string fileToCrop = source;
            string ext = Path.GetExtension(source).ToLowerInvariant();
            if (ext == ".pdf")
            {
                int pageIdx = isFront ? FrontPageIndex : BackPageIndex;
                var renderedBmp = await PdfRendererService.RenderPdfPageAsync(source, pageIdx);
                if (renderedBmp != null)
                {
                    string tempPng = Path.Combine(Path.GetTempPath(), $"dasmo_pdf_page_{Guid.NewGuid():N}.png");
                    var enc = new PngBitmapEncoder();
                    enc.Frames.Add(BitmapFrame.Create(renderedBmp));
                    using (var fs = File.OpenWrite(tempPng)) { enc.Save(fs); }
                    fileToCrop = tempPng;
                }
            }

            // Find active window as dialog owner safely
            Window? owner = System.Windows.Application.Current?.Windows.OfType<DocumentStackerWindow>().FirstOrDefault(w => w.IsVisible)
                            ?? System.Windows.Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive)
                            ?? System.Windows.Application.Current?.MainWindow;

            string? cropped = ImageCropDialog.ShowCropDialog(fileToCrop, owner, startInQuadMode, initialPreset: "idcard");
            if (!string.IsNullOrEmpty(cropped) && File.Exists(cropped))
            {
                if (isFront)
                {
                    if (string.IsNullOrEmpty(FrontOriginalPath)) FrontOriginalPath = source;
                    FrontPath = cropped;
                }
                else
                {
                    if (string.IsNullOrEmpty(BackOriginalPath)) BackOriginalPath = source;
                    BackPath = cropped;
                }
                ProgressText = $"✅ {(isFront ? "Front" : "Back")} card crop updated!";
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "OpenCropDialog failed for {Slot}", isFront ? "Front" : "Back");
            WpfMessageBox.Show($"Could not open crop editor:\n{ex.Message}", "Crop Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RevertSlot(bool isFront)
    {
        if (isFront && !string.IsNullOrEmpty(FrontOriginalPath) && File.Exists(FrontOriginalPath))
        {
            _frontRawBitmap = null;
            _frontRawPath = string.Empty;
            _frontRawPageIndex = -1;
            FrontPath = FrontOriginalPath;
            ProgressText = "Reverted front image to original.";
        }
        else if (!isFront && !string.IsNullOrEmpty(BackOriginalPath) && File.Exists(BackOriginalPath))
        {
            _backRawBitmap = null;
            _backRawPath = string.Empty;
            _backRawPageIndex = -1;
            BackPath = BackOriginalPath;
            ProgressText = "Reverted back image to original.";
        }
    }

    public async Task RotateSlotAsync(bool isFront, bool clockwise = true)
    {
        string current = isFront ? FrontPath : BackPath;
        if (string.IsNullOrEmpty(current) || !File.Exists(current)) return;

        IsProcessing = true;
        ProgressText = $"Rotating {(isFront ? "front" : "back")} image 90°...";

        try
        {
            string ext = Path.GetExtension(current).ToLowerInvariant();
            string fileToRotate = current;
            if (ext == ".pdf")
            {
                int pageIdx = isFront ? FrontPageIndex : BackPageIndex;
                var renderedBmp = await PdfRendererService.RenderPdfPageAsync(current, pageIdx);
                if (renderedBmp != null)
                {
                    string tempPng = Path.Combine(Path.GetTempPath(), $"dasmo_pdf_page_{Guid.NewGuid():N}.png");
                    var enc = new PngBitmapEncoder();
                    enc.Frames.Add(BitmapFrame.Create(renderedBmp));
                    using (var fs = File.OpenWrite(tempPng)) { enc.Save(fs); }
                    fileToRotate = tempPng;
                }
            }

            var rotated = await ImageCropService.Instance.RotateImage90Async(fileToRotate, clockwise);
            if (!string.IsNullOrEmpty(rotated) && File.Exists(rotated))
            {
                if (isFront)
                {
                    FrontPath = rotated;
                }
                else
                {
                    BackPath = rotated;
                }
                ProgressText = $"✅ {(isFront ? "Front" : "Back")} rotated 90°!";
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to rotate slot {Slot}", isFront ? "Front" : "Back");
            ProgressText = "Could not rotate image.";
        }
        finally
        {
            IsProcessing = false;
        }
    }

    private void Import2PagePdf()
    {
        string? path = RequestImportPdf?.Invoke();
        if (path == null) return;

        FrontPath = path;
        FrontPageIndex = 0;
        BackPath = path;
        BackPageIndex = 1;
    }

    private async Task PrintNowAsync()
    {
        IsProcessing = true;
        ProgressText = "Preparing A4 sheet for instant print...";

        try
        {
            var bmp = await _stackerService.RenderA4SheetBitmapAsync(
                FrontPath, FrontPageIndex, FrontScale,
                BackPath, BackPageIndex, BackScale,
                LayoutMode, AutoWhiten, PitchBlackText, SelectedPreset);

            if (bmp != null)
            {
                PrintService.PrintImageDirect(bmp, "A4_Stacked_ID_Document");
                _isSaved = true;
                OnPropertyChanged(nameof(HasUnsavedWork));
            }
            else
            {
                WpfMessageBox.Show("Failed to render document for printing.", "Print Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to print A4 stacked document directly");
            WpfMessageBox.Show($"Failed to print:\n{ex.Message}", "Print Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsProcessing = false;
            ProgressText = string.Empty;
        }
    }

    private async Task StackAsync()
    {
        IsProcessing = true;
        ProgressText = "Combining front & back onto single A4 sheet...";
        ResultText = string.Empty;

        try
        {
            long targetBytes = TargetSizeUnit == "MB"
                ? (long)TargetSize * 1024 * 1024
                : (long)TargetSize * 1024;

            string filter = SelectedOutputFormat == "PDF"
                ? "PDF Document (*.pdf)|*.pdf"
                : SelectedOutputFormat == ".jpg"
                    ? "JPEG Image (*.jpg)|*.jpg"
                    : "PNG Image (*.png)|*.png";

            string defaultExt = SelectedOutputFormat == "PDF" ? ".pdf" : SelectedOutputFormat;
            string defaultName = $"A4_Stacked_Document{defaultExt}";

            string? outputPath = RequestSaveFile?.Invoke(filter, defaultName);
            if (outputPath == null)
            {
                IsProcessing = false;
                ProgressText = string.Empty;
                return;
            }

            CompressionResult result;
            if (SelectedOutputFormat == "PDF")
            {
                result = await _stackerService.StackToPdfAsync(
                    FrontPath, FrontPageIndex, FrontScale,
                    BackPath, BackPageIndex, BackScale,
                    outputPath, targetBytes,
                    LayoutMode, AutoWhiten, PitchBlackText, SelectedPreset);
            }
            else
            {
                result = await _stackerService.StackToImageAsync(
                    FrontPath, FrontPageIndex, FrontScale,
                    BackPath, BackPageIndex, BackScale,
                    outputPath, targetBytes, SelectedOutputFormat,
                    LayoutMode, AutoWhiten, PitchBlackText, SelectedPreset);
            }

            if (result.Success)
            {
                _isSaved = true;
                OnPropertyChanged(nameof(HasUnsavedWork));
                double pct = result.OriginalSizeBytes > 0
                    ? 100.0 * (1.0 - (double)result.NewSizeBytes / result.OriginalSizeBytes)
                    : 0;
                ResultText = $"✅ Successfully Stacked!\nSize: {result.CompressedSizeFormatted} ({pct:F1}% saved)\nSaved to: {Path.GetFileName(outputPath)}";
                OutputHistoryService.Instance.Record(outputPath, "A4 Doc Stacker", result.OriginalSizeBytes, result.NewSizeBytes);
            }
            else
            {
                ResultText = $"❌ Failed: {result.Message}";
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error in DocumentStackerViewModel.StackAsync");
            ResultText = $"❌ Error: {ex.Message}";
        }
        finally
        {
            IsProcessing = false;
            ProgressText = string.Empty;
        }
    }
}
