using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Serilog;
using SmartSaver.Models;
using SmartSaver.Services;
using PdfSharpCore.Pdf.IO;
using WpfClipboard = System.Windows.Clipboard;

namespace SmartSaver.ViewModels;

public class PdfToImageViewModel : ViewModelBase
{
    private string _filePath = string.Empty;
    private readonly CompressionEngine _engine;

    public string FilePath => _filePath;
    public string FileName => string.IsNullOrEmpty(_filePath) ? "No file selected" : Path.GetFileName(_filePath);

    private long _fileSizeBytes;
    public long FileSizeBytes
    {
        get => _fileSizeBytes;
        set => SetProperty(ref _fileSizeBytes, value);
    }
    public string FileSizeFormatted => CompressionResult.FormatFileSize(FileSizeBytes);

    private int _totalPages;
    public int TotalPages
    {
        get => _totalPages;
        set => SetProperty(ref _totalPages, value);
    }

    public ObservableCollection<string> Formats { get; } = new() 
    { 
        ".jpg", 
        ".jpeg", 
        ".png", 
        ".webp", 
        ".bmp", 
        ".tiff" 
    };

    private string _selectedFormat = ".jpg";
    public string SelectedFormat
    {
        get => _selectedFormat;
        set => SetProperty(ref _selectedFormat, value);
    }

    public ObservableCollection<int> DpiOptions { get; } = new() 
    { 
        72, 
        100, 
        150, 
        200, 
        300, 
        400, 
        600, 
        1200 
    };

    private int _selectedDpi = 300;
    public int SelectedDpi
    {
        get => _selectedDpi;
        set => SetProperty(ref _selectedDpi, value);
    }

    private string _outputDirectory = string.Empty;
    public string OutputDirectory
    {
        get => _outputDirectory;
        set
        {
            if (SetProperty(ref _outputDirectory, value))
            {
                OnPropertyChanged(nameof(EffectiveOutputDirectory));
            }
        }
    }

    private bool _createSubfolder;
    public bool CreateSubfolder
    {
        get => _createSubfolder;
        set
        {
            if (SetProperty(ref _createSubfolder, value))
            {
                OnPropertyChanged(nameof(EffectiveOutputDirectory));
            }
        }
    }

    public string SubfolderNamePreview => !string.IsNullOrEmpty(_filePath)
        ? $"{Path.GetFileNameWithoutExtension(_filePath)}_Images"
        : "[PDF_Name]_Images";

    public string EffectiveOutputDirectory
    {
        get
        {
            string baseDir = !string.IsNullOrWhiteSpace(OutputDirectory)
                ? OutputDirectory
                : (!string.IsNullOrEmpty(_filePath) ? (Path.GetDirectoryName(_filePath) ?? string.Empty) : string.Empty);

            if (string.IsNullOrWhiteSpace(baseDir))
                baseDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            if (CreateSubfolder && !string.IsNullOrEmpty(_filePath))
            {
                return Path.Combine(baseDir, SubfolderNamePreview);
            }

            return baseDir;
        }
    }

    private bool _openFolderOnComplete = true;
    public bool OpenFolderOnComplete
    {
        get => _openFolderOnComplete;
        set => SetProperty(ref _openFolderOnComplete, value);
    }

