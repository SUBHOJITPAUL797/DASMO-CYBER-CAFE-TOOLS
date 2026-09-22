using System.IO;
using System.Windows.Input;
using SmartSaver.Services;
using SmartSaver.Helpers;
using Serilog;

namespace SmartSaver.ViewModels;

public class ImageResizeViewModel : ViewModelBase
{
    private readonly string _filePath;
    private readonly ImageCompressor _imageCompressor;
    private bool _isSyncingDimensions;

    public string FilePath => _filePath;
    
    private string _fileName = string.Empty;
    public string FileName { get => _fileName; set => SetProperty(ref _fileName, value); }

    private string _currentFileSize = string.Empty;
    public string CurrentFileSize { get => _currentFileSize; set => SetProperty(ref _currentFileSize, value); }

    private int _originalWidth;
    public int OriginalWidth => _originalWidth;

    private int _originalHeight;
    public int OriginalHeight => _originalHeight;

    private string _originalDimensions = string.Empty;
    public string OriginalDimensions
    {
        get => _originalDimensions;
        set => SetProperty(ref _originalDimensions, value);
    }

    private string _dimensionStatusText = string.Empty;
    public string DimensionStatusText
    {
        get => _dimensionStatusText;
        set => SetProperty(ref _dimensionStatusText, value);
    }

    private string _resizeMode = "fileSize";
    public string ResizeMode
    {
        get => _resizeMode;
        set
        {
            if (SetProperty(ref _resizeMode, value))
            {
                UpdateDimensionStatus();
            }
        }
    }

    private int _targetSizeKB = 200;
    public int TargetSizeKB { get => _targetSizeKB; set => SetProperty(ref _targetSizeKB, value); }

    private string _targetSizeUnit = "KB";
    public string TargetSizeUnit { get => _targetSizeUnit; set => SetProperty(ref _targetSizeUnit, value); }

    private int _targetWidth;
    public int TargetWidth
    {
        get => _targetWidth;
        set
        {
            if (SetProperty(ref _targetWidth, value))
            {
                if (MaintainAspectRatio && !_isSyncingDimensions && _originalWidth > 0 && _originalHeight > 0 && value > 0)
                {
                    _isSyncingDimensions = true;
                    try
                    {
                        TargetHeight = Math.Max(1, (int)Math.Round((double)value * _originalHeight / _originalWidth));
                    }
                    finally
                    {
                        _isSyncingDimensions = false;
                    }
                }
                UpdateDimensionStatus();
            }
        }
    }

    private int _targetHeight;
    public int TargetHeight
    {
        get => _targetHeight;
        set
        {
            if (SetProperty(ref _targetHeight, value))
            {
                if (MaintainAspectRatio && !_isSyncingDimensions && _originalWidth > 0 && _originalHeight > 0 && value > 0)
                {
                    _isSyncingDimensions = true;
                    try
                    {
                        TargetWidth = Math.Max(1, (int)Math.Round((double)value * _originalWidth / _originalHeight));
                    }
                    finally
                    {
                        _isSyncingDimensions = false;
                    }
                }
                UpdateDimensionStatus();
            }
        }
    }

    private bool _maintainAspectRatio = false; // Default to false so user inputs are exact portal dimensions!
    public bool MaintainAspectRatio
    {
        get => _maintainAspectRatio;
        set
        {
            if (SetProperty(ref _maintainAspectRatio, value))
            {
                if (value && _originalWidth > 0 && _originalHeight > 0)
                {
                    _isSyncingDimensions = true;
                    try
                    {
                        if (_targetWidth > 0)
                        {
                            TargetHeight = Math.Max(1, (int)Math.Round((double)_targetWidth * _originalHeight / _originalWidth));
                        }
                        else if (_targetHeight > 0)
                        {
                            TargetWidth = Math.Max(1, (int)Math.Round((double)_targetHeight * _originalWidth / _originalHeight));
                        }
                    }
                    finally
                    {
                        _isSyncingDimensions = false;
                    }
                }
                UpdateDimensionStatus();
            }
        }
    }

    private string _outputMode = "newFile";
    public string OutputMode { get => _outputMode; set => SetProperty(ref _outputMode, value); }

    private bool _isProcessing;
    public bool IsProcessing { get => _isProcessing; set => SetProperty(ref _isProcessing, value); }

    private string _progressText = string.Empty;
    public string ProgressText { get => _progressText; set => SetProperty(ref _progressText, value); }

    private string _resultText = string.Empty;
    public string ResultText
    {
        get => _resultText;
        set
        {
            if (SetProperty(ref _resultText, value))
            {
                OnPropertyChanged(nameof(HasResult));
            }
        }
    }

