using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
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

public class PassportStudioViewModel : ViewModelBase
{
    private readonly PassportStudioService _studioService = PassportStudioService.Instance;
    private Bitmap? _loadedOriginalBmp;
    private Bitmap? _renderedSheetBmp;

    #region Photo Source & Dimensions
    private string _filePath = string.Empty;
    public string FilePath
    {
        get => _filePath;
        set => SetProperty(ref _filePath, value);
    }

    private string _fileName = "No photo selected";
    public string FileName
    {
        get => _fileName;
        set => SetProperty(ref _fileName, value);
    }

    private bool _hasPhoto;
    public bool HasPhoto
    {
        get => _hasPhoto;
        set
        {
            if (SetProperty(ref _hasPhoto, value))
            {
                OnPropertyChanged(nameof(HasUnsavedWork));
            }
        }
    }

    private bool _isSavedOrPrinted;
    public bool HasUnsavedWork => HasPhoto && !_isSavedOrPrinted;

    private BitmapSource? _photoPreviewImage;
    public BitmapSource? PhotoPreviewImage
    {
        get => _photoPreviewImage;
        set => SetProperty(ref _photoPreviewImage, value);
    }

    private Bitmap? _cachedAiCutoutBmp;
    private CancellationTokenSource? _aiSegCts;

    private bool _isAiSegmenting;
    public bool IsAiSegmenting
    {
        get => _isAiSegmenting;
        set
        {
            if (SetProperty(ref _isAiSegmenting, value))
            {
                OnPropertyChanged(nameof(AiStatusIcon));
                OnPropertyChanged(nameof(AiStatusText));
            }
        }
    }

    private bool _isAiCutoutActive;
    public bool IsAiCutoutActive
    {
        get => _isAiCutoutActive;
        set
        {
            if (SetProperty(ref _isAiCutoutActive, value))
            {
                OnPropertyChanged(nameof(AiStatusIcon));
                OnPropertyChanged(nameof(AiStatusText));
            }
        }
    }

    private string _aiStatusMessage = string.Empty;
    public string AiStatusMessage
    {
        get => _aiStatusMessage;
        set
        {
            if (SetProperty(ref _aiStatusMessage, value))
            {
                OnPropertyChanged(nameof(AiStatusIcon));
                OnPropertyChanged(nameof(AiStatusText));
            }
        }
    }

    public string AiStatusIcon => IsAiSegmenting ? "🧠" : (IsAiCutoutActive ? "✨" : "⚡");
    public string AiStatusText => IsAiSegmenting
        ? "AI analyzing portrait & hair edges..."
        : (IsAiCutoutActive ? "AI Smart Background Active (Hair & Edge Perfect)" : "Adaptive Edge Detection Active");
    #endregion

    #region Studio Lighting & Background
    private StudioBackgroundType _backgroundType = StudioBackgroundType.StudioWhite;
    public StudioBackgroundType BackgroundType
    {
        get => _backgroundType;
        set
        {
            if (SetProperty(ref _backgroundType, value))
            {
                OnPropertyChanged(nameof(IsOriginalBg));
                OnPropertyChanged(nameof(IsWhiteBg));
                OnPropertyChanged(nameof(IsSkyBlueBg));
                OnPropertyChanged(nameof(IsRoyalBlueBg));
                OnPropertyChanged(nameof(IsSoftGrayBg));
                OnPropertyChanged(nameof(IsCrimsonRedBg));
                OnPropertyChanged(nameof(IsLightGreenBg));
                OnPropertyChanged(nameof(IsCreamBg));
                OnPropertyChanged(nameof(IsCustomBg));
                TriggerSheetUpdate();
            }
        }
    }

    public bool IsOriginalBg => _backgroundType == StudioBackgroundType.Original;
    public bool IsWhiteBg => _backgroundType == StudioBackgroundType.StudioWhite;
    public bool IsSkyBlueBg => _backgroundType == StudioBackgroundType.SkyBlue;
    public bool IsRoyalBlueBg => _backgroundType == StudioBackgroundType.RoyalBlue;
    public bool IsSoftGrayBg => _backgroundType == StudioBackgroundType.SoftGray;
    public bool IsCrimsonRedBg => _backgroundType == StudioBackgroundType.CrimsonRed;
    public bool IsLightGreenBg => _backgroundType == StudioBackgroundType.LightGreen;
    public bool IsCreamBg => _backgroundType == StudioBackgroundType.LightCream;
    public bool IsCustomBg => _backgroundType == StudioBackgroundType.CustomColor;

    private Color _customColor = Color.FromArgb(74, 144, 226); // Default Sky Blue
    public Color CustomColor
    {
        get => _customColor;
        set
        {
            if (SetProperty(ref _customColor, value))
            {
                OnPropertyChanged(nameof(SelectedColorHex));
                OnPropertyChanged(nameof(SelectedColorBrush));
                TriggerSheetUpdate();
            }
        }
    }

