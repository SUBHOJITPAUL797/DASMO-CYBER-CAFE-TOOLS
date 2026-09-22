using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using Serilog;
using SmartSaver.Models;
using SmartSaver.Services;


namespace SmartSaver.ViewModels;

public class BatchCompressItem : ViewModelBase
{
    public string FilePath { get; }
    public string FileName => Path.GetFileName(FilePath);
    public long OriginalSizeBytes { get; }
    public string OriginalSizeFormatted => CompressionResult.FormatFileSize(OriginalSizeBytes);

    private string _status = "Ready";
    public string Status { get => _status; set => SetProperty(ref _status, value); }

    private string _resultSizeFormatted = "-";
    public string ResultSizeFormatted { get => _resultSizeFormatted; set => SetProperty(ref _resultSizeFormatted, value); }

    private bool _isCompleted;
    public bool IsCompleted { get => _isCompleted; set => SetProperty(ref _isCompleted, value); }

    private bool _isSuccess;
    public bool IsSuccess { get => _isSuccess; set => SetProperty(ref _isSuccess, value); }

    public BatchCompressItem(string filePath)
    {
        FilePath = filePath;
        OriginalSizeBytes = File.Exists(filePath) ? new FileInfo(filePath).Length : 0;
    }
}

public class BatchCompressViewModel : ViewModelBase
{
    private readonly List<string> _filePaths;
    private readonly CompressionEngine _engine;

    public ObservableCollection<BatchCompressItem> Items { get; } = new();

