using System.IO;
using System.Collections.ObjectModel;
using System.Windows.Input;
using SmartSaver.Models;
using SmartSaver.Services;
using System.Windows.Forms; // Requires WindowsForms integration or similar, will use a placeholder or dialog helper.

namespace SmartSaver.ViewModels;

public class SettingsViewModel : ViewModelBase
{
    private readonly SettingsManager _settingsManager = SettingsManager.Instance;
    private AppSettings _settings;

    // Auto-Compress Settings
    private bool _autoCompressEnabled;
    public bool AutoCompressEnabled { get => _autoCompressEnabled; set => SetProperty(ref _autoCompressEnabled, value); }

    private string _actionOnNewFile = "prompt";
    public string ActionOnNewFile { get => _actionOnNewFile; set => SetProperty(ref _actionOnNewFile, value); }

    public ObservableCollection<string> WatchFolders { get; } = new();
    
    private string _selectedWatchFolder = string.Empty;
    public string SelectedWatchFolder { get => _selectedWatchFolder; set { SetProperty(ref _selectedWatchFolder, value); ((RelayCommand)RemoveWatchFolderCommand).OnCanExecuteChanged(); } }

    private int _targetSize;
    public int TargetSize { get => _targetSize; set => SetProperty(ref _targetSize, value); }

    private string _targetSizeUnit = "KB";
    public string TargetSizeUnit { get => _targetSizeUnit; set => SetProperty(ref _targetSizeUnit, value); }

    private bool _keepOriginalBackup;
    public bool KeepOriginalBackup { get => _keepOriginalBackup; set => SetProperty(ref _keepOriginalBackup, value); }

    private bool _compressPdf;
    public bool CompressPdf { get => _compressPdf; set => SetProperty(ref _compressPdf, value); }

    private bool _compressJpeg;
    public bool CompressJpeg { get => _compressJpeg; set => SetProperty(ref _compressJpeg, value); }

    private bool _compressPng;
    public bool CompressPng { get => _compressPng; set => SetProperty(ref _compressPng, value); }

    private bool _compressDocx;
    public bool CompressDocx { get => _compressDocx; set => SetProperty(ref _compressDocx, value); }

    private bool _compressXlsx;
    public bool CompressXlsx { get => _compressXlsx; set => SetProperty(ref _compressXlsx, value); }

    // Image Resize Settings
    private string _defaultMode = "ask";
    public string DefaultMode { get => _defaultMode; set => SetProperty(ref _defaultMode, value); }

    private int _defaultTargetSize;
    public int DefaultTargetSize { get => _defaultTargetSize; set => SetProperty(ref _defaultTargetSize, value); }

    private string _defaultTargetSizeUnit = "KB";
    public string DefaultTargetSizeUnit { get => _defaultTargetSizeUnit; set => SetProperty(ref _defaultTargetSizeUnit, value); }

    private int _defaultWidth;
    public int DefaultWidth { get => _defaultWidth; set => SetProperty(ref _defaultWidth, value); }

    private int _defaultHeight;
    public int DefaultHeight { get => _defaultHeight; set => SetProperty(ref _defaultHeight, value); }

    private bool _maintainAspectRatio;
    public bool MaintainAspectRatio { get => _maintainAspectRatio; set => SetProperty(ref _maintainAspectRatio, value); }

    private string _outputMode = "newFile";
    public string OutputMode { get => _outputMode; set => SetProperty(ref _outputMode, value); }

    private string _outputSuffix = "_resized";
    public string OutputSuffix { get => _outputSuffix; set => SetProperty(ref _outputSuffix, value); }

    private int _minimumJpegQuality;
    public int MinimumJpegQuality { get => _minimumJpegQuality; set => SetProperty(ref _minimumJpegQuality, value); }

    // General Settings
    private bool _startWithWindows;
    public bool StartWithWindows { get => _startWithWindows; set => SetProperty(ref _startWithWindows, value); }

    private bool _showTrayIcon;
    public bool ShowTrayIcon { get => _showTrayIcon; set => SetProperty(ref _showTrayIcon, value); }

