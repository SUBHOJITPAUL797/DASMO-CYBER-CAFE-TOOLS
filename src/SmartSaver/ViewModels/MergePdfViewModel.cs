using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using Microsoft.Win32;
using Serilog;
using SmartSaver.Models;
using SmartSaver.Services;
using PdfSharpCore.Pdf.IO;

namespace SmartSaver.ViewModels;

public class MergePdfItemViewModel : ViewModelBase
{
    private int _orderNumber;
    public int OrderNumber
    {
        get => _orderNumber;
        set => SetProperty(ref _orderNumber, value);
    }

    public string FilePath { get; }
    public string FileName => Path.GetFileName(FilePath);
    public long FileSizeBytes { get; }
    public string FileSizeFormatted => CompressionResult.FormatFileSize(FileSizeBytes);
    public int PageCount { get; }

    public MergePdfItemViewModel(string filePath, int orderNumber)
    {
        FilePath = filePath;
        _orderNumber = orderNumber;

        if (File.Exists(filePath))
        {
            FileSizeBytes = new FileInfo(filePath).Length;
            try
            {
                using var doc = PdfReader.Open(filePath, PdfDocumentOpenMode.InformationOnly);
                PageCount = doc.PageCount;
            }
            catch
            {
                PageCount = 1;
            }
        }
    }
}

public class MergePdfViewModel : ViewModelBase
{
    private readonly CompressionEngine _engine;
    private ObservableCollection<MergePdfItemViewModel> _files = new();
    private MergePdfItemViewModel? _selectedFile;
    private bool _isCompressEnabled = false;
    private int _targetSize = 200;
    private string _targetSizeUnit = "KB";
    private string _outputFileName = "Merged_Document.pdf";
    private string _outputDirectory = string.Empty;
    private bool _isProcessing;
    private string _progressText = string.Empty;
    private string _resultText = string.Empty;
    private bool _isSuccess;
    private string _lastMergedPath = string.Empty;
    private bool _openOnComplete = true;

    public ObservableCollection<MergePdfItemViewModel> Files
    {
        get => _files;
        set => SetProperty(ref _files, value);
    }

    public MergePdfItemViewModel? SelectedFile
    {
        get => _selectedFile;
        set
        {
            if (SetProperty(ref _selectedFile, value))
            {
                (MoveUpCommand as RelayCommand)?.OnCanExecuteChanged();
                (MoveDownCommand as RelayCommand)?.OnCanExecuteChanged();
                (RemoveFileCommand as RelayCommand)?.OnCanExecuteChanged();
            }
        }
    }

    public bool IsCompressEnabled
    {
        get => _isCompressEnabled;
        set => SetProperty(ref _isCompressEnabled, value);
    }

    public int TargetSize
    {
        get => _targetSize;
        set => SetProperty(ref _targetSize, value);
    }

    public string TargetSizeUnit
    {
        get => _targetSizeUnit;
        set => SetProperty(ref _targetSizeUnit, value);
    }

    public string OutputFileName
    {
        get => _outputFileName;
        set => SetProperty(ref _outputFileName, value);
    }

    public string OutputDirectory
    {
        get => _outputDirectory;
        set => SetProperty(ref _outputDirectory, value);
    }

    public bool IsProcessing
    {
        get => _isProcessing;
        set
        {
            if (SetProperty(ref _isProcessing, value))
            {
                (MergeCommand as RelayCommand)?.OnCanExecuteChanged();
                (AddFilesCommand as RelayCommand)?.OnCanExecuteChanged();
            }
        }
    }

    public string ProgressText
    {
        get => _progressText;
        set => SetProperty(ref _progressText, value);
    }

    public string ResultText
    {
        get => _resultText;
        set => SetProperty(ref _resultText, value);
    }

    public bool IsSuccess
    {
        get => _isSuccess;
        set => SetProperty(ref _isSuccess, value);
    }

    public bool OpenOnComplete
    {
        get => _openOnComplete;
        set => SetProperty(ref _openOnComplete, value);
    }

    public string TotalFilesInfo => $"{Files.Count} file(s) | {Files.Sum(f => f.PageCount)} total page(s) | {CompressionResult.FormatFileSize(Files.Sum(f => f.FileSizeBytes))}";

