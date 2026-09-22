using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using Serilog;
using SmartSaver.Models;
using SmartSaver.Services;

namespace SmartSaver.ViewModels;

public class ImageToPdfItem : ViewModelBase
{
    public string FilePath { get; set; } = string.Empty;
    public string FileName => Path.GetFileName(FilePath);
    public long FileSizeBytes { get; set; }
    public string FileSizeFormatted => CompressionResult.FormatFileSize(FileSizeBytes);
}

public class ImageToPdfViewModel : ViewModelBase
{
    private readonly CompressionEngine _engine;

    public ObservableCollection<ImageToPdfItem> Files { get; } = new();

    private ImageToPdfItem? _selectedFile;
    public ImageToPdfItem? SelectedFile
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

    private bool _fitToA4 = true;
    public bool FitToA4
    {
        get => _fitToA4;
        set => SetProperty(ref _fitToA4, value);
    }

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

    private string _outputFileName = "Images_Converted.pdf";
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
                (ConvertCommand as RelayCommand)?.OnCanExecuteChanged();
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

    public ICommand ConvertCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand AddFilesCommand { get; }
    public ICommand RemoveFileCommand { get; }
    public ICommand MoveUpCommand { get; }
    public ICommand MoveDownCommand { get; }
    public ICommand BrowseDirectoryCommand { get; }
    public ICommand SetPresetSizeCommand { get; }
    public ICommand OpenResultCommand { get; }

    public Action? RequestClose { get; set; }

    public ImageToPdfViewModel(IEnumerable<string>? initialFiles = null)
    {
        var imgComp = new ImageCompressor();
        var pdfComp = new PdfCompressor();
        var offComp = new OfficeCompressor(imgComp);
        _engine = new CompressionEngine(imgComp, pdfComp, offComp);

        if (initialFiles != null)
        {
            foreach (var f in initialFiles)
            {
                AddFile(f);
            }
        }

        if (Files.Count > 0)
        {
            OutputDirectory = Path.GetDirectoryName(Files[0].FilePath) ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            OutputFileName = $"{Path.GetFileNameWithoutExtension(Files[0].FilePath)}_converted.pdf";
        }
        else
        {
            OutputDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        ConvertCommand = new RelayCommand(async _ => await ConvertAsync(), _ => !IsProcessing && Files.Count > 0);
        CancelCommand = new RelayCommand(_ => RequestClose?.Invoke());
        AddFilesCommand = new RelayCommand(_ => AddFilesFromDialog());
        RemoveFileCommand = new RelayCommand(_ => RemoveSelectedFile(), _ => SelectedFile != null);
        MoveUpCommand = new RelayCommand(_ => MoveSelectedFile(-1), _ => CanMove(-1));
        MoveDownCommand = new RelayCommand(_ => MoveSelectedFile(1), _ => CanMove(1));
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
    }

    public void AddFile(string path)
    {
        if (File.Exists(path) && !Files.Any(f => f.FilePath.Equals(path, StringComparison.OrdinalIgnoreCase)))
        {
            Files.Add(new ImageToPdfItem
            {
                FilePath = path,
                FileSizeBytes = new FileInfo(path).Length
            });
            (ConvertCommand as RelayCommand)?.OnCanExecuteChanged();
        }
    }

    private void AddFilesFromDialog()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Add Image Files to Convert to PDF",
            Filter = "All Supported Images (*.jpg;*.jpeg;*.jpe;*.jfif;*.png;*.webp;*.bmp;*.gif;*.tiff;*.tif)|*.jpg;*.jpeg;*.jpe;*.jfif;*.png;*.webp;*.bmp;*.gif;*.tiff;*.tif|JPEG Images (*.jpg;*.jpeg;*.jfif;*.jpe)|*.jpg;*.jpeg;*.jfif;*.jpe|PNG Images (*.png)|*.png|WebP Images (*.webp)|*.webp|BMP Images (*.bmp)|*.bmp|TIFF Images (*.tiff;*.tif)|*.tiff;*.tif|All Files (*.*)|*.*",
            Multiselect = true
        };
        if (dlg.ShowDialog() == true)
        {
            foreach (var f in dlg.FileNames) AddFile(f);
        }
    }

    private void RemoveSelectedFile()
    {
        if (SelectedFile != null)
        {
            Files.Remove(SelectedFile);
            SelectedFile = null;
            (ConvertCommand as RelayCommand)?.OnCanExecuteChanged();
        }
    }

    private bool CanMove(int delta)
    {
        if (SelectedFile == null) return false;
        int idx = Files.IndexOf(SelectedFile);
        int target = idx + delta;
        return target >= 0 && target < Files.Count;
    }

    private void MoveSelectedFile(int delta)
    {
        if (SelectedFile == null) return;
        int idx = Files.IndexOf(SelectedFile);
        int target = idx + delta;
        if (target < 0 || target >= Files.Count) return;

        var item = SelectedFile;
        Files.Move(idx, target);
        SelectedFile = item;
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

    private async Task ConvertAsync()
    {
        if (Files.Count == 0) return;

        IsProcessing = true;
        ProgressText = "Converting images into A4 PDF...";
        ResultText = string.Empty;
        IsSuccess = false;

        try
        {
            Directory.CreateDirectory(OutputDirectory);

            string outName = string.IsNullOrWhiteSpace(OutputFileName) ? "Images_Converted.pdf" : OutputFileName;
            if (!outName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) outName += ".pdf";

            string fullOutPath = Path.Combine(OutputDirectory, outName);
            int counter = 1;
            string baseNoExt = Path.GetFileNameWithoutExtension(outName);
            while (File.Exists(fullOutPath))
            {
                fullOutPath = Path.Combine(OutputDirectory, $"{baseNoExt}_{counter++}.pdf");
            }

            long? targetBytes = null;
            if (IsCompressEnabled)
            {
                targetBytes = TargetSizeUnit == "MB"
                    ? (long)TargetSizeKB * 1024 * 1024
                    : (long)TargetSizeKB * 1024;
            }

            var imagePaths = Files.Select(f => f.FilePath).ToList();
            var res = await _engine.ImagesToPdfAsync(imagePaths, fullOutPath, FitToA4, targetBytes);

            if (res.Success && File.Exists(fullOutPath))
            {
                OutputHistoryService.Instance.Record(fullOutPath, "Images -> PDF", res.OriginalSizeBytes, res.NewSizeBytes);
                _lastResultPath = fullOutPath;
                IsSuccess = true;
                ResultText = $"✅ {res.Message}\n→ {Path.GetFileName(fullOutPath)}";

                if (OpenOnComplete)
                {
                    OpenResultFile();
                }
            }
            else
            {
                IsSuccess = false;
                ResultText = $"❌ {res.Message ?? "Failed to convert images to PDF."}";
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to convert images to PDF");
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
