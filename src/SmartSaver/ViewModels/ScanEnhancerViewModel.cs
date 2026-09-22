using System.IO;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Serilog;
using SmartSaver.Models;
using SmartSaver.Services;

namespace SmartSaver.ViewModels;

public class ScanEnhancerViewModel : ViewModelBase
{
    private string _filePath = string.Empty;
    private readonly ScanEnhancerService _enhancerService;
    private CancellationTokenSource? _previewCts;

    public string FilePath => _filePath;
    public string FileName => string.IsNullOrEmpty(_filePath) ? "No file selected" : Path.GetFileName(_filePath);
    public bool HasFile => !string.IsNullOrEmpty(_filePath) && File.Exists(_filePath);

    private long _fileSizeBytes;
    public long FileSizeBytes
    {
        get => _fileSizeBytes;
        set => SetProperty(ref _fileSizeBytes, value);
    }
    public string FileSizeFormatted => CompressionResult.FormatFileSize(FileSizeBytes);

    private ScanFilterType _selectedFilter = ScanFilterType.MagicWhite;
    public ScanFilterType SelectedFilter
    {
        get => _selectedFilter;
        set
        {
            if (SetProperty(ref _selectedFilter, value))
            {
                TriggerLivePreview();
            }
        }
    }

    private int _intensity = 80;
    public int Intensity
    {
        get => _intensity;
        set
        {
            if (SetProperty(ref _intensity, value))
            {
                OnPropertyChanged(nameof(IntensityText));
                TriggerLivePreview();
            }
        }
    }
    public string IntensityText => $"{Intensity}%";

    private ImageSource? _previewImageSource;
    public ImageSource? PreviewImageSource
    {
        get => _showOriginal ? _originalImageSource : _previewImageSource;
        set => SetProperty(ref _previewImageSource, value);
    }

    private ImageSource? _originalImageSource;
    public ImageSource? OriginalImageSource
    {
        get => _originalImageSource;
        set => SetProperty(ref _originalImageSource, value);
    }

    private bool _showOriginal = false;
    public bool ShowOriginal
    {
        get => _showOriginal;
        set
        {
            if (SetProperty(ref _showOriginal, value))
            {
                OnPropertyChanged(nameof(PreviewImageSource));
                OnPropertyChanged(nameof(PreviewModeLabel));
            }
        }
    }
    public string PreviewModeLabel => _showOriginal ? "👁️ Showing Original (Before)" : "✨ Showing Enhanced (After)";

    private bool _isCompressEnabled = false;
    public bool IsCompressEnabled
    {
        get => _isCompressEnabled;
        set => SetProperty(ref _isCompressEnabled, value);
    }

    private int _targetSizeKB = 200;
    public int TargetSizeKB
    {
        get => _targetSizeKB;
        set => SetProperty(ref _targetSizeKB, value);
    }

    private string _targetSizeUnit = "KB";
    public string TargetSizeUnit
    {
        get => _targetSizeUnit;
        set => SetProperty(ref _targetSizeUnit, value);
    }

    public List<GovtPortalPreset> PortalPresets { get; } = GovtPortalPresets.All;

    private GovtPortalPreset? _selectedPortalPreset;
    public GovtPortalPreset? SelectedPortalPreset
    {
        get => _selectedPortalPreset;
        set
        {
            if (SetProperty(ref _selectedPortalPreset, value) && value != null)
            {
                if (value.Name != "⚡ Select Govt / Exam Portal Preset...")
                {
                    IsCompressEnabled = true;
                    TargetSizeKB = value.TargetSizeKB;
                    TargetSizeUnit = "KB";
                }
            }
        }
    }

    private string _outputDirectory = string.Empty;
    public string OutputDirectory
    {
        get => _outputDirectory;
        set => SetProperty(ref _outputDirectory, value);
    }

    private string _outputFileName = string.Empty;
    public string OutputFileName
    {
        get => _outputFileName;
        set => SetProperty(ref _outputFileName, value);
    }

    private bool _openOnComplete = true;
    public bool OpenOnComplete
    {
        get => _openOnComplete;
        set => SetProperty(ref _openOnComplete, value);
    }