    public ICommand AddFilesCommand { get; }
    public ICommand RemoveFileCommand { get; }
    public ICommand MoveUpCommand { get; }
    public ICommand MoveDownCommand { get; }
    public ICommand BrowseDirectoryCommand { get; }
    public ICommand PresetTargetCommand { get; }
    public ICommand MergeCommand { get; }
    public ICommand OpenResultCommand { get; }
    public ICommand CancelCommand { get; }

    public event EventHandler? RequestClose;

    public MergePdfViewModel(IEnumerable<string>? initialFiles = null)
    {
        var imageCompressor = new ImageCompressor();
        var pdfCompressor = new PdfCompressor();
        var officeCompressor = new OfficeCompressor(imageCompressor);
        _engine = new CompressionEngine(imageCompressor, pdfCompressor, officeCompressor);

        AddFilesCommand = new RelayCommand(_ => AddFiles(), _ => !IsProcessing);
        RemoveFileCommand = new RelayCommand(_ => RemoveSelectedFile(), _ => SelectedFile != null && !IsProcessing);
        MoveUpCommand = new RelayCommand(_ => MoveSelectedUp(), _ => CanMoveSelectedUp());
        MoveDownCommand = new RelayCommand(_ => MoveSelectedDown(), _ => CanMoveSelectedDown());
        BrowseDirectoryCommand = new RelayCommand(_ => BrowseDirectory(), _ => !IsProcessing);
        PresetTargetCommand = new RelayCommand(param => SetPreset(param));
        MergeCommand = new RelayCommand(async _ => await MergeAsync(), _ => Files.Count >= 2 && !IsProcessing);
        OpenResultCommand = new RelayCommand(_ => OpenResultFile(), _ => !string.IsNullOrEmpty(_lastMergedPath) && File.Exists(_lastMergedPath));
        CancelCommand = new RelayCommand(_ => RequestClose?.Invoke(this, EventArgs.Empty));

        if (initialFiles != null)
        {
            var pdfs = initialFiles.Where(f => File.Exists(f) && Path.GetExtension(f).Equals(".pdf", StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var pdf in pdfs)
            {
                Files.Add(new MergePdfItemViewModel(pdf, Files.Count + 1));
            }

            if (pdfs.Count > 0)
            {
                string firstDir = Path.GetDirectoryName(pdfs[0]) ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                OutputDirectory = firstDir;
                string firstBase = Path.GetFileNameWithoutExtension(pdfs[0]);
                OutputFileName = $"{firstBase}_merged.pdf";
            }
        }

        if (string.IsNullOrEmpty(OutputDirectory))
        {
            OutputDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        OnPropertyChanged(nameof(TotalFilesInfo));
    }

    public void AddFile(string path)
    {
        if (!string.IsNullOrEmpty(path))
            AddFiles(new[] { path });
    }

    public void AddFiles(IEnumerable<string>? paths = null)
    {
        if (paths == null)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Select PDF Files to Merge",
                Filter = "PDF Files (*.pdf)|*.pdf",
                Multiselect = true
            };

            if (dlg.ShowDialog() == true)
            {
                paths = dlg.FileNames;
            }
        }

        if (paths != null)
        {
            foreach (var p in paths)
            {
                if (File.Exists(p) && Path.GetExtension(p).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
                {
                    if (!Files.Any(f => f.FilePath.Equals(p, StringComparison.OrdinalIgnoreCase)))
                    {
                        Files.Add(new MergePdfItemViewModel(p, Files.Count + 1));
                    }
                }
            }

            if (string.IsNullOrEmpty(OutputDirectory) && Files.Count > 0)
            {
                OutputDirectory = Path.GetDirectoryName(Files[0].FilePath) ?? OutputDirectory;
                OutputFileName = $"{Path.GetFileNameWithoutExtension(Files[0].FilePath)}_merged.pdf";
            }

            RenumberItems();
            OnPropertyChanged(nameof(TotalFilesInfo));
            (MergeCommand as RelayCommand)?.OnCanExecuteChanged();
        }
    }

    private void RemoveSelectedFile()
    {
        if (SelectedFile != null)
        {
            Files.Remove(SelectedFile);
            SelectedFile = null;
            RenumberItems();
            OnPropertyChanged(nameof(TotalFilesInfo));
            (MergeCommand as RelayCommand)?.OnCanExecuteChanged();
        }
    }

    private bool CanMoveSelectedUp()
    {
        if (SelectedFile == null || IsProcessing) return false;
        int idx = Files.IndexOf(SelectedFile);
        return idx > 0;
    }

    private void MoveSelectedUp()
    {
        if (!CanMoveSelectedUp() || SelectedFile == null) return;
        int idx = Files.IndexOf(SelectedFile);
        Files.Move(idx, idx - 1);
        RenumberItems();
        (MoveUpCommand as RelayCommand)?.OnCanExecuteChanged();
        (MoveDownCommand as RelayCommand)?.OnCanExecuteChanged();
    }

    private bool CanMoveSelectedDown()
    {
        if (SelectedFile == null || IsProcessing) return false;
        int idx = Files.IndexOf(SelectedFile);
        return idx >= 0 && idx < Files.Count - 1;
    }

    private void MoveSelectedDown()
    {
        if (!CanMoveSelectedDown() || SelectedFile == null) return;
        int idx = Files.IndexOf(SelectedFile);
        Files.Move(idx, idx + 1);
        RenumberItems();
        (MoveUpCommand as RelayCommand)?.OnCanExecuteChanged();
        (MoveDownCommand as RelayCommand)?.OnCanExecuteChanged();
    }

    private void RenumberItems()
    {
        for (int i = 0; i < Files.Count; i++)
        {
            Files[i].OrderNumber = i + 1;
        }
    }

    private void SetPreset(object? param)
    {
        if (param is string val)
        {
            if (val == "ORIGINAL")
            {
                IsCompressEnabled = false;
            }
            else if (int.TryParse(val, out int kb))
            {
                IsCompressEnabled = true;
                if (kb >= 1000)
                {
                    TargetSize = kb / 1024;
                    TargetSizeUnit = "MB";
                }
                else
                {
                    TargetSize = kb;
                    TargetSizeUnit = "KB";
                }
            }
        }
    }

    private void BrowseDirectory()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select Destination Folder",
            InitialDirectory = OutputDirectory
        };

