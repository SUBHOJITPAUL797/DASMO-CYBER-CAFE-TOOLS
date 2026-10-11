using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Serilog;
using SmartSaver.Models;
using SmartSaver.Services;
using WpfMessageBox = System.Windows.MessageBox;

namespace SmartSaver.ViewModels;

public record SignaturePreset(
    string Name,
    double Width,
    double Height,
    bool IsPixel,
    int TargetSizeKB,
    int MinSizeKB = 0,
    string Description = "")
{
    public override string ToString() => Name;
}

public class SignatureResizeViewModel : ViewModelBase
{
    private readonly SignatureResizeService _resizeService;
    private byte[]? _inputBytes;
    private byte[]? _outputBytes;

    #region File & Input Properties
    private string _filePath = string.Empty;
    public string FilePath
    {
        get => _filePath;
        set => SetProperty(ref _filePath, value);
    }

    private string _fileName = "No file selected";
    public string FileName
    {
        get => _fileName;
        set => SetProperty(ref _fileName, value);
    }

    private long _originalFileSizeBytes;
    public long OriginalFileSizeBytes
    {
        get => _originalFileSizeBytes;
        set => SetProperty(ref _originalFileSizeBytes, value);
    }
    public string OriginalFileSizeFormatted => CompressionResult.FormatFileSize(OriginalFileSizeBytes);

    private int _originalWidth;
    public int OriginalWidth
    {
        get => _originalWidth;
        set => SetProperty(ref _originalWidth, value);
    }

    private int _originalHeight;
    public int OriginalHeight
    {
        get => _originalHeight;
        set => SetProperty(ref _originalHeight, value);
    }

    public string OriginalDimensionsText => OriginalWidth > 0 && OriginalHeight > 0
        ? $"{OriginalWidth} × {OriginalHeight} px"
        : string.Empty;

    private bool _hasInputImage;
    public bool HasInputImage
    {
        get => _hasInputImage;
        set
        {
            if (SetProperty(ref _hasInputImage, value))
            {
                OnPropertyChanged(nameof(CanResize));
                OnPropertyChanged(nameof(ShowEmptyState));
                OnPropertyChanged(nameof(ShowLoadingState));
                OnPropertyChanged(nameof(ShowResultState));
            }
        }
    }

    private BitmapSource? _inputPreviewImage;
    public BitmapSource? InputPreviewImage
    {
        get => _inputPreviewImage;
        set => SetProperty(ref _inputPreviewImage, value);
    }
    #endregion

    #region Unit & Dimension Controls (Pixel vs Centimeter)
    private bool _isPixelMode = true;
    public bool IsPixelMode
    {
        get => _isPixelMode;
        set
        {
            if (SetProperty(ref _isPixelMode, value))
            {
                _isCentimeterMode = !value;
                OnPropertyChanged(nameof(IsCentimeterMode));
                OnPropertyChanged(nameof(DimensionUnitLabel));
                OnPropertyChanged(nameof(EffectiveWidthPx));
                OnPropertyChanged(nameof(EffectiveHeightPx));
                OnPropertyChanged(nameof(EquivalentPixelsText));
                TriggerAutoPreview(50);
            }
        }
    }

    private bool _isCentimeterMode = false;
    public bool IsCentimeterMode
    {
        get => _isCentimeterMode;
        set
        {
            if (SetProperty(ref _isCentimeterMode, value))
            {
                _isPixelMode = !value;
                OnPropertyChanged(nameof(IsPixelMode));
                OnPropertyChanged(nameof(DimensionUnitLabel));
                OnPropertyChanged(nameof(EffectiveWidthPx));
                OnPropertyChanged(nameof(EffectiveHeightPx));
                OnPropertyChanged(nameof(EquivalentPixelsText));
                TriggerAutoPreview(50);
            }
        }
    }

    public string DimensionUnitLabel => IsPixelMode ? "px" : "cm";

    // Pixel Dimensions (defaults to Pi7 standard: 140 x 60)
    private string _widthPxText = "140";
    public string WidthPxText
    {
        get => _widthPxText;
        set
        {
            if (SetProperty(ref _widthPxText, value))
            {
                string raw = (value ?? string.Empty).Trim();
                if (int.TryParse(raw, out int parsed))
                {
                    if (parsed >= 10 && parsed <= 5000)
                    {
                        if (_widthPx != parsed)
                        {
                            _widthPx = parsed;
                            OnPropertyChanged(nameof(WidthPx));
                            OnPropertyChanged(nameof(EffectiveWidthPx));
                            OnPropertyChanged(nameof(EquivalentPixelsText));
                            OnPropertyChanged(nameof(CanResize));
                            TriggerAutoPreview(120);
                        }
                    }
                }
            }
        }
    }

    private int _widthPx = 140;
    public int WidthPx
    {
        get => _widthPx;
        set
        {
            if (SetProperty(ref _widthPx, value))
            {
                _widthPxText = value.ToString();
                OnPropertyChanged(nameof(WidthPxText));
                OnPropertyChanged(nameof(EffectiveWidthPx));
                OnPropertyChanged(nameof(EquivalentPixelsText));
                OnPropertyChanged(nameof(CanResize));
                TriggerAutoPreview(120);
            }
        }
    }

    private string _heightPxText = "60";
    public string HeightPxText
    {
        get => _heightPxText;
        set
        {
            if (SetProperty(ref _heightPxText, value))
            {
                string raw = (value ?? string.Empty).Trim();
                if (int.TryParse(raw, out int parsed))
                {
                    if (parsed >= 10 && parsed <= 5000)
                    {
                        if (_heightPx != parsed)
                        {
                            _heightPx = parsed;
                            OnPropertyChanged(nameof(HeightPx));
                            OnPropertyChanged(nameof(EffectiveHeightPx));
                            OnPropertyChanged(nameof(EquivalentPixelsText));
                            OnPropertyChanged(nameof(CanResize));
                            TriggerAutoPreview(120);
                        }
                    }
                }
            }
        }
    }

    private int _heightPx = 60;
    public int HeightPx
    {
        get => _heightPx;
        set
        {
            if (SetProperty(ref _heightPx, value))
            {
                _heightPxText = value.ToString();
                OnPropertyChanged(nameof(HeightPxText));
                OnPropertyChanged(nameof(EffectiveHeightPx));
                OnPropertyChanged(nameof(EquivalentPixelsText));
                OnPropertyChanged(nameof(CanResize));
                TriggerAutoPreview(120);
            }
        }
    }

    // Centimeter Dimensions (defaults to 3.5 x 1.5 cm)
    private string _widthCmText = "3.5";
    public string WidthCmText
    {
        get => _widthCmText;
        set
        {
            if (SetProperty(ref _widthCmText, value))
            {
                string normalized = (value ?? string.Empty).Trim().Replace(',', '.');
                if (double.TryParse(normalized, NumberStyles.Any, CultureInfo.InvariantCulture, out double parsed))
                {
                    if (parsed >= 0.5 && parsed <= 50.0)
                    {
                        double safeVal = Math.Round(parsed, 2);
                        if (Math.Abs(_widthCm - safeVal) > 0.0001)
                        {
                            _widthCm = safeVal;
                            OnPropertyChanged(nameof(WidthCm));
                            OnPropertyChanged(nameof(EffectiveWidthPx));
                            OnPropertyChanged(nameof(EquivalentPixelsText));
                            OnPropertyChanged(nameof(CanResize));
                            TriggerAutoPreview(120);
                        }
                    }
                }
            }
        }
    }

    private double _widthCm = 3.5;
    public double WidthCm
    {
        get => _widthCm;
        set
        {
            if (SetProperty(ref _widthCm, value))
            {
                _widthCmText = value.ToString("0.##", CultureInfo.InvariantCulture);
                OnPropertyChanged(nameof(WidthCmText));
                OnPropertyChanged(nameof(EffectiveWidthPx));
                OnPropertyChanged(nameof(EquivalentPixelsText));
                OnPropertyChanged(nameof(CanResize));
                TriggerAutoPreview(120);
            }
        }
    }