    private bool _autoCopyPathToClipboard = true;
    public bool AutoCopyPathToClipboard
    {
        get => _autoCopyPathToClipboard;
        set => SetProperty(ref _autoCopyPathToClipboard, value);
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
    public string LastResultPath
    {
        get => _lastResultPath;
        set => SetProperty(ref _lastResultPath, value);
    }

    public List<string> CreatedFiles { get; } = new();

    public ICommand ConvertCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand BrowseDirectoryCommand { get; }
    public ICommand ResetDirectoryCommand { get; }
    public ICommand OpenResultFolderCommand { get; }
    public ICommand SelectInExplorerCommand { get; }
    public ICommand CopyPathCommand { get; }

    public Action? RequestClose { get; set; }

    public PdfToImageViewModel(string filePath)
    {
        var imgComp = new ImageCompressor();
        var pdfComp = new PdfCompressor();
        var offComp = new OfficeCompressor(imgComp);
        _engine = new CompressionEngine(imgComp, pdfComp, offComp);

        ConvertCommand = new RelayCommand(async _ => await ConvertAsync(), _ => !IsProcessing && TotalPages > 0);
        CancelCommand = new RelayCommand(_ => RequestClose?.Invoke());
        BrowseDirectoryCommand = new RelayCommand(_ => BrowseDirectory());
        ResetDirectoryCommand = new RelayCommand(_ => ResetDirectory());
        OpenResultFolderCommand = new RelayCommand(_ => OpenResultFolder());
        SelectInExplorerCommand = new RelayCommand(_ => SelectFileInExplorer());
        CopyPathCommand = new RelayCommand(_ => CopyPathToClipboard());

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
        try
        {
            using var doc = PdfReader.Open(filePath, PdfDocumentOpenMode.InformationOnly);
            TotalPages = doc.PageCount;
        }
        catch
        {
            TotalPages = 1;
        }

        OutputDirectory = Path.GetDirectoryName(filePath) ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        OnPropertyChanged(nameof(FilePath));
        OnPropertyChanged(nameof(FileName));
        OnPropertyChanged(nameof(FileSizeFormatted));
        OnPropertyChanged(nameof(SubfolderNamePreview));
        OnPropertyChanged(nameof(EffectiveOutputDirectory));
        (ConvertCommand as RelayCommand)?.OnCanExecuteChanged();
    }

    private void ResetDirectory()
    {
        if (!string.IsNullOrEmpty(_filePath) && File.Exists(_filePath))
        {
            OutputDirectory = Path.GetDirectoryName(_filePath) ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }
    }

    private void BrowseDirectory()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select Folder for Converted Images",
            InitialDirectory = OutputDirectory
        };
        if (dlg.ShowDialog() == true)
        {
            OutputDirectory = dlg.FolderName;
        }
    }

    private async Task ConvertAsync()
    {
        IsProcessing = true;
        ProgressText = "Extracting pages to high-res images...";
        ResultText = string.Empty;
        IsSuccess = false;
        CreatedFiles.Clear();

        try
        {
            string targetDir = EffectiveOutputDirectory;
            if (string.IsNullOrWhiteSpace(targetDir))
            {
                targetDir = Path.GetDirectoryName(_filePath) ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            }
            Directory.CreateDirectory(targetDir);

            var createdFiles = await _engine.PdfToImagesAsync(_filePath, targetDir, SelectedFormat, SelectedDpi);

            if (createdFiles.Count > 0)
            {
                CreatedFiles.AddRange(createdFiles);
                LastResultPath = createdFiles[0];

                foreach (var f in createdFiles)
                {
                    OutputHistoryService.Instance.Record(f, "PDF -> Image");
                }

                IsSuccess = true;
                ResultText = $"✅ Successfully exported {createdFiles.Count} page(s) as {SelectedFormat.ToUpperInvariant()} ({SelectedDpi} DPI)!\n📁 {Path.GetFileName(LastResultPath)}";

                // Auto copy path to clipboard for instant portal upload
                if (AutoCopyPathToClipboard && !string.IsNullOrEmpty(LastResultPath))
                {
                    try
                    {
                        if (System.Windows.Application.Current?.Dispatcher != null)
                        {
                            System.Windows.Application.Current.Dispatcher.Invoke(() => WpfClipboard.SetText(LastResultPath));
                        }
                        else
                        {
                            WpfClipboard.SetText(LastResultPath);
                        }
                        ResultText += "\n📋 (File path copied to clipboard — Ready to Paste into Portal!)";
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "Failed to copy converted path to clipboard");
                    }
                }

                // Open folder and select / highlight output file in Windows Explorer
                if (OpenFolderOnComplete && !string.IsNullOrEmpty(LastResultPath))
                {
                    SelectFileInExplorer();
                }
            }
            else
            {
                IsSuccess = false;
                ResultText = "❌ Failed to convert PDF pages to images.";
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to convert PDF to images");
            IsSuccess = false;
            ResultText = $"❌ Error: {ex.Message}";
        }
        finally
        {
            IsProcessing = false;
            ProgressText = string.Empty;
        }
    }

    private void SelectFileInExplorer()
    {
        string target = !string.IsNullOrEmpty(LastResultPath) && File.Exists(LastResultPath)
            ? LastResultPath
            : EffectiveOutputDirectory;

        if (string.IsNullOrEmpty(target)) return;

        try
        {
            if (File.Exists(target))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{target}\"",
                    UseShellExecute = true
                });
            }
            else if (Directory.Exists(target))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = target,
                    UseShellExecute = true
                });
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to select file in Explorer: {Target}", target);
        }
    }

    private void OpenResultFolder()
    {
        string target = EffectiveOutputDirectory;
        if (!string.IsNullOrEmpty(target) && Directory.Exists(target))
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = target,
                    UseShellExecute = true
                });
            }
            catch { }
        }
    }

    private void CopyPathToClipboard()
    {
        string path = !string.IsNullOrEmpty(LastResultPath) ? LastResultPath : EffectiveOutputDirectory;
        if (!string.IsNullOrEmpty(path))
        {
            try
            {
                if (System.Windows.Application.Current?.Dispatcher != null)
                {
                    System.Windows.Application.Current.Dispatcher.Invoke(() => WpfClipboard.SetText(path));
                }
                else
                {
                    WpfClipboard.SetText(path);
                }
            }
            catch { }
        }
    }
}