        if (dlg.ShowDialog() == true)
        {
            OutputDirectory = dlg.FolderName;
        }
    }

    private async Task MergeAsync()
    {
        if (Files.Count < 2)
        {
            ResultText = "❌ Please add at least 2 PDF files to merge.";
            IsSuccess = false;
            return;
        }

        IsProcessing = true;
        ResultText = string.Empty;
        ProgressText = "Merging PDF files...";
        IsSuccess = false;

        try
        {
            string outDir = string.IsNullOrWhiteSpace(OutputDirectory)
                ? Path.GetDirectoryName(Files[0].FilePath)!
                : OutputDirectory;

            Directory.CreateDirectory(outDir);

            string outName = string.IsNullOrWhiteSpace(OutputFileName)
                ? $"{Path.GetFileNameWithoutExtension(Files[0].FilePath)}_merged.pdf"
                : OutputFileName;

            if (!outName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                outName += ".pdf";

            string fullOutputPath = Path.Combine(outDir, outName);

            // Avoid overwriting without notice if name already exists
            int counter = 1;
            string baseNoExt = Path.GetFileNameWithoutExtension(outName);
            while (File.Exists(fullOutputPath))
            {
                fullOutputPath = Path.Combine(outDir, $"{baseNoExt}_{counter++}.pdf");
            }

            long? targetBytes = null;
            if (IsCompressEnabled)
            {
                targetBytes = TargetSizeUnit == "MB"
                    ? (long)TargetSize * 1024 * 1024
                    : (long)TargetSize * 1024;
            }

            FileWatcherService.IgnoreOutputFile(fullOutputPath);

            var fileList = Files.Select(f => f.FilePath).ToList();
            var result = await _engine.MergePdfsAsync(fileList, fullOutputPath, targetBytes);

            if (result.Success)
            {
                OutputHistoryService.Instance.Record(fullOutputPath, "Merged PDFs", result.OriginalSizeBytes, result.NewSizeBytes);
                _lastMergedPath = fullOutputPath;
                IsSuccess = true;
                ResultText = $"✅ Successfully merged {Files.Count} PDF files!\n{result.Message}\n→ {Path.GetFileName(fullOutputPath)}";
                
                if (OpenOnComplete && File.Exists(fullOutputPath))
                {
                    OpenResultFile();
                }
            }
            else
            {
                IsSuccess = false;
                ResultText = $"❌ {result.Message ?? "Failed to merge PDF files."}";
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error in MergePdfViewModel.MergeAsync");
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
        if (!string.IsNullOrEmpty(_lastMergedPath) && File.Exists(_lastMergedPath))
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = _lastMergedPath,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Could not open merged file: {Path}", _lastMergedPath);
            }
        }
    }
}
