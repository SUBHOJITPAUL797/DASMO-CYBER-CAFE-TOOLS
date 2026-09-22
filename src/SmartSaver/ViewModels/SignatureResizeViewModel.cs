using System;
using System.Collections.ObjectModel;
using System.IO;
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
            }
        }
    }

    public string DimensionUnitLabel => IsPixelMode ? "px" : "cm";

    // Pixel Dimensions (defaults to Pi7 standard: 140 x 60)
    private int _widthPx = 140;
    public int WidthPx
    {
        get => _widthPx;
        set
        {
            if (SetProperty(ref _widthPx, value))
            {
                OnPropertyChanged(nameof(EffectiveWidthPx));
                OnPropertyChanged(nameof(EquivalentPixelsText));
                OnPropertyChanged(nameof(CanResize));
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
                OnPropertyChanged(nameof(EffectiveHeightPx));
                OnPropertyChanged(nameof(EquivalentPixelsText));
                OnPropertyChanged(nameof(CanResize));
            }
        }
    }

    // Centimeter Dimensions (defaults to 3.5 x 1.5 cm)
    private double _widthCm = 3.5;
    public double WidthCm
    {
        get => _widthCm;
        set
        {
            if (SetProperty(ref _widthCm, value))
            {
                OnPropertyChanged(nameof(EffectiveWidthPx));
                OnPropertyChanged(nameof(EquivalentPixelsText));
                OnPropertyChanged(nameof(CanResize));
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
                OnPropertyChanged(nameof(EffectiveHeightPx));
                OnPropertyChanged(nameof(EquivalentPixelsText));
                OnPropertyChanged(nameof(CanResize));
            }
        }
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
    private int _targetSizeKB = 20;
    public int TargetSizeKB
    {
        get => _targetSizeKB;
        set => SetProperty(ref _targetSizeKB, value);
    }

    private int _minSizeKB = 0;
    public int MinSizeKB
    {
        get => _minSizeKB;
        set => SetProperty(ref _minSizeKB, value);
    }

    private bool _cleanBackground = true;
    public bool CleanBackground
    {
        get => _cleanBackground;
        set => SetProperty(ref _cleanBackground, value);
    }

    private bool _convertBlueToBlack = true;
    public bool ConvertBlueToBlack
    {
        get => _convertBlueToBlack;
        set => SetProperty(ref _convertBlueToBlack, value);
    }

    private bool _autoCrop = true;
    public bool AutoCrop
    {
        get => _autoCrop;
        set => SetProperty(ref _autoCrop, value);
    }

    private bool _maintainAspectRatio = false;
    public bool MaintainAspectRatio
    {
        get => _maintainAspectRatio;
        set => SetProperty(ref _maintainAspectRatio, value);
    }

    public ObservableCollection<string> OutputFormats { get; } = new() { ".jpg", ".png" };

    private string _selectedOutputFormat = ".jpg";
    public string SelectedOutputFormat
    {
        get => _selectedOutputFormat;
        set => SetProperty(ref _selectedOutputFormat, value);
    }
    #endregion

    #region Presets
    public ObservableCollection<SignaturePreset> Presets { get; } = new()
    {
        new("⚡ Select Exam / Portal Preset...", 140, 60, true, 20, 0),
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
        set => SetProperty(ref _hasResult, value);
    }

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
    public ICommand CopyResultCommand { get; }
    public ICommand PrintCommand { get; }
    public ICommand ResetCommand { get; }

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
        CopyResultCommand = new RelayCommand(_ => CopyResultToClipboard(), _ => HasResult && ResultImage != null);
        PrintCommand = new RelayCommand(_ => PrintResult(), _ => HasResult && ResultImage != null);
        ResetCommand = new RelayCommand(_ => Reset());
        QuickPresetCommand = new RelayCommand(p => ApplyQuickPreset(p?.ToString()));

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

    public async Task ExecuteResizeAsync()
    {
        if (_inputBytes == null || _inputBytes.Length == 0) return;

        IsProcessing = true;
        ProgressText = "Resizing & optimizing signature...";
        ResultSummary = string.Empty;

        try
        {
            int targetW = EffectiveWidthPx;
            int targetH = EffectiveHeightPx;
            long targetBytes = (long)TargetSizeKB * 1024;
            long minBytes = (long)MinSizeKB * 1024;

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
                convertBlueToBlack: ConvertBlueToBlack);

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

                ResultSummary = $"✅ Ready! {unitInfo} • {result.OutputSizeFormatted} ({pct:F1}% saved)";

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
                ResultSummary = $"❌ Failed: {result.Message}";
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error in ExecuteResizeAsync");
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
            System.Windows.Clipboard.SetImage(ResultImage);
            WpfMessageBox.Show("✅ Resized signature copied to clipboard!\nYou can paste it directly into portals or WhatsApp.", "Copied", MessageBoxButton.OK, MessageBoxImage.Information);
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
            case "SSC":
            case "IBPS":
                SelectedPreset = Presets.FirstOrDefault(p => p.Name.Contains("SSC / IBPS")) ?? Presets[1];
                break;
            case "UPSC":
                SelectedPreset = Presets.FirstOrDefault(p => p.Name.Contains("UPSC Signature")) ?? Presets[4];
                break;
            case "PSC":
                SelectedPreset = Presets.FirstOrDefault(p => p.Name.Contains("State PSC")) ?? Presets[3];
                break;
            case "THUMB":
                SelectedPreset = Presets.FirstOrDefault(p => p.Name.Contains("Thumb")) ?? Presets[6];
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

        // Reset to Pi7 defaults
        IsPixelMode = true;
        WidthPx = 140;
        HeightPx = 60;
        WidthCm = 3.5;
        HeightCm = 1.5;
        TargetSizeKB = 20;
        MinSizeKB = 0;
        SelectedDpi = 300;
        CleanBackground = true;
        AutoCrop = true;
        MaintainAspectRatio = false;
        SelectedPreset = Presets[0];
    }
}