    private string _heightCmText = "1.5";
    public string HeightCmText
    {
        get => _heightCmText;
        set
        {
            if (SetProperty(ref _heightCmText, value))
            {
                string normalized = (value ?? string.Empty).Trim().Replace(',', '.');
                if (double.TryParse(normalized, NumberStyles.Any, CultureInfo.InvariantCulture, out double parsed))
                {
                    if (parsed >= 0.5 && parsed <= 50.0)
                    {
                        double safeVal = Math.Round(parsed, 2);
                        if (Math.Abs(_heightCm - safeVal) > 0.0001)
                        {
                            _heightCm = safeVal;
                            OnPropertyChanged(nameof(HeightCm));
                            OnPropertyChanged(nameof(EffectiveHeightPx));
                            OnPropertyChanged(nameof(EquivalentPixelsText));
                            OnPropertyChanged(nameof(CanResize));
                            TriggerAutoPreview(120);
                        }
                    }
                }
            }
        }
    }

    private double _heightCm = 1.5;
    public double HeightCm
    {
        get => _heightCm;
        set
        {
            if (SetProperty(ref _heightCm, value))
            {
                _heightCmText = value.ToString("0.##", CultureInfo.InvariantCulture);
                OnPropertyChanged(nameof(HeightCmText));
                OnPropertyChanged(nameof(EffectiveHeightPx));
                OnPropertyChanged(nameof(EquivalentPixelsText));
                OnPropertyChanged(nameof(CanResize));
                TriggerAutoPreview(120);
            }
        }
    }

    public void NormalizeDimensionTexts()
    {
        _widthCmText = _widthCm.ToString("0.##", CultureInfo.InvariantCulture);
        OnPropertyChanged(nameof(WidthCmText));
        _heightCmText = _heightCm.ToString("0.##", CultureInfo.InvariantCulture);
        OnPropertyChanged(nameof(HeightCmText));
        _widthPxText = _widthPx.ToString();
        OnPropertyChanged(nameof(WidthPxText));
        _heightPxText = _heightPx.ToString();
        OnPropertyChanged(nameof(HeightPxText));
        _targetSizeKBText = _targetSizeKB.ToString();
        OnPropertyChanged(nameof(TargetSizeKBText));
        _minSizeKBText = _minSizeKB.ToString();
        OnPropertyChanged(nameof(MinSizeKBText));
    }

    public ObservableCollection<int> DpiOptions { get; } = new() { 300, 200, 150 };

    private int _selectedDpi = 300;
    public int SelectedDpi
    {
        get => _selectedDpi;
        set
        {
            if (SetProperty(ref _selectedDpi, value))
            {
                OnPropertyChanged(nameof(EffectiveWidthPx));
                OnPropertyChanged(nameof(EffectiveHeightPx));
                OnPropertyChanged(nameof(EquivalentPixelsText));
                TriggerAutoPreview(80);
            }
        }
    }

    /// <summary>
    /// Effective pixel width taking unit selection into account.
    /// </summary>
    public int EffectiveWidthPx
    {
        get
        {
            if (IsPixelMode) return WidthPx > 0 ? WidthPx : 140;
            return WidthCm > 0 ? (int)Math.Round(WidthCm * SelectedDpi / 2.54) : 140;
        }
    }

    /// <summary>
    /// Effective pixel height taking unit selection into account.
    /// </summary>
    public int EffectiveHeightPx
    {
        get
        {
            if (IsPixelMode) return HeightPx > 0 ? HeightPx : 60;
            return HeightCm > 0 ? (int)Math.Round(HeightCm * SelectedDpi / 2.54) : 60;
        }
    }

    public string EquivalentPixelsText => IsCentimeterMode
        ? $"= {EffectiveWidthPx} × {EffectiveHeightPx} px (at {SelectedDpi} DPI)"
        : $"= {(WidthPx * 2.54 / SelectedDpi):F2} × {(HeightPx * 2.54 / SelectedDpi):F2} cm (at {SelectedDpi} DPI)";
    #endregion

    #region Target File Size & Options
    private string _targetSizeKBText = "20";
    public string TargetSizeKBText
    {
        get => _targetSizeKBText;
        set
        {
            if (SetProperty(ref _targetSizeKBText, value))
            {
                string raw = (value ?? string.Empty).Trim();
                if (int.TryParse(raw, out int parsed))
                {
                    if (parsed >= 5 && parsed <= 10000)
                    {
                        if (_targetSizeKB != parsed)
                        {
                            _targetSizeKB = parsed;
                            OnPropertyChanged(nameof(TargetSizeKB));
                            TriggerAutoPreview(150);
                        }
                    }
                }
            }
        }
    }

    private int _targetSizeKB = 20;
    public int TargetSizeKB
    {
        get => _targetSizeKB;
        set
        {
            if (SetProperty(ref _targetSizeKB, value))
            {
                _targetSizeKBText = value.ToString();
                OnPropertyChanged(nameof(TargetSizeKBText));
                TriggerAutoPreview(150);
            }
        }
    }

    private string _minSizeKBText = "0";
    public string MinSizeKBText
    {
        get => _minSizeKBText;
        set
        {
            if (SetProperty(ref _minSizeKBText, value))
            {
                string raw = (value ?? string.Empty).Trim();
                if (int.TryParse(raw, out int parsed))
                {
                    if (parsed >= 0 && parsed <= 10000)
                    {
                        if (_minSizeKB != parsed)
                        {
                            _minSizeKB = parsed;
                            OnPropertyChanged(nameof(MinSizeKB));
                            TriggerAutoPreview(150);
                        }
                    }
                }
            }
        }
    }

    private int _minSizeKB = 0;
    public int MinSizeKB
    {
        get => _minSizeKB;
        set
        {
            if (SetProperty(ref _minSizeKB, value))
            {
                _minSizeKBText = value.ToString();
                OnPropertyChanged(nameof(MinSizeKBText));
                TriggerAutoPreview(150);
            }
        }
    }

    private bool _cleanBackground = true;
    public bool CleanBackground
    {
        get => _cleanBackground;
        set
        {
            if (SetProperty(ref _cleanBackground, value))
            {
                TriggerAutoPreview(50);
            }
        }
    }

    private bool _convertBlueToBlack = true;
    public bool ConvertBlueToBlack
    {
        get => _convertBlueToBlack;
        set
        {
            if (SetProperty(ref _convertBlueToBlack, value))
            {
                if (value && !IsInkBlack)
                {
                    _inkColorMode = "Black";
                    OnPropertyChanged(nameof(InkColorMode));
                    OnPropertyChanged(nameof(IsInkBlack));
                    OnPropertyChanged(nameof(IsInkRoyalBlue));
                    OnPropertyChanged(nameof(IsInkNavyBlue));
                    OnPropertyChanged(nameof(IsInkOriginal));
                    OnPropertyChanged(nameof(InkColorBadgeText));
                }
                else if (!value && IsInkBlack)
                {
                    _inkColorMode = "Original";
                    OnPropertyChanged(nameof(InkColorMode));
                    OnPropertyChanged(nameof(IsInkBlack));
                    OnPropertyChanged(nameof(IsInkRoyalBlue));
                    OnPropertyChanged(nameof(IsInkNavyBlue));
                    OnPropertyChanged(nameof(IsInkOriginal));
                    OnPropertyChanged(nameof(InkColorBadgeText));
                }
                TriggerAutoPreview(50);
            }
        }
    }