    public string SelectedColorHex => $"#{CustomColor.R:X2}{CustomColor.G:X2}{CustomColor.B:X2}";
    public System.Windows.Media.SolidColorBrush SelectedColorBrush =>
        new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(CustomColor.R, CustomColor.G, CustomColor.B));

    private float _backgroundTolerance = 45f;
    public float BackgroundTolerance
    {
        get => _backgroundTolerance;
        set
        {
            if (SetProperty(ref _backgroundTolerance, value))
            {
                TriggerSheetUpdate();
            }
        }
    }

    private bool _autoEnhance = true;
    public bool AutoEnhance
    {
        get => _autoEnhance;
        set { if (SetProperty(ref _autoEnhance, value)) TriggerSheetUpdate(); }
    }

    private float _brightness = 0f;
    public float Brightness
    {
        get => _brightness;
        set { if (SetProperty(ref _brightness, value)) TriggerSheetUpdate(); }
    }

    private float _contrast = 0f;
    public float Contrast
    {
        get => _contrast;
        set { if (SetProperty(ref _contrast, value)) TriggerSheetUpdate(); }
    }

    private float _skinWarmth = 0f;
    public float SkinWarmth
    {
        get => _skinWarmth;
        set { if (SetProperty(ref _skinWarmth, value)) TriggerSheetUpdate(); }
    }
    #endregion

    #region Sheet & Paper Settings
    private string _paperSize = "A4";
    public string PaperSize
    {
        get => _paperSize;
        set
        {
            if (SetProperty(ref _paperSize, value))
            {
                OnPropertyChanged(nameof(IsA4));
                OnPropertyChanged(nameof(Is4x6));
                UpdateMaxCopies();
                TriggerSheetUpdate();
            }
        }
    }

    public bool IsA4
    {
        get => PaperSize.Equals("A4", StringComparison.OrdinalIgnoreCase);
        set
        {
            if (value) PaperSize = "A4";
        }
    }

    public bool Is4x6
    {
        get => PaperSize.Equals("4x6", StringComparison.OrdinalIgnoreCase);
        set
        {
            if (value) PaperSize = "4x6";
        }
    }

    private bool _isLandscape = false;
    public bool IsLandscape
    {
        get => _isLandscape;
        set
        {
            if (SetProperty(ref _isLandscape, value))
            {
                OnPropertyChanged(nameof(IsPortrait));
                UpdateMaxCopies();
                TriggerSheetUpdate();
            }
        }
    }
    public bool IsPortrait
    {
        get => !_isLandscape;
        set
        {
            if (value)
                IsLandscape = false;
            else
                IsLandscape = true;
        }
    }

    private bool _isFitPageMode = true;
    public bool IsFitPageMode
    {
        get => _isFitPageMode;
        set
        {
            if (SetProperty(ref _isFitPageMode, value))
            {
                OnPropertyChanged(nameof(IsActualSizeMode));
            }
        }
    }
    public bool IsActualSizeMode
    {
        get => !_isFitPageMode;
        set => IsFitPageMode = !value;
    }

    private double _marginMm = 6.0;
    public double MarginMm
    {
        get => _marginMm;
        set
        {
            double safeVal = double.IsNaN(value) || double.IsInfinity(value) ? 6.0 : Math.Clamp(Math.Round(value, 1), 0.0, 50.0);
            if (SetProperty(ref _marginMm, safeVal)) { UpdateMaxCopies(); TriggerSheetUpdate(); }
        }
    }

    private string _topMarginMmText = "6";
    public string TopMarginMmText
    {
        get => _topMarginMmText;
        set
        {
            if (SetProperty(ref _topMarginMmText, value))
            {
                string normalized = (value ?? string.Empty).Trim().Replace(',', '.');
                if (double.TryParse(normalized, NumberStyles.Any, CultureInfo.InvariantCulture, out double parsed))
                {
                    double maxAllowed = MaxTopMarginMm > 0 ? MaxTopMarginMm : 250.0;
                    if (parsed >= 0.0 && parsed <= maxAllowed)
                    {
                        double safeVal = Math.Round(parsed, 1);
                        if (Math.Abs(_topMarginMm - safeVal) > 0.001)
                        {
                            _topMarginMm = safeVal;
                            OnPropertyChanged(nameof(TopMarginMm));
                            OnPropertyChanged(nameof(TopMarginCm));
                            OnPropertyChanged(nameof(TopMarginText));
                            UpdateMaxCopies();
                            TriggerSheetUpdate();
                        }
                    }
                }
            }
        }
    }

    private double _topMarginMm = 6.0;
    public double TopMarginMm
    {
        get => _topMarginMm;
        set
        {
            double maxAllowed = MaxTopMarginMm > 0 ? MaxTopMarginMm : 250.0;
            double safeVal = double.IsNaN(value) || double.IsInfinity(value) ? 6.0 : Math.Clamp(Math.Round(value, 1), 0.0, maxAllowed);
            if (SetProperty(ref _topMarginMm, safeVal))
            {
                _topMarginMmText = safeVal.ToString("0.#", CultureInfo.InvariantCulture);
                OnPropertyChanged(nameof(TopMarginMmText));
                OnPropertyChanged(nameof(TopMarginCm));
                OnPropertyChanged(nameof(TopMarginText));
                UpdateMaxCopies();
                TriggerSheetUpdate();
            }
        }
    }

    public double MaxTopMarginMm
    {
        get
        {
            bool is4x6 = PaperSize.Equals("4x6", StringComparison.OrdinalIgnoreCase);
            double sheetH = is4x6 ? 152.4 : 297.0;
            if (IsLandscape) sheetH = is4x6 ? 101.6 : 210.0;
            double photoH = PhotoHeightCm * 10.0;
            return Math.Max(0.0, Math.Round(sheetH - photoH - 3.0, 1));
        }
    }

    public double TopMarginCm => Math.Round(TopMarginMm / 10.0, 2);
    public string TopMarginText => $"{TopMarginMm:F1} mm ({TopMarginCm:F1} cm)";

    private double _gapMm = 3.0;
    public double GapMm
    {
        get => _gapMm;
        set
        {
            double safeVal = double.IsNaN(value) || double.IsInfinity(value) ? 3.0 : Math.Clamp(Math.Round(value, 1), 0.0, 50.0);
            if (SetProperty(ref _gapMm, safeVal)) { UpdateMaxCopies(); TriggerSheetUpdate(); }
        }
    }

    private int _borderThickness = 1;
    public int BorderThickness
    {
        get => _borderThickness;
        set
        {
            int safeVal = Math.Clamp(value, 0, 10);
            if (SetProperty(ref _borderThickness, safeVal)) TriggerSheetUpdate();
        }
    }

    private bool _isComboMode = false;
    public bool IsComboMode
    {
        get => _isComboMode;
        set { if (SetProperty(ref _isComboMode, value)) TriggerSheetUpdate(); }
    }
    #endregion

    #region Single Size Presets & Counts
    public ObservableCollection<string> SizePresets { get; } = new()
    {
        "Passport (3.5 × 4.5 cm)",
        "Stamp Size (2.5 × 3.0 cm)",
        "PAN Card (2.5 × 3.5 cm)",
        "US / Schengen Visa (5.0 × 5.0 cm)",
        "Custom Size"
    };

    private string _selectedSizePreset = "Passport (3.5 × 4.5 cm)";
    public string SelectedSizePreset
    {
        get => _selectedSizePreset;
        set
        {
            if (SetProperty(ref _selectedSizePreset, value))
            {
                ApplyPresetDimensions(value);
                UpdateMaxCopies();
                TriggerSheetUpdate();
            }
        }
    }

    private string _photoWidthCmText = "3.5";
    public string PhotoWidthCmText
    {
        get => _photoWidthCmText;
        set
        {
            if (SetProperty(ref _photoWidthCmText, value))
            {
                string normalized = (value ?? string.Empty).Trim().Replace(',', '.');
                if (double.TryParse(normalized, NumberStyles.Any, CultureInfo.InvariantCulture, out double parsed))
                {
                    if (parsed >= 0.5 && parsed <= 30.0)
                    {
                        double safeVal = Math.Round(parsed, 2);
                        if (Math.Abs(_photoWidthCm - safeVal) > 0.0001)
                        {
                            _photoWidthCm = safeVal;
                            OnPropertyChanged(nameof(PhotoWidthCm));
                            if (_selectedSizePreset != "Custom Size")
                            {
                                _selectedSizePreset = "Custom Size";
                                OnPropertyChanged(nameof(SelectedSizePreset));
                            }
                            UpdateMaxCopies();
                            TriggerSheetUpdate();
                        }
                    }
                }
            }
        }
    }

    private double _photoWidthCm = 3.5;
    public double PhotoWidthCm
    {
        get => _photoWidthCm;
        set
        {
            double safeVal = double.IsNaN(value) || double.IsInfinity(value) ? 3.5 : Math.Clamp(Math.Round(value, 2), 0.5, 30.0);
            if (SetProperty(ref _photoWidthCm, safeVal))
            {
                _photoWidthCmText = safeVal.ToString("0.##", CultureInfo.InvariantCulture);
                OnPropertyChanged(nameof(PhotoWidthCmText));
                UpdateMaxCopies();
                TriggerSheetUpdate();
            }
        }
    }

    private string _photoHeightCmText = "4.5";
    public string PhotoHeightCmText
    {
        get => _photoHeightCmText;
        set
        {
            if (SetProperty(ref _photoHeightCmText, value))
            {
                string normalized = (value ?? string.Empty).Trim().Replace(',', '.');
                if (double.TryParse(normalized, NumberStyles.Any, CultureInfo.InvariantCulture, out double parsed))
                {
                    if (parsed >= 0.5 && parsed <= 30.0)
                    {
                        double safeVal = Math.Round(parsed, 2);
                        if (Math.Abs(_photoHeightCm - safeVal) > 0.0001)
                        {
                            _photoHeightCm = safeVal;
                            OnPropertyChanged(nameof(PhotoHeightCm));
                            if (_selectedSizePreset != "Custom Size")
                            {
                                _selectedSizePreset = "Custom Size";
                                OnPropertyChanged(nameof(SelectedSizePreset));
                            }
                            UpdateMaxCopies();
                            TriggerSheetUpdate();
                        }
                    }
                }
            }
        }
    }

    private double _photoHeightCm = 4.5;
    public double PhotoHeightCm
    {
        get => _photoHeightCm;
        set
        {
            double safeVal = double.IsNaN(value) || double.IsInfinity(value) ? 4.5 : Math.Clamp(Math.Round(value, 2), 0.5, 30.0);
            if (SetProperty(ref _photoHeightCm, safeVal))
            {
                _photoHeightCmText = safeVal.ToString("0.##", CultureInfo.InvariantCulture);
                OnPropertyChanged(nameof(PhotoHeightCmText));
                UpdateMaxCopies();
                TriggerSheetUpdate();
            }
        }
    }

    private int _copiesCount = 8;
    public int CopiesCount
    {
        get => _copiesCount;
        set
        {
            int safeVal = Math.Clamp(value, 1, Math.Max(1, MaxCopies));
            if (SetProperty(ref _copiesCount, safeVal)) TriggerSheetUpdate();
        }
    }

    private int _maxCopies = 30;
    public int MaxCopies
    {
        get => _maxCopies;
        set => SetProperty(ref _maxCopies, value);
    }
    #endregion

    #region Multi-Size Combo Batches
    private int _comboBatch1Count = 8; // Passport 3.5x4.5
    public int ComboBatch1Count
    {
        get => _comboBatch1Count;
        set { if (SetProperty(ref _comboBatch1Count, Math.Max(0, value))) TriggerSheetUpdate(); }
    }

    private int _comboBatch2Count = 4; // Stamp 2.5x3.0
    public int ComboBatch2Count
    {
        get => _comboBatch2Count;
        set { if (SetProperty(ref _comboBatch2Count, Math.Max(0, value))) TriggerSheetUpdate(); }
    }

    private int _comboBatch3Count = 0; // PAN 2.5x3.5
    public int ComboBatch3Count
    {
        get => _comboBatch3Count;
        set { if (SetProperty(ref _comboBatch3Count, Math.Max(0, value))) TriggerSheetUpdate(); }
    }
    #endregion

    #region Live Sheet Preview & Status
    private BitmapSource? _sheetPreviewImage;
    public BitmapSource? SheetPreviewImage
    {
        get => _sheetPreviewImage;
        set => SetProperty(ref _sheetPreviewImage, value);
    }

    private string _sheetSummary = "Ready to generate sheet";
    public string SheetSummary
    {
        get => _sheetSummary;
        set => SetProperty(ref _sheetSummary, value);
    }

    private bool _isProcessing;
    public bool IsProcessing
    {
        get => _isProcessing;
        set => SetProperty(ref _isProcessing, value);
    }
    #endregion

    #region Single Photo Export (Govt / Online Uploads)
    private bool _isSingleExportDialogOpen;
    public bool IsSingleExportDialogOpen
    {
        get => _isSingleExportDialogOpen;
        set => SetProperty(ref _isSingleExportDialogOpen, value);
    }

    private int _singleExportPresetIndex = 0;
    public int SingleExportPresetIndex
    {
        get => _singleExportPresetIndex;
        set
        {
            if (SetProperty(ref _singleExportPresetIndex, value))
            {
                ApplySinglePreset(value);
                TriggerSingleExportPreviewUpdate();
            }
        }
    }

    public ObservableCollection<string> SinglePresets { get; } = new()
    {
        "🏛️ SSC / UPSC / Railways / IBPS (20–50 KB, 3.5×4.5 cm)",
        "💳 PAN Card (NSDL / UTI) (2.5 × 3.5 cm | 200 DPI | <50 KB)",
        "🌍 Passport Seva / Standard (50–100 KB, 3.5×4.5 cm)",
        "🛂 US / Schengen Visa (50–100 KB, 5.0×5.0 cm)",
        "⚙️ Custom Dimensions & Target KB"
    };

    private int _singleDpi = 300;
    public int SingleDpi
    {
        get => _singleDpi;
        set
        {
            if (SetProperty(ref _singleDpi, value))
                TriggerSingleExportPreviewUpdate();
        }
    }

    public ObservableCollection<int> SingleDpiOptions { get; } = new() { 200, 300 };

    private string _singleWidthCmText = "3.5";
    public string SingleWidthCmText
    {
        get => _singleWidthCmText;
        set
        {
            if (SetProperty(ref _singleWidthCmText, value))
            {
                string normalized = (value ?? string.Empty).Trim().Replace(',', '.');
                if (double.TryParse(normalized, NumberStyles.Any, CultureInfo.InvariantCulture, out double parsed))
                {
                    if (parsed >= 1.0 && parsed <= 20.0)
                    {
                        double safeVal = Math.Round(parsed, 2);
                        if (Math.Abs(_singleWidthCm - safeVal) > 0.0001)
                        {
                            _singleWidthCm = safeVal;
                            OnPropertyChanged(nameof(SingleWidthCm));
                            if (_singleExportPresetIndex != 4)
                            {
                                _singleExportPresetIndex = 4;
                                OnPropertyChanged(nameof(SingleExportPresetIndex));
                            }
                            TriggerSingleExportPreviewUpdate();
                        }
                    }
                }
            }
        }
    }

    private double _singleWidthCm = 3.5;
    public double SingleWidthCm
    {
        get => _singleWidthCm;
        set
        {
            double safeVal = double.IsNaN(value) || double.IsInfinity(value) ? 3.5 : Math.Clamp(Math.Round(value, 2), 1.0, 20.0);
            if (SetProperty(ref _singleWidthCm, safeVal))
            {
                _singleWidthCmText = safeVal.ToString("0.##", CultureInfo.InvariantCulture);
                OnPropertyChanged(nameof(SingleWidthCmText));
                TriggerSingleExportPreviewUpdate();
            }
        }
    }

    private string _singleHeightCmText = "4.5";
    public string SingleHeightCmText
    {
        get => _singleHeightCmText;
        set
        {
            if (SetProperty(ref _singleHeightCmText, value))
            {
                string normalized = (value ?? string.Empty).Trim().Replace(',', '.');
                if (double.TryParse(normalized, NumberStyles.Any, CultureInfo.InvariantCulture, out double parsed))
                {
                    if (parsed >= 1.0 && parsed <= 20.0)
                    {
                        double safeVal = Math.Round(parsed, 2);
                        if (Math.Abs(_singleHeightCm - safeVal) > 0.0001)
                        {
                            _singleHeightCm = safeVal;
                            OnPropertyChanged(nameof(SingleHeightCm));
                            if (_singleExportPresetIndex != 4)
                            {
                                _singleExportPresetIndex = 4;
                                OnPropertyChanged(nameof(SingleExportPresetIndex));
                            }
                            TriggerSingleExportPreviewUpdate();
                        }
                    }
                }
            }
        }
    }

    private double _singleHeightCm = 4.5;
    public double SingleHeightCm
    {
        get => _singleHeightCm;
        set
        {
            double safeVal = double.IsNaN(value) || double.IsInfinity(value) ? 4.5 : Math.Clamp(Math.Round(value, 2), 1.0, 20.0);
            if (SetProperty(ref _singleHeightCm, safeVal))
            {
                _singleHeightCmText = safeVal.ToString("0.##", CultureInfo.InvariantCulture);
                OnPropertyChanged(nameof(SingleHeightCmText));
                TriggerSingleExportPreviewUpdate();
            }
        }
    }

    private int _singleMinKb = 20;
    public int SingleMinKb
    {
        get => _singleMinKb;
        set
        {
            if (SetProperty(ref _singleMinKb, Math.Max(1, value)))
                TriggerSingleExportPreviewUpdate();
        }
    }

    private int _singleMaxKb = 50;
    public int SingleMaxKb
    {
        get => _singleMaxKb;
        set
        {
            if (SetProperty(ref _singleMaxKb, Math.Max(2, value)))
                TriggerSingleExportPreviewUpdate();
        }
    }

    private bool _singleAddStamp = false;
    public bool SingleAddStamp
    {
        get => _singleAddStamp;
        set
        {
            if (SetProperty(ref _singleAddStamp, value))
                TriggerSingleExportPreviewUpdate();
        }
    }

    private string _singleCandidateName = string.Empty;
    public string SingleCandidateName
    {
        get => _singleCandidateName;
        set
        {
            if (SetProperty(ref _singleCandidateName, value))
                TriggerSingleExportPreviewUpdate();
        }
    }

    private string _singleDateOfPhoto = DateTime.Today.ToString("dd-MM-yyyy");
    public string SingleDateOfPhoto
    {
        get => _singleDateOfPhoto;
        set
        {
            if (SetProperty(ref _singleDateOfPhoto, value))
                TriggerSingleExportPreviewUpdate();
        }
    }

    private BitmapSource? _singleExportPreviewImage;
    public BitmapSource? SingleExportPreviewImage
    {
        get => _singleExportPreviewImage;
        set => SetProperty(ref _singleExportPreviewImage, value);
    }

    private string _singleExportStatusText = "Ready to export";
    public string SingleExportStatusText
    {
        get => _singleExportStatusText;
        set => SetProperty(ref _singleExportStatusText, value);
    }

    private bool _isSingleExportValid = true;
    public bool IsSingleExportValid
    {
        get => _isSingleExportValid;
        set => SetProperty(ref _isSingleExportValid, value);
    }

    private byte[]? _cachedSingleBytes;
    #endregion

    #region Commands
    public ICommand BrowsePhotoCommand { get; }
    public ICommand PasteClipboardCommand { get; }
    public ICommand RotateLeftCommand { get; }
    public ICommand RotateRightCommand { get; }
    public ICommand CropPhotoCommand { get; }
    public ICommand SelectBgCommand { get; }
    public ICommand PickCustomColorCommand { get; }
    public ICommand FillMaxCopiesCommand { get; }
    public ICommand ResetLightingCommand { get; }
    public ICommand SetPaperSizeCommand { get; }
    public ICommand SetOrientationCommand { get; }
    public ICommand SetCopiesCommand { get; }
    public ICommand SetViewModeCommand { get; }
    public ICommand PrintSheetCommand { get; }
    public ICommand SaveSheetPdfCommand { get; }
    public ICommand SaveSheetJpgCommand { get; }
    public ICommand SetTopMarginCommand { get; }
    public ICommand NudgeTopMarginCommand { get; }
    public ICommand OpenSingleExportCommand { get; }
    public ICommand OpenPanPhotoExportCommand { get; }
    public ICommand CloseSingleExportCommand { get; }
    public ICommand SaveSinglePhotoCommand { get; }
    public ICommand CopySinglePhotoCommand { get; }
    public ICommand SelectSinglePresetCommand { get; }

    public Func<string, string, string?>? RequestSaveFile { get; set; }
    #endregion

    private CancellationTokenSource? _renderCts;
    private readonly SemaphoreSlim _renderLock = new(1, 1);

    public static bool SuppressDialogsForTests { get; set; } = false;

    private static void SafeUiInvoke(Action action)
    {
        var app = System.Windows.Application.Current;
        if (app == null || app.Dispatcher == null || app.Dispatcher.HasShutdownStarted || app.Dispatcher.HasShutdownFinished)
        {
            action();
            return;
        }

        if (app.Dispatcher.CheckAccess())
        {
            action();
            return;
        }

        try
        {
            if (!app.Dispatcher.Thread.IsAlive)
            {
                action();
                return;
            }
            app.Dispatcher.BeginInvoke(action);
        }
        catch
        {
            action();
        }
    }

    private static void ShowAlert(string message, string caption, MessageBoxButton button, MessageBoxImage image)
    {
        if (SuppressDialogsForTests) return;
        if (System.Windows.Application.Current?.MainWindow == null && System.Windows.Application.Current?.Windows.Count == 0) return;
        WpfMessageBox.Show(message, caption, button, image);
    }

    public PassportStudioViewModel(string initialPath = "")
    {
        BrowsePhotoCommand = new RelayCommand(_ => BrowsePhoto());
        PasteClipboardCommand = new RelayCommand(_ => PasteFromClipboard());
        RotateLeftCommand = new RelayCommand(_ => RotatePhoto(false), _ => HasPhoto && !IsProcessing);
        RotateRightCommand = new RelayCommand(_ => RotatePhoto(true), _ => HasPhoto && !IsProcessing);
        CropPhotoCommand = new RelayCommand(_ => CropPhoto(), _ => HasPhoto && !IsProcessing);
        SelectBgCommand = new RelayCommand(p => SetBackgroundFromParameter(p?.ToString()));
        PickCustomColorCommand = new RelayCommand(_ => PickCustomColor());
        FillMaxCopiesCommand = new RelayCommand(_ => { CopiesCount = MaxCopies; });
        ResetLightingCommand = new RelayCommand(_ => ResetLighting());
        SetPaperSizeCommand = new RelayCommand(p => { if (p != null) PaperSize = p.ToString()!; });
        SetOrientationCommand = new RelayCommand(p => { IsLandscape = string.Equals(p?.ToString(), "Landscape", StringComparison.OrdinalIgnoreCase); });
        SetCopiesCommand = new RelayCommand(p =>
        {
            if (int.TryParse(p?.ToString(), out int c))
            {
                CopiesCount = Math.Clamp(c, 1, MaxCopies);
            }
        });
        SetViewModeCommand = new RelayCommand(p => { IsFitPageMode = string.Equals(p?.ToString(), "FitPage", StringComparison.OrdinalIgnoreCase); });
        SetTopMarginCommand = new RelayCommand(p =>
        {
            if (p != null && double.TryParse(p.ToString(), out double m))
            {
                TopMarginMm = m;
            }
        });
        NudgeTopMarginCommand = new RelayCommand(p =>
        {
            if (p != null && double.TryParse(p.ToString(), out double delta))
            {
                TopMarginMm += delta;
            }
        });
        PrintSheetCommand = new RelayCommand(_ => PrintSheet(), _ => HasPhoto && !IsProcessing);
        SaveSheetPdfCommand = new RelayCommand(async _ => await SaveSheetPdfAsync(), _ => HasPhoto && !IsProcessing);
        SaveSheetJpgCommand = new RelayCommand(async _ => await SaveSheetJpgAsync(), _ => HasPhoto && !IsProcessing);
        OpenSingleExportCommand = new RelayCommand(_ => OpenSingleExport(), _ => HasPhoto && !IsProcessing);
        OpenPanPhotoExportCommand = new RelayCommand(_ =>
        {
            OpenSingleExport();
            SingleExportPresetIndex = 1;
            ApplySinglePreset(1);
            TriggerSingleExportPreviewUpdate();
        }, _ => HasPhoto && !IsProcessing);
        CloseSingleExportCommand = new RelayCommand(_ => CloseSingleExport());
        SaveSinglePhotoCommand = new RelayCommand(async _ => await SaveSinglePhotoAsync(), _ => HasPhoto && !IsProcessing);
        CopySinglePhotoCommand = new RelayCommand(_ => CopySinglePhoto(), _ => HasPhoto && !IsProcessing);
        SelectSinglePresetCommand = new RelayCommand(p =>
        {
            if (p != null && int.TryParse(p.ToString(), out int idx))
            {
                SingleExportPresetIndex = idx;
            }
        });

        UpdateMaxCopies();

        if (!string.IsNullOrWhiteSpace(initialPath) && File.Exists(initialPath))
        {
            LoadPhoto(initialPath);
        }
    }

    public void LoadPhoto(string path)
    {
        try
        {
            if (!File.Exists(path)) return;

            var safeBmp = SmartSaver.Helpers.ImageHelper.LoadOrientedBitmap(path);

            _loadedOriginalBmp?.Dispose();
            _loadedOriginalBmp = safeBmp;

            FilePath = path;
            FileName = Path.GetFileName(path);
            _isSavedOrPrinted = false;
            HasPhoto = true;
            OnPropertyChanged(nameof(HasUnsavedWork));

            PhotoPreviewImage = BitmapToBitmapSource(_loadedOriginalBmp);

            lock (_renderLock)
            {
                _cachedAiCutoutBmp?.Dispose();
                _cachedAiCutoutBmp = null;
            }
            IsAiCutoutActive = false;

            TriggerSheetUpdate();
            TriggerAiSegmentationAsync(path);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to load photo in PassportStudio");
            ShowAlert($"Could not open photo:\n{ex.Message}", "Open Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void TriggerAiSegmentationAsync(string path)
    {
        _aiSegCts?.Cancel();
        _aiSegCts?.Dispose();
        _aiSegCts = new CancellationTokenSource();
        var token = _aiSegCts.Token;

        Task.Run(async () =>
        {
            try
            {
                if (_loadedOriginalBmp == null) return;
                Bitmap bmpClone;
                lock (_loadedOriginalBmp)
                {
                    bmpClone = (Bitmap)_loadedOriginalBmp.Clone();
                }

                SafeUiInvoke(() =>
                {
                    IsAiSegmenting = true;
                });

                Bitmap? cutout = await _studioService.SegmentPortraitAiAsync(bmpClone, path);
                bmpClone.Dispose();

                if (token.IsCancellationRequested)
                {
                    cutout?.Dispose();
                    return;
                }

                if (cutout != null)
                {
                    lock (_renderLock)
                    {
                        _cachedAiCutoutBmp?.Dispose();
                        _cachedAiCutoutBmp = cutout;
                    }

                    SafeUiInvoke(() =>
                    {
                        IsAiCutoutActive = true;
                    });

                    TriggerSheetUpdate();

                    if (IsSingleExportDialogOpen)
                    {
                        TriggerSingleExportPreviewUpdate();
                    }
                }
                else
                {
                    SafeUiInvoke(() =>
                    {
                        IsAiCutoutActive = false;
                    });
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Background AI segmentation completed with fallback");
                SafeUiInvoke(() =>
                {
                    IsAiCutoutActive = false;
                });
            }
            finally
            {
                SafeUiInvoke(() =>
                {
                    IsAiSegmenting = false;
                });
            }
        });
    }

    private void BrowsePhoto()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select Passport Photo",
            Filter = "All Supported Images (*.jpg;*.jpeg;*.jpe;*.jfif;*.png;*.webp;*.bmp;*.gif;*.tiff;*.tif)|*.jpg;*.jpeg;*.jpe;*.jfif;*.png;*.webp;*.bmp;*.gif;*.tiff;*.tif|JPEG Images (*.jpg;*.jpeg;*.jfif;*.jpe)|*.jpg;*.jpeg;*.jfif;*.jpe|PNG Images (*.png)|*.png|WebP Images (*.webp)|*.webp|BMP Images (*.bmp)|*.bmp|TIFF Images (*.tiff;*.tif)|*.tiff;*.tif|All Files (*.*)|*.*"
        };
        if (dlg.ShowDialog() == true)
        {
            LoadPhoto(dlg.FileName);
        }
    }

    private void PasteFromClipboard()
    {
        try
        {
            string? temp = ClipboardHelper.SaveClipboardImageToFile();
            if (!string.IsNullOrEmpty(temp) && File.Exists(temp))
            {
                LoadPhoto(temp);
                FileName = "Clipboard_Photo.png";
                return;
            }
            ShowAlert("No photo found in clipboard.", "Clipboard Empty", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to paste photo from clipboard");
        }
    }

    public void RotatePhoto(bool clockwise)
    {
        if (_loadedOriginalBmp == null) return;

        try
        {
            lock (_loadedOriginalBmp)
            {
                _loadedOriginalBmp.RotateFlip(clockwise ? RotateFlipType.Rotate90FlipNone : RotateFlipType.Rotate270FlipNone);
            }
            lock (_renderLock)
            {
                _cachedAiCutoutBmp?.RotateFlip(clockwise ? RotateFlipType.Rotate90FlipNone : RotateFlipType.Rotate270FlipNone);
            }

            // Intelligently swap custom slot dimensions to match rotated orientation
            double temp = PhotoWidthCm;
            PhotoWidthCm = PhotoHeightCm;
            PhotoHeightCm = temp;

            UpdateMaxCopies();

            PhotoPreviewImage = BitmapToBitmapSource(_loadedOriginalBmp);
            TriggerSheetUpdate();

            if (IsAiSegmenting)
            {
                TriggerAiSegmentationAsync(FilePath);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to rotate photo");
            ShowAlert($"Rotate failed: {ex.Message}", "Rotate Error", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    public void CropPhoto()
    {
        if (_loadedOriginalBmp == null) return;

        try
        {
            string tempFile = Path.Combine(Path.GetTempPath(), $"passport_crop_src_{Guid.NewGuid():N}.png");
            lock (_loadedOriginalBmp)
            {
                using var copy = new Bitmap(_loadedOriginalBmp);
                copy.Save(tempFile, ImageFormat.Png);
            }

            var owner = System.Windows.Application.Current?.Windows.OfType<Views.PassportStudioDialog>().FirstOrDefault(w => w.IsVisible)
                        ?? System.Windows.Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive)
                        ?? System.Windows.Application.Current?.MainWindow;

            string? croppedPath = Views.ImageCropDialog.ShowCropDialog(tempFile, owner, startInQuadMode: false, initialPreset: "passport");
            if (!string.IsNullOrEmpty(croppedPath) && File.Exists(croppedPath))
            {
                string originalName = FileName;
                LoadPhoto(croppedPath);
                if (!string.IsNullOrEmpty(originalName) && !originalName.StartsWith("passport_crop_"))
                {
                    FileName = $"{Path.GetFileNameWithoutExtension(originalName)}_cropped.png";
                }
            }

            try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to launch crop dialog in PassportStudio");
            ShowAlert($"Crop tool error:\n{ex.Message}", "Crop Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    public void PickCustomColor()
    {
        try
        {
            using var dlg = new System.Windows.Forms.ColorDialog
            {
                Color = CustomColor,
                FullOpen = true,
                AnyColor = true
            };
            if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                CustomColor = dlg.Color;
                BackgroundType = StudioBackgroundType.CustomColor;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to open ColorDialog");
        }
    }

    private void SetBackgroundFromParameter(string? param)
    {
        if (string.IsNullOrEmpty(param)) return;
        switch (param.ToLowerInvariant())
        {
            case "white":
                CustomColor = Color.FromArgb(255, 255, 255);
                BackgroundType = StudioBackgroundType.StudioWhite;
                break;
            case "blue":
            case "skyblue":
                CustomColor = Color.FromArgb(74, 144, 226);
                BackgroundType = StudioBackgroundType.SkyBlue;
                break;
            case "royalblue":
                CustomColor = Color.FromArgb(13, 71, 161);
                BackgroundType = StudioBackgroundType.RoyalBlue;
                break;
            case "gray":
                CustomColor = Color.FromArgb(224, 224, 224);
                BackgroundType = StudioBackgroundType.SoftGray;
                break;
            case "red":
            case "crimson":
                CustomColor = Color.FromArgb(183, 28, 28);
                BackgroundType = StudioBackgroundType.CrimsonRed;
                break;
            case "green":
                CustomColor = Color.FromArgb(165, 214, 167);
                BackgroundType = StudioBackgroundType.LightGreen;
                break;
            case "cream":
                CustomColor = Color.FromArgb(253, 251, 247);
                BackgroundType = StudioBackgroundType.LightCream;
                break;
            case "custom":
                PickCustomColor();
                break;
            default:
                BackgroundType = StudioBackgroundType.Original;
                break;
        }
    }

    private void ResetLighting()
    {
        AutoEnhance = true;
        Brightness = 0f;
        Contrast = 0f;
        SkinWarmth = 0f;
    }

    private void ApplyPresetDimensions(string preset)
    {
        if (preset.StartsWith("Passport"))
        {
            PhotoWidthCm = 3.5;
            PhotoHeightCm = 4.5;
        }
        else if (preset.StartsWith("Stamp"))
        {
            PhotoWidthCm = 2.5;
            PhotoHeightCm = 3.0;
        }
        else if (preset.StartsWith("PAN"))
        {
            PhotoWidthCm = 2.5;
            PhotoHeightCm = 3.5;
        }
        else if (preset.StartsWith("US"))
        {
            PhotoWidthCm = 5.0;
            PhotoHeightCm = 5.0;
        }
    }

    private void UpdateMaxCopies()
    {
        OnPropertyChanged(nameof(MaxTopMarginMm));
        if (_topMarginMm > MaxTopMarginMm && MaxTopMarginMm > 0)
        {
            _topMarginMm = MaxTopMarginMm;
            _topMarginMmText = _topMarginMm.ToString("0.#", CultureInfo.InvariantCulture);
            OnPropertyChanged(nameof(TopMarginMmText));
            OnPropertyChanged(nameof(TopMarginMm));
            OnPropertyChanged(nameof(TopMarginCm));
            OnPropertyChanged(nameof(TopMarginText));
        }
        var cap = _studioService.CalculateCapacity(PaperSize, PhotoWidthCm, PhotoHeightCm, GapMm, MarginMm, IsLandscape, TopMarginMm);
        MaxCopies = cap.total;
        if (CopiesCount > MaxCopies) CopiesCount = Math.Max(1, MaxCopies);
    }

    public void NormalizeDimensionTexts()
    {
        _photoWidthCmText = _photoWidthCm.ToString("0.##", CultureInfo.InvariantCulture);
        OnPropertyChanged(nameof(PhotoWidthCmText));
        _photoHeightCmText = _photoHeightCm.ToString("0.##", CultureInfo.InvariantCulture);
        OnPropertyChanged(nameof(PhotoHeightCmText));
        _topMarginMmText = _topMarginMm.ToString("0.#", CultureInfo.InvariantCulture);
        OnPropertyChanged(nameof(TopMarginMmText));
        _singleWidthCmText = _singleWidthCm.ToString("0.##", CultureInfo.InvariantCulture);
        OnPropertyChanged(nameof(SingleWidthCmText));
        _singleHeightCmText = _singleHeightCm.ToString("0.##", CultureInfo.InvariantCulture);
        OnPropertyChanged(nameof(SingleHeightCmText));
    }

    private void TriggerSheetUpdate()
    {
        if (!HasPhoto || _loadedOriginalBmp == null) return;

        _renderCts?.Cancel();
        _renderCts?.Dispose();
        _renderCts = new CancellationTokenSource();
        var token = _renderCts.Token;

        Task.Run(async () =>
        {
            try
            {
                // Debounce rapid slider events by 120ms
                await Task.Delay(120, token);
                if (token.IsCancellationRequested) return;

                await _renderLock.WaitAsync(token);
                try
                {
                    if (token.IsCancellationRequested || _loadedOriginalBmp == null) return;

                    var config = BuildCurrentConfig();
                    Bitmap? aiCutout = null;
                    if (_cachedAiCutoutBmp != null)
                    {
                        try { aiCutout = (Bitmap)_cachedAiCutoutBmp.Clone(); } catch { }
                    }
                    using var processedPortrait = _studioService.ProcessPortrait(_loadedOriginalBmp, config, aiCutout);
                    aiCutout?.Dispose();

                    if (token.IsCancellationRequested) return;

                    // Render lightweight 100 DPI sheet for 60fps real-time screen preview
                    var sheet = await _studioService.GenerateSheetAsync(processedPortrait, config, dpi: 100);

                    if (token.IsCancellationRequested)
                    {
                        sheet.Dispose();
                        return;
                    }

                    SafeUiInvoke(() =>
                    {
                        if (token.IsCancellationRequested)
                        {
                            sheet.Dispose();
                            return;
                        }

                        _renderedSheetBmp?.Dispose();
                        _renderedSheetBmp = sheet;
                        SheetPreviewImage = BitmapToBitmapSource(sheet);

                        int totalPhotos = config.Batches.Sum(b => b.Count);
                        string orientStr = IsLandscape ? "Landscape" : "Portrait";
                        SheetSummary = $"📄 {PaperSize} {orientStr} (300 DPI Print-Ready) • {totalPhotos} Photos Packed • Top Gap: {TopMarginMm:F1} mm • Gap: {GapMm:F1} mm • Border: {BorderThickness} px";
                        IsProcessing = false;
                    });
                }
                finally
                {
                    _renderLock.Release();
                }
            }
            catch (OperationCanceledException)
            {
                // Clean exit on debounce cancellation
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed in debounced TriggerSheetUpdate");
                SafeUiInvoke(() =>
                {
                    SheetSummary = $"Render Notice: {ex.Message}";
                    IsProcessing = false;
                });
            }
        });
    }

    private PassportSheetConfig BuildCurrentConfig()
    {
        var config = new PassportSheetConfig
        {
            PaperSize = PaperSize,
            IsLandscape = IsLandscape,
            MarginMm = MarginMm,
            TopMarginMm = TopMarginMm,
            GapMm = GapMm,
            BorderThicknessPx = BorderThickness,
            BorderColor = Color.FromArgb(170, 170, 170),
            BackgroundType = BackgroundType,
            CustomColor = CustomColor,
            BackgroundTolerance = BackgroundTolerance,
            Brightness = Brightness,
            Contrast = Contrast,
            Warmth = SkinWarmth,
            AutoEnhance = AutoEnhance
        };

        if (IsComboMode)
        {
            // If the photo is rotated into landscape (width > height), orient combo batches horizontally as well
            bool isLandscapePhoto = _loadedOriginalBmp != null && _loadedOriginalBmp.Width > _loadedOriginalBmp.Height;
            double b1W = isLandscapePhoto ? 4.5 : 3.5;
            double b1H = isLandscapePhoto ? 3.5 : 4.5;
            double b2W = isLandscapePhoto ? 3.0 : 2.5;
            double b2H = isLandscapePhoto ? 2.5 : 3.0;
            double b3W = isLandscapePhoto ? 3.5 : 2.5;
            double b3H = isLandscapePhoto ? 2.5 : 3.5;

            config.Batches.Add(new PassportBatchItem($"Passport ({b1W}×{b1H} cm)", b1W, b1H, ComboBatch1Count));
            config.Batches.Add(new PassportBatchItem($"Stamp ({b2W}×{b2H} cm)", b2W, b2H, ComboBatch2Count));
            if (ComboBatch3Count > 0)
            {
                config.Batches.Add(new PassportBatchItem($"PAN ({b3W}×{b3H} cm)", b3W, b3H, ComboBatch3Count));
            }
        }
        else
        {
            config.Batches.Add(new PassportBatchItem("Custom", PhotoWidthCm, PhotoHeightCm, CopiesCount));
        }

        return config;
    }

    private async Task<Bitmap?> GenerateMasterSheet300DpiAsync()
    {
        if (_loadedOriginalBmp == null) return null;
        var config = BuildCurrentConfig();
        Bitmap? aiCutout = null;
        lock (_renderLock)
        {
            if (_cachedAiCutoutBmp != null)
            {
                try { aiCutout = (Bitmap)_cachedAiCutoutBmp.Clone(); } catch { }
            }
        }
        using var processedPortrait = _studioService.ProcessPortrait(_loadedOriginalBmp, config, aiCutout);
        aiCutout?.Dispose();
        return await _studioService.GenerateSheetAsync(processedPortrait, config, dpi: 300);
    }

    private async void PrintSheet()
    {
        if (_loadedOriginalBmp == null) return;

        try
        {
            IsProcessing = true;
            using var masterBmp = await GenerateMasterSheet300DpiAsync();
            if (masterBmp != null)
            {
                var bs = BitmapToBitmapSource(masterBmp);
                string preferredPaper = PaperSize.Equals("4x6", StringComparison.OrdinalIgnoreCase) 
                    ? "4 × 6 in Photo (10 × 15 cm)" 
                    : "A4 (210 × 297 mm)";
                string preferredOri = IsLandscape ? "Landscape" : "Portrait";
                var owner = System.Windows.Application.Current?.Windows.OfType<Views.PassportStudioDialog>().FirstOrDefault()
                            ?? System.Windows.Application.Current?.MainWindow;
                Views.NativePrintDialog.ShowPrintDialog(bs, $"DASMO Passport Photo Sheet ({PaperSize})", owner, preferredPaper, preferredOri);
                _isSavedOrPrinted = true;
                OnPropertyChanged(nameof(HasUnsavedWork));
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to print passport studio sheet");
            ShowAlert($"Printing failed:\n{ex.Message}", "Print Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsProcessing = false;
        }
    }

    private async Task SaveSheetJpgAsync()
    {
        if (_loadedOriginalBmp == null) return;

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save Passport Photo Sheet (300 DPI)",
            Filter = "JPEG Image (*.jpg)|*.jpg|PNG Image (*.png)|*.png",
            FileName = $"Passport_Photos_{PaperSize}_{DateTime.Now:yyyyMMdd_HHmmss}.jpg"
        };

        if (dlg.ShowDialog() == true)
        {
            try
            {
                IsProcessing = true;
                using var masterBmp = await GenerateMasterSheet300DpiAsync();
                if (masterBmp != null)
                {
                    await Task.Run(() =>
                    {
                        masterBmp.Save(dlg.FileName, ImageFormat.Jpeg);
                    });
                    _isSavedOrPrinted = true;
                    OnPropertyChanged(nameof(HasUnsavedWork));
                    ShowAlert($"✅ High-Resolution 300 DPI sheet saved successfully:\n{dlg.FileName}", "Saved", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                ShowAlert($"Save failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsProcessing = false;
            }
        }
    }

    private async Task SaveSheetPdfAsync()
    {
        if (_loadedOriginalBmp == null) return;

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save Passport Photo Sheet as PDF (300 DPI)",
            Filter = "PDF Document (*.pdf)|*.pdf",
            FileName = $"Passport_Photos_{PaperSize}_{DateTime.Now:yyyyMMdd_HHmmss}.pdf"
        };

        if (dlg.ShowDialog() == true)
        {
            try
            {
                IsProcessing = true;
                using var masterBmp = await GenerateMasterSheet300DpiAsync();
                if (masterBmp != null)
                {
                    await Task.Run(() =>
                    {
                        using var doc = new PdfSharpCore.Pdf.PdfDocument();
                        var page = doc.AddPage();
                        bool isLandscape = masterBmp.Width > masterBmp.Height;

                        if (PaperSize.Equals("4x6", StringComparison.OrdinalIgnoreCase))
                        {
                            if (isLandscape)
                            {
                                page.Width = PdfSharpCore.Drawing.XUnit.FromInch(6.0);
                                page.Height = PdfSharpCore.Drawing.XUnit.FromInch(4.0);
                            }
                            else
                            {
                                page.Width = PdfSharpCore.Drawing.XUnit.FromInch(4.0);
                                page.Height = PdfSharpCore.Drawing.XUnit.FromInch(6.0);
                            }
                        }
                        else
                        {
                            page.Size = PdfSharpCore.PageSize.A4;
                            if (isLandscape)
                            {
                                page.Orientation = PdfSharpCore.PageOrientation.Landscape;
                            }
                        }

                        using var ms = new MemoryStream();
                        masterBmp.Save(ms, ImageFormat.Jpeg);
                        ms.Seek(0, SeekOrigin.Begin);

                        using var ximg = PdfSharpCore.Drawing.XImage.FromStream(() => new MemoryStream(ms.ToArray()));
                        using var gfx = PdfSharpCore.Drawing.XGraphics.FromPdfPage(page);
                        gfx.DrawImage(ximg, 0, 0, page.Width, page.Height);

                        doc.Save(dlg.FileName);
                    });
                    _isSavedOrPrinted = true;
                    OnPropertyChanged(nameof(HasUnsavedWork));
                    ShowAlert($"✅ High-Resolution 300 DPI PDF saved successfully:\n{dlg.FileName}", "Saved", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                ShowAlert($"PDF save failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsProcessing = false;
            }
        }
    }

    private static BitmapSource BitmapToBitmapSource(Bitmap bmp)
    {
        lock (bmp)
        {
            using var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
            ms.Seek(0, SeekOrigin.Begin);

            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.StreamSource = ms;
            bi.EndInit();
            bi.Freeze();
            return bi;
        }
    }

    #region Single Photo Export Methods
    private void ApplySinglePreset(int index)
    {
        switch (index)
        {
            case 0: // SSC / UPSC / IBPS / Railways (20–50 KB, 3.5×4.5 cm, 300 DPI)
                SingleWidthCm = 3.5;
                SingleHeightCm = 4.5;
                SingleDpi = 300;
                SingleMinKb = 20;
                SingleMaxKb = 50;
                break;
            case 1: // PAN Card Portal (10–45 KB, 2.5×3.5 cm, 200 DPI)
                SingleWidthCm = 2.5;
                SingleHeightCm = 3.5;
                SingleDpi = 200;
                SingleMinKb = 10;
                SingleMaxKb = 45;
                break;
            case 2: // Passport Seva / Standard (50–100 KB, 3.5×4.5 cm, 300 DPI)
                SingleWidthCm = 3.5;
                SingleHeightCm = 4.5;
                SingleDpi = 300;
                SingleMinKb = 50;
                SingleMaxKb = 100;
                break;
            case 3: // US / Schengen Visa (50–100 KB, 5.0×5.0 cm, 300 DPI)
                SingleWidthCm = 5.0;
                SingleHeightCm = 5.0;
                SingleDpi = 300;
                SingleMinKb = 50;
                SingleMaxKb = 100;
                break;
            case 4: // Custom
                break;
        }
    }

    private void OpenSingleExport()
    {
        if (_loadedOriginalBmp == null) return;
        IsSingleExportDialogOpen = true;
        TriggerSingleExportPreviewUpdate();
    }

    private void CloseSingleExport()
    {
        IsSingleExportDialogOpen = false;
    }

    public void TriggerSingleExportPreviewUpdate()
    {
        if (!HasPhoto || _loadedOriginalBmp == null) return;

        Task.Run(() =>
        {
            try
            {
                var sheetConfig = BuildCurrentConfig();
                Bitmap? aiCutout = null;
                lock (_renderLock)
                {
                    if (_cachedAiCutoutBmp != null)
                    {
                        try { aiCutout = (Bitmap)_cachedAiCutoutBmp.Clone(); } catch { }
                    }
                }
                var exportConfig = new SinglePhotoExportConfig
                {
                    WidthCm = SingleWidthCm,
                    HeightCm = SingleHeightCm,
                    Dpi = SingleDpi,
                    MinKb = SingleMinKb,
                    MaxKb = SingleMaxKb,
                    AddCandidateStamp = SingleAddStamp,
                    CandidateName = SingleCandidateName,
                    DateOfPhoto = SingleDateOfPhoto
                };

                var result = _studioService.ExportSinglePassportPhoto(_loadedOriginalBmp, sheetConfig, exportConfig, aiCutout);
                aiCutout?.Dispose();
                if (result.Success && result.ImageBytes != null)
                {
                    _cachedSingleBytes = result.ImageBytes;
                    using var ms = new MemoryStream(result.ImageBytes);
                    using var bmp = new Bitmap(ms);
                    var bs = BitmapToBitmapSource(bmp);

                    double kb = result.FileSizeBytes / 1024.0;
                    bool inRange = kb >= SingleMinKb - 0.5 && kb <= SingleMaxKb + 0.5;

                    SafeUiInvoke(() =>
                    {
                        SingleExportPreviewImage = bs;
                        IsSingleExportValid = inRange;
                        SingleExportStatusText = inRange
                            ? $"🟢 Perfect Size: {kb:F1} KB (Allowed: {SingleMinKb} KB – {SingleMaxKb} KB) • {result.WidthPx}×{result.HeightPx} px @ {SingleDpi} DPI"
                            : $"⚠️ Output Size: {kb:F1} KB (Target: {SingleMinKb} KB – {SingleMaxKb} KB) • {result.WidthPx}×{result.HeightPx} px @ {SingleDpi} DPI";
                    });
                }
                else
                {
                    SafeUiInvoke(() =>
                    {
                        SingleExportStatusText = $"❌ Error: {result.ErrorMessage}";
                        IsSingleExportValid = false;
                    });
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to render single export preview");
            }
        });
    }

    private async Task SaveSinglePhotoAsync()
    {
        if (_loadedOriginalBmp == null) return;

        if (_cachedSingleBytes == null)
        {
            TriggerSingleExportPreviewUpdate();
            await Task.Delay(150);
        }

        if (_cachedSingleBytes == null || _cachedSingleBytes.Length == 0)
        {
            ShowAlert("Please wait for the photo preview to render.", "Export", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        string safeName = !string.IsNullOrWhiteSpace(SingleCandidateName)
            ? $"Passport_{SingleCandidateName.Trim().Replace(" ", "_")}_{SingleWidthCm:F1}x{SingleHeightCm:F1}cm.jpg"
            : $"Passport_Single_Photo_{SingleWidthCm:F1}x{SingleHeightCm:F1}cm_{DateTime.Now:yyyyMMdd_HHmmss}.jpg";

        string? targetPath = null;
        if (RequestSaveFile != null)
        {
            targetPath = RequestSaveFile("JPEG Image (*.jpg)|*.jpg", safeName);
        }
        else
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Save Single Passport Photo for Govt / Online Upload",
                Filter = "JPEG Image (*.jpg)|*.jpg",
                FileName = safeName
            };
            if (dlg.ShowDialog() == true)
            {
                targetPath = dlg.FileName;
            }
        }

        if (!string.IsNullOrEmpty(targetPath))
        {
            try
            {
                IsProcessing = true;
                await File.WriteAllBytesAsync(targetPath, _cachedSingleBytes);
                _isSavedOrPrinted = true;
                OnPropertyChanged(nameof(HasUnsavedWork));

                double kb = _cachedSingleBytes.Length / 1024.0;
                ShowAlert(
                    $"✅ Single Passport Photo saved successfully:\n{targetPath}\n\n" +
                    $"• File Size: {kb:F1} KB (Ready for online portal upload)\n" +
                    $"• Dimensions: {SingleWidthCm:F1} × {SingleHeightCm:F1} cm @ 300 DPI",
                    "Single Photo Exported", MessageBoxButton.OK, MessageBoxImage.Information);

                CloseSingleExport();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to save single passport photo");
                ShowAlert($"Failed to save: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsProcessing = false;
            }
        }
    }

    private void CopySinglePhoto()
    {
        if (SingleExportPreviewImage != null)
        {
            try
            {
                System.Windows.Clipboard.SetImage(SingleExportPreviewImage);
                ShowAlert("📋 Single photo copied to clipboard!\nYou can paste it directly into applications or browsers.", "Copied", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to copy single photo to clipboard");
            }
        }
    }
    #endregion
}