    public bool HasResult => !string.IsNullOrEmpty(_resultText);

    public List<SmartSaver.Models.GovtPortalPreset> PortalPresets { get; } = SmartSaver.Models.GovtPortalPresets.All;

    private SmartSaver.Models.GovtPortalPreset? _selectedPortalPreset;
    public SmartSaver.Models.GovtPortalPreset? SelectedPortalPreset
    {
        get => _selectedPortalPreset;
        set
        {
            if (SetProperty(ref _selectedPortalPreset, value) && value != null)
            {
                if (value.Name != "⚡ Select Govt / Exam Portal Preset...")
                {
                    TargetSizeKB = value.TargetSizeKB;
                    TargetSizeUnit = "KB";
                    if (value.WidthPx.HasValue && value.HeightPx.HasValue)
                    {
                        _isSyncingDimensions = true;
                        try
                        {
                            MaintainAspectRatio = false; // Portal presets require exact fixed pixel dimensions
                            TargetWidth = value.WidthPx.Value;
                            TargetHeight = value.HeightPx.Value;
                            ResizeMode = "dimensions";
                        }
                        finally
                        {
                            _isSyncingDimensions = false;
                        }
                        UpdateDimensionStatus();
                    }
                    else
                    {
                        ResizeMode = "fileSize";
                    }
                }
            }
        }
    }

    public ICommand ResizeCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand SetPresetSizeCommand { get; }
    public ICommand SetDimensionPresetCommand { get; }

    public Action? RequestClose { get; set; }

    public ImageResizeViewModel(string filePath)
    {
        _filePath = filePath;
        _imageCompressor = new ImageCompressor();
        
        Initialize();

        ResizeCommand = new RelayCommand(async _ => await ResizeAsync(), _ => !IsProcessing);
        CancelCommand = new RelayCommand(_ => RequestClose?.Invoke(), _ => !IsProcessing);
        SetPresetSizeCommand = new RelayCommand(param =>
        {
            if (param is string sizeStr && int.TryParse(sizeStr, out int size))
            {
                TargetSizeKB = size;
                TargetSizeUnit = "KB";
                ResizeMode = "fileSize";
            }
        });
        SetDimensionPresetCommand = new RelayCommand(param =>
        {
            if (param is string dimStr)
            {
                var parts = dimStr.Split('x');
                if (parts.Length == 2 && int.TryParse(parts[0], out int w) && int.TryParse(parts[1], out int h))
                {
                    _isSyncingDimensions = true;
                    try
                    {
                        MaintainAspectRatio = false; // Presets require exact dimensions
                        TargetWidth = w;
                        TargetHeight = h;
                        ResizeMode = "dimensions";
                    }
                    finally
                    {
                        _isSyncingDimensions = false;
                    }
                    UpdateDimensionStatus();
                }
            }
        });
    }