    private string _inkColorMode = "Black"; // "Black", "RoyalBlue", "NavyBlue", "Original"
    public string InkColorMode
    {
        get => _inkColorMode;
        set
        {
            if (SetProperty(ref _inkColorMode, value))
            {
                if (string.Equals(value, "Black", StringComparison.OrdinalIgnoreCase))
                {
                    _convertBlueToBlack = true;
                }
                else
                {
                    _convertBlueToBlack = false;
                }
                OnPropertyChanged(nameof(ConvertBlueToBlack));
                OnPropertyChanged(nameof(IsInkBlack));
                OnPropertyChanged(nameof(IsInkRoyalBlue));
                OnPropertyChanged(nameof(IsInkNavyBlue));
                OnPropertyChanged(nameof(IsInkOriginal));
                OnPropertyChanged(nameof(InkColorBadgeText));
                TriggerAutoPreview(50);
            }
        }
    }

    public bool IsInkBlack => string.Equals(InkColorMode, "Black", StringComparison.OrdinalIgnoreCase);
    public bool IsInkRoyalBlue => string.Equals(InkColorMode, "RoyalBlue", StringComparison.OrdinalIgnoreCase) || string.Equals(InkColorMode, "Blue", StringComparison.OrdinalIgnoreCase);
    public bool IsInkNavyBlue => string.Equals(InkColorMode, "NavyBlue", StringComparison.OrdinalIgnoreCase);
    public bool IsInkOriginal => string.Equals(InkColorMode, "Original", StringComparison.OrdinalIgnoreCase);

    public string InkColorBadgeText => InkColorMode switch
    {
        "RoyalBlue" or "Blue" => "🖊️ Royal Blue Pen Ink Active (Official Indian Ballpoint / Gel Pen Ink)",
        "NavyBlue" => "🖋️ Dark Navy Blue Ink Active (Classic Fountain Pen Standard)",
        "Original" => "🎨 Original Pen Ink Active (Enhanced Contrast)",
        _ => "⬛ Pitch Black Ink Active (Strict Exam / SSC / UPSC Standard)"
    };

    private bool _autoCrop = true;
    public bool AutoCrop
    {
        get => _autoCrop;
        set
        {
            if (SetProperty(ref _autoCrop, value))
            {
                TriggerAutoPreview(50);
            }
        }
    }

    private bool _maintainAspectRatio = true;
    public bool MaintainAspectRatio
    {
        get => _maintainAspectRatio;
        set
        {
            if (SetProperty(ref _maintainAspectRatio, value))
            {
                TriggerAutoPreview(50);
            }
        }
    }

