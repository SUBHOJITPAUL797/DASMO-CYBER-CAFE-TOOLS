using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Serilog;
using SmartSaver.Models;
using SmartSaver.Services;
using SmartSaver.Views;

namespace SmartSaver.ViewModels;

public class ExcelPrintStudioViewModel : ViewModelBase
{
    private string _filePath = string.Empty;
    public string FilePath
    {
        get => _filePath;
        set
        {
            if (SetProperty(ref _filePath, value))
            {
                OnPropertyChanged(nameof(FileName));
                OnPropertyChanged(nameof(HasDocument));
                OnPropertyChanged(nameof(FileSizeFormatted));
            }
        }
    }

    public string FileName => string.IsNullOrEmpty(FilePath) ? "No file selected" : Path.GetFileName(FilePath);
    public bool HasDocument => !string.IsNullOrEmpty(FilePath) && File.Exists(FilePath);

    public string FileSizeFormatted
    {
        get
        {
            if (!HasDocument) return string.Empty;
            try
            {
                var fi = new FileInfo(FilePath);
                return fi.Length < 1024 * 1024
                    ? $"{fi.Length / 1024.0:F1} KB"
                    : $"{fi.Length / (1024.0 * 1024.0):F2} MB";
            }
            catch { return string.Empty; }
        }
    }

    private ExcelWorkbookInfo? _workbookInfo;
    public ExcelWorkbookInfo? WorkbookInfo
    {
        get => _workbookInfo;
        set => SetProperty(ref _workbookInfo, value);
    }

    public ObservableCollection<string> SheetNames { get; } = new();

    private string _selectedSheetName = string.Empty;
    public string SelectedSheetName
    {
        get => _selectedSheetName;
        set
        {
            if (SetProperty(ref _selectedSheetName, value) && !string.IsNullOrEmpty(value) && HasDocument)
            {
                SwitchToSheet(value);
            }
        }
    }

    private ExcelSheetData? _currentSheet;
    public ExcelSheetData? CurrentSheet
    {
        get => _currentSheet;
        set
        {
            if (SetProperty(ref _currentSheet, value))
            {
                OnPropertyChanged(nameof(TotalRowsText));
                OnPropertyChanged(nameof(TotalColumnsText));
            }
        }
    }

    public string TotalRowsText => CurrentSheet != null
        ? $"{CurrentSheet.TotalRows:N0} Data Rows"
        : "0 Rows";

    public string TotalColumnsText => CurrentSheet != null
        ? $"{CurrentSheet.TotalColumns} Columns"
        : "0 Columns";

    public ObservableCollection<ExcelColumnItem> ColumnsList { get; } = new();

    #region Range Controls

    private string _rangeMode = "Entire";
    public string RangeMode
    {
        get => _rangeMode;
        set
        {
            if (SetProperty(ref _rangeMode, value))
            {
                OnPropertyChanged(nameof(IsEntireRange));
                OnPropertyChanged(nameof(IsCustomRange));
                SchedulePreviewUpdate();
            }
        }
    }

    public bool IsEntireRange
    {
        get => RangeMode == "Entire";
        set
        {
            if (value && RangeMode != "Entire")
                RangeMode = "Entire";
        }
    }

    public bool IsCustomRange
    {
        get => RangeMode == "Custom";
        set
        {
            if (value && RangeMode != "Custom")
                RangeMode = "Custom";
        }
    }

    private int _startRow = 1;
    public int StartRow
    {
        get => _startRow;
        set
        {
            int val = Math.Max(1, value);
            if (SetProperty(ref _startRow, val))
            {
                SchedulePreviewUpdate();
            }
        }
    }

    private int _endRow = 100;
    public int EndRow
    {
        get => _endRow;
        set
        {
            int val = Math.Max(1, value);
            if (SetProperty(ref _endRow, val))
            {
                SchedulePreviewUpdate();
            }
        }
    }

    #endregion

    #region Orientation Controls

    private ExcelPrintOrientation _orientation = ExcelPrintOrientation.Auto;
    public ExcelPrintOrientation Orientation
    {
        get => _orientation;
        set
        {
            if (SetProperty(ref _orientation, value))
            {
                OnPropertyChanged(nameof(IsOrientationAuto));
                OnPropertyChanged(nameof(IsOrientationLandscape));
                OnPropertyChanged(nameof(IsOrientationPortrait));
                SchedulePreviewUpdate();
            }
        }
    }

    public bool IsOrientationAuto
    {
        get => Orientation == ExcelPrintOrientation.Auto;
        set
        {
            if (value && Orientation != ExcelPrintOrientation.Auto)
                Orientation = ExcelPrintOrientation.Auto;
        }
    }

    public bool IsOrientationLandscape
    {
        get => Orientation == ExcelPrintOrientation.Landscape;
        set
        {
            if (value && Orientation != ExcelPrintOrientation.Landscape)
                Orientation = ExcelPrintOrientation.Landscape;
        }
    }

    public bool IsOrientationPortrait
    {
        get => Orientation == ExcelPrintOrientation.Portrait;
        set
        {
            if (value && Orientation != ExcelPrintOrientation.Portrait)
                Orientation = ExcelPrintOrientation.Portrait;
        }
    }

    #endregion

    #region Margins Controls

    private string _marginPreset = "Narrow";
    public string MarginPreset
    {
        get => _marginPreset;
        set => SetProperty(ref _marginPreset, value);
    }

    private double _marginLeftMm = 5.0;
    public double MarginLeftMm
    {
        get => _marginLeftMm;
        set
        {
            if (SetProperty(ref _marginLeftMm, Math.Round(Math.Clamp(value, 2.0, 40.0), 1)))
                SchedulePreviewUpdate();
        }
    }

    private double _marginRightMm = 5.0;
    public double MarginRightMm
    {
        get => _marginRightMm;
        set
        {
            if (SetProperty(ref _marginRightMm, Math.Round(Math.Clamp(value, 2.0, 40.0), 1)))
                SchedulePreviewUpdate();
        }
    }