    private bool _isProcessing;
    public bool IsProcessing
    {
        get => _isProcessing;
        set
        {
            if (SetProperty(ref _isProcessing, value))
            {
                (EnhanceCommand as RelayCommand)?.OnCanExecuteChanged();
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

    private bool _isSuccess;
    public bool IsSuccess
    {
        get => _isSuccess;
        set => SetProperty(ref _isSuccess, value);
    }

    private string _lastResultPath = string.Empty;

    public ICommand EnhanceCommand { get; }
    public ICommand PrintCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand BrowseDirectoryCommand { get; }
    public ICommand SetPresetSizeCommand { get; }
    public ICommand OpenResultCommand { get; }
    public ICommand ToggleCompareCommand { get; }
    public ICommand SetFilterCommand { get; }

    public Action? RequestClose { get; set; }

    public ScanEnhancerViewModel(string filePath)
    {
        var imgComp = new ImageCompressor();
        _enhancerService = new ScanEnhancerService(imgComp);

        EnhanceCommand = new RelayCommand(async _ => await EnhanceAsync(), _ => !IsProcessing && FileSizeBytes > 0);
        PrintCommand = new RelayCommand(_ =>
        {
            if (PreviewImageSource != null)
            {
                PrintService.PrintImageDirect(PreviewImageSource, "Enhanced_Document_Scan");
            }
        }, _ => HasFile && PreviewImageSource != null);
        CancelCommand = new RelayCommand(_ => RequestClose?.Invoke());
        BrowseDirectoryCommand = new RelayCommand(_ => BrowseDirectory());
        SetPresetSizeCommand = new RelayCommand(param =>
        {
            if (param is string s && int.TryParse(s, out int kb))
            {
                IsCompressEnabled = true;
                TargetSizeKB = kb;
                TargetSizeUnit = "KB";
            }
        });
        OpenResultCommand = new RelayCommand(_ => OpenResultFile());
        ToggleCompareCommand = new RelayCommand(_ => ShowOriginal = !ShowOriginal);
        SetFilterCommand = new RelayCommand(param =>
        {
            if (param is ScanFilterType filter)
            {
                SelectedFilter = filter;
            }
            else if (param is string s && Enum.TryParse<ScanFilterType>(s, out var parsed))
            {
                SelectedFilter = parsed;
            }
        });

        if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath))
        {
            LoadFile(filePath);
        }
    }

    public void LoadFile(string filePath)
    {
        if (!File.Exists(filePath)) return;

        _filePath = filePath;
        FileSizeBytes = new FileInfo(filePath).Length;
        OutputDirectory = Path.GetDirectoryName(filePath) ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string ext = Path.GetExtension(filePath);
        string baseName = Path.GetFileNameWithoutExtension(filePath);
        OutputFileName = $"{baseName}_enhanced{ext}";

        LoadOriginalPreview();
        TriggerLivePreview();

        OnPropertyChanged(nameof(FilePath));
        OnPropertyChanged(nameof(FileName));
        OnPropertyChanged(nameof(HasFile));
        OnPropertyChanged(nameof(FileSizeFormatted));
        (EnhanceCommand as RelayCommand)?.OnCanExecuteChanged();
    }

    private void LoadOriginalPreview()
    {
        try
        {
            if (!File.Exists(_filePath)) return;

            byte[] bytes = File.ReadAllBytes(_filePath);
            using var ms = new MemoryStream(bytes);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();

            OriginalImageSource = bmp;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to load original preview image for {Path}", _filePath);
        }
    }

    private void TriggerLivePreview()
    {
        if (!HasFile) return;

        _previewCts?.Cancel();
        _previewCts = new CancellationTokenSource();
        var ct = _previewCts.Token;

        string srcPath = _filePath;
        var filter = _selectedFilter;
        int currentInt = _intensity;

        Task.Run(async () =>
        {
            try
            {
                // Debounce slightly to allow slider dragging smoothness
                await Task.Delay(40, ct);
                if (ct.IsCancellationRequested) return;

                byte[]? bytes = _enhancerService.GeneratePreviewBytes(srcPath, filter, currentInt, maxDimension: 800);
                if (bytes == null || ct.IsCancellationRequested) return;

                System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                {
                    if (ct.IsCancellationRequested) return;

                    using var ms = new MemoryStream(bytes);
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.StreamSource = ms;
                    bmp.EndInit();
                    bmp.Freeze();

                    _previewImageSource = bmp;
                    if (!_showOriginal)
                    {
                        OnPropertyChanged(nameof(PreviewImageSource));
                    }
                });
            }
            catch (Exception ex)
            {
                Log.Debug("Live preview update aborted: {Msg}", ex.Message);
            }
        }, ct);
    }

    private void BrowseDirectory()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select Output Folder",
            InitialDirectory = OutputDirectory
        };
        if (dlg.ShowDialog() == true)
        {
            OutputDirectory = dlg.FolderName;
        }
    }

    private async Task EnhanceAsync()
    {
        IsProcessing = true;
        ProgressText = "Enhancing document scan...";
        ResultText = string.Empty;
        IsSuccess = false;

        try
        {
            Directory.CreateDirectory(OutputDirectory);

            string outName = string.IsNullOrWhiteSpace(OutputFileName) ? $"{Path.GetFileNameWithoutExtension(_filePath)}_enhanced{Path.GetExtension(_filePath)}" : OutputFileName;
            string fullOutPath = Path.Combine(OutputDirectory, outName);
            int counter = 1;
            string baseNoExt = Path.GetFileNameWithoutExtension(outName);
            string ext = Path.GetExtension(outName);
            while (File.Exists(fullOutPath))
            {
                fullOutPath = Path.Combine(OutputDirectory, $"{baseNoExt}_{counter++}{ext}");
            }

            long? targetBytes = null;
            if (IsCompressEnabled)
            {
                targetBytes = TargetSizeUnit == "MB"
                    ? (long)TargetSizeKB * 1024 * 1024
                    : (long)TargetSizeKB * 1024;
            }

            int intensity = _intensity;
            bool ok = await Task.Run(() => _enhancerService.EnhanceDocument(_filePath, fullOutPath, SelectedFilter, intensity, targetBytes));

            if (ok && File.Exists(fullOutPath))
            {
                long newSize = new FileInfo(fullOutPath).Length;
                OutputHistoryService.Instance.Record(fullOutPath, "Enhanced Scan", FileSizeBytes, newSize);
                _lastResultPath = fullOutPath;
                IsSuccess = true;
                ResultText = $"✅ Enhanced scan saved ({CompressionResult.FormatFileSize(newSize)})!\n→ {Path.GetFileName(fullOutPath)}";

                if (OpenOnComplete)
                {
                    OpenResultFile();
                }
            }
            else
            {
                IsSuccess = false;
                ResultText = "❌ Failed to enhance document scan.";
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed in ScanEnhancerViewModel");
            IsSuccess = false;
            ResultText = $"❌ Error: {ex.Message}";
        }
        finally
        {
            IsProcessing = false;
            ProgressText = string.Empty;
        }
    }

    private void OpenResultFile()
    {
        if (!string.IsNullOrEmpty(_lastResultPath) && File.Exists(_lastResultPath))
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = _lastResultPath,
                    UseShellExecute = true
                });
            }
            catch { }
        }
    }
}
