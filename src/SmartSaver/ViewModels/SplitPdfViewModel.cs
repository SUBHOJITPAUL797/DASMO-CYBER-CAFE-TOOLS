using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Serilog;
using SmartSaver.Models;
using SmartSaver.Services;
using PdfSharpCore.Pdf.IO;

namespace SmartSaver.ViewModels;

public class SplitPdfViewModel : ViewModelBase
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

    public ObservableCollection<PdfPageItem> Pages { get; } = new();
    public ObservableCollection<string> LoadedPdfs { get; } = new();

    public bool HasPages => Pages.Count > 0;
    public bool HasMultiplePdfs => LoadedPdfs.Count > 1;
    public string MultiPdfNames => string.Join(", ", LoadedPdfs.Select(Path.GetFileName));

    public int SelectedPagesCount => Pages.Count(p => p.IsSelected);
    public string PagesSummaryText => Pages.Count == 0
        ? "No pages loaded"
        : $"{Pages.Count} total page(s) • {SelectedPagesCount} selected for export";

    private bool _isRangeMode = true;
    public bool IsRangeMode
    {
        get => _isRangeMode;
        set
        {
            if (SetProperty(ref _isRangeMode, value))
            {
                OnPropertyChanged(nameof(ExportButtonText));
                OnPropertyChanged(nameof(OpenResultButtonText));
            }
        }
    }

    public string ExportButtonText => IsRangeMode ? "✂️ Export Combined PDF" : "✂️ Split to Separate Files";
    public string OpenResultButtonText => IsRangeMode ? "📂 Open Result PDF" : "📁 Open Destination Folder";

    private string _pageRangeText = "1";
    public string PageRangeText
    {
        get => _pageRangeText;
        set => SetProperty(ref _pageRangeText, value);
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
                (ExecuteCommand as RelayCommand)?.OnCanExecuteChanged();
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
        set
        {
            if (SetProperty(ref _resultText, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    private bool _isSuccess;
    public bool IsSuccess
    {
        get => _isSuccess;
        set
        {
            if (SetProperty(ref _isSuccess, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => !IsSuccess && !string.IsNullOrEmpty(ResultText);

    private string _lastResultPath = string.Empty;

    public ICommand ExecuteCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand BrowseDirectoryCommand { get; }
    public ICommand SetPresetSizeCommand { get; }
    public ICommand OpenResultCommand { get; }

    public ICommand AddPdfCommand { get; }
    public ICommand SelectAllCommand { get; }
    public ICommand DeselectAllCommand { get; }
    public ICommand InvertSelectionCommand { get; }
    public ICommand DeleteSelectedCommand { get; }
    public ICommand ResetPagesCommand { get; }
    public ICommand RotateAllSelectedCommand { get; }

    public Action? RequestClose { get; set; }

    public SplitPdfViewModel(string filePath)
    {
        var imgComp = new ImageCompressor();
        var pdfComp = new PdfCompressor();
        var offComp = new OfficeCompressor(imgComp);
        _engine = new CompressionEngine(imgComp, pdfComp, offComp);

        ExecuteCommand = new RelayCommand(async _ => await ExecuteAsync(), _ => !IsProcessing && Pages.Any(p => p.IsSelected));
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

        AddPdfCommand = new RelayCommand(_ => BrowseAndAddPdf());
        SelectAllCommand = new RelayCommand(_ => SetAllSelection(true));
        DeselectAllCommand = new RelayCommand(_ => SetAllSelection(false));
        InvertSelectionCommand = new RelayCommand(_ => InvertSelection());
        DeleteSelectedCommand = new RelayCommand(_ => DeleteSelectedPages());
        ResetPagesCommand = new RelayCommand(_ => ResetToOriginal());
        RotateAllSelectedCommand = new RelayCommand(_ => RotateAllSelected(90));

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

        LoadedPdfs.Clear();
        LoadedPdfs.Add(filePath);
        Pages.Clear();

        int pageCount = 1;
        try
        {
            using var doc = PdfReader.Open(filePath, PdfDocumentOpenMode.InformationOnly);
            pageCount = doc.PageCount;
        }
        catch
        {
            pageCount = 1;
        }

        TotalPages = pageCount;
        OutputDirectory = Path.GetDirectoryName(filePath) ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string baseName = Path.GetFileNameWithoutExtension(filePath);
        OutputFileName = $"{baseName}_extracted.pdf";
        PageRangeText = TotalPages > 1 ? "1-2" : "1";

        // Create placeholder page items immediately
        for (int i = 1; i <= pageCount; i++)
        {
            var item = CreatePageItem(filePath, i, i);
            Pages.Add(item);
        }

        UpdateDisplayIndices();

        OnPropertyChanged(nameof(FilePath));
        OnPropertyChanged(nameof(FileName));
        OnPropertyChanged(nameof(FileSizeFormatted));
        OnPropertyChanged(nameof(HasPages));
        OnPropertyChanged(nameof(HasMultiplePdfs));
        OnPropertyChanged(nameof(MultiPdfNames));

        // Start asynchronous thumbnail rendering in background
        _ = LoadThumbnailsForPdfAsync(filePath);
    }

    public void AppendPdfFile(string filePath)
    {
        if (!File.Exists(filePath)) return;

        if (string.IsNullOrEmpty(_filePath))
        {
            _filePath = filePath;
            FileSizeBytes = new FileInfo(filePath).Length;
            if (string.IsNullOrEmpty(OutputDirectory))
            {
                OutputDirectory = Path.GetDirectoryName(filePath) ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            }
            string baseName = Path.GetFileNameWithoutExtension(filePath);
            OutputFileName = $"{baseName}_organized.pdf";
            OnPropertyChanged(nameof(FilePath));
            OnPropertyChanged(nameof(FileName));
            OnPropertyChanged(nameof(FileSizeFormatted));
        }

        if (!LoadedPdfs.Contains(filePath))
        {
            LoadedPdfs.Add(filePath);
        }

        int pageCount = 1;
        try
        {
            using var doc = PdfReader.Open(filePath, PdfDocumentOpenMode.InformationOnly);
            pageCount = doc.PageCount;
        }
        catch
        {
            pageCount = 1;
        }

        int startDisplay = Pages.Count + 1;
        var newItems = new List<PdfPageItem>();
        for (int i = 1; i <= pageCount; i++)
        {
            var item = CreatePageItem(filePath, i, startDisplay++);
            Pages.Add(item);
            newItems.Add(item);
        }

        UpdateDisplayIndices();
        OnPropertyChanged(nameof(HasMultiplePdfs));
        OnPropertyChanged(nameof(MultiPdfNames));

        _ = LoadThumbnailsForPdfAsync(filePath, newItems);
    }

    private void BrowseAndAddPdf()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select PDF Files to Insert",
            Filter = "PDF Files (*.pdf)|*.pdf|All Files (*.*)|*.*",
            Multiselect = true
        };

        if (dlg.ShowDialog() == true)
        {
            foreach (var file in dlg.FileNames)
            {
                AppendPdfFile(file);
            }
        }
    }

    private PdfPageItem CreatePageItem(string pdfPath, int originalIndex, int displayIndex)
    {
        var item = new PdfPageItem
        {
            SourcePdfPath = pdfPath,
            OriginalPageIndex = originalIndex,
            DisplayIndex = displayIndex,
            IsSelected = true,
            HasMultipleSources = LoadedPdfs.Count > 1
        };

        item.OnMoveLeftRequested = MovePageLeft;
        item.OnMoveRightRequested = MovePageRight;
        item.OnDeleteRequested = RemovePage;
        item.OnPreviewRequested = PreviewPage;
        item.OnSelectionChanged = UpdateSummary;

        return item;
    }

    public void PreviewPage(PdfPageItem item)
    {
        if (item == null || string.IsNullOrEmpty(item.SourcePdfPath) || !File.Exists(item.SourcePdfPath)) return;

        try
        {
            int zeroBasedIndex = Math.Max(0, item.OriginalPageIndex - 1);
            var dlg = new Views.QuickPeekDialog(item.SourcePdfPath, zeroBasedIndex, item.Rotation);
            var parentWin = System.Windows.Application.Current?.Windows.OfType<System.Windows.Window>().FirstOrDefault(w => w.IsActive)
                         ?? System.Windows.Application.Current?.MainWindow;
            if (parentWin != null && dlg != parentWin) dlg.Owner = parentWin;

            dlg.ShowDialog();

            if (dlg.ResultRotation != item.Rotation)
            {
                item.Rotation = dlg.ResultRotation;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to open page preview for {Path} page {Page}", item.SourcePdfPath, item.OriginalPageIndex);
        }
    }

    private async Task LoadThumbnailsForPdfAsync(string pdfPath, List<PdfPageItem>? specificItems = null)
    {
        try
        {
            var targetItems = specificItems ?? Pages.Where(p => p.SourcePdfPath.Equals(pdfPath, StringComparison.OrdinalIgnoreCase)).ToList();
            if (targetItems.Count == 0) return;

            await Task.Run(() =>
            {
                var thumbs = PdfRendererService.RenderAllThumbnails(pdfPath, 240);
                for (int i = 0; i < targetItems.Count; i++)
                {
                    int origPage = targetItems[i].OriginalPageIndex - 1;
                    if (origPage >= 0 && origPage < thumbs.Count)
                    {
                        var thumb = thumbs[origPage];
                        var item = targetItems[i];

                        var dispatcher = System.Windows.Application.Current?.Dispatcher;
                        if (dispatcher != null)
                        {
                            dispatcher.InvokeAsync(() => item.Thumbnail = thumb);
                        }
                        else
                        {
                            item.Thumbnail = thumb;
                        }
                    }
                }
            });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed background thumbnail rendering for {Path}", pdfPath);
        }
    }

    public void MovePageLeft(PdfPageItem item)
    {
        int idx = Pages.IndexOf(item);
        if (idx > 0)
        {
            Pages.Move(idx, idx - 1);
            UpdateDisplayIndices();
        }
    }

    public void MovePageRight(PdfPageItem item)
    {
        int idx = Pages.IndexOf(item);
        if (idx >= 0 && idx < Pages.Count - 1)
        {
            Pages.Move(idx, idx + 1);
            UpdateDisplayIndices();
        }
    }

    public void ReorderPage(PdfPageItem source, PdfPageItem target)
    {
        if (source == null || target == null || ReferenceEquals(source, target)) return;

        int oldIdx = Pages.IndexOf(source);
        int newIdx = Pages.IndexOf(target);

        if (oldIdx >= 0 && newIdx >= 0 && oldIdx != newIdx)
        {
            Pages.Move(oldIdx, newIdx);
            UpdateDisplayIndices();
        }
    }

    public void RemovePage(PdfPageItem item)
    {
        if (item == null) return;
        if (Pages.Remove(item))
        {
            UpdateDisplayIndices();
        }
    }

    public void DeleteSelectedPages()
    {
        var toRemove = Pages.Where(p => p.IsSelected).ToList();
        if (toRemove.Count == 0) return;

        foreach (var p in toRemove)
        {
            Pages.Remove(p);
        }
        UpdateDisplayIndices();
    }

    public void SetAllSelection(bool isSelected)
    {
        foreach (var p in Pages)
        {
            p.IsSelected = isSelected;
        }
        UpdateSummary();
    }

    public void InvertSelection()
    {
        foreach (var p in Pages)
        {
            p.IsSelected = !p.IsSelected;
        }
        UpdateSummary();
    }

    public void RotateAllSelected(int deltaAngle)
    {
        foreach (var p in Pages.Where(p => p.IsSelected))
        {
            p.Rotation = (p.Rotation + deltaAngle) % 360;
        }
    }

    public void ResetToOriginal()
    {
        if (LoadedPdfs.Count == 0 && !string.IsNullOrEmpty(_filePath) && File.Exists(_filePath))
        {
            LoadFile(_filePath);
            return;
        }

        if (LoadedPdfs.Count == 1)
        {
            LoadFile(LoadedPdfs[0]);
            return;
        }

        if (LoadedPdfs.Count > 1)
        {
            var pdfs = LoadedPdfs.ToList();
            string primary = pdfs[0];
            LoadFile(primary);
            for (int i = 1; i < pdfs.Count; i++)
            {
                AppendPdfFile(pdfs[i]);
            }
        }
    }

    private void UpdateDisplayIndices()
    {
        bool multi = LoadedPdfs.Count > 1 || Pages.Select(p => p.SourcePdfPath).Distinct().Count() > 1;
        for (int i = 0; i < Pages.Count; i++)
        {
            Pages[i].DisplayIndex = i + 1;
            Pages[i].HasMultipleSources = multi;
        }
        TotalPages = Pages.Count;
        UpdateSummary();
        (ExecuteCommand as RelayCommand)?.OnCanExecuteChanged();
    }

    private void UpdateSummary()
    {
        OnPropertyChanged(nameof(SelectedPagesCount));
        OnPropertyChanged(nameof(PagesSummaryText));
        OnPropertyChanged(nameof(HasPages));
        (ExecuteCommand as RelayCommand)?.OnCanExecuteChanged();
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

    private async Task ExecuteAsync()
    {
        var selected = Pages.Where(p => p.IsSelected).ToList();
        if (selected.Count == 0)
        {
            ResultText = "❌ Please select at least one page to export.";
            IsSuccess = false;
            return;
        }

        IsProcessing = true;
        ProgressText = "Processing and exporting PDF pages...";
        ResultText = string.Empty;
        IsSuccess = false;

        try
        {
            if (string.IsNullOrWhiteSpace(OutputDirectory))
            {
                var firstSource = selected.FirstOrDefault(p => !string.IsNullOrEmpty(p.SourcePdfPath))?.SourcePdfPath;
                OutputDirectory = !string.IsNullOrEmpty(firstSource) && File.Exists(firstSource)
                    ? Path.GetDirectoryName(firstSource) ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                    : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            }

            Directory.CreateDirectory(OutputDirectory);

            long? targetBytes = null;
            if (IsCompressEnabled)
            {
                targetBytes = TargetSizeUnit == "MB"
                    ? (long)TargetSizeKB * 1024 * 1024
                    : (long)TargetSizeKB * 1024;
            }

            if (IsRangeMode)
            {
                // Single organized combined PDF
                var extractionItems = selected.Select(p => new PdfPageExtractionItem
                {
                    SourcePdfPath = p.SourcePdfPath,
                    PageIndex = p.OriginalPageIndex,
                    Rotation = p.Rotation
                }).ToList();

                string defaultBase = !string.IsNullOrEmpty(_filePath)
                    ? Path.GetFileNameWithoutExtension(_filePath)
                    : (selected.FirstOrDefault(p => !string.IsNullOrEmpty(p.SourcePdfPath)) != null
                        ? Path.GetFileNameWithoutExtension(selected.First(p => !string.IsNullOrEmpty(p.SourcePdfPath)).SourcePdfPath)
                        : "Document");

                string outName = string.IsNullOrWhiteSpace(OutputFileName)
                    ? $"{defaultBase}_organized.pdf"
                    : OutputFileName;
                if (!outName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) outName += ".pdf";

                string fullOutPath = Path.Combine(OutputDirectory, outName);
                int counter = 1;
                string baseNoExt = Path.GetFileNameWithoutExtension(outName);
                while (File.Exists(fullOutPath))
                {
                    fullOutPath = Path.Combine(OutputDirectory, $"{baseNoExt}_{counter++}.pdf");
                }

                var res = await _engine.ExtractPdfPagesOrderedAsync(fullOutPath, extractionItems, targetBytes);
                if (res.Success)
                {
                    OutputHistoryService.Instance.Record(fullOutPath, "Organized PDF", res.OriginalSizeBytes, res.NewSizeBytes);
                    _lastResultPath = fullOutPath;
                    IsSuccess = true;
                    ResultText = $"✅ Successfully exported {selected.Count} organized page(s)!\n{res.Message}\n→ {Path.GetFileName(fullOutPath)}";
                    if (OpenOnComplete && File.Exists(fullOutPath))
                    {
                        OpenResultFile();
                    }
                }
                else
                {
                    IsSuccess = false;
                    ResultText = $"❌ {res.Message ?? "Failed to export pages."}";
                }
            }
            else
            {
                // Split selected pages into individual single-page files
                int exported = 0;
                foreach (var page in selected)
                {
                    string pageSourceBase = !string.IsNullOrEmpty(page.SourcePdfPath)
                        ? Path.GetFileNameWithoutExtension(page.SourcePdfPath)
                        : (!string.IsNullOrEmpty(_filePath) ? Path.GetFileNameWithoutExtension(_filePath) : "Document");

                    string outName = $"{pageSourceBase}_page_{page.DisplayIndex}.pdf";
                    string outPath = Path.Combine(OutputDirectory, outName);
                    int counter = 1;
                    while (File.Exists(outPath))
                    {
                        outPath = Path.Combine(OutputDirectory, $"{pageSourceBase}_page_{page.DisplayIndex}_{counter++}.pdf");
                    }

                    var singleItem = new[]
                    {
                        new PdfPageExtractionItem
                        {
                            SourcePdfPath = page.SourcePdfPath,
                            PageIndex = page.OriginalPageIndex,
                            Rotation = page.Rotation
                        }
                    };

                    var res = await _engine.ExtractPdfPagesOrderedAsync(outPath, singleItem, targetBytes);
                    if (res.Success)
                    {
                        OutputHistoryService.Instance.Record(outPath, "Split PDF Page", res.OriginalSizeBytes, res.NewSizeBytes);
                        exported++;
                    }
                }

                _lastResultPath = OutputDirectory;
                IsSuccess = exported > 0;
                ResultText = IsSuccess
                    ? $"✅ Successfully split {exported} page(s) into individual files in:\n📁 {OutputDirectory}"
                    : "❌ Failed to split pages.";

                if (OpenOnComplete && IsSuccess)
                {
                    OpenResultFolder();
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error in SplitPdfViewModel");
            IsSuccess = false;
            ResultText = $"❌ Error: {ex.Message}";
        }
        finally
        {
            IsProcessing = false;
            ProgressText = string.Empty;
        }
    }

    public static List<int> ParsePageRange(string input, int maxPages)
    {
        var result = new HashSet<int>();
        if (string.IsNullOrWhiteSpace(input)) return result.ToList();

        var tokens = input.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var tok in tokens)
        {
            if (tok.Contains('-'))
            {
                var parts = tok.Split('-');
                if (parts.Length == 2 && int.TryParse(parts[0], out int start) && int.TryParse(parts[1], out int end))
                {
                    int min = Math.Min(start, end);
                    int max = Math.Max(start, end);
                    for (int p = min; p <= max; p++)
                    {
                        if (p >= 1 && p <= maxPages) result.Add(p);
                    }
                }
            }
            else if (int.TryParse(tok, out int single))
            {
                if (single >= 1 && single <= maxPages) result.Add(single);
            }
        }

        return result.OrderBy(p => p).ToList();
    }

    private void OpenResultFile()
    {
        if (!string.IsNullOrEmpty(_lastResultPath) && (File.Exists(_lastResultPath) || Directory.Exists(_lastResultPath)))
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = _lastResultPath,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Could not open result path: {Path}", _lastResultPath);
            }
        }
    }

    private void OpenResultFolder()
    {
        if (!string.IsNullOrEmpty(OutputDirectory) && Directory.Exists(OutputDirectory))
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = OutputDirectory,
                    UseShellExecute = true
                });
            }
            catch { }
        }
    }
}
