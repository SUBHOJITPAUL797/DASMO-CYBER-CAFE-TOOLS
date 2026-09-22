using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using Serilog;
using SmartSaver.Models;
using SmartSaver.Services;

namespace SmartSaver.ViewModels;

public class FileCompressViewModel : ViewModelBase
{
    private readonly string _filePath;
    private readonly string _originalExtension;
    private readonly CompressionEngine _engine;

    public string FileName => Path.GetFileName(_filePath);
    public string CurrentFileSize => CompressionResult.FormatFileSize(new FileInfo(_filePath).Length);

    private int _targetSize = 200;
    public int TargetSize
    {
        get => _targetSize;
        set { SetProperty(ref _targetSize, value); OnPropertyChanged(nameof(CanCompress)); }
    }

    private string _targetSizeUnit = "KB";
    public string TargetSizeUnit
    {
        get => _targetSizeUnit;
        set { SetProperty(ref _targetSizeUnit, value); OnPropertyChanged(nameof(CanCompress)); }
    }

    private string _selectedOutputFormat = "Same as input";
    public string SelectedOutputFormat
    {
        get => _selectedOutputFormat;
        set { SetProperty(ref _selectedOutputFormat, value); UpdateFormatNote(); }
    }

    private string _formatNote = string.Empty;
    public string FormatNote { get => _formatNote; set => SetProperty(ref _formatNote, value); }

    private string _outputMode = "newFile";
    public string OutputMode { get => _outputMode; set => SetProperty(ref _outputMode, value); }

    private bool _isProcessing;
    public bool IsProcessing { get => _isProcessing; set { SetProperty(ref _isProcessing, value); OnPropertyChanged(nameof(CanCompress)); } }

    private string _progressText = string.Empty;
    public string ProgressText { get => _progressText; set => SetProperty(ref _progressText, value); }

    private string _resultText = string.Empty;
    public string ResultText { get => _resultText; set { SetProperty(ref _resultText, value); OnPropertyChanged(nameof(HasResult)); } }

    public bool HasResult => !string.IsNullOrEmpty(_resultText);
    public bool CanCompress => !IsProcessing && (IsConvertOnlyMode || TargetSize > 0);

    // When true, the user wants format conversion only — no compression applied
    private bool _isConvertOnlyMode;
    public bool IsConvertOnlyMode
    {
        get => _isConvertOnlyMode;
        set
        {
            if (SetProperty(ref _isConvertOnlyMode, value))
            {
                OnPropertyChanged(nameof(CanCompress));
                OnPropertyChanged(nameof(ActionButtonLabel));
                OnPropertyChanged(nameof(SectionOneVisible));
                UpdateFormatNote();
            }
        }
    }

    public string ActionButtonLabel => IsConvertOnlyMode ? "🔄 Convert Format" : "⚡ Compress Now";
    public bool SectionOneVisible => !IsConvertOnlyMode;