    private static readonly System.Windows.Media.Brush PanCardBlueMediaBrush = 
        new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(220, 232, 245));

    private string _backgroundMode = "White"; // "Transparent", "PanCardBlue", "White"
    public string BackgroundMode
    {
        get => _backgroundMode;
        set
        {
            if (SetProperty(ref _backgroundMode, value))
            {
                if (value == "Transparent")
                {
                    _transparentBackground = true;
                    _selectedOutputFormat = ".png";
                    OnPropertyChanged(nameof(TransparentBackground));
                    OnPropertyChanged(nameof(SelectedOutputFormat));
                    OnPropertyChanged(nameof(IsTransparentPngMode));
                }
                else if (value == "PanCardBlue")
                {
                    _transparentBackground = false;
                    _selectedOutputFormat = ".jpg";
                    OnPropertyChanged(nameof(TransparentBackground));
                    OnPropertyChanged(nameof(SelectedOutputFormat));
                    OnPropertyChanged(nameof(IsTransparentPngMode));
                }
                else
                {
                    _transparentBackground = false;
                    _selectedOutputFormat = ".jpg";
                    OnPropertyChanged(nameof(TransparentBackground));
                    OnPropertyChanged(nameof(SelectedOutputFormat));
                    OnPropertyChanged(nameof(IsTransparentPngMode));
                }
                OnPropertyChanged(nameof(IsBackgroundTransparent));
                OnPropertyChanged(nameof(IsBackgroundPanBlue));
                OnPropertyChanged(nameof(IsBackgroundWhite));
                OnPropertyChanged(nameof(PreviewBackgroundBrush));
                OnPropertyChanged(nameof(EffectivePreviewMode));
                OnPropertyChanged(nameof(IsPreviewCheckerboard));
                OnPropertyChanged(nameof(IsPreviewPanCardBlue));
                OnPropertyChanged(nameof(IsPreviewWhite));
                TriggerAutoPreview(50);
            }
        }
    }

    public bool IsBackgroundTransparent => BackgroundMode == "Transparent";
    public bool IsBackgroundPanBlue => BackgroundMode == "PanCardBlue";
    public bool IsBackgroundWhite => BackgroundMode == "White";

    private bool _transparentBackground = false;
    public bool TransparentBackground
    {
        get => _transparentBackground;
        set
        {
            if (SetProperty(ref _transparentBackground, value))
            {
                if (value)
                {
                    _backgroundMode = "Transparent";
                    SelectedOutputFormat = ".png";
                }
                else
                {
                    if (_backgroundMode == "Transparent")
                        _backgroundMode = "White";
                    SelectedOutputFormat = ".jpg";
                }
                OnPropertyChanged(nameof(BackgroundMode));
                OnPropertyChanged(nameof(IsBackgroundTransparent));
                OnPropertyChanged(nameof(IsBackgroundPanBlue));
                OnPropertyChanged(nameof(IsBackgroundWhite));
                OnPropertyChanged(nameof(IsTransparentPngMode));
                OnPropertyChanged(nameof(PreviewBackgroundBrush));
                OnPropertyChanged(nameof(EffectivePreviewMode));
                OnPropertyChanged(nameof(IsPreviewCheckerboard));
                OnPropertyChanged(nameof(IsPreviewPanCardBlue));
                OnPropertyChanged(nameof(IsPreviewWhite));
                TriggerAutoPreview(50);
            }
        }
    }

    public bool IsTransparentPngMode => TransparentBackground || SelectedOutputFormat == ".png";

    private string _previewBackgroundMode = "Auto"; // "Auto", "Checkerboard", "PanCardBlue", "White"
    public string PreviewBackgroundMode
    {
        get => _previewBackgroundMode;
        set
        {
            if (SetProperty(ref _previewBackgroundMode, value))
            {
                OnPropertyChanged(nameof(PreviewBackgroundBrush));
                OnPropertyChanged(nameof(EffectivePreviewMode));
                OnPropertyChanged(nameof(IsPreviewCheckerboard));
                OnPropertyChanged(nameof(IsPreviewPanCardBlue));
                OnPropertyChanged(nameof(IsPreviewWhite));
            }
        }
    }

    public string EffectivePreviewMode
    {
        get
        {
            if (_previewBackgroundMode != "Auto") return _previewBackgroundMode;
            if (IsTransparentPngMode) return "Checkerboard";
            if (BackgroundMode == "PanCardBlue") return "PanCardBlue";
            return "White";
        }
    }

    public System.Windows.Media.Brush PreviewBackgroundBrush
    {
        get
        {
            string mode = EffectivePreviewMode;
            if (mode == "Checkerboard")
            {
                if (System.Windows.Application.Current != null &&
                    System.Windows.Application.Current.TryFindResource("CheckerboardBrush") is System.Windows.Media.Brush cbBrush)
                {
                    return cbBrush;
                }
                return CreateDefaultCheckerboardBrush();
            }
            if (mode == "PanCardBlue")
            {
                return PanCardBlueMediaBrush;
            }
            return System.Windows.Media.Brushes.White;
        }
    }

    public bool IsPreviewCheckerboard => EffectivePreviewMode == "Checkerboard";
    public bool IsPreviewPanCardBlue => EffectivePreviewMode == "PanCardBlue";
    public bool IsPreviewWhite => EffectivePreviewMode == "White";

    private static System.Windows.Media.DrawingBrush CreateDefaultCheckerboardBrush()
    {
        var db = new System.Windows.Media.DrawingBrush
        {
            Viewport = new System.Windows.Rect(0, 0, 16, 16),
            ViewportUnits = System.Windows.Media.BrushMappingMode.Absolute,
            TileMode = System.Windows.Media.TileMode.Tile
        };
        var group = new System.Windows.Media.DrawingGroup();
        group.Children.Add(new System.Windows.Media.GeometryDrawing(
            System.Windows.Media.Brushes.White, null, new System.Windows.Media.RectangleGeometry(new System.Windows.Rect(0, 0, 16, 16))));
        var grayGroup = new System.Windows.Media.GeometryGroup();
        grayGroup.Children.Add(new System.Windows.Media.RectangleGeometry(new System.Windows.Rect(0, 0, 8, 8)));
        grayGroup.Children.Add(new System.Windows.Media.RectangleGeometry(new System.Windows.Rect(8, 8, 8, 8)));
        var slateBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(203, 213, 225));
        group.Children.Add(new System.Windows.Media.GeometryDrawing(slateBrush, null, grayGroup));
        db.Drawing = group;
        db.Freeze();
        return db;
    }

    public ObservableCollection<string> OutputFormats { get; } = new() { ".jpg", ".png" };

    private string _selectedOutputFormat = ".jpg";
    public string SelectedOutputFormat
    {
        get => _selectedOutputFormat;
        set
        {
            if (SetProperty(ref _selectedOutputFormat, value))
            {
                if (value == ".jpg" && TransparentBackground)
                {
                    _transparentBackground = false;
                    if (_backgroundMode == "Transparent")
                        _backgroundMode = "White";
                    OnPropertyChanged(nameof(TransparentBackground));
                    OnPropertyChanged(nameof(BackgroundMode));
                    OnPropertyChanged(nameof(IsBackgroundTransparent));
                    OnPropertyChanged(nameof(IsBackgroundPanBlue));
                    OnPropertyChanged(nameof(IsBackgroundWhite));
                }
                OnPropertyChanged(nameof(IsTransparentPngMode));
                OnPropertyChanged(nameof(PreviewBackgroundBrush));
                OnPropertyChanged(nameof(EffectivePreviewMode));
                OnPropertyChanged(nameof(IsPreviewCheckerboard));
                OnPropertyChanged(nameof(IsPreviewPanCardBlue));
                OnPropertyChanged(nameof(IsPreviewWhite));
                TriggerAutoPreview(50);
            }
        }
    }
    #endregion

    #region Presets
    public ObservableCollection<SignaturePreset> Presets { get; } = new()
    {
        new("⚡ Select Exam / Portal Preset...", 140, 60, true, 20, 0),
        new("💳 PAN Card Signature (2 × 4.5 cm | 200 DPI | <50 KB)", 4.5, 2.0, false, 30, 10, "Official NSDL & UTIITSL e-PAN portal specification"),
        new("💳 PAN Card Blue BG (2 × 4.5 cm | 200 DPI | No White Box)", 4.5, 2.0, false, 30, 10, "Matches PAN Card Blue background (#DCE8F5) so JPEG blends with ZERO white box"),
        new("🖊️ Royal Blue Pen Signature (2 × 4.5 cm | 200 DPI | Blue Ink)", 4.5, 2.0, false, 30, 10, "Authentic Royal Blue ballpoint/gel pen ink signature for bank, PAN & official documents"),
        new("💳 PAN Card Photo (2.5 × 3.5 cm | 200 DPI | <50 KB)", 2.5, 3.5, false, 30, 10, "Official NSDL & UTIITSL 2.5 x 3.5 cm Photo (<50 KB)"),
        new("✨ Transparent Signature (PNG | No White Box)", 4.5, 2.0, false, 50, 0, "100% transparent background for printing directly on card patterns"),
        new("✍️ SSC / IBPS Signature (140 × 60 px | 10–20 KB)", 140, 60, true, 20, 10, "Standard SSC, IBPS, Bank PO signature"),
        new("✍️ SSC High-Res Signature (280 × 120 px | 10–20 KB)", 280, 120, true, 20, 10, "2x crisp SSC signature"),
        new("✍️ State PSC / Police (3.5 × 1.5 cm | 10–20 KB)", 3.5, 1.5, false, 20, 10, "State Govt portal centimeter format"),
        new("✍️ UPSC Signature (350 × 350 px | 20–50 KB)", 350, 350, true, 50, 20, "UPSC Civil Services / NDA / CDS"),
        new("✍️ UPSC High-Res (1000 × 1000 px | 20–300 KB)", 1000, 1000, true, 100, 20, "UPSC OTR portal high-res signature"),
        new("📸 Left Thumb Impression / LTI (240 × 240 px | 10–50 KB)", 240, 240, true, 50, 10, "Standard square thumb impression"),
        new("⚙️ Custom Dimensions", 140, 60, true, 20, 0, "Manual width, height, and KB size")
    };

    private SignaturePreset? _selectedPreset;
    public SignaturePreset? SelectedPreset
    {
        get => _selectedPreset;
        set
        {
            if (SetProperty(ref _selectedPreset, value) && value != null)
            {
                if (value.Name.StartsWith("⚡")) return;

                if (value.Name.Contains("Royal Blue Pen Signature"))
                {
                    IsCentimeterMode = true;
                    WidthCm = 4.5;
                    HeightCm = 2.0;
                    SelectedDpi = 200;
                    TargetSizeKB = 30;
                    MinSizeKB = 10;
                    CleanBackground = true;
                    InkColorMode = "RoyalBlue";
                    AutoCrop = true;
                    MaintainAspectRatio = true;
                    BackgroundMode = "White";
                    SelectedOutputFormat = ".jpg";
                    TriggerAutoPreview(0);
                    return;
                }

                if (value.Name.Contains("PAN Card Blue BG"))
                {
                    IsCentimeterMode = true;
                    WidthCm = 4.5;
                    HeightCm = 2.0;
                    SelectedDpi = 200;
                    TargetSizeKB = 30;
                    MinSizeKB = 10;
                    CleanBackground = true;
                    ConvertBlueToBlack = true;
                    AutoCrop = true;
                    MaintainAspectRatio = true;
                    BackgroundMode = "PanCardBlue";
                    TriggerAutoPreview(0);
                    return;
                }

                if (value.Name.Contains("PAN Card Photo"))
                {
                    IsCentimeterMode = true;
                    WidthCm = 2.5;
                    HeightCm = 3.5;
                    SelectedDpi = 200;
                    TargetSizeKB = 30;
                    MinSizeKB = 10;
                    CleanBackground = false;
                    ConvertBlueToBlack = false;
                    AutoCrop = false;
                    MaintainAspectRatio = true;
                    BackgroundMode = "White";
                    SelectedOutputFormat = ".jpg";
                    TriggerAutoPreview(0);
                    return;
                }

                if (value.Name.Contains("PAN Card"))
                {
                    IsCentimeterMode = true;
                    WidthCm = 4.5;
                    HeightCm = 2.0;
                    SelectedDpi = 200;
                    TargetSizeKB = 30;
                    MinSizeKB = 10;
                    CleanBackground = true;
                    ConvertBlueToBlack = true;
                    AutoCrop = true;
                    MaintainAspectRatio = true;
                    BackgroundMode = "White";
                    SelectedOutputFormat = ".jpg";
                    TriggerAutoPreview(0);
                    return;
                }

                if (value.Name.Contains("Transparent Signature"))
                {
                    IsCentimeterMode = true;
                    WidthCm = 4.5;
                    HeightCm = 2.0;
                    SelectedDpi = 200;
                    TargetSizeKB = 50;
                    MinSizeKB = 0;
                    CleanBackground = true;
                    ConvertBlueToBlack = true;
                    AutoCrop = true;
                    MaintainAspectRatio = true;
                    TransparentBackground = true;
                    SelectedOutputFormat = ".png";
                    TriggerAutoPreview(0);
                    return;
                }

                if (value.IsPixel)
                {
                    IsPixelMode = true;
                    WidthPx = (int)value.Width;
                    HeightPx = (int)value.Height;
                }
                else
                {
                    IsCentimeterMode = true;
                    WidthCm = value.Width;
                    HeightCm = value.Height;
                }

                TargetSizeKB = value.TargetSizeKB;
                MinSizeKB = value.MinSizeKB;
                TriggerAutoPreview(0);
            }
        }
    }
    #endregion

    #region Result & Processing State
    private bool _isProcessing;
    public bool IsProcessing
    {
        get => _isProcessing;
        set
        {
            if (SetProperty(ref _isProcessing, value))
            {
                OnPropertyChanged(nameof(CanResize));
            }
        }
    }

    public bool CanResize => !IsProcessing && HasInputImage;

    private string _progressText = string.Empty;
    public string ProgressText
    {
        get => _progressText;
        set => SetProperty(ref _progressText, value);
    }

    private bool _hasResult;
    public bool HasResult
    {
        get => _hasResult;
        set
        {
            if (SetProperty(ref _hasResult, value))
            {
                OnPropertyChanged(nameof(ShowEmptyState));
                OnPropertyChanged(nameof(ShowLoadingState));
                OnPropertyChanged(nameof(ShowResultState));
            }
        }
    }

    public bool ShowEmptyState => !HasInputImage;
    public bool ShowLoadingState => HasInputImage && !HasResult;
    public bool ShowResultState => HasResult;

    private BitmapSource? _resultImage;
    public BitmapSource? ResultImage
    {
        get => _resultImage;
        set => SetProperty(ref _resultImage, value);
    }

    private string _resultSummary = string.Empty;
    public string ResultSummary
    {
        get => _resultSummary;
        set => SetProperty(ref _resultSummary, value);
    }

    private string _lastSavedPath = string.Empty;
    public string LastSavedPath
    {
        get => _lastSavedPath;
        set => SetProperty(ref _lastSavedPath, value);
    }
    #endregion

    #region Commands
    public ICommand BrowseCommand { get; }
    public ICommand PasteClipboardCommand { get; }
    public ICommand ResizeCommand { get; }
    public ICommand SaveAsCommand { get; }
    public ICommand SavePanPortalJpegCommand { get; }
    public ICommand SavePanCardBlueJpegCommand { get; }
    public ICommand SaveTransparentPngCommand { get; }
    public ICommand CopyResultCommand { get; }
    public ICommand PrintCommand { get; }
    public ICommand ResetCommand { get; }
    public ICommand SetBackgroundModeCommand { get; }
    public ICommand SetPreviewBackgroundModeCommand { get; }
    public ICommand SetInkColorModeCommand { get; }

    public ICommand IncrementWidthCommand { get; }
    public ICommand DecrementWidthCommand { get; }
    public ICommand IncrementHeightCommand { get; }
    public ICommand DecrementHeightCommand { get; }
    public ICommand IncrementSizeCommand { get; }
    public ICommand DecrementSizeCommand { get; }
    public ICommand IncrementMinSizeCommand { get; }
    public ICommand DecrementMinSizeCommand { get; }
    public ICommand QuickPresetCommand { get; }

    public Func<string, string, string?>? RequestSaveFile { get; set; }
    public Action? RequestClose { get; set; }
    #endregion

    public SignatureResizeViewModel(string initialPath = "")
    {
        var imgComp = new ImageCompressor();
        _resizeService = new SignatureResizeService(imgComp);

        SelectedPreset = Presets[0];

        BrowseCommand = new RelayCommand(_ => BrowseFile());
        PasteClipboardCommand = new RelayCommand(_ => PasteFromClipboard());
        ResizeCommand = new RelayCommand(async _ => await ExecuteResizeAsync(), _ => CanResize);
        SaveAsCommand = new RelayCommand(_ => SaveAs(), _ => HasResult && _outputBytes != null);
        SavePanPortalJpegCommand = new RelayCommand(async _ => await SavePanPortalJpegAsync(), _ => HasInputImage);
        SavePanCardBlueJpegCommand = new RelayCommand(async _ => await SavePanCardBlueJpegAsync(), _ => HasInputImage);
        SaveTransparentPngCommand = new RelayCommand(async _ => await SaveTransparentPngAsync(), _ => HasInputImage);
        CopyResultCommand = new RelayCommand(_ => CopyResultToClipboard(), _ => HasResult && ResultImage != null);
        PrintCommand = new RelayCommand(_ => PrintResult(), _ => HasResult && ResultImage != null);
        ResetCommand = new RelayCommand(_ => Reset());
        QuickPresetCommand = new RelayCommand(p => ApplyQuickPreset(p?.ToString()));
        SetBackgroundModeCommand = new RelayCommand(p => BackgroundMode = p?.ToString() ?? "White");
        SetPreviewBackgroundModeCommand = new RelayCommand(p => PreviewBackgroundMode = p?.ToString() ?? "Auto");
        SetInkColorModeCommand = new RelayCommand(p => InkColorMode = p?.ToString() ?? "Black");

        IncrementWidthCommand = new RelayCommand(_ => IncrementWidth());
        DecrementWidthCommand = new RelayCommand(_ => DecrementWidth());
        IncrementHeightCommand = new RelayCommand(_ => IncrementHeight());
        DecrementHeightCommand = new RelayCommand(_ => DecrementHeight());
        IncrementSizeCommand = new RelayCommand(_ => IncrementSize());
        DecrementSizeCommand = new RelayCommand(_ => DecrementSize());
        IncrementMinSizeCommand = new RelayCommand(_ => IncrementMinSize());
        DecrementMinSizeCommand = new RelayCommand(_ => DecrementMinSize());

        if (!string.IsNullOrWhiteSpace(initialPath) && File.Exists(initialPath))
        {
            LoadFile(initialPath);
        }
    }

    public void LoadFile(string path)
    {
        if (!File.Exists(path)) return;

        try
        {
            FilePath = path;
            FileName = Path.GetFileName(path);
            _inputBytes = File.ReadAllBytes(path);
            OriginalFileSizeBytes = _inputBytes.Length;

            InputPreviewImage = SignatureResizeService.BytesToBitmapSource(_inputBytes);
            if (InputPreviewImage != null)
            {
                OriginalWidth = InputPreviewImage.PixelWidth;
                OriginalHeight = InputPreviewImage.PixelHeight;
            }

            HasInputImage = true;
            HasResult = false;
            ResultImage = null;
            ResultSummary = string.Empty;

            OnPropertyChanged(nameof(OriginalFileSizeFormatted));
            OnPropertyChanged(nameof(OriginalDimensionsText));
            Log.Information("Signature loaded from {Path}, {W}x{H}, {Size} bytes", path, OriginalWidth, OriginalHeight, OriginalFileSizeBytes);

            TriggerAutoPreview(0);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to load signature file {Path}", path);
            WpfMessageBox.Show($"Could not open image:\n{ex.Message}", "Open Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    public void LoadFromBytes(byte[] bytes, string label = "Clipboard Image")
    {
        if (bytes == null || bytes.Length == 0) return;

        try
        {
            _inputBytes = bytes;
            FilePath = string.Empty;
            FileName = label;
            OriginalFileSizeBytes = bytes.Length;

            InputPreviewImage = SignatureResizeService.BytesToBitmapSource(bytes);
            if (InputPreviewImage != null)
            {
                OriginalWidth = InputPreviewImage.PixelWidth;
                OriginalHeight = InputPreviewImage.PixelHeight;
            }

            HasInputImage = true;
            HasResult = false;
            ResultImage = null;
            ResultSummary = string.Empty;

            OnPropertyChanged(nameof(OriginalFileSizeFormatted));
            OnPropertyChanged(nameof(OriginalDimensionsText));

            TriggerAutoPreview(0);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to load image from bytes");
        }
    }

    private void BrowseFile()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select Candidate Signature or Photo",
            Filter = "All Supported Images (*.jpg;*.jpeg;*.jpe;*.jfif;*.png;*.webp;*.bmp;*.gif;*.tiff;*.tif)|*.jpg;*.jpeg;*.jpe;*.jfif;*.png;*.webp;*.bmp;*.gif;*.tiff;*.tif|JPEG Images (*.jpg;*.jpeg;*.jfif;*.jpe)|*.jpg;*.jpeg;*.jfif;*.jpe|PNG Images (*.png)|*.png|WebP Images (*.webp)|*.webp|BMP Images (*.bmp)|*.bmp|TIFF Images (*.tiff;*.tif)|*.tiff;*.tif|All Files (*.*)|*.*"
        };

        if (dlg.ShowDialog() == true)
        {
            LoadFile(dlg.FileName);
        }
    }

    private void PasteFromClipboard()
    {
        try
        {
            string? tempPath = ClipboardHelper.SaveClipboardImageToFile();
            if (!string.IsNullOrEmpty(tempPath) && File.Exists(tempPath))
            {
                LoadFile(tempPath);
                FileName = "Clipboard_Signature.png";
                return;
            }

            WpfMessageBox.Show("No image found in clipboard.\n\nPlease copy an image or take a screenshot first.", "Clipboard Empty", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to paste image from clipboard");
            WpfMessageBox.Show($"Failed to paste image:\n{ex.Message}", "Paste Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private CancellationTokenSource? _autoPreviewCts;

    /// <summary>
    /// Debounces and triggers automatic live preview rendering in background.
    /// </summary>
    public void TriggerAutoPreview(int delayMs = 120)
    {
        if (_inputBytes == null || _inputBytes.Length == 0) return;

        _autoPreviewCts?.Cancel();
        _autoPreviewCts?.Dispose();
        _autoPreviewCts = new CancellationTokenSource();
        var token = _autoPreviewCts.Token;

        Task.Run(async () =>
        {
            if (delayMs > 0)
            {
                try { await Task.Delay(delayMs, token); } catch { return; }
            }
            if (token.IsCancellationRequested) return;

            var app = System.Windows.Application.Current;
            if (app?.Dispatcher != null && !app.Dispatcher.HasShutdownStarted)
            {
                try
                {
                    await app.Dispatcher.InvokeAsync(async () =>
                    {
                        if (token.IsCancellationRequested) return;
                        await ExecuteResizeAsync(isManualSave: false);
                    });
                }
                catch (Exception ex)
                {
                    Log.Debug(ex, "Dispatcher invoke cancelled or interrupted in TriggerAutoPreview");
                }
            }
            else
            {
                try
                {
                    await ExecuteResizeAsync(isManualSave: false);
                }
                catch (Exception ex)
                {
                    Log.Debug(ex, "ExecuteResizeAsync cancelled or interrupted in TriggerAutoPreview");
                }
            }
        }, token);
    }

    public async Task ExecuteResizeAsync(bool isManualSave = true)
    {
        if (_inputBytes == null || _inputBytes.Length == 0) return;

        IsProcessing = true;
        ProgressText = isManualSave ? "Resizing & optimizing signature..." : "Rendering live preview...";
        if (isManualSave)
        {
            ResultSummary = string.Empty;
        }

        try
        {
            int targetW = EffectiveWidthPx;
            int targetH = EffectiveHeightPx;
            long targetBytes = (long)TargetSizeKB * 1024;
            long minBytes = (long)MinSizeKB * 1024;

            SixLabors.ImageSharp.Color? customBg = BackgroundMode == "PanCardBlue" ? SignatureResizeService.PanCardBlue : null;

            var result = await _resizeService.ResizeSignatureFromBytesAsync(
                inputBytes: _inputBytes,
                targetWidthPx: targetW,
                targetHeightPx: targetH,
                targetBytes: targetBytes,
                minBytes: minBytes,
                cleanBackground: CleanBackground,
                autoCrop: AutoCrop,
                maintainAspectRatio: MaintainAspectRatio,
                outputExtension: SelectedOutputFormat,
                dpi: SelectedDpi,
                convertBlueToBlack: ConvertBlueToBlack,
                transparentBackground: TransparentBackground,
                customBackgroundColor: customBg,
                inkColor: InkColorMode);

            if (result.Success && result.OutputBytes != null)
            {
                _outputBytes = result.OutputBytes;
                ResultImage = SignatureResizeService.BytesToBitmapSource(_outputBytes);
                HasResult = true;

                double pct = result.OriginalSizeBytes > 0
                    ? 100.0 * (1.0 - (double)result.OutputSizeBytes / result.OriginalSizeBytes)
                    : 0;

                string unitInfo = IsPixelMode
                    ? $"{result.Width} × {result.Height} px"
                    : $"{WidthCm:F1} × {HeightCm:F1} cm ({result.Width} × {result.Height} px)";

                string bgTag = TransparentBackground 
                    ? " • ✨ Transparent (No White Box)" 
                    : (BackgroundMode == "PanCardBlue" ? " • 💳 PAN Card Blue (No White Box)" : " • ⚪ Crisp White BG");

                string inkTag = InkColorMode switch
                {
                    "RoyalBlue" or "Blue" => " • 🖊️ Royal Blue Ink",
                    "NavyBlue" => " • 🖋️ Navy Blue Ink",
                    "Original" => " • 🎨 Original Ink",
                    _ => " • ⬛ Pitch Black Ink"
                };

                if (isManualSave)
                {
                    ResultSummary = $"✅ Ready! {unitInfo} • {result.OutputSizeFormatted} ({pct:F1}% saved){bgTag}{inkTag}";

                    // Auto-save alongside source file if source is on disk
                    if (!string.IsNullOrWhiteSpace(FilePath) && File.Exists(FilePath))
                    {
                        try
                        {
                            string dir = Path.GetDirectoryName(FilePath)!;
                            string nameNoExt = Path.GetFileNameWithoutExtension(FilePath);
                            string autoPath = Path.Combine(dir, $"{nameNoExt}_resized{SelectedOutputFormat}");
                            File.WriteAllBytes(autoPath, _outputBytes);
                            LastSavedPath = autoPath;
                            OutputHistoryService.Instance.Record(autoPath, "Signature Resizer", result.OriginalSizeBytes, result.OutputSizeBytes);
                            ResultSummary += $"\nSaved: {Path.GetFileName(autoPath)}";
                        }
                        catch { }
                    }
                }
                else
                {
                    ResultSummary = $"⚡ Live Preview: {unitInfo} • {result.OutputSizeFormatted} ({pct:F1}% saved){bgTag}{inkTag}";
                }
            }
            else
            {
                if (isManualSave)
                {
                    ResultSummary = $"❌ Failed: {result.Message}";
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error in ExecuteResizeAsync");
            if (isManualSave)
            {
                ResultSummary = $"❌ Error: {ex.Message}";
            }
        }
        finally
        {
            IsProcessing = false;
            ProgressText = string.Empty;
        }
    }

    public async Task SavePanPortalJpegAsync()
    {
        if (_inputBytes == null || _inputBytes.Length == 0) return;

        IsProcessing = true;
        ProgressText = "Generating NSDL/UTIITSL Official PAN Card Upload JPEG (<50 KB, 200 DPI)...";

        try
        {
            var result = await _resizeService.ResizePanCardSignatureAsync(
                _inputBytes,
                transparentBackground: false,
                targetBytes: 30 * 1024,
                minBytes: 10 * 1024,
                customBackgroundColor: null,
                inkColor: InkColorMode);

            if (result.Success && result.OutputBytes != null)
            {
                _outputBytes = result.OutputBytes;
                ResultImage = SignatureResizeService.BytesToBitmapSource(_outputBytes);
                HasResult = true;
                ResultSummary = $"✅ NSDL/UTI Portal Ready! 2 × 4.5 cm (354 × 157 px @ 200 DPI) • {result.OutputSizeFormatted} (Portal Limit: <50 KB)";

                string defaultName = string.IsNullOrWhiteSpace(FilePath)
                    ? "PAN_Signature_NSDL_200DPI.jpg"
                    : $"{Path.GetFileNameWithoutExtension(FilePath)}_PAN_200DPI.jpg";

                string? savePath = RequestSaveFile?.Invoke("JPEG Image (*.jpg)|*.jpg", defaultName);
                if (string.IsNullOrWhiteSpace(savePath))
                {
                    var dlg = new Microsoft.Win32.SaveFileDialog
                    {
                        Title = "Save Official PAN Portal Signature (NSDL / UTIITSL)",
                        FileName = defaultName,
                        Filter = "JPEG Image (*.jpg)|*.jpg|All Files (*.*)|*.*"
                    };
                    if (dlg.ShowDialog() == true) savePath = dlg.FileName;
                }

                if (!string.IsNullOrWhiteSpace(savePath))
                {
                    File.WriteAllBytes(savePath, result.OutputBytes);
                    LastSavedPath = savePath;
                    OutputHistoryService.Instance.Record(savePath, "PAN Signature Resizer", result.OriginalSizeBytes, result.OutputSizeBytes);
                    WpfMessageBox.Show($"✅ Official PAN Portal Signature saved successfully!\n\nFile: {Path.GetFileName(savePath)}\nSize: {result.OutputSizeFormatted} (Meets <50 KB limit)\nResolution: 200 DPI (2 × 4.5 cm)\nReady to upload to NSDL / UTIITSL without errors!", "PAN Signature Saved", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            else
            {
                ResultSummary = $"❌ Failed: {result.Message}";
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error in SavePanPortalJpegAsync");
            ResultSummary = $"❌ Error: {ex.Message}";
        }
        finally
        {
            IsProcessing = false;
            ProgressText = string.Empty;
        }
    }

    public async Task SavePanCardBlueJpegAsync()
    {
        if (_inputBytes == null || _inputBytes.Length == 0) return;

        IsProcessing = true;
        ProgressText = "Generating PAN Card Blue Matched JPEG (<50 KB, 200 DPI, No White Box)...";

        try
        {
            var result = await _resizeService.ResizePanCardSignatureAsync(
                _inputBytes,
                transparentBackground: false,
                targetBytes: 30 * 1024,
                minBytes: 10 * 1024,
                customBackgroundColor: SignatureResizeService.PanCardBlue,
                inkColor: InkColorMode);

            if (result.Success && result.OutputBytes != null)
            {
                _outputBytes = result.OutputBytes;
                ResultImage = SignatureResizeService.BytesToBitmapSource(_outputBytes);
                HasResult = true;
                BackgroundMode = "PanCardBlue";
                PreviewBackgroundMode = "PanCardBlue";
                ResultSummary = $"✅ PAN Card Blue Matched JPEG Ready! 2 × 4.5 cm (354 × 157 px @ 200 DPI) • {result.OutputSizeFormatted} (Zero White Box — Blends seamlessly on blue PAN card)";

                string defaultName = string.IsNullOrWhiteSpace(FilePath)
                    ? "PAN_Signature_BlueMatched_200DPI.jpg"
                    : $"{Path.GetFileNameWithoutExtension(FilePath)}_PAN_BlueMatched_200DPI.jpg";

                string? savePath = RequestSaveFile?.Invoke("JPEG Image (*.jpg)|*.jpg", defaultName);
                if (string.IsNullOrWhiteSpace(savePath))
                {
                    var dlg = new Microsoft.Win32.SaveFileDialog
                    {
                        Title = "Save PAN Card Matched Blue JPEG (No White Box)",
                        FileName = defaultName,
                        Filter = "JPEG Image (*.jpg)|*.jpg|All Files (*.*)|*.*"
                    };
                    if (dlg.ShowDialog() == true) savePath = dlg.FileName;
                }

                if (!string.IsNullOrWhiteSpace(savePath))
                {
                    File.WriteAllBytes(savePath, result.OutputBytes);
                    LastSavedPath = savePath;
                    OutputHistoryService.Instance.Record(savePath, "PAN Signature Blue JPEG", result.OriginalSizeBytes, result.OutputSizeBytes);
                    WpfMessageBox.Show($"✅ PAN Card Matched Blue JPEG saved successfully!\n\nFile: {Path.GetFileName(savePath)}\nBackground: Official PAN Card Blue (#DCE8F5)\nSize: {result.OutputSizeFormatted} (<50 KB)\nResolution: 200 DPI (2 × 4.5 cm)\n\nZero white background box! Blends perfectly into blue PAN card designs even on sites requiring .JPG format!", "PAN Signature Saved", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            else
            {
                ResultSummary = $"❌ Failed: {result.Message}";
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error in SavePanCardBlueJpegAsync");
            ResultSummary = $"❌ Error: {ex.Message}";
        }
        finally
        {
            IsProcessing = false;
            ProgressText = string.Empty;
        }
    }

    public async Task SaveTransparentPngAsync()
    {
        if (_inputBytes == null || _inputBytes.Length == 0) return;

        IsProcessing = true;
        ProgressText = "Removing paper background & generating transparent PNG...";

        try
        {
            var result = await _resizeService.ResizePanCardSignatureAsync(
                _inputBytes,
                transparentBackground: true,
                targetBytes: 50 * 1024,
                minBytes: 0,
                customBackgroundColor: null,
                inkColor: InkColorMode);

            if (result.Success && result.OutputBytes != null)
            {
                _outputBytes = result.OutputBytes;
                ResultImage = SignatureResizeService.BytesToBitmapSource(_outputBytes);
                HasResult = true;
                TransparentBackground = true;
                SelectedOutputFormat = ".png";
                ResultSummary = $"✨ Transparent Signature Ready! Background 100% removed (No white box) • {result.OutputSizeFormatted}";

                string defaultName = string.IsNullOrWhiteSpace(FilePath)
                    ? "Transparent_Signature_NoBG.png"
                    : $"{Path.GetFileNameWithoutExtension(FilePath)}_transparent.png";

                string? savePath = RequestSaveFile?.Invoke("PNG Image (*.png)|*.png", defaultName);
                if (string.IsNullOrWhiteSpace(savePath))
                {
                    var dlg = new Microsoft.Win32.SaveFileDialog
                    {
                        Title = "Save Transparent Signature (No Background)",
                        FileName = defaultName,
                        Filter = "PNG Image (*.png)|*.png|All Files (*.*)|*.*"
                    };
                    if (dlg.ShowDialog() == true) savePath = dlg.FileName;
                }

                if (!string.IsNullOrWhiteSpace(savePath))
                {
                    File.WriteAllBytes(savePath, result.OutputBytes);
                    LastSavedPath = savePath;
                    OutputHistoryService.Instance.Record(savePath, "Transparent Signature", result.OriginalSizeBytes, result.OutputSizeBytes);
                    WpfMessageBox.Show($"✨ Transparent signature saved successfully!\n\nFile: {Path.GetFileName(savePath)}\nBackground: 100% Transparent (Zero white box)\nInk: Pitch-black enhanced ink\n\nPerfect for printing directly onto PAN cards, PVC cards, and certificates!", "Transparent Signature Saved", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            else
            {
                ResultSummary = $"❌ Failed: {result.Message}";
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error in SaveTransparentPngAsync");
            ResultSummary = $"❌ Error: {ex.Message}";
        }
        finally
        {
            IsProcessing = false;
            ProgressText = string.Empty;
        }
    }

    private void SaveAs()
    {
        if (_outputBytes == null || _outputBytes.Length == 0) return;

        string defaultName = string.IsNullOrWhiteSpace(FilePath)
            ? $"Signature_Resized_{EffectiveWidthPx}x{EffectiveHeightPx}{SelectedOutputFormat}"
            : $"{Path.GetFileNameWithoutExtension(FilePath)}_resized{SelectedOutputFormat}";

        string filter = SelectedOutputFormat == ".png"
            ? "PNG Image (*.png)|*.png|All Files (*.*)|*.*"
            : "JPEG Image (*.jpg;*.jpeg)|*.jpg;*.jpeg|All Files (*.*)|*.*";

        string? outputPath = RequestSaveFile?.Invoke(filter, defaultName);
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            // Fallback: Use standard SaveFileDialog
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Save Resized Signature",
                FileName = defaultName,
                Filter = filter
            };
            if (dlg.ShowDialog() == true)
            {
                outputPath = dlg.FileName;
            }
        }

        if (!string.IsNullOrWhiteSpace(outputPath))
        {
            try
            {
                File.WriteAllBytes(outputPath, _outputBytes);
                LastSavedPath = outputPath;
                OutputHistoryService.Instance.Record(outputPath, "Signature Resizer", OriginalFileSizeBytes, _outputBytes.Length);
                WpfMessageBox.Show($"Signature saved successfully to:\n{outputPath}", "Saved", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to save signature to {Path}", outputPath);
                WpfMessageBox.Show($"Failed to save:\n{ex.Message}", "Save Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private void CopyResultToClipboard()
    {
        if (ResultImage == null) return;

        try
        {
            bool isTransparent = TransparentBackground || SelectedOutputFormat == ".png";
            bool copied;

            if (isTransparent)
            {
                copied = ClipboardHelper.CopyTransparentBitmapToClipboard(ResultImage, _outputBytes);
            }
            else
            {
                var dataObj = new System.Windows.DataObject();
                dataObj.SetImage(ResultImage);
                if (_outputBytes != null)
                {
                    dataObj.SetData("PNG", new MemoryStream(_outputBytes), false);
                }
                System.Windows.Clipboard.SetDataObject(dataObj, true);
                copied = true;
            }

            if (copied)
            {
                string msg = isTransparent
                    ? "✨ Pure handwriting signature copied to clipboard (100% alpha transparency)!\n\nPress Ctrl+V in Photoshop, Word, Paint, CorelDRAW, or PVC card software — NO white background box will appear! Only the isolated handwriting ink fits perfectly onto any document or blue PAN card."
                    : (BackgroundMode == "PanCardBlue"
                        ? "💳 Signature copied with PAN Card Blue matched background!\n\nReady to paste onto PAN card forms and PVC card software without white edges."
                        : "✅ Resized signature copied to clipboard!\n\nYou can paste it directly into portals or WhatsApp.");
                WpfMessageBox.Show(msg, "Copied to Clipboard", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to copy result to clipboard");
        }
    }

    private void PrintResult()
    {
        if (ResultImage == null) return;

        try
        {
            PrintService.PrintImageDirect(ResultImage, "Resized_Signature");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to print resized signature");
        }
    }

    #region Stepper Methods
    private void IncrementWidth()
    {
        if (IsPixelMode)
            WidthPx = Math.Min(4000, WidthPx + 10);
        else
            WidthCm = Math.Round(Math.Min(50.0, WidthCm + 0.1), 2);
    }

    private void DecrementWidth()
    {
        if (IsPixelMode)
            WidthPx = Math.Max(10, WidthPx - 10);
        else
            WidthCm = Math.Round(Math.Max(0.5, WidthCm - 0.1), 2);
    }

    private void IncrementHeight()
    {
        if (IsPixelMode)
            HeightPx = Math.Min(4000, HeightPx + 10);
        else
            HeightCm = Math.Round(Math.Min(50.0, HeightCm + 0.1), 2);
    }

    private void DecrementHeight()
    {
        if (IsPixelMode)
            HeightPx = Math.Max(10, HeightPx - 10);
        else
            HeightCm = Math.Round(Math.Max(0.5, HeightCm - 0.1), 2);
    }

    private void IncrementSize()
    {
        TargetSizeKB = Math.Min(10000, TargetSizeKB + 5);
    }

    private void DecrementSize()
    {
        TargetSizeKB = Math.Max(5, TargetSizeKB - 5);
    }

    private void IncrementMinSize()
    {
        MinSizeKB = Math.Min(TargetSizeKB, MinSizeKB + 5);
    }

    private void DecrementMinSize()
    {
        MinSizeKB = Math.Max(0, MinSizeKB - 5);
    }
    #endregion

    public void ApplyQuickPreset(string? key)
    {
        if (string.IsNullOrEmpty(key)) return;
        switch (key.ToUpperInvariant())
        {
            case "PAN":
            case "PANCARD":
            case "NSDL":
            case "UTI":
            case "PANSIGN":
                SelectedPreset = Presets.FirstOrDefault(p => p.Name.Contains("PAN Card Signature") || (p.Name.Contains("PAN Card") && !p.Name.Contains("Photo") && !p.Name.Contains("Blue"))) ?? Presets[1];
                break;
            case "PANBLUE":
            case "PAN_BLUE":
            case "BLUEPAN":
                SelectedPreset = Presets.FirstOrDefault(p => p.Name.Contains("PAN Card Blue BG")) ?? Presets[2];
                break;
            case "BLUE":
            case "BLUEINK":
            case "ROYALBLUE":
                SelectedPreset = Presets.FirstOrDefault(p => p.Name.Contains("Royal Blue Pen")) ?? Presets[3];
                InkColorMode = "RoyalBlue";
                break;
            case "PANPHOTO":
            case "PAN_PHOTO":
                SelectedPreset = Presets.FirstOrDefault(p => p.Name.Contains("PAN Card Photo")) ?? Presets[4];
                break;
            case "TRANSPARENT":
            case "NOBG":
            case "PNG":
                SelectedPreset = Presets.FirstOrDefault(p => p.Name.Contains("Transparent Signature")) ?? Presets[5];
                break;
            case "SSC":
            case "IBPS":
                SelectedPreset = Presets.FirstOrDefault(p => p.Name.Contains("SSC / IBPS")) ?? Presets[6];
                break;
            case "UPSC":
                SelectedPreset = Presets.FirstOrDefault(p => p.Name.Contains("UPSC Signature")) ?? Presets[8];
                break;
            case "PSC":
                SelectedPreset = Presets.FirstOrDefault(p => p.Name.Contains("State PSC")) ?? Presets[7];
                break;
            case "THUMB":
                SelectedPreset = Presets.FirstOrDefault(p => p.Name.Contains("Thumb")) ?? Presets[10];
                break;
        }
    }

    private void Reset()
    {
        _inputBytes = null;
        _outputBytes = null;
        FilePath = string.Empty;
        FileName = "No file selected";
        OriginalFileSizeBytes = 0;
        OriginalWidth = 0;
        OriginalHeight = 0;
        InputPreviewImage = null;
        ResultImage = null;
        HasInputImage = false;
        HasResult = false;
        ResultSummary = string.Empty;
        LastSavedPath = string.Empty;

        // Reset to default settings (preserving aspect ratio so signatures are never squeezed)
        IsPixelMode = true;
        WidthPx = 140;
        HeightPx = 60;
        WidthCm = 3.5;
        HeightCm = 1.5;
        TargetSizeKB = 20;
        MinSizeKB = 0;
        SelectedDpi = 300;
        CleanBackground = true;
        ConvertBlueToBlack = true;
        InkColorMode = "Black";
        AutoCrop = true;
        MaintainAspectRatio = true;
        TransparentBackground = false;
        SelectedOutputFormat = ".jpg";
        SelectedPreset = Presets[0];
    }
}