    private double _marginTopMm = 5.0;
    public double MarginTopMm
    {
        get => _marginTopMm;
        set
        {
            if (SetProperty(ref _marginTopMm, Math.Round(Math.Clamp(value, 2.0, 40.0), 1)))
                SchedulePreviewUpdate();
        }
    }

    private double _marginBottomMm = 5.0;
    public double MarginBottomMm
    {
        get => _marginBottomMm;
        set
        {
            if (SetProperty(ref _marginBottomMm, Math.Round(Math.Clamp(value, 2.0, 40.0), 1)))
                SchedulePreviewUpdate();
        }
    }

    #endregion

    #region Formatting & Styling Options

    private bool _repeatHeaderOnEveryPage = true;
    public bool RepeatHeaderOnEveryPage
    {
        get => _repeatHeaderOnEveryPage;
        set
        {
            if (SetProperty(ref _repeatHeaderOnEveryPage, value))
                SchedulePreviewUpdate();
        }
    }

    private string _fontSizePreset = "Standard";
    public string FontSizePreset
    {
        get => _fontSizePreset;
        set
        {
            if (SetProperty(ref _fontSizePreset, value))
            {
                OnPropertyChanged(nameof(IsFontCompact));
                OnPropertyChanged(nameof(IsFontStandard));
                OnPropertyChanged(nameof(IsFontComfortable));
                SchedulePreviewUpdate();
            }
        }
    }

    public bool IsFontCompact
    {
        get => FontSizePreset == "Compact";
        set
        {
            if (value && FontSizePreset != "Compact")
                FontSizePreset = "Compact";
        }
    }

    public bool IsFontStandard
    {
        get => FontSizePreset == "Standard";
        set
        {
            if (value && FontSizePreset != "Standard")
                FontSizePreset = "Standard";
        }
    }

    public bool IsFontComfortable
    {
        get => FontSizePreset == "Comfortable";
        set
        {
            if (value && FontSizePreset != "Comfortable")
                FontSizePreset = "Comfortable";
        }
    }

    public double FontSizePoints => FontSizePreset switch
    {
        "Compact" => 7.2,
        "Comfortable" => 10.0,
        _ => 8.2 // Standard
    };

    private bool _addSerialNo = true;
    public bool AddSerialNo
    {
        get => _addSerialNo;
        set
        {
            if (SetProperty(ref _addSerialNo, value))
                SchedulePreviewUpdate();
        }
    }

    private bool _zebraStriping = true;
    public bool ZebraStriping
    {
        get => _zebraStriping;
        set
        {
            if (SetProperty(ref _zebraStriping, value))
                SchedulePreviewUpdate();
        }
    }

    private bool _drawGridlines = true;
    public bool DrawGridlines
    {
        get => _drawGridlines;
        set
        {
            if (SetProperty(ref _drawGridlines, value))
                SchedulePreviewUpdate();
        }
    }

    private string _documentTitle = string.Empty;
    public string DocumentTitle
    {
        get => _documentTitle;
        set
        {
            if (SetProperty(ref _documentTitle, value))
                SchedulePreviewUpdate();
        }
    }

    private string _shopFooter = "DASMO CYBER CAFE TOOLS";
    public string ShopFooter
    {
        get => _shopFooter;
        set
        {
            if (SetProperty(ref _shopFooter, value))
                SchedulePreviewUpdate();
        }
    }

    private ExcelCellAlignment _cellAlignment = ExcelCellAlignment.Left;
    public ExcelCellAlignment CellAlignment
    {
        get => _cellAlignment;
        set
        {
            if (SetProperty(ref _cellAlignment, value))
            {
                OnPropertyChanged(nameof(IsAlignmentLeft));
                OnPropertyChanged(nameof(IsAlignmentCenter));
                SchedulePreviewUpdate();
            }
        }
    }

    public bool IsAlignmentLeft
    {
        get => CellAlignment == ExcelCellAlignment.Left;
        set
        {
            if (value && CellAlignment != ExcelCellAlignment.Left)
                CellAlignment = ExcelCellAlignment.Left;
        }
    }

    public bool IsAlignmentCenter
    {
        get => CellAlignment == ExcelCellAlignment.Center;
        set
        {
            if (value && CellAlignment != ExcelCellAlignment.Center)
                CellAlignment = ExcelCellAlignment.Center;
        }
    }

    #endregion

    #region Grouping, Filtering & Page Break Controls

    public ObservableCollection<GroupColumnOption> GroupByColumns { get; } = new();

    private GroupColumnOption? _selectedGroupByColumn;
    public GroupColumnOption? SelectedGroupByColumn
    {
        get => _selectedGroupByColumn;
        set
        {
            if (SetProperty(ref _selectedGroupByColumn, value))
            {
                OnPropertyChanged(nameof(HasSelectedGroupColumn));
                OnPropertyChanged(nameof(SelectedColumnName));
                OnPropertyChanged(nameof(StartNewPageCheckboxText));
                OnPropertyChanged(nameof(StartNewPageDescriptionText));
                OnPropertyChanged(nameof(RestartSlNoCheckboxText));
                UpdateGroupValuesList();
                SchedulePreviewUpdate();
            }
        }
    }

    public bool HasSelectedGroupColumn => SelectedGroupByColumn != null && SelectedGroupByColumn.Index >= 0;

    public string SelectedColumnName => SelectedGroupByColumn != null && SelectedGroupByColumn.Index >= 0
        ? SelectedGroupByColumn.Name
        : "group";

    public string StartNewPageCheckboxText => SelectedGroupByColumn != null && SelectedGroupByColumn.Index >= 0
        ? $"✂️ Start each {SelectedGroupByColumn.Name} on a new page"
        : "✂️ Start each group / section on a new page";

    public string StartNewPageDescriptionText => SelectedGroupByColumn != null && SelectedGroupByColumn.Index >= 0
        ? $"Ends page when {SelectedGroupByColumn.Name} changes; next {SelectedGroupByColumn.Name} starts on new page"
        : "Ends page when value changes; next group starts on new page";