    private void Initialize()
    {
        FileName = Path.GetFileName(_filePath);
        if (File.Exists(_filePath))
        {
            var fileInfo = new FileInfo(_filePath);
            CurrentFileSize = FormatFileSize(fileInfo.Length);

            try
            {
                var info = SixLabors.ImageSharp.Image.Identify(_filePath);
                if (info != null)
                {
                    int w = info.Width;
                    int h = info.Height;
                    var orientation = info.Metadata.ExifProfile?.GetValue(SixLabors.ImageSharp.Metadata.Profiles.Exif.ExifTag.Orientation);
                    if (orientation != null && (orientation.Value == 5 || orientation.Value == 6 || orientation.Value == 7 || orientation.Value == 8))
                    {
                        w = info.Height;
                        h = info.Width;
                    }

                    _originalWidth = w;
                    _originalHeight = h;
                    OriginalDimensions = $"{_originalWidth} × {_originalHeight} px";
                }
                else
                {
                    OriginalDimensions = "Unknown";
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to read dimensions of {Path}", _filePath);
                OriginalDimensions = "Unknown";
            }
        }

        var settings = SettingsManager.Instance.Current.ImageResize;
        
        ResizeMode = settings.DefaultMode == "ask" ? "fileSize" : settings.DefaultMode;
        
        if (settings.DefaultTargetSizeKB >= 1024 && settings.DefaultTargetSizeKB % 1024 == 0)
        {
            TargetSizeKB = settings.DefaultTargetSizeKB / 1024;
            TargetSizeUnit = "MB";
        }
        else
        {
            TargetSizeKB = settings.DefaultTargetSizeKB;
            TargetSizeUnit = "KB";
        }

        if (settings.DefaultWidth > 0 && settings.DefaultHeight > 0)
        {
            _targetWidth = settings.DefaultWidth;
            _targetHeight = settings.DefaultHeight;
        }
        else if (_originalWidth > 0 && _originalHeight > 0)
        {
            _targetWidth = _originalWidth;
            _targetHeight = _originalHeight;
        }

        OutputMode = settings.OutputMode;
        UpdateDimensionStatus();
    }

    private void UpdateDimensionStatus()
    {
        if (TargetWidth <= 0 || TargetHeight <= 0)
        {
            DimensionStatusText = "⚠️ Please enter valid width and height (> 0 px).";
            return;
        }

        if (MaintainAspectRatio)
        {
            DimensionStatusText = $"🔗 Aspect Ratio Locked • Scaling to {TargetWidth} × {TargetHeight} px";
        }
        else
        {
            DimensionStatusText = $"⚡ Exact Portal Dimensions: {TargetWidth} × {TargetHeight} px (Strict Fit)";
        }
    }

    private async Task ResizeAsync()
    {
        if (ResizeMode == "dimensions" && (TargetWidth <= 0 || TargetHeight <= 0))
        {
            ResultText = "❌ Please enter a valid Width and Height greater than 0 px.";
            return;
        }

        if (ResizeMode == "fileSize" && TargetSizeKB <= 0)
        {
            ResultText = "❌ Please enter a target size greater than 0 KB.";
            return;
        }

        IsProcessing = true;
        ProgressText = "Processing image...";
        ResultText = string.Empty;
        ((RelayCommand)ResizeCommand).OnCanExecuteChanged();
        ((RelayCommand)CancelCommand).OnCanExecuteChanged();

        try
        {
            bool success = false;
            string tempFile = string.Empty;
            string actualResultText = string.Empty;

            await Task.Run(() =>
            {
                if (ResizeMode == "fileSize")
                {
                    long targetBytes = TargetSizeUnit == "MB" ? TargetSizeKB * 1024L * 1024L : TargetSizeKB * 1024L;
                    tempFile = FileHelper.GetTempFilePath(_filePath);
                    success = _imageCompressor.CompressToTargetSize(_filePath, tempFile, targetBytes);
                }
                else
                {
                    tempFile = FileHelper.GetTempFilePath(_filePath);
                    success = _imageCompressor.ResizeToDimensions(_filePath, tempFile, TargetWidth, TargetHeight, MaintainAspectRatio);
                }

                if (success)
                {
                    string targetFile = _filePath;
                    if (OutputMode == "replace")
                    {
                        FileHelper.SafeReplaceFile(_filePath, tempFile);
                    }
                    else
                    {
                        var settings = SettingsManager.Instance.Current.ImageResize;
                        targetFile = FileHelper.SaveAsNewFile(_filePath, tempFile, settings.OutputSuffix);
                    }

                    long origBytes = File.Exists(_filePath) ? new FileInfo(_filePath).Length : 0;
                    long newBytes = File.Exists(targetFile) ? new FileInfo(targetFile).Length : 0;
                    OutputHistoryService.Instance.Record(targetFile, "Image Resized", origBytes, newBytes);

                    if (File.Exists(targetFile))
                    {
                        try
                        {
                            var outInfo = SixLabors.ImageSharp.Image.Identify(targetFile);
                            if (outInfo != null)
                            {
                                actualResultText = $"✅ Resized to {outInfo.Width} × {outInfo.Height} px ({FormatFileSize(newBytes)})!\n→ {Path.GetFileName(targetFile)}";
                            }
                            else
                            {
                                actualResultText = $"✅ Resized successfully ({FormatFileSize(newBytes)})!\n→ {Path.GetFileName(targetFile)}";
                            }
                        }
                        catch
                        {
                            actualResultText = $"✅ Resized successfully ({FormatFileSize(newBytes)})!\n→ {Path.GetFileName(targetFile)}";
                        }
                    }
                }
            });

            if (success)
            {
                ResultText = !string.IsNullOrEmpty(actualResultText) ? actualResultText : "Success!";
                await Task.Delay(1800);
                RequestClose?.Invoke();
            }
            else
            {
                ResultText = "❌ Failed to resize image.";
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error resizing image {FilePath}", _filePath);
            ResultText = $"❌ Error: {ex.Message}";
        }
        finally
        {
            IsProcessing = false;
            ((RelayCommand)ResizeCommand).OnCanExecuteChanged();
            ((RelayCommand)CancelCommand).OnCanExecuteChanged();
        }
    }

    private string FormatFileSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        return $"{bytes / (1024.0 * 1024.0):F1} MB";
    }
}