    public int TotalFileCount => Items.Count;
    public string TotalOriginalSizeFormatted => CompressionResult.FormatFileSize(Items.Sum(i => i.OriginalSizeBytes));

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
        set => SetProperty(ref _selectedOutputFormat, value);
    }

    private string _outputMode = "subfolder"; // "subfolder" or "replace"
    public string OutputMode
    {
        get => _outputMode;
        set => SetProperty(ref _outputMode, value);
    }

    private bool _isProcessing;
    public bool IsProcessing
    {
        get => _isProcessing;
        set { SetProperty(ref _isProcessing, value); OnPropertyChanged(nameof(CanCompress)); }
    }

    private int _processedCount;
    public int ProcessedCount
    {
        get => _processedCount;
        set { SetProperty(ref _processedCount, value); OnPropertyChanged(nameof(ProgressPercent)); }
    }

    public double ProgressPercent => TotalFileCount > 0 ? (100.0 * ProcessedCount / TotalFileCount) : 0;

    private string _progressText = string.Empty;
    public string ProgressText { get => _progressText; set => SetProperty(ref _progressText, value); }

    private string _resultSummaryText = string.Empty;
    public string ResultSummaryText
    {
        get => _resultSummaryText;
        set { SetProperty(ref _resultSummaryText, value); OnPropertyChanged(nameof(HasResultSummary)); }
    }

    public bool HasResultSummary => !string.IsNullOrEmpty(_resultSummaryText);
    public bool CanCompress => !IsProcessing && !IsMerging && TargetSize > 0 && TotalFileCount > 0;

    // --- PDF Merge Support ---
    /// <summary>True when every selected file is a .pdf — enables the Merge PDFs panel.</summary>
    public bool IsPdfOnlyBatch => _filePaths.Count > 1 &&
        _filePaths.All(p => Path.GetExtension(p).Equals(".pdf", StringComparison.OrdinalIgnoreCase));

    private bool _isMerging;
    public bool IsMerging
    {
        get => _isMerging;
        set { SetProperty(ref _isMerging, value); OnPropertyChanged(nameof(CanCompress)); OnPropertyChanged(nameof(CanMerge)); }
    }

    private string _mergedOutputName = "Merged_Document.pdf";
    public string MergedOutputName
    {
        get => _mergedOutputName;
        set => SetProperty(ref _mergedOutputName, value);
    }

    private string _mergeResultText = string.Empty;
    public string MergeResultText
    {
        get => _mergeResultText;
        set { SetProperty(ref _mergeResultText, value); OnPropertyChanged(nameof(HasMergeResult)); }
    }

    public bool HasMergeResult => !string.IsNullOrEmpty(_mergeResultText);
    public bool CanMerge => !IsProcessing && !IsMerging && IsPdfOnlyBatch;

    public ObservableCollection<string> SizeUnits { get; } = new() { "KB", "MB" };
    public ObservableCollection<string> OutputFormats { get; } = new() { "Same as input", ".jpg", ".jpeg", ".png", ".pdf" };

    public ICommand CompressAllCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand SetPresetSizeCommand { get; }
    public ICommand MergePdfsCommand { get; }

    public Action? RequestClose { get; set; }

    public BatchCompressViewModel(IEnumerable<string> filePaths)
    {
        _filePaths = filePaths.Distinct(StringComparer.OrdinalIgnoreCase).Where(File.Exists).ToList();

        foreach (var path in _filePaths)
        {
            Items.Add(new BatchCompressItem(path));
        }

        var imageCompressor = new ImageCompressor();
        var pdfCompressor = new PdfCompressor();
        var officeCompressor = new OfficeCompressor(imageCompressor);
        _engine = new CompressionEngine(imageCompressor, pdfCompressor, officeCompressor);

        var settings = SettingsManager.Instance.Current;
        _targetSize = settings.AutoCompress.TargetSizeKB;

        CompressAllCommand = new RelayCommand(async _ => await CompressAllAsync(), _ => CanCompress);
        CancelCommand = new RelayCommand(_ => RequestClose?.Invoke());
        SetPresetSizeCommand = new RelayCommand(param =>
        {
            if (param is string sizeStr && int.TryParse(sizeStr, out int size))
            {
                TargetSize = size;
                TargetSizeUnit = "KB";
            }
        });
        MergePdfsCommand = new RelayCommand(async _ => await MergePdfsAsync(), _ => CanMerge);

        // Auto-set output name from first file
        if (_filePaths.Count > 0)
            MergedOutputName = Path.GetFileNameWithoutExtension(_filePaths[0]) + "_Merged.pdf";
    }

    private async Task CompressAllAsync()
    {
        if (Items.Count == 0) return;

        IsProcessing = true;
        ResultSummaryText = string.Empty;
        ProcessedCount = 0;

        long totalOriginalBytes = Items.Sum(i => i.OriginalSizeBytes);
        long totalNewBytes = 0;
        int successCount = 0;

        long targetBytes = TargetSizeUnit == "MB"
            ? (long)TargetSize * 1024 * 1024
            : (long)TargetSize * 1024;

        try
        {
            for (int i = 0; i < Items.Count; i++)
            {
                var item = Items[i];
                item.Status = "Compressing...";
                ProgressText = $"Compressing file {i + 1} of {Items.Count}: {item.FileName}...";

                string origExt = Path.GetExtension(item.FilePath).ToLowerInvariant();
                string outputExt = SelectedOutputFormat == "Same as input" ? origExt : SelectedOutputFormat;

                string outputPath;
                if (OutputMode == "replace" && outputExt == origExt)
                {
                    outputPath = item.FilePath;
                }
                else
                {
                    string? dir = Path.GetDirectoryName(item.FilePath);
                    if (string.IsNullOrEmpty(dir)) dir = Environment.CurrentDirectory;
                    string targetDir = OutputMode == "subfolder" ? Path.Combine(dir, "_Compressed") : dir;
                    Directory.CreateDirectory(targetDir);

                    string nameNoExt = Path.GetFileNameWithoutExtension(item.FilePath);
                    string suffix = OutputMode == "subfolder" ? string.Empty : "_compressed";
                    outputPath = Path.Combine(targetDir, $"{nameNoExt}{suffix}{outputExt}");

                    int counter = 1;
                    while (File.Exists(outputPath) && OutputMode != "replace")
                    {
                        outputPath = Path.Combine(targetDir, $"{nameNoExt}{suffix}_{counter++}{outputExt}");
                    }
                }

                FileWatcherService.IgnoreOutputFile(outputPath);
                var result = await _engine.CompressFileAsync(
                    item.FilePath, outputPath, targetBytes, outputExt, keepBackup: OutputMode == "replace");
                FileWatcherService.IgnoreOutputFile(outputPath);

                if (result.Success)
                {
                    item.Status = string.IsNullOrEmpty(result.Message) ? "✅ Compressed" : "⚠️ Best Effort";
                    item.ResultSizeFormatted = result.CompressedSizeFormatted;
                    item.IsCompleted = true;
                    item.IsSuccess = true;
                    totalNewBytes += result.NewSizeBytes;
                    successCount++;

                    OutputHistoryService.Instance.Record(result.FilePath, "Batch Compressed", result.OriginalSizeBytes, result.NewSizeBytes);
                }
                else
                {
                    item.Status = "❌ Failed";
                    item.ResultSizeFormatted = item.OriginalSizeFormatted;
                    item.IsCompleted = true;
                    item.IsSuccess = false;
                    totalNewBytes += item.OriginalSizeBytes;
                }

                ProcessedCount = i + 1;
            }

            long totalSaved = Math.Max(0, totalOriginalBytes - totalNewBytes);
            double pctSaved = totalOriginalBytes > 0 ? (100.0 * totalSaved / totalOriginalBytes) : 0;

            string destLocation = OutputMode == "subfolder" ? "'_Compressed' subfolder" : "original folder";
            ResultSummaryText = $"✅ Batch Complete! {successCount}/{Items.Count} files compressed successfully in {destLocation}.\n" +
                                $"Saved {CompressionResult.FormatFileSize(totalSaved)} total ({pctSaved:F1}% reduction).";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error in BatchCompressViewModel.CompressAllAsync");
            ResultSummaryText = $"❌ Error during batch compression: {ex.Message}";
        }
        finally
        {
            IsProcessing = false;
            ProgressText = string.Empty;
        }
    }

    private async Task MergePdfsAsync()
    {
        if (_filePaths.Count < 2) return;

        IsMerging = true;
        MergeResultText = string.Empty;

        try
        {
            // Determine output path — save in _Compressed subfolder beside the first file
            string firstDir = Path.GetDirectoryName(_filePaths[0]) ?? Environment.CurrentDirectory;
            string outputDir = OutputMode == "subfolder"
                ? Path.Combine(firstDir, "_Compressed")
                : firstDir;

            Directory.CreateDirectory(outputDir);

            string safeName = string.IsNullOrWhiteSpace(MergedOutputName)
                ? "Merged_Document.pdf"
                : MergedOutputName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)
                    ? MergedOutputName
                    : MergedOutputName + ".pdf";

            string outputPath = Path.Combine(outputDir, safeName);

            // Avoid overwriting existing file
            int counter = 1;
            while (File.Exists(outputPath))
                outputPath = Path.Combine(outputDir, $"{Path.GetFileNameWithoutExtension(safeName)}_{counter++}.pdf");

            bool ok = await Task.Run(() =>
            {
                var merger = new PdfMerger();
                return merger.MergePdfs(_filePaths, outputPath);
            });

            if (ok)
            {
                long outSize = new FileInfo(outputPath).Length;
                MergeResultText = $"✅ Merged {_filePaths.Count} PDFs → {Path.GetFileName(outputPath)} ({CompressionResult.FormatFileSize(outSize)})";
            }
            else
            {
                MergeResultText = "❌ Merge failed. Check that all files are valid PDFs.";
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error merging PDFs");
            MergeResultText = $"❌ Error: {ex.Message}";
        }
        finally
        {
            IsMerging = false;
        }
    }
}
