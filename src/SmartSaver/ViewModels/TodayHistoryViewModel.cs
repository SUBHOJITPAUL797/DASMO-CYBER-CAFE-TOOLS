using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using Serilog;
using SmartSaver.Models;
using SmartSaver.Services;

namespace SmartSaver.ViewModels;

public class TodayHistoryViewModel : ViewModelBase
{
    private readonly OutputHistoryService _historyService;

    public ObservableCollection<HistoryRecord> DisplayRecords { get; } = new();
    public ObservableCollection<HistoryRecord> FilteredRecords => DisplayRecords;

    private HistoryRecord? _selectedRecord;
    public HistoryRecord? SelectedRecord
    {
        get => _selectedRecord;
        set
        {
            if (SetProperty(ref _selectedRecord, value))
            {
                (OpenFileCommand as RelayCommand)?.OnCanExecuteChanged();
                (OpenFolderCommand as RelayCommand)?.OnCanExecuteChanged();
                (CopyPathCommand as RelayCommand)?.OnCanExecuteChanged();
                (OpenSelectedCommand as RelayCommand)?.OnCanExecuteChanged();
            }
        }
    }

    private string _filterTime = "Today";
    public string FilterTime
    {
        get => _filterTime;
        set
        {
            if (SetProperty(ref _filterTime, value))
            {
                RefreshList();
            }
        }
    }

    private string _searchQuery = string.Empty;
    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            if (SetProperty(ref _searchQuery, value))
            {
                RefreshList();
            }
        }
    }

    public ObservableCollection<string> FilterOptions { get; } = new() { "Today", "Past 7 Days", "All History" };

    public string TotalProcessedText => $"{DisplayRecords.Count} file(s) processed";

    public string TotalSavedSummary
    {
        get
        {
            long totalOrig = DisplayRecords.Sum(r => r.OriginalSizeBytes);
            long totalNew = DisplayRecords.Sum(r => r.NewSizeBytes);
            long saved = Math.Max(0, totalOrig - totalNew);
            if (saved > 0)
            {
                return $"⚡ {DisplayRecords.Count} files processed • Saved {CompressionResult.FormatFileSize(saved)} storage";
            }
            return $"⚡ {DisplayRecords.Count} files recorded in activity history";
        }
    }

    public ICommand OpenFileCommand { get; }
    public ICommand OpenSelectedCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public ICommand CopyPathCommand { get; }
    public ICommand ClearHistoryCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand CloseCommand { get; }
    public ICommand ResumeInEditorCommand { get; }

    public Action? RequestClose { get; set; }

    public TodayHistoryViewModel()
    {
        _historyService = OutputHistoryService.Instance;

        ResumeInEditorCommand = new RelayCommand(param =>
        {
            if (param is HistoryRecord rec) ResumeInEditor(rec);
            else if (SelectedRecord != null) ResumeInEditor(SelectedRecord);
        });

        OpenFileCommand = new RelayCommand(param =>
        {
            if (param is HistoryRecord rec) OpenFile(rec);
            else OpenSelectedFile();
        });

        OpenSelectedCommand = OpenFileCommand;

        OpenFolderCommand = new RelayCommand(param =>
        {
            if (param is HistoryRecord rec) OpenFolder(rec);
            else OpenSelectedFolder();
        });

        CopyPathCommand = new RelayCommand(param =>
        {
            if (param is HistoryRecord rec) CopyPath(rec);
            else CopySelectedPath();
        });

        RefreshCommand = new RelayCommand(_ => RefreshList());

        ClearHistoryCommand = new RelayCommand(_ =>
        {
            if (System.Windows.MessageBox.Show("Are you sure you want to clear output history?", "Clear History", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                _historyService.Clear();
                RefreshList();
            }
        });

        CloseCommand = new RelayCommand(_ => RequestClose?.Invoke());

        _historyService.OnRecordAdded += () =>
        {
            System.Windows.Application.Current?.Dispatcher.Invoke(RefreshList);
        };

        RefreshList();
    }

    public void RefreshList()
    {
        DisplayRecords.Clear();
        var all = _historyService.Records.ToList();

        var query = FilterTime switch
        {
            "Today" => all.Where(r => r.Timestamp.Date == DateTime.Today),
            "Past 7 Days" => all.Where(r => r.Timestamp.Date >= DateTime.Today.AddDays(-7)),
            _ => all
        };

        if (!string.IsNullOrWhiteSpace(SearchQuery))
        {
            string q = SearchQuery.Trim().ToLowerInvariant();
            query = query.Where(r => r.FileName.ToLowerInvariant().Contains(q) || 
                                     r.FilePath.ToLowerInvariant().Contains(q) ||
                                     r.Operation.ToLowerInvariant().Contains(q));
        }

        foreach (var r in query)
        {
            DisplayRecords.Add(r);
        }

        OnPropertyChanged(nameof(TotalProcessedText));
        OnPropertyChanged(nameof(TotalSavedSummary));
        OnPropertyChanged(nameof(FilteredRecords));
    }

    private void OpenFile(HistoryRecord rec)
    {
        if (File.Exists(rec.FilePath))
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = rec.FilePath,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Could not open file: {Path}", rec.FilePath);
            }
        }
    }

    private void OpenSelectedFile()
    {
        if (SelectedRecord != null) OpenFile(SelectedRecord);
    }

    private void OpenFolder(HistoryRecord rec)
    {
        try
        {
            if (File.Exists(rec.FilePath))
            {
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{rec.FilePath}\"");
            }
            else
            {
                string? dir = Path.GetDirectoryName(rec.FilePath);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                {
                    System.Diagnostics.Process.Start("explorer.exe", $"\"{dir}\"");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not open folder for: {Path}", rec.FilePath);
        }
    }

    private void OpenSelectedFolder()
    {
        if (SelectedRecord != null) OpenFolder(SelectedRecord);
    }

    private void CopyPath(HistoryRecord rec)
    {
        try
        {
            System.Windows.Clipboard.SetText(rec.FilePath);
        }
        catch { }
    }

    private void CopySelectedPath()
    {
        if (SelectedRecord != null) CopyPath(SelectedRecord);
    }

    private void ResumeInEditor(HistoryRecord rec)
    {
        if (string.IsNullOrWhiteSpace(rec.FilePath) || !File.Exists(rec.FilePath))
        {
            System.Windows.MessageBox.Show($"The PDF file could not be found at:\n{rec.FilePath}", "File Not Found", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            RequestClose?.Invoke();

            var existingEditor = System.Windows.Application.Current?.Windows.OfType<Views.PdfEditorWindow>().FirstOrDefault();
            if (existingEditor != null)
            {
                existingEditor.WindowState = WindowState.Normal;
                existingEditor.Activate();
                existingEditor.Focus();
                _ = existingEditor.Vm.LoadDocumentAsync(rec.FilePath);
            }
            else
            {
                var editor = new Views.PdfEditorWindow(rec.FilePath);
                editor.Show();
                editor.Activate();
                editor.Focus();
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to resume PDF Editor for {Path}", rec.FilePath);
            System.Windows.MessageBox.Show($"Could not open PDF Editor Studio:\n{ex.Message}", "Resume Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