    public string RestartSlNoCheckboxText => SelectedGroupByColumn != null && SelectedGroupByColumn.Index >= 0
        ? $"🔢 Restart Sl No. (1, 2, 3...) for each {SelectedGroupByColumn.Name}"
        : "🔢 Restart Sl No. (1, 2, 3...) for each group / section";

    public ObservableCollection<string> GroupValues { get; } = new();

    private string _selectedGroupValue = "All (Print All Records / Values)";
    public string SelectedGroupValue
    {
        get => _selectedGroupValue;
        set
        {
            if (SetProperty(ref _selectedGroupValue, value))
            {
                SchedulePreviewUpdate();
            }
        }
    }

    private bool _sortByGroupColumn = true;
    public bool SortByGroupColumn
    {
        get => _sortByGroupColumn;
        set
        {
            if (SetProperty(ref _sortByGroupColumn, value))
            {
                SchedulePreviewUpdate();
            }
        }
    }

    private bool _pageBreakOnGroupChange = false;
    public bool PageBreakOnGroupChange
    {
        get => _pageBreakOnGroupChange;
        set
        {
            if (SetProperty(ref _pageBreakOnGroupChange, value))
            {
                SchedulePreviewUpdate();
            }
        }
    }

    private bool _restartSerialNoPerGroup = false;
    public bool RestartSerialNoPerGroup
    {
        get => _restartSerialNoPerGroup;
        set
        {
            if (SetProperty(ref _restartSerialNoPerGroup, value))
            {
                SchedulePreviewUpdate();
            }
        }
    }

    #region Row-Based Page Split (Row Count Limit)

    private string _rowsPerPagePreset = "Auto";
    public string RowsPerPagePreset
    {
        get => _rowsPerPagePreset;
        set
        {
            if (SetProperty(ref _rowsPerPagePreset, value))
            {
                OnPropertyChanged(nameof(IsRowsPerPageAuto));
                OnPropertyChanged(nameof(IsRowsPerPage20));
                OnPropertyChanged(nameof(IsRowsPerPage25));
                OnPropertyChanged(nameof(IsRowsPerPage30));
                OnPropertyChanged(nameof(IsRowsPerPageCustom));
                SchedulePreviewUpdate();
            }
        }
    }

    public bool IsRowsPerPageAuto
    {
        get => RowsPerPagePreset == "Auto";
        set { if (value && RowsPerPagePreset != "Auto") RowsPerPagePreset = "Auto"; }
    }

    public bool IsRowsPerPage20
    {
        get => RowsPerPagePreset == "20";
        set { if (value && RowsPerPagePreset != "20") RowsPerPagePreset = "20"; }
    }

    public bool IsRowsPerPage25
    {
        get => RowsPerPagePreset == "25";
        set { if (value && RowsPerPagePreset != "25") RowsPerPagePreset = "25"; }
    }

    public bool IsRowsPerPage30
    {
        get => RowsPerPagePreset == "30";
        set { if (value && RowsPerPagePreset != "30") RowsPerPagePreset = "30"; }
    }

    public bool IsRowsPerPageCustom
    {
        get => RowsPerPagePreset == "Custom";
        set { if (value && RowsPerPagePreset != "Custom") RowsPerPagePreset = "Custom"; }
    }

    private int _customRowsPerPage = 25;
    public int CustomRowsPerPage
    {
        get => _customRowsPerPage;
        set
        {
            int clamped = Math.Clamp(value, 5, 100);
            if (SetProperty(ref _customRowsPerPage, clamped))
            {
                if (RowsPerPagePreset == "Custom")
                    SchedulePreviewUpdate();
            }
        }
    }

    public int MaxRowsPerPage => RowsPerPagePreset switch
    {
        "20" => 20,
        "25" => 25,
        "30" => 30,
        "Custom" => Math.Max(5, CustomRowsPerPage),
        _ => 0
    };

    #endregion

    private void UpdateGroupValuesList()
    {
        GroupValues.Clear();
        GroupValues.Add("All (Print All Records / Values)");

        if (CurrentSheet == null || SelectedGroupByColumn == null || SelectedGroupByColumn.Index < 0)
        {
            _selectedGroupValue = "All (Print All Records / Values)";
            OnPropertyChanged(nameof(SelectedGroupValue));
            return;
        }

        int colIdx = SelectedGroupByColumn.Index;
        var distinctValues = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in CurrentSheet.Rows)
        {
            if (colIdx < row.Count)
            {
                string val = row[colIdx]?.Trim() ?? string.Empty;
                if (!string.IsNullOrEmpty(val))
                {
                    distinctValues.Add(val);
                }
            }
        }

        var sortedValues = distinctValues.OrderBy(v => v, StringComparer.CurrentCultureIgnoreCase).ToList();
        foreach (var v in sortedValues)
        {
            GroupValues.Add(v);
        }