    public ObservableCollection<string> SizeUnits { get; } = new() { "KB", "MB" };
    public ObservableCollection<string> OutputFormats { get; } = new();
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
                    TargetSize = value.TargetSizeKB;
                    TargetSizeUnit = "KB";
                    if (value.RecommendedFormat != "Same as input" && OutputFormats.Contains(value.RecommendedFormat))
                    {
                        SelectedOutputFormat = value.RecommendedFormat;
                    }
                    if (!string.IsNullOrEmpty(value.Description))
                    {
                        FormatNote = $"📋 {value.Description}";
                    }
                }
            }
        }
    }

    public System.Windows.Input.ICommand CompressCommand { get; }
    public System.Windows.Input.ICommand CancelCommand { get; }
    public System.Windows.Input.ICommand SetPresetSizeCommand { get; }

    public Action? RequestClose { get; set; }

    // These are the file types we can natively compress
    private static readonly HashSet<string> SupportedForCompression = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".bmp", ".webp", ".tiff", ".tif",
        ".pdf", ".docx", ".xlsx"
    };

    // Output format options by input type
    private static readonly Dictionary<string, List<string>> FormatOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        [".jpg"]  = new() { "Same as input", ".jpg", ".jpeg", ".png", ".webp", ".pdf" },
        [".jpeg"] = new() { "Same as input", ".jpg", ".jpeg", ".png", ".webp", ".pdf" },
        [".png"]  = new() { "Same as input", ".jpg", ".jpeg", ".png", ".webp", ".pdf" },
        [".bmp"]  = new() { "Same as input", ".jpg", ".jpeg", ".png", ".webp", ".pdf" },
        [".webp"] = new() { "Same as input", ".jpg", ".jpeg", ".png", ".webp", ".pdf" },
        [".tiff"] = new() { "Same as input", ".jpg", ".jpeg", ".png", ".pdf" },
        [".tif"]  = new() { "Same as input", ".jpg", ".jpeg", ".png", ".pdf" },
        [".pdf"]  = new() { "Same as input" },
        [".docx"] = new() { "Same as input" },
        [".xlsx"] = new() { "Same as input" },
    };

    public FileCompressViewModel(string filePath)
    {
        _filePath = filePath;
        _originalExtension = Path.GetExtension(filePath).ToLowerInvariant();

        var imageCompressor = new ImageCompressor();
        var pdfCompressor = new PdfCompressor();
        var officeCompressor = new OfficeCompressor(imageCompressor);
        _engine = new CompressionEngine(imageCompressor, pdfCompressor, officeCompressor);

        // Populate output formats
        if (FormatOptions.TryGetValue(_originalExtension, out var formats))
        {
            foreach (var f in formats) OutputFormats.Add(f);
        }
        else
        {
            // Unsupported type — only option is to warn
            OutputFormats.Add("Same as input");
        }
        _selectedOutputFormat = OutputFormats.FirstOrDefault() ?? "Same as input";
        UpdateFormatNote();

        // Pre-fill default target size from settings
        var settings = SettingsManager.Instance.Current;
        _targetSize = settings.AutoCompress.TargetSizeKB;
        _targetSizeUnit = "KB";

        CompressCommand = new RelayCommand(async _ => await CompressAsync(), _ => CanCompress);
        CancelCommand = new RelayCommand(_ => RequestClose?.Invoke());
        SetPresetSizeCommand = new RelayCommand(param =>
        {
            if (param is string sizeStr && int.TryParse(sizeStr, out int size))
            {
                TargetSize = size;
                TargetSizeUnit = "KB";
            }
        });
    }

    private void UpdateFormatNote()
    {
        if (!SupportedForCompression.Contains(_originalExtension))
        {
            FormatNote = $"Note: '{_originalExtension}' files cannot be compressed by SmartSaver. Only images, PDFs, and Office docs are supported.";
            return;
        }

        if (IsConvertOnlyMode)
        {
            string outputExt = SelectedOutputFormat == "Same as input" ? _originalExtension : SelectedOutputFormat;
            if (outputExt == _originalExtension)
                FormatNote = "⚠️ Select a different format to convert to.";
            else
                FormatNote = $"🔄 Will convert {_originalExtension} → {outputExt} without changing quality or size.";
            return;
        }

        if (SelectedOutputFormat != "Same as input" && SelectedOutputFormat != _originalExtension)
        {
            FormatNote = $"File will be converted from {_originalExtension} to {SelectedOutputFormat} during compression.";
        }
        else
        {
            FormatNote = string.Empty;
        }
    }

    private async Task CompressAsync()
    {
        if (!SupportedForCompression.Contains(_originalExtension))
        {
            ResultText = $"❌ Unsupported file type '{_originalExtension}'.\nSupported: images (jpg/png/webp/bmp), PDF, Word (docx), Excel (xlsx).";
            return;
        }

        IsProcessing = true;
        ResultText = string.Empty;

        // Determine actual output extension
        string outputExt = SelectedOutputFormat == "Same as input" ? _originalExtension : SelectedOutputFormat;

        // Determine output path
        string outputPath;
        if (OutputMode == "replace" && outputExt == _originalExtension)
        {
            outputPath = _filePath;
        }
        else
        {
            string? dir = Path.GetDirectoryName(_filePath);
            if (string.IsNullOrEmpty(dir)) dir = Environment.CurrentDirectory;
            string nameNoExt = Path.GetFileNameWithoutExtension(_filePath);
            string suffix = !IsConvertOnlyMode && outputExt == _originalExtension ? "_compressed" : string.Empty;
            outputPath = Path.Combine(dir, $"{nameNoExt}{suffix}{outputExt}");

            int counter = 1;
            while (File.Exists(outputPath))
            {
                outputPath = Path.Combine(dir, $"{nameNoExt}{suffix}_{counter++}{outputExt}");
            }
        }

        try
        {
            // ── CONVERT FORMAT ONLY (no compression) ───────────────────────────
            if (IsConvertOnlyMode)
            {
                if (outputExt == _originalExtension)
                {
                    ResultText = "⚠️ Please select a different output format to convert to.";
                    return;
                }

                ProgressText = $"Converting {_originalExtension} → {outputExt}...";
                FileWatcherService.IgnoreOutputFile(outputPath);

                var result = await _engine.ConvertFormatAsync(_filePath, outputPath, outputExt);
                FileWatcherService.IgnoreOutputFile(outputPath);

                if (result.Success)
                {
                    OutputHistoryService.Instance.Record(result.FilePath, "Converted", result.OriginalSizeBytes, result.NewSizeBytes);
                    ResultText = $"✅ Converted! {result.OriginalSizeFormatted} → {result.CompressedSizeFormatted}\n→ {Path.GetFileName(outputPath)}";
                }
                else
                {
                    ResultText = $"❌ {result.Message ?? "Format conversion failed."}";
                }
                return;
            }

            // ── COMPRESS (with optional format change) ──────────────────────────
            long targetBytes = TargetSizeUnit == "MB"
                ? (long)TargetSize * 1024 * 1024
                : (long)TargetSize * 1024;

            ProgressText = $"Compressing to {TargetSize} {TargetSizeUnit}...";
            FileWatcherService.IgnoreOutputFile(outputPath);
            var compResult = await _engine.CompressFileAsync(_filePath, outputPath, targetBytes, outputExt, keepBackup: OutputMode == "replace");
            FileWatcherService.IgnoreOutputFile(outputPath);

            if (compResult.Success)
            {
                OutputHistoryService.Instance.Record(compResult.FilePath, "Compressed", compResult.OriginalSizeBytes, compResult.NewSizeBytes);
                double pct = compResult.OriginalSizeBytes > 0
                    ? 100.0 * (1.0 - (double)compResult.NewSizeBytes / compResult.OriginalSizeBytes)
                    : 0;
                string note = !string.IsNullOrEmpty(compResult.Message) ? $"{compResult.Message}\n" : string.Empty;
                ResultText = $"✅ Done! {compResult.OriginalSizeFormatted} → {compResult.CompressedSizeFormatted} ({pct:F1}% saved)\n{note}→ {Path.GetFileName(outputPath)}";
            }
            else
            {
                ResultText = $"❌ {compResult.Message ?? "Compression failed."}";
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error in FileCompressViewModel.CompressAsync");
            ResultText = $"❌ Error: {ex.Message}";
        }
        finally
        {
            IsProcessing = false;
            ProgressText = string.Empty;
        }
    }
}