    private bool _enableNotifications;
    public bool EnableNotifications { get => _enableNotifications; set => SetProperty(ref _enableNotifications, value); }

    private bool _enableExplorerPdfPreview = true;
    public bool EnableExplorerPdfPreview { get => _enableExplorerPdfPreview; set => SetProperty(ref _enableExplorerPdfPreview, value); }

    private string _logLevel = "Info";
    public string LogLevel { get => _logLevel; set => SetProperty(ref _logLevel, value); }

    public ICommand SaveCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand AddWatchFolderCommand { get; }
    public ICommand RemoveWatchFolderCommand { get; }
    public ICommand ResetToDefaultsCommand { get; }
    public ICommand OpenLogFolderCommand { get; }
    public ICommand FixPdfPreviewCommand { get; }

    public Action? RequestClose { get; set; }
    public Action<object, EventArgs>? RequestSave { get; set; }

    public ObservableCollection<string> LogLevels { get; } = new() { "Info", "Debug", "Off" };
    public ObservableCollection<string> SizeUnits { get; } = new() { "KB", "MB" };

    public SettingsViewModel()
    {
        _settings = _settingsManager.Current;
        LoadSettings();

        SaveCommand = new RelayCommand(_ => SaveSettings());
        CancelCommand = new RelayCommand(_ => RequestClose?.Invoke());
        AddWatchFolderCommand = new RelayCommand(_ => AddWatchFolder());
        RemoveWatchFolderCommand = new RelayCommand(_ => RemoveWatchFolder(), _ => !string.IsNullOrEmpty(SelectedWatchFolder));
        ResetToDefaultsCommand = new RelayCommand(_ => ResetToDefaults());
        OpenLogFolderCommand = new RelayCommand(_ => OpenLogFolder());
        FixPdfPreviewCommand = new RelayCommand(_ => FixPdfPreview());
    }