        _selectedGroupValue = GroupValues[0];
        OnPropertyChanged(nameof(SelectedGroupValue));
    }

    #endregion

    #region Preview & Pagination State

    private BitmapSource? _currentPreviewImage;
    public BitmapSource? CurrentPreviewImage
    {
        get => _currentPreviewImage;
        set => SetProperty(ref _currentPreviewImage, value);
    }

    private double _zoomFactor = 1.0;
    public double ZoomFactor
    {
        get => _zoomFactor;
        set
        {
            double clamped = Math.Clamp(Math.Round(value, 2), 0.2, 4.0);
            if (SetProperty(ref _zoomFactor, clamped))
            {
                OnPropertyChanged(nameof(ZoomPercentageText));
            }
        }
    }

    public string ZoomPercentageText => $"{(int)Math.Round(_zoomFactor * 100)}%";

    private int _currentPageIndex = 0;
    public int CurrentPageIndex
    {
        get => _currentPageIndex;
        set
        {
            if (SetProperty(ref _currentPageIndex, value))
            {
                UpdatePageNavigationState();
                RenderCurrentPagePreview();
            }
        }
    }

    private int _totalPages = 0;
    public int TotalPages
    {
        get => _totalPages;
        set
        {
            if (SetProperty(ref _totalPages, value))
            {
                UpdatePageNavigationState();
            }
        }
    }

    private string _pageNavigationText = "Page 0 of 0";
    public string PageNavigationText
    {
        get => _pageNavigationText;
        set => SetProperty(ref _pageNavigationText, value);
    }

    private bool _canGoPreviousPage;
    public bool CanGoPreviousPage
    {
        get => _canGoPreviousPage;
        set => SetProperty(ref _canGoPreviousPage, value);
    }

    private bool _canGoNextPage;
    public bool CanGoNextPage
    {
        get => _canGoNextPage;
        set => SetProperty(ref _canGoNextPage, value);
    }

    private bool _isGenerating;
    public bool IsGenerating
    {
        get => _isGenerating;
        set => SetProperty(ref _isGenerating, value);
    }

    private string _statusText = "Ready";
    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    public string? TempPdfPath { get; private set; }

    #endregion

    #region Commands

    public ICommand BrowseFileCommand { get; }
    public ICommand SelectAllColumnsCommand { get; }
    public ICommand DeselectAllColumnsCommand { get; }
    public ICommand SetMarginPresetCommand { get; }
    public ICommand SetOrientationCommand { get; }
    public ICommand SetFontSizeCommand { get; }
    public ICommand SetAlignmentCommand { get; }
    public ICommand PreviousPageCommand { get; }
    public ICommand NextPageCommand { get; }
    public ICommand FirstPageCommand { get; }
    public ICommand LastPageCommand { get; }
    public ICommand PrintCommand { get; }
    public ICommand ExportPdfCommand { get; }
    public ICommand OpenPdfCommand { get; }
    public ICommand ZoomInCommand { get; }
    public ICommand ZoomOutCommand { get; }
    public ICommand ZoomResetCommand { get; }
    public ICommand ResumeRecentDraftCommand { get; }

    #endregion

    #region Draft & Session Recovery Properties
    private ExcelDraftSession? _recentDraft;
    public ExcelDraftSession? RecentDraft
    {
        get => _recentDraft;
        set
        {
            if (SetProperty(ref _recentDraft, value))
            {
                OnPropertyChanged(nameof(HasRecentDraft));
                OnPropertyChanged(nameof(RecentDraftFileName));
                OnPropertyChanged(nameof(RecentDraftText));
            }
        }
    }

    public bool HasRecentDraft => RecentDraft != null && File.Exists(RecentDraft.FilePath);
    public string RecentDraftFileName => RecentDraft != null ? RecentDraft.FileName : string.Empty;
    public string RecentDraftText => RecentDraft != null
        ? $"⚡ Resume Recent Work: {RecentDraft.FileName} ({RecentDraft.SummaryText})"
        : string.Empty;

    public void UpdateRecentDraftState()
    {
        try
        {
            RecentDraft = ExcelDraftService.Instance.GetMostRecentDraft();
        }
        catch { }
    }
    #endregion

    private CancellationTokenSource? _debounceCts;
    private readonly object _lock = new();

    public ExcelPrintStudioViewModel(string initialPath = "")
    {
        // Commands
        BrowseFileCommand = new RelayCommand(_ => BrowseForFile());
        SelectAllColumnsCommand = new RelayCommand(_ => SetAllColumnsSelection(true));
        DeselectAllColumnsCommand = new RelayCommand(_ => SetAllColumnsSelection(false));
        SetMarginPresetCommand = new RelayCommand(p => ApplyMarginPreset(p?.ToString()));
        SetOrientationCommand = new RelayCommand(p => ApplyOrientation(p?.ToString()));
        SetFontSizeCommand = new RelayCommand(p => FontSizePreset = p?.ToString() ?? "Standard");
        SetAlignmentCommand = new RelayCommand(p => CellAlignment = p?.ToString() == "Center" ? ExcelCellAlignment.Center : ExcelCellAlignment.Left);

        ZoomInCommand = new RelayCommand(_ => ZoomFactor = Math.Min(4.0, ZoomFactor + 0.15));
        ZoomOutCommand = new RelayCommand(_ => ZoomFactor = Math.Max(0.2, ZoomFactor - 0.15));
        ZoomResetCommand = new RelayCommand(_ => ZoomFactor = 1.0);

        PreviousPageCommand = new RelayCommand(_ => { if (CurrentPageIndex > 0) CurrentPageIndex--; }, _ => CanGoPreviousPage);
        NextPageCommand = new RelayCommand(_ => { if (CurrentPageIndex < TotalPages - 1) CurrentPageIndex++; }, _ => CanGoNextPage);
        FirstPageCommand = new RelayCommand(_ => { CurrentPageIndex = 0; }, _ => CanGoPreviousPage);
        LastPageCommand = new RelayCommand(_ => { CurrentPageIndex = Math.Max(0, TotalPages - 1); }, _ => CanGoNextPage);

        PrintCommand = new RelayCommand(async _ => await ExecutePrintAsync(), _ => HasDocument && !IsGenerating && TotalPages > 0);
        ExportPdfCommand = new RelayCommand(_ => ExecuteExportPdf(), _ => HasDocument && !string.IsNullOrEmpty(TempPdfPath) && File.Exists(TempPdfPath));
        OpenPdfCommand = new RelayCommand(_ => ExecuteOpenPdf(), _ => HasDocument && !string.IsNullOrEmpty(TempPdfPath) && File.Exists(TempPdfPath));

        ResumeRecentDraftCommand = new RelayCommand(_ =>
        {
            if (RecentDraft != null && File.Exists(RecentDraft.FilePath))
            {
                LoadFile(RecentDraft.FilePath);
            }
        }, _ => HasRecentDraft);

        UpdateRecentDraftState();

        if (!string.IsNullOrEmpty(initialPath) && File.Exists(initialPath))
        {
            LoadFile(initialPath);
        }
    }

    public void LoadFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return;

            IsGenerating = true;
            StatusText = "Loading spreadsheet...";
            FilePath = path;
            DocumentTitle = Path.GetFileNameWithoutExtension(path).ToUpperInvariant();

            WorkbookInfo = ExcelPrintStudioService.LoadWorkbook(path);

            SheetNames.Clear();
            foreach (var s in WorkbookInfo.SheetNames)
            {
                SheetNames.Add(s);
            }

            _selectedSheetName = WorkbookInfo.SelectedSheetName;
            OnPropertyChanged(nameof(SelectedSheetName));

            SetupSheetData(WorkbookInfo.CurrentSheet);
            CheckAndRestoreDraft(path);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to load Excel file {Path}", path);
            StatusText = $"Error: {ex.Message}";
            System.Windows.MessageBox.Show($"Could not open file:\n{ex.Message}", "Excel Load Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsGenerating = false;
        }
    }

    private void SwitchToSheet(string sheetName)
    {
        if (string.IsNullOrEmpty(FilePath) || !File.Exists(FilePath)) return;

        try
        {
            IsGenerating = true;
            StatusText = $"Switching to sheet '{sheetName}'...";
            var wb = ExcelPrintStudioService.LoadWorkbook(FilePath, sheetName);
            WorkbookInfo = wb;
            SetupSheetData(wb.CurrentSheet);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to switch sheet to {Sheet}", sheetName);
            StatusText = $"Error switching sheet: {ex.Message}";
        }
        finally
        {
            IsGenerating = false;
        }
    }

    private void SetupSheetData(ExcelSheetData sheet)
    {
        CurrentSheet = sheet;

        // Reset ranges
        _startRow = 1;
        _endRow = Math.Max(1, sheet.TotalRows);
        OnPropertyChanged(nameof(StartRow));
        OnPropertyChanged(nameof(EndRow));

        // Populate columns
        ColumnsList.Clear();
        for (int i = 0; i < sheet.Headers.Count; i++)
        {
            var colItem = new ExcelColumnItem
            {
                OriginalIndex = i,
                HeaderName = sheet.Headers[i],
                IsSelected = true
            };
            ColumnsList.Add(colItem);
        }

        // Populate grouping columns
        GroupByColumns.Clear();
        GroupByColumns.Add(new GroupColumnOption { Index = -1, Name = "None", DisplayName = "(None - Do not group)" });

        for (int i = 0; i < sheet.Headers.Count; i++)
        {
            string h = sheet.Headers[i];
            GroupByColumns.Add(new GroupColumnOption
            {
                Index = i,
                Name = h,
                DisplayName = $"{i + 1}. {h}"
            });
        }

        _selectedGroupByColumn = GroupByColumns[0];
        OnPropertyChanged(nameof(SelectedGroupByColumn));
        OnPropertyChanged(nameof(HasSelectedGroupColumn));
        UpdateGroupValuesList();

        // Auto orientation detection: If > 5 cols, default to landscape
        if (sheet.Headers.Count > 5)
        {
            _orientation = ExcelPrintOrientation.Landscape;
        }
        else
        {
            _orientation = ExcelPrintOrientation.Auto;
        }
        OnPropertyChanged(nameof(Orientation));
        OnPropertyChanged(nameof(IsOrientationAuto));
        OnPropertyChanged(nameof(IsOrientationLandscape));
        OnPropertyChanged(nameof(IsOrientationPortrait));

        SchedulePreviewUpdate(immediate: true);
    }

    private void SetAllColumnsSelection(bool isSelected)
    {
        foreach (var col in ColumnsList)
        {
            col.IsSelected = isSelected;
        }
        SchedulePreviewUpdate();
    }

    private void ApplyMarginPreset(string? preset)
    {
        if (string.IsNullOrEmpty(preset)) return;

        MarginPreset = preset;
        switch (preset.ToLowerInvariant())
        {
            case "edge":
            case "edgetoedge":
            case "2":
                _marginLeftMm = 2.0;
                _marginRightMm = 2.0;
                _marginTopMm = 3.0;
                _marginBottomMm = 3.0;
                break;
            case "narrow":
            case "5":
                _marginLeftMm = 5.0;
                _marginRightMm = 5.0;
                _marginTopMm = 5.0;
                _marginBottomMm = 5.0;
                break;
            case "normal":
            case "10":
                _marginLeftMm = 10.0;
                _marginRightMm = 10.0;
                _marginTopMm = 10.0;
                _marginBottomMm = 10.0;
                break;
            case "spacious":
            case "15":
                _marginLeftMm = 15.0;
                _marginRightMm = 15.0;
                _marginTopMm = 15.0;
                _marginBottomMm = 15.0;
                break;
        }

        OnPropertyChanged(nameof(MarginLeftMm));
        OnPropertyChanged(nameof(MarginRightMm));
        OnPropertyChanged(nameof(MarginTopMm));
        OnPropertyChanged(nameof(MarginBottomMm));
        SchedulePreviewUpdate();
    }

    private void ApplyOrientation(string? ori)
    {
        if (string.IsNullOrEmpty(ori)) return;
        Orientation = ori.ToLowerInvariant() switch
        {
            "landscape" => ExcelPrintOrientation.Landscape,
            "portrait" => ExcelPrintOrientation.Portrait,
            _ => ExcelPrintOrientation.Auto
        };
    }

    public void SchedulePreviewUpdate(bool immediate = false)
    {
        if (CurrentSheet == null || !HasDocument) return;

        lock (_lock)
        {
            _debounceCts?.Cancel();
            _debounceCts?.Dispose();
            _debounceCts = new CancellationTokenSource();
        }

        var token = _debounceCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                if (!immediate)
                {
                    await Task.Delay(200, token);
                }

                if (token.IsCancellationRequested) return;

                await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    IsGenerating = true;
                    StatusText = "Generating smart A4 layout...";
                });

                // Prepare print config
                var config = BuildCurrentConfig();
                string tempDir = Path.Combine(Path.GetTempPath(), "SmartSaver_ExcelPrint");
                Directory.CreateDirectory(tempDir);
                string tempPdf = Path.Combine(tempDir, $"preview_{Guid.NewGuid():N}.pdf");

                var result = ExcelPrintStudioService.GeneratePrintPdf(CurrentSheet, config, tempPdf);

                if (token.IsCancellationRequested) return;

                await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    if (result.Success)
                    {
                        TempPdfPath = tempPdf;
                        TotalPages = result.TotalPages;
                        if (CurrentPageIndex >= TotalPages)
                        {
                            CurrentPageIndex = Math.Max(0, TotalPages - 1);
                        }
                        else
                        {
                            RenderCurrentPagePreview();
                        }
                        StatusText = $"Ready • {result.TotalPages} pages ({result.RowsPrinted:N0} rows)";
                        SaveCurrentDraft(result.TotalPages, result.RowsPrinted);
                    }
                    else
                    {
                        StatusText = $"Layout error: {result.ErrorMessage}";
                    }
                    IsGenerating = false;
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Log.Error(ex, "Error updating Excel print preview");
                await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    StatusText = $"Preview error: {ex.Message}";
                    IsGenerating = false;
                });
            }
        }, token);
    }

    private ExcelPrintConfig BuildCurrentConfig()
    {
        var selectedCols = ColumnsList
            .Where(c => c.IsSelected)
            .Select(c => c.OriginalIndex)
            .ToList();

        if (selectedCols.Count == 0 && CurrentSheet != null)
        {
            selectedCols = Enumerable.Range(0, CurrentSheet.Headers.Count).ToList();
        }

        return new ExcelPrintConfig
        {
            Orientation = Orientation,
            MarginLeftMm = MarginLeftMm,
            MarginRightMm = MarginRightMm,
            MarginTopMm = MarginTopMm,
            MarginBottomMm = MarginBottomMm,
            RepeatHeaderOnEveryPage = RepeatHeaderOnEveryPage,
            StartDataRow = IsCustomRange ? StartRow : 1,
            EndDataRow = IsCustomRange ? EndRow : 0,
            SelectedColumnIndices = selectedCols,
            AddSerialNo = AddSerialNo,
            FontSize = FontSizePoints,
            ZebraStriping = ZebraStriping,
            DrawGridlines = DrawGridlines,
            DocumentTitle = DocumentTitle,
            ShopFooter = ShopFooter,
            CellAlignment = CellAlignment,
            GroupByColumnIndex = SelectedGroupByColumn?.Index ?? -1,
            FilterByColumnValue = SelectedGroupValue,
            SortByGroupColumn = SortByGroupColumn,
            PageBreakOnGroupChange = PageBreakOnGroupChange,
            RestartSerialNoPerGroup = RestartSerialNoPerGroup,
            MaxRowsPerPage = MaxRowsPerPage
        };
    }

    private int _previewRenderSequence = 0;

    private void RenderCurrentPagePreview()
    {
        if (string.IsNullOrEmpty(TempPdfPath) || !File.Exists(TempPdfPath) || TotalPages == 0)
        {
            CurrentPreviewImage = null;
            return;
        }

        int pageIdx = Math.Clamp(CurrentPageIndex, 0, TotalPages - 1);
        int seq = Interlocked.Increment(ref _previewRenderSequence);

        Task.Run(() =>
        {
            try
            {
                var bmp = PdfRendererService.RenderPageThumbnail(TempPdfPath, pageIdx, 1400);
                if (bmp != null)
                {
                    System.Windows.Application.Current.Dispatcher.Invoke(() =>
                    {
                        if (seq == _previewRenderSequence)
                        {
                            CurrentPreviewImage = bmp;
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to render preview page {Page}", pageIdx);
            }
        });
    }

    private void UpdatePageNavigationState()
    {
        PageNavigationText = TotalPages > 0
            ? $"Page {CurrentPageIndex + 1} of {TotalPages}"
            : "Page 0 of 0";

        CanGoPreviousPage = CurrentPageIndex > 0;
        CanGoNextPage = CurrentPageIndex < TotalPages - 1;
    }

    private void BrowseForFile()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select Excel Spreadsheet or CSV File",
            Filter = "Excel & CSV Files (*.xlsx;*.xlsm;*.csv;*.tsv;*.xls)|*.xlsx;*.xlsm;*.csv;*.tsv;*.xls|Excel Workbooks (*.xlsx;*.xlsm)|*.xlsx;*.xlsm|CSV / TSV Text Files (*.csv;*.tsv)|*.csv;*.tsv|All Files (*.*)|*.*"
        };

        if (dlg.ShowDialog() == true)
        {
            LoadFile(dlg.FileName);
        }
    }

    private async Task ExecutePrintAsync()
    {
        if (CurrentSheet == null || !HasDocument)
        {
            System.Windows.MessageBox.Show("No document available to print.", "Print Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            IsGenerating = true;
            StatusText = "Preparing pages for native print dialog...";

            string? pdfToPrint = TempPdfPath;
            if (string.IsNullOrEmpty(pdfToPrint) || !File.Exists(pdfToPrint))
            {
                string tempDir = Path.Combine(Path.GetTempPath(), "SmartSaver_ExcelPrint");
                Directory.CreateDirectory(tempDir);
                pdfToPrint = Path.Combine(tempDir, $"print_{Guid.NewGuid():N}.pdf");
                var config = BuildCurrentConfig();
                var res = ExcelPrintStudioService.GeneratePrintPdf(CurrentSheet, config, pdfToPrint);
                if (!res.Success)
                    throw new Exception(res.ErrorMessage ?? "Failed to generate PDF for printing");
                TempPdfPath = pdfToPrint;
            }

            // Render all pages directly to disk via memory-efficient stream (~15MB peak) at 300 DPI
            string printJobDir = Path.Combine(Path.GetTempPath(), "SmartSaver_PrintJob_" + Guid.NewGuid().ToString("N"));
            var pageImages = await Task.Run(() => PdfRendererService.RenderPdfPagesToDisk(pdfToPrint, printJobDir, 300));

            IsGenerating = false;
            StatusText = "Ready";

            if (pageImages.Count > 0)
            {
                string jobTitle = string.IsNullOrWhiteSpace(DocumentTitle) ? "Excel A4 Print" : DocumentTitle;
                string preferredOri = Orientation == ExcelPrintOrientation.Landscape ? "Landscape" : (Orientation == ExcelPrintOrientation.Portrait ? "Portrait" : "Auto");
                NativePrintDialog.ShowPrintDialog(pageImages, jobTitle, preferredPaperSize: "A4", preferredOrientation: preferredOri);

                long origBytes = 0;
                try { origBytes = new FileInfo(FilePath).Length; } catch { }
                OutputHistoryService.Instance.Record(FilePath, "Excel Print Studio (Printed)", origBytes, origBytes);
            }
            else
            {
                // Fallback: Open in default viewer with print prompt
                ExecuteOpenPdf();
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to launch native print dialog for Excel sheet");
            IsGenerating = false;
            StatusText = $"Print error: {ex.Message}";
            System.Windows.MessageBox.Show($"Could not open print dialog:\n{ex.Message}", "Print Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ExecuteExportPdf()
    {
        if (CurrentSheet == null || !HasDocument)
        {
            System.Windows.MessageBox.Show("No generated document is available to export.", "Export Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        string defaultName = Path.GetFileNameWithoutExtension(FilePath) + "_A4_Print.pdf";
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save Formatted A4 PDF",
            Filter = "PDF Document (*.pdf)|*.pdf",
            FileName = defaultName
        };

        if (dlg.ShowDialog() == true)
        {
            try
            {
                var config = BuildCurrentConfig();
                var res = ExcelPrintStudioService.GeneratePrintPdf(CurrentSheet, config, dlg.FileName);
                if (!res.Success)
                {
                    throw new Exception(res.ErrorMessage ?? "PDF generation failed");
                }

                StatusText = $"Saved to {Path.GetFileName(dlg.FileName)}";

                long origBytes = 0;
                try { origBytes = new FileInfo(FilePath).Length; } catch { }
                long outBytes = 0;
                try { outBytes = new FileInfo(dlg.FileName).Length; } catch { }
                OutputHistoryService.Instance.Record(dlg.FileName, "Excel A4 Export", origBytes, outBytes > 0 ? outBytes : origBytes);

                var prompt = System.Windows.MessageBox.Show(
                    $"Pristine A4 PDF saved successfully!\n\nLocation: {dlg.FileName}\n\nWould you like to open it now?",
                    "Export Successful", MessageBoxButton.YesNo, MessageBoxImage.Information);

                if (prompt == MessageBoxResult.Yes)
                {
                    Process.Start(new ProcessStartInfo(dlg.FileName) { UseShellExecute = true });
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to save PDF to {Target}", dlg.FileName);
                System.Windows.MessageBox.Show($"Failed to save PDF:\n{ex.Message}", "Save Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private void ExecuteOpenPdf()
    {
        if (CurrentSheet == null || !HasDocument)
        {
            System.Windows.MessageBox.Show("No document is available to view.", "View Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            string? pdfToOpen = TempPdfPath;
            if (string.IsNullOrEmpty(pdfToOpen) || !File.Exists(pdfToOpen))
            {
                string tempDir = Path.Combine(Path.GetTempPath(), "SmartSaver_ExcelPrint");
                Directory.CreateDirectory(tempDir);
                pdfToOpen = Path.Combine(tempDir, $"print_{Guid.NewGuid():N}.pdf");
                var config = BuildCurrentConfig();
                var res = ExcelPrintStudioService.GeneratePrintPdf(CurrentSheet, config, pdfToOpen);
                if (!res.Success)
                    throw new Exception(res.ErrorMessage ?? "Failed to generate PDF");
                TempPdfPath = pdfToOpen;
            }

            Process.Start(new ProcessStartInfo(pdfToOpen) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to open PDF in default viewer");
            System.Windows.MessageBox.Show($"Could not open PDF viewer:\n{ex.Message}", "Viewer Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    #region Draft Persistence & Recovery Engine
    private void CheckAndRestoreDraft(string path)
    {
        var draft = ExcelDraftService.Instance.LoadDraft(path);
        if (draft == null) return;

        try
        {
            if (!string.IsNullOrEmpty(draft.SelectedSheetName) &&
                SheetNames.Contains(draft.SelectedSheetName) &&
                _selectedSheetName != draft.SelectedSheetName)
            {
                _selectedSheetName = draft.SelectedSheetName;
                OnPropertyChanged(nameof(SelectedSheetName));
                var wb = ExcelPrintStudioService.LoadWorkbook(path, draft.SelectedSheetName);
                CurrentSheet = wb.CurrentSheet;
            }

            RangeMode = draft.RangeMode;
            _startRow = draft.StartRow;
            _endRow = draft.EndRow;
            OnPropertyChanged(nameof(StartRow));
            OnPropertyChanged(nameof(EndRow));

            Orientation = draft.Orientation switch
            {
                "Landscape" => ExcelPrintOrientation.Landscape,
                "Portrait" => ExcelPrintOrientation.Portrait,
                _ => ExcelPrintOrientation.Auto
            };

            MarginPreset = draft.MarginPreset;
            _marginLeftMm = draft.MarginLeftMm;
            _marginRightMm = draft.MarginRightMm;
            _marginTopMm = draft.MarginTopMm;
            _marginBottomMm = draft.MarginBottomMm;
            OnPropertyChanged(nameof(MarginLeftMm));
            OnPropertyChanged(nameof(MarginRightMm));
            OnPropertyChanged(nameof(MarginTopMm));
            OnPropertyChanged(nameof(MarginBottomMm));

            _repeatHeaderOnEveryPage = draft.RepeatHeaderOnEveryPage;
            OnPropertyChanged(nameof(RepeatHeaderOnEveryPage));
            FontSizePreset = draft.FontSizePreset;
            _addSerialNo = draft.AddSerialNo;
            OnPropertyChanged(nameof(AddSerialNo));
            _zebraStriping = draft.ZebraStriping;
            OnPropertyChanged(nameof(ZebraStriping));
            _drawGridlines = draft.DrawGridlines;
            OnPropertyChanged(nameof(DrawGridlines));

            if (!string.IsNullOrEmpty(draft.DocumentTitle))
            {
                _documentTitle = draft.DocumentTitle;
                OnPropertyChanged(nameof(DocumentTitle));
            }
            if (!string.IsNullOrEmpty(draft.ShopFooter))
            {
                _shopFooter = draft.ShopFooter;
                OnPropertyChanged(nameof(ShopFooter));
            }

            CellAlignment = draft.CellAlignment == "Center" ? ExcelCellAlignment.Center : ExcelCellAlignment.Left;

            // Restore column selections
            if (draft.SelectedColumnIndices != null && draft.SelectedColumnIndices.Count > 0)
            {
                var set = new HashSet<int>(draft.SelectedColumnIndices);
                foreach (var col in ColumnsList)
                {
                    col.IsSelected = set.Contains(col.OriginalIndex);
                }
            }

            // Restore Grouping
            if (draft.SelectedGroupByColumnIndex >= -1 && draft.SelectedGroupByColumnIndex < GroupByColumns.Count)
            {
                var match = GroupByColumns.FirstOrDefault(g => g.Index == draft.SelectedGroupByColumnIndex);
                if (match != null)
                {
                    _selectedGroupByColumn = match;
                    OnPropertyChanged(nameof(SelectedGroupByColumn));
                    OnPropertyChanged(nameof(HasSelectedGroupColumn));
                    UpdateGroupValuesList();
                }
            }

            if (!string.IsNullOrEmpty(draft.SelectedGroupValue))
            {
                _selectedGroupValue = draft.SelectedGroupValue;
                OnPropertyChanged(nameof(SelectedGroupValue));
            }

            _sortByGroupColumn = draft.SortByGroupColumn;
            OnPropertyChanged(nameof(SortByGroupColumn));
            _pageBreakOnGroupChange = draft.PageBreakOnGroupChange;
            OnPropertyChanged(nameof(PageBreakOnGroupChange));
            if (draft.MaxRowsPerPage > 0)
            {
                if (draft.MaxRowsPerPage == 20) RowsPerPagePreset = "20";
                else if (draft.MaxRowsPerPage == 25) RowsPerPagePreset = "25";
                else if (draft.MaxRowsPerPage == 30) RowsPerPagePreset = "30";
                else
                {
                    RowsPerPagePreset = "Custom";
                    CustomRowsPerPage = draft.MaxRowsPerPage;
                }
            }
            else
            {
                RowsPerPagePreset = "Auto";
            }
            OnPropertyChanged(nameof(MaxRowsPerPage));

            StatusText = "⚡ Restored your saved in-progress draft (filters & ranges intact)";
            SchedulePreviewUpdate(immediate: true);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to restore Excel draft for {Path}", path);
        }
    }

    private void SaveCurrentDraft(int totalPages = 0, int rowsPrinted = 0)
    {
        if (string.IsNullOrEmpty(FilePath) || !File.Exists(FilePath) || CurrentSheet == null) return;

        try
        {
            var selectedCols = ColumnsList.Where(c => c.IsSelected).Select(c => c.OriginalIndex).ToList();
            var session = new ExcelDraftSession
            {
                FilePath = FilePath,
                SelectedSheetName = SelectedSheetName,
                RangeMode = RangeMode,
                StartRow = StartRow,
                EndRow = EndRow,
                Orientation = Orientation switch
                {
                    ExcelPrintOrientation.Landscape => "Landscape",
                    ExcelPrintOrientation.Portrait => "Portrait",
                    _ => "Auto"
                },
                MarginPreset = MarginPreset,
                MarginLeftMm = MarginLeftMm,
                MarginRightMm = MarginRightMm,
                MarginTopMm = MarginTopMm,
                MarginBottomMm = MarginBottomMm,
                RepeatHeaderOnEveryPage = RepeatHeaderOnEveryPage,
                FontSizePreset = FontSizePreset,
                AddSerialNo = AddSerialNo,
                ZebraStriping = ZebraStriping,
                DrawGridlines = DrawGridlines,
                DocumentTitle = DocumentTitle,
                ShopFooter = ShopFooter,
                CellAlignment = CellAlignment == ExcelCellAlignment.Center ? "Center" : "Left",
                SelectedGroupByColumnIndex = SelectedGroupByColumn?.Index ?? -1,
                SelectedGroupByColumnName = SelectedGroupByColumn?.Name ?? string.Empty,
                SelectedGroupValue = SelectedGroupValue,
                SortByGroupColumn = SortByGroupColumn,
                PageBreakOnGroupChange = PageBreakOnGroupChange,
                RestartSerialNoPerGroup = RestartSerialNoPerGroup,
                MaxRowsPerPage = MaxRowsPerPage,
                SelectedColumnIndices = selectedCols,
                TotalPages = totalPages > 0 ? totalPages : TotalPages,
                RowsPrinted = rowsPrinted
            };

            ExcelDraftService.Instance.SaveDraft(session);

            long origBytes = 0;
            try { origBytes = new FileInfo(FilePath).Length; } catch { }
            long pdfBytes = 0;
            if (!string.IsNullOrEmpty(TempPdfPath) && File.Exists(TempPdfPath))
            {
                try { pdfBytes = new FileInfo(TempPdfPath).Length; } catch { }
            }

            OutputHistoryService.Instance.Record(FilePath, "Excel Print Studio (In-Progress)", origBytes, pdfBytes > 0 ? pdfBytes : origBytes);

            UpdateRecentDraftState();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to auto-save Excel draft session");
        }
    }
    #endregion
}

public class GroupColumnOption
{
    public int Index { get; set; } = -1;
    public string Name { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;

    public override string ToString() => DisplayName;
}
