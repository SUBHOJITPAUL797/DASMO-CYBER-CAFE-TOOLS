using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Serilog;
using SmartSaver.Models;
using SmartSaver.Services;

namespace SmartSaver.ViewModels;

public class PhotoStampViewModel : ViewModelBase
{
    private string _filePath = string.Empty;
    private readonly PhotoStampService _stampService;

    public string FilePath => _filePath;
    public string FileName => string.IsNullOrEmpty(_filePath) ? "No file selected" : Path.GetFileName(_filePath);

    private long _fileSizeBytes;
    public long FileSizeBytes
    {
        get => _fileSizeBytes;
        set => SetProperty(ref _fileSizeBytes, value);
    }
    public string FileSizeFormatted => CompressionResult.FormatFileSize(FileSizeBytes);

    private string _candidateName = string.Empty;
    public string CandidateName
    {
        get => _candidateName;
        set => SetProperty(ref _candidateName, value);
    }

    private string _dateText = DateTime.Today.ToString("dd-MM-yyyy");
    public string DateText
    {
        get => _dateText;
        set
        {
            if (SetProperty(ref _dateText, value))
            {
                OnPropertyChanged(nameof(DateOfPhoto));
            }
        }
    }

    public string DateOfPhoto
    {
        get => _dateText;
        set => DateText = value;
    }

    public ObservableCollection<string> DatePrefixes { get; } = new() { "DOP:", "DOB:", "DATE:" };

    private string _selectedDatePrefix = "DOP:";
    public string SelectedDatePrefix
    {
        get => _selectedDatePrefix;
        set => SetProperty(ref _selectedDatePrefix, value);
    }

    private bool _standardPassportDimensions = true;
    public bool StandardPassportDimensions
    {
        get => _standardPassportDimensions;
        set => SetProperty(ref _standardPassportDimensions, value);
    }

    private bool _isCompressEnabled = true;
    public bool IsCompressEnabled
    {
        get => _isCompressEnabled;
        set => SetProperty(ref _isCompressEnabled, value);
    }

    private int _targetSizeKB = 50;
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
                (StampCommand as RelayCommand)?.OnCanExecuteChanged();
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

    public ICommand StampCommand { get; }
    public ICommand PrintPhotoCommand { get; }
    public ICommand PrintSheetCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand BrowseDirectoryCommand { get; }
    public ICommand SetPresetSizeCommand { get; }
    public ICommand OpenResultCommand { get; }

    public Action? RequestClose { get; set; }

    public PhotoStampViewModel(string filePath)
    {
        var imgComp = new ImageCompressor();
        _stampService = new PhotoStampService(imgComp);

        StampCommand = new RelayCommand(async _ => await StampAsync(), _ => !IsProcessing && FileSizeBytes > 0);
        PrintPhotoCommand = new RelayCommand(_ =>
        {
            string pathToPrint = !string.IsNullOrEmpty(_lastResultPath) && File.Exists(_lastResultPath) ? _lastResultPath : _filePath;
            if (File.Exists(pathToPrint))
            {
                byte[] bytes = File.ReadAllBytes(pathToPrint);
                using var ms = new MemoryStream(bytes);
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = ms;
                bmp.EndInit();
                bmp.Freeze();
                PrintService.PrintImageDirect(bmp, "Candidate_Passport_Photo", 3.5, 4.5);
            }
        }, _ => FileSizeBytes > 0);

        PrintSheetCommand = new RelayCommand(param =>
        {
            int copies = (param is string s && int.TryParse(s, out int c)) ? c : 8;
            string pathToPrint = !string.IsNullOrEmpty(_lastResultPath) && File.Exists(_lastResultPath) ? _lastResultPath : _filePath;
            if (File.Exists(pathToPrint))
            {
                byte[] bytes = File.ReadAllBytes(pathToPrint);
                using var ms = new MemoryStream(bytes);
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = ms;
                bmp.EndInit();
                bmp.Freeze();
                PrintService.PrintPassportPhotoSheet(bmp, copies, "4x6", CandidateName);
            }
        }, _ => FileSizeBytes > 0);

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
        string baseName = Path.GetFileNameWithoutExtension(filePath);
        OutputFileName = $"{baseName}_stamped.jpg";

        OnPropertyChanged(nameof(FilePath));
        OnPropertyChanged(nameof(FileName));
        OnPropertyChanged(nameof(FileSizeFormatted));
        (StampCommand as RelayCommand)?.OnCanExecuteChanged();
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

    private async Task StampAsync()
    {
        IsProcessing = true;
        ProgressText = "Applying candidate name and date stamp...";
        ResultText = string.Empty;
        IsSuccess = false;

        try
        {
            Directory.CreateDirectory(OutputDirectory);

            string outName = string.IsNullOrWhiteSpace(OutputFileName) ? $"{Path.GetFileNameWithoutExtension(_filePath)}_stamped.jpg" : OutputFileName;
            if (!outName.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) && !outName.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase))
            {
                outName += ".jpg";
            }

            string fullOutPath = Path.Combine(OutputDirectory, outName);
            int counter = 1;
            string baseNoExt = Path.GetFileNameWithoutExtension(outName);
            while (File.Exists(fullOutPath))
            {
                fullOutPath = Path.Combine(OutputDirectory, $"{baseNoExt}_{counter++}.jpg");
            }

            long? targetBytes = null;
            if (IsCompressEnabled)
            {
                targetBytes = TargetSizeUnit == "MB"
                    ? (long)TargetSizeKB * 1024 * 1024
                    : (long)TargetSizeKB * 1024;
            }

            bool ok = await Task.Run(() => _stampService.StampPhoto(
                _filePath, fullOutPath, CandidateName, DateText, SelectedDatePrefix, StandardPassportDimensions, targetBytes));

            if (ok && File.Exists(fullOutPath))
            {
                long newSize = new FileInfo(fullOutPath).Length;
                OutputHistoryService.Instance.Record(fullOutPath, "Photo Stamped", FileSizeBytes, newSize);
                _lastResultPath = fullOutPath;
                IsSuccess = true;
                ResultText = $"✅ Stamped photo created ({CompressionResult.FormatFileSize(newSize)})!\n→ {Path.GetFileName(fullOutPath)}";

                if (OpenOnComplete)
                {
                    OpenResultFile();
                }
            }
            else
            {
                IsSuccess = false;
                ResultText = "❌ Failed to stamp photo.";
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed in PhotoStampViewModel");
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