    private void FixPdfPreview()
    {
        bool ok = Helpers.RegistryHelper.FixExplorerPdfPreviewHandler();
        if (ok)
        {
            System.Windows.MessageBox.Show("✅ Explorer PDF Preview Pane Fixed Successfully!\n\nPDF pages will now preview live on the right side pane of File Explorer & portal dialogs without security warnings.", "DASMO CYBER COMPRESSOR", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
        }
        else
        {
            System.Windows.MessageBox.Show("Could not apply registry fix automatically.", "DASMO CYBER COMPRESSOR", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }
    }

    private void LoadSettings()
    {
        AutoCompressEnabled = _settings.AutoCompress.Enabled;
        ActionOnNewFile = string.IsNullOrEmpty(_settings.AutoCompress.ActionOnNewFile) ? "prompt" : _settings.AutoCompress.ActionOnNewFile;
        
        WatchFolders.Clear();
        foreach (var folder in _settings.AutoCompress.WatchFolders)
        {
            WatchFolders.Add(folder);
        }

        int targetKB = _settings.AutoCompress.TargetSizeKB;
        if (targetKB >= 1024 && targetKB % 1024 == 0)
        {
            TargetSize = targetKB / 1024;
            TargetSizeUnit = "MB";
        }
        else
        {
            TargetSize = targetKB;
            TargetSizeUnit = "KB";
        }

        KeepOriginalBackup = _settings.AutoCompress.KeepOriginalBackup;

        var exts = _settings.AutoCompress.SupportedExtensions;
        CompressPdf = exts.Contains(".pdf");
        CompressJpeg = exts.Contains(".jpg") || exts.Contains(".jpeg");
        CompressPng = exts.Contains(".png");
        CompressDocx = exts.Contains(".docx");
        CompressXlsx = exts.Contains(".xlsx");

        DefaultMode = _settings.ImageResize.DefaultMode;
        
        int defTargetKB = _settings.ImageResize.DefaultTargetSizeKB;
        if (defTargetKB >= 1024 && defTargetKB % 1024 == 0)
        {
            DefaultTargetSize = defTargetKB / 1024;
            DefaultTargetSizeUnit = "MB";
        }
        else
        {
            DefaultTargetSize = defTargetKB;
            DefaultTargetSizeUnit = "KB";
        }

        DefaultWidth = _settings.ImageResize.DefaultWidth;
        DefaultHeight = _settings.ImageResize.DefaultHeight;
        MaintainAspectRatio = _settings.ImageResize.MaintainAspectRatio;
        OutputMode = _settings.ImageResize.OutputMode;
        OutputSuffix = _settings.ImageResize.OutputSuffix;
        MinimumJpegQuality = _settings.ImageResize.MinimumJpegQuality;

        StartWithWindows = _settings.General.StartWithWindows;
        ShowTrayIcon = _settings.General.ShowTrayIcon;
        EnableNotifications = _settings.General.EnableNotifications;
        EnableExplorerPdfPreview = _settings.General.EnableExplorerPdfPreview;
        LogLevel = _settings.General.LogLevel;
    }

    private void SaveSettings()
    {
        _settingsManager.Update(settings =>
        {
            settings.AutoCompress.Enabled = AutoCompressEnabled;
            settings.AutoCompress.ActionOnNewFile = ActionOnNewFile;
            settings.AutoCompress.WatchFolders = WatchFolders.ToList();
            
            settings.AutoCompress.TargetSizeKB = TargetSizeUnit == "MB" ? TargetSize * 1024 : TargetSize;
            settings.AutoCompress.KeepOriginalBackup = KeepOriginalBackup;

            var exts = new List<string>();
            if (CompressPdf) exts.Add(".pdf");
            if (CompressJpeg) { exts.Add(".jpg"); exts.Add(".jpeg"); }
            if (CompressPng) exts.Add(".png");
            if (CompressDocx) exts.Add(".docx");
            if (CompressXlsx) exts.Add(".xlsx");
            settings.AutoCompress.SupportedExtensions = exts;

            settings.ImageResize.DefaultMode = DefaultMode;
            settings.ImageResize.DefaultTargetSizeKB = DefaultTargetSizeUnit == "MB" ? DefaultTargetSize * 1024 : DefaultTargetSize;
            settings.ImageResize.DefaultWidth = DefaultWidth;
            settings.ImageResize.DefaultHeight = DefaultHeight;
            settings.ImageResize.MaintainAspectRatio = MaintainAspectRatio;
            settings.ImageResize.OutputMode = OutputMode;
            settings.ImageResize.OutputSuffix = OutputSuffix;
            settings.ImageResize.MinimumJpegQuality = MinimumJpegQuality;

            settings.General.StartWithWindows = StartWithWindows;
            settings.General.ShowTrayIcon = ShowTrayIcon;
            settings.General.EnableNotifications = EnableNotifications;
            settings.General.EnableExplorerPdfPreview = EnableExplorerPdfPreview;
            settings.General.LogLevel = LogLevel;
        });

        if (EnableExplorerPdfPreview)
        {
            Helpers.RegistryHelper.FixExplorerPdfPreviewHandler();
        }

        RequestSave?.Invoke(this, EventArgs.Empty);
        RequestClose?.Invoke();
    }

    private void AddWatchFolder()
    {
        using var dialog = new FolderBrowserDialog();
        if (dialog.ShowDialog() == DialogResult.OK)
        {
            if (!WatchFolders.Contains(dialog.SelectedPath))
            {
                WatchFolders.Add(dialog.SelectedPath);
            }
        }
    }

    private void RemoveWatchFolder()
    {
        if (!string.IsNullOrEmpty(SelectedWatchFolder))
        {
            WatchFolders.Remove(SelectedWatchFolder);
        }
    }

    private void ResetToDefaults()
    {
        if (System.Windows.MessageBox.Show("Are you sure you want to reset all settings to their defaults?", "Reset Settings", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question) == System.Windows.MessageBoxResult.Yes)
        {
            _settingsManager.ResetToDefaults();
            _settings = _settingsManager.Current;
            LoadSettings();
        }
    }

    private void OpenLogFolder()
    {
        string logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DASMO CYBER COMPRESSOR", "logs");
        Directory.CreateDirectory(logDir);
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = logDir,
                UseShellExecute = true
            });
        }
        catch { }
    }
}
