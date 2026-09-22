using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Serilog;
using SmartSaver.Models;
using SmartSaver.Services;
using WpfMessageBox = System.Windows.MessageBox;

namespace SmartSaver.ViewModels;

public enum PdfEditorTool
{
    Select,
    Whiteout,
    Text,
    EditText,   // ← new: click existing PDF text to edit it in-place
    Image,
    Pen
}

public class PdfPageThumbnailItem : ViewModelBase
{
    public int PageIndex { get; set; }
    public int PageNumber => PageIndex + 1;

    private BitmapSource? _thumbnail;
    public BitmapSource? Thumbnail
    {
        get => _thumbnail;
        set => SetProperty(ref _thumbnail, value);
    }
}

public class PdfEditorViewModel : ViewModelBase
{
    private readonly PdfEditorService _service;

    #region Properties

    private string _filePath = string.Empty;
    public string FilePath
    {
        get => _filePath;
        set
        {
            if (SetProperty(ref _filePath, value))
            {
                FileName = string.IsNullOrEmpty(value) ? "No Document" : Path.GetFileName(value);
                OnPropertyChanged(nameof(HasDocument));
                OnPropertyChanged(nameof(IsNormalDocumentReady));
                OnPropertyChanged(nameof(ShowEmptyState));
            }
        }
    }

    private string _fileName = "No Document";
    public string FileName
    {
        get => _fileName;
        set => SetProperty(ref _fileName, value);
    }

    public bool HasDocument => !string.IsNullOrEmpty(FilePath) && File.Exists(FilePath);

    private string? _currentPassword;
    public string? CurrentPassword
    {
        get => _currentPassword;
        private set => SetProperty(ref _currentPassword, value);
    }

    private bool _isPasswordProtected;
    public bool IsPasswordProtected
    {
        get => _isPasswordProtected;
        set
        {
            if (SetProperty(ref _isPasswordProtected, value))
            {
                OnPropertyChanged(nameof(IsNormalDocumentReady));
                OnPropertyChanged(nameof(ShowEmptyState));
            }
        }
    }

    private string _pdfPassword = string.Empty;
    public string PdfPassword
    {
        get => _pdfPassword;
        set => SetProperty(ref _pdfPassword, value);
    }

    private string _passwordErrorMessage = string.Empty;
    public string PasswordErrorMessage
    {
        get => _passwordErrorMessage;
        set
        {
            if (SetProperty(ref _passwordErrorMessage, value))
            {
                OnPropertyChanged(nameof(HasPasswordError));
            }
        }
    }

    public bool HasPasswordError => !string.IsNullOrEmpty(PasswordErrorMessage);

    private string? _pendingProtectedPdfPath;
    public string? PendingProtectedPdfPath
    {
        get => _pendingProtectedPdfPath;
        set => SetProperty(ref _pendingProtectedPdfPath, value);
    }

    public bool IsNormalDocumentReady => HasDocument && !IsPasswordProtected;
    public bool ShowEmptyState => !HasDocument && !IsPasswordProtected;

    private bool _hasUnsavedEdits;
    public bool HasUnsavedEdits
    {
        get => _hasUnsavedEdits;
        set => SetProperty(ref _hasUnsavedEdits, value);
    }

    private int _totalPages = 0;
    public int TotalPages
    {
        get => _totalPages;
        set
        {
            if (SetProperty(ref _totalPages, value))
            {
                UpdatePageInfo();
            }
        }
    }

    private int _currentPageIndex = 0;
    public int CurrentPageIndex
    {
        get => _currentPageIndex;
        set
        {
            if (value >= 0 && value < TotalPages && value != _currentPageIndex)
            {
                // Save active edits on the current page before switching
                SaveCurrentEditsToStore(immediateDiskSave: true);

                if (SetProperty(ref _currentPageIndex, value))
                {
                    OnPropertyChanged(nameof(CurrentPageNumber));
                    UpdatePageInfo();
                    _ = LoadCurrentPageAsync();
                }
            }
        }
    }

    public int CurrentPageNumber => CurrentPageIndex + 1;

    private string _pageInfoText = "No PDF loaded";
    public string PageInfoText
    {
        get => _pageInfoText;
        set => SetProperty(ref _pageInfoText, value);
    }

    public string PageNumberText => TotalPages > 0 ? $"Page {CurrentPageNumber} of {TotalPages}" : "Page 0 of 0";

    private BitmapSource? _currentPagePreview;
    public BitmapSource? CurrentPagePreview
    {
        get => _currentPagePreview;
        set => SetProperty(ref _currentPagePreview, value);
    }

    private double _pageNativeWidth = 595.0; // Standard A4 in points (72 DPI)
    public double PageNativeWidth
    {
        get => _pageNativeWidth;
        set => SetProperty(ref _pageNativeWidth, value);
    }

    private double _pageNativeHeight = 842.0; // Standard A4 in points
    public double PageNativeHeight
    {
        get => _pageNativeHeight;
        set => SetProperty(ref _pageNativeHeight, value);
    }

    private double _zoomFactor = 1.0;
    public double ZoomFactor
    {
        get => _zoomFactor;
        set
        {
            double clamped = Math.Clamp(Math.Round(value, 2), 0.4, 3.5);
            if (SetProperty(ref _zoomFactor, clamped))
            {
                OnPropertyChanged(nameof(ZoomPercentText));
                OnPropertyChanged(nameof(CanvasDisplayWidth));
                OnPropertyChanged(nameof(CanvasDisplayHeight));
            }
        }
    }

    public string ZoomPercentText => $"{(int)Math.Round(ZoomFactor * 100)}%";
    public double CanvasDisplayWidth => PageNativeWidth * ZoomFactor;
    public double CanvasDisplayHeight => PageNativeHeight * ZoomFactor;

    private bool _isLoading;
    public bool IsLoading
    {
        get => _isLoading;
        set => SetProperty(ref _isLoading, value);
    }

    private string _statusMessage = "Ready to edit";
    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    // Tools
    private PdfEditorTool _selectedTool = PdfEditorTool.Select;
    public PdfEditorTool SelectedTool
    {
        get => _selectedTool;
        set
        {
            if (SetProperty(ref _selectedTool, value))
            {
                // Clear any active selection so borders don't linger across tool switches
                SelectedItem = null;

                OnPropertyChanged(nameof(IsSelectTool));
                OnPropertyChanged(nameof(IsWhiteoutTool));
                OnPropertyChanged(nameof(IsTextTool));
                OnPropertyChanged(nameof(IsEditTextTool));
                OnPropertyChanged(nameof(IsImageTool));
                OnPropertyChanged(nameof(IsPenTool));

                // Load/clear extracted text blocks when switching to/from EditText
                if (value == PdfEditorTool.EditText)
                    _ = LoadExtractedTextBlocksAsync();
                else
                    ExtractedTextBlocks.Clear();
            }
        }
    }

    public bool IsSelectTool
    {
        get => SelectedTool == PdfEditorTool.Select;
        set { if (value) SelectedTool = PdfEditorTool.Select; }
    }

    public bool IsWhiteoutTool
    {
        get => SelectedTool == PdfEditorTool.Whiteout;
        set { if (value) SelectedTool = PdfEditorTool.Whiteout; }
    }

    public bool IsTextTool
    {
        get => SelectedTool == PdfEditorTool.Text;
        set { if (value) SelectedTool = PdfEditorTool.Text; }
    }

    public bool IsEditTextTool
    {
        get => SelectedTool == PdfEditorTool.EditText;
        set { if (value) SelectedTool = PdfEditorTool.EditText; }
    }

    public bool IsImageTool
    {
        get => SelectedTool == PdfEditorTool.Image;
        set { if (value) SelectedTool = PdfEditorTool.Image; }
    }

    public bool IsPenTool
    {
        get => SelectedTool == PdfEditorTool.Pen;
        set { if (value) SelectedTool = PdfEditorTool.Pen; }
    }

    // Collections
    public ObservableCollection<string> FontFamilies { get; } = new()
    {
        "Arial", "Times New Roman", "Calibri", "Segoe UI", "Courier New", "Georgia", "Verdana", "Trebuchet MS", "Impact", "Nirmala UI", "Mangal", "Vrinda"
    };

    private string _selectedFontFamily = "Arial";
    public string SelectedFontFamily
    {
        get => _selectedFontFamily;
        set
        {
            if (SetProperty(ref _selectedFontFamily, value) && SelectedItem is PdfTextItem ti)
            {
                ti.FontFamily = value;
                TriggerItemChanged();
            }
        }
    }

    private double _fontSizePt = 12.0;
    public double FontSizePt
    {
        get => _fontSizePt;
        set
        {
            double safeVal = Math.Clamp(Math.Round(value, 1), 5.0, 72.0);
            if (SetProperty(ref _fontSizePt, safeVal) && SelectedItem is PdfTextItem ti)
            {
                ti.FontSizePt = safeVal;
                TriggerItemChanged();
            }
        }
    }

    private bool _isBold;
    public bool IsBold
    {
        get => _isBold;
        set
        {
            if (SetProperty(ref _isBold, value) && SelectedItem is PdfTextItem ti)
            {
                ti.IsBold = value;
                TriggerItemChanged();
            }
        }
    }

    private bool _isItalic;
    public bool IsItalic
    {
        get => _isItalic;
        set
        {
            if (SetProperty(ref _isItalic, value) && SelectedItem is PdfTextItem ti)
            {
                ti.IsItalic = value;
                TriggerItemChanged();
            }
        }
    }

    private string _textColorHex = "#000000";
    public string TextColorHex
    {
        get => _textColorHex;
        set
        {
            if (SetProperty(ref _textColorHex, value) && SelectedItem is PdfTextItem ti)
            {
                ti.TextColorHex = value;
                TriggerItemChanged();
            }
        }
    }

    private bool _hasOpaqueBackground = true;
    public bool HasOpaqueBackground
    {
        get => _hasOpaqueBackground;
        set
        {
            if (SetProperty(ref _hasOpaqueBackground, value) && SelectedItem is PdfTextItem ti)
            {
                ti.HasOpaqueBackground = value;
                TriggerItemChanged();
            }
        }
    }

    // Selected canvas item
    private PdfEditItem? _selectedItem;
    public PdfEditItem? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (_selectedItem != null) _selectedItem.IsSelected = false;
            if (SetProperty(ref _selectedItem, value))
            {
                if (_selectedItem != null) _selectedItem.IsSelected = true;
                OnPropertyChanged(nameof(HasSelectedItem));
                SyncToolPropertiesFromSelected();
            }
        }
    }

    public bool HasSelectedItem => SelectedItem != null;

    // Collections
    public ObservableCollection<PdfEditItem> CurrentPageEdits { get; } = new();
    public ObservableCollection<PdfPageThumbnailItem> PageThumbnails { get; } = new();

    /// <summary>Text blocks extracted by PdfPig for the "✏️ Edit Text" tool.</summary>
    public ObservableCollection<SmartSaver.Models.PdfExtractedTextBlock> ExtractedTextBlocks { get; } = new();

    private sealed class PageUndoState
    {
        public List<PdfEditItem> Edits { get; set; } = new();
        public List<SmartSaver.Models.PdfExtractedTextBlock> Blocks { get; set; } = new();
    }

    private readonly Dictionary<int, List<PdfEditItem>> _allEdits = new();
    private readonly Dictionary<int, Stack<PageUndoState>> _undoStacks = new();
    private readonly Dictionary<int, Stack<PageUndoState>> _redoStacks = new();
    private List<PdfPageDimension> _dimensions = new();

    #endregion

    #region Commands
    public ICommand OpenPdfCommand { get; }
    public ICommand SavePdfCommand { get; }
    public ICommand SaveAsPdfCommand { get; }
    public ICommand PrintPdfCommand { get; }
    public ICommand SelectToolCommand { get; }
    public ICommand NextPageCommand { get; }
    public ICommand PrevPageCommand { get; }
    public ICommand GoToPageCommand { get; }
    public ICommand ZoomInCommand { get; }
    public ICommand ZoomOutCommand { get; }
    public ICommand ZoomResetCommand { get; }
    public ICommand FitWidthCommand { get; }
    public ICommand UndoCommand { get; }
    public ICommand RedoCommand { get; }
    public ICommand DeleteSelectedCommand { get; }
    public ICommand PasteClipboardImageCommand { get; }
    public ICommand InsertImageCommand { get; }
    public ICommand SetTextColorCommand { get; }
    public ICommand IncreaseFontSizeCommand { get; }
    public ICommand DecreaseFontSizeCommand { get; }
    public ICommand UnlockPdfCommand { get; }
    public ICommand CancelUnlockCommand { get; }

    public Func<string, string, string?>? RequestSaveFile { get; set; }
    public Action? RequestClose { get; set; }
    public Action<string, string>? ShowMessage { get; set; }
    #endregion

    public PdfEditorViewModel(string initialPath = "")
    {
        _service = PdfEditorService.Instance;

        UnlockPdfCommand = new RelayCommand(async _ => await UnlockPdfWithPasswordAsync(), _ => !IsLoading);
        CancelUnlockCommand = new RelayCommand(_ => CancelUnlock());

        OpenPdfCommand = new RelayCommand(_ => BrowseAndOpenPdf());
        SavePdfCommand = new RelayCommand(async _ => await SavePdfAsync(false), _ => HasDocument && !IsLoading);
        SaveAsPdfCommand = new RelayCommand(async _ => await SavePdfAsync(true), _ => HasDocument && !IsLoading);
        PrintPdfCommand = new RelayCommand(_ => PrintCurrentPdf(), _ => HasDocument && !IsLoading);

        SelectToolCommand = new RelayCommand(p =>
        {
            if (p is string s && Enum.TryParse<PdfEditorTool>(s, out var tool))
            {
                SelectedTool = tool;
            }
        });

        NextPageCommand = new RelayCommand(_ => { if (CurrentPageIndex < TotalPages - 1) CurrentPageIndex++; }, _ => CurrentPageIndex < TotalPages - 1);
        PrevPageCommand = new RelayCommand(_ => { if (CurrentPageIndex > 0) CurrentPageIndex--; }, _ => CurrentPageIndex > 0);
        GoToPageCommand = new RelayCommand(p =>
        {
            if (p != null && int.TryParse(p.ToString(), out int idx))
            {
                CurrentPageIndex = idx;
            }
        });

        ZoomInCommand = new RelayCommand(_ => ZoomFactor += 0.15);
        ZoomOutCommand = new RelayCommand(_ => ZoomFactor -= 0.15);
        ZoomResetCommand = new RelayCommand(_ => ZoomFactor = 1.0);
        FitWidthCommand = new RelayCommand(_ => ZoomFactor = 1.25);

        UndoCommand = new RelayCommand(_ => Undo(), _ => CanUndo());
        RedoCommand = new RelayCommand(_ => Redo(), _ => CanRedo());
        DeleteSelectedCommand = new RelayCommand(_ => DeleteSelectedItem(), _ => HasSelectedItem);

        PasteClipboardImageCommand = new RelayCommand(_ => PasteImageFromClipboard(), _ => HasDocument);
        InsertImageCommand = new RelayCommand(_ => InsertImageFromFile(), _ => HasDocument);

        SetTextColorCommand = new RelayCommand(p =>
        {
            if (p is string hex)
            {
                TextColorHex = hex;
            }
        });

        IncreaseFontSizeCommand = new RelayCommand(_ => FontSizePt += 1.0);
        DecreaseFontSizeCommand = new RelayCommand(_ => FontSizePt -= 1.0);

        CurrentPageEdits.CollectionChanged += (s, e) =>
        {
            if (e.NewItems != null)
            {
                foreach (PdfEditItem item in e.NewItems)
                {
                    item.PropertyChanged += Item_PropertyChanged;
                }
            }
            if (e.OldItems != null)
            {
                foreach (PdfEditItem item in e.OldItems)
                {
                    item.PropertyChanged -= Item_PropertyChanged;
                }
            }
        };

        try
        {
            _draftDebounceTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(500)
            };
            _draftDebounceTimer.Tick += (s, e) =>
            {
                _draftDebounceTimer.Stop();
                PersistDraftToDisk(runAsync: true);
            };
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "DispatcherTimer could not be initialized for draft debouncing");
        }

        if (!string.IsNullOrEmpty(initialPath) && File.Exists(initialPath))
        {
            _ = LoadDocumentAsync(initialPath);
        }
    }

    private readonly DispatcherTimer? _draftDebounceTimer;
    private bool _isSuppressingItemPropertySync;

    private void Item_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_isSuppressingItemPropertySync) return;
        if (e.PropertyName == nameof(PdfEditItem.IsSelected) ||
            e.PropertyName == nameof(PdfEditItem.IsHitTestVisible))
        {
            return;
        }

        if (sender is PdfTextItem textItem)
        {
            // 1. Keep linked whiteout bounds in sync so background is always concealed as text grows, moves, or shrinks
            // Suppress item property sync while updating whiteout to avoid recursive cascading PropertyChanged events
            if (!string.IsNullOrEmpty(textItem.LinkedWhiteoutId) &&
                (e.PropertyName == nameof(PdfTextItem.Width) ||
                 e.PropertyName == nameof(PdfTextItem.Height) ||
                 e.PropertyName == nameof(PdfTextItem.X) ||
                 e.PropertyName == nameof(PdfTextItem.Y)))
            {
                var whiteout = CurrentPageEdits.OfType<PdfWhiteoutItem>()
                    .FirstOrDefault(w => w.Id == textItem.LinkedWhiteoutId);
                if (whiteout != null)
                {
                    try
                    {
                        _isSuppressingItemPropertySync = true;
                        double padX = Math.Max(1.0, textItem.FontSizePt * 0.1);
                        whiteout.X = Math.Max(0, textItem.X - padX);
                        whiteout.Y = Math.Max(0, textItem.Y - 1.0);

                        // Whiteout covers at least the original text (so original text doesn't show),
                        // but expands to cover the replacement text if it is longer.
                        // If text shrinks, whiteout shrinks down to the original text width (never staying oversized).
                        double minWhiteoutWidth = (textItem.OriginalBlockWidth > 0 ? textItem.OriginalBlockWidth : 15.0) + padX * 2 + 2.0;
                        double dynamicWhiteoutWidth = textItem.Width + padX * 2;
                        whiteout.Width = Math.Max(minWhiteoutWidth, dynamicWhiteoutWidth);
                        whiteout.Height = Math.Max(textItem.Height + 2.0, (textItem.OriginalBlockHeight > 0 ? textItem.OriginalBlockHeight : 10.0) + 2.0);
                    }
                    finally
                    {
                        _isSuppressingItemPropertySync = false;
                    }
                }
            }

            // 2. Dynamic horizontal text flow / shift:
            // When text width expands, automatically shift subsequent text on the same baseline
            if (e.PropertyName == nameof(PdfTextItem.Width))
            {
                ShiftSubsequentItemsOnLine(textItem);
            }
        }

        // Debounce disk I/O while typing or actively updating properties (zero lag on UI thread)
        SaveCurrentEditsToStore(immediateDiskSave: false);
    }

    private bool _isShiftingLineItems;
    private void ShiftSubsequentItemsOnLine(PdfTextItem textItem)
    {
        if (textItem == null || _isShiftingLineItems) return;
        try
        {
            _isShiftingLineItems = true;
            _isSuppressingItemPropertySync = true;
            double rightEdge = textItem.X + textItem.Width;

            bool IsSameLine(double otherBaselineY, double otherY)
            {
                if (textItem.BaseLineY > 0 && otherBaselineY > 0)
                    return Math.Abs(textItem.BaseLineY - otherBaselineY) <= 3.0;
                return Math.Abs(textItem.Y - otherY) <= Math.Max(4.0, textItem.FontSizePt * 0.4);
            }

            // 1. Shift any existing edit items to the right on the same line
            foreach (var other in CurrentPageEdits.OfType<PdfTextItem>().Where(t => t != textItem && t.X > textItem.X).ToList())
            {
                if (IsSameLine(other.BaseLineY, other.Y))
                {
                    double requiredX = rightEdge + 5.0;
                    if (other.X < requiredX)
                    {
                        double shift = requiredX - other.X;
                        other.X += shift;
                        if (!string.IsNullOrEmpty(other.LinkedWhiteoutId))
                        {
                            var w = CurrentPageEdits.OfType<PdfWhiteoutItem>().FirstOrDefault(x => x.Id == other.LinkedWhiteoutId);
                            if (w != null) w.X += shift;
                        }
                    }
                }
            }

            // 2. Shift / promote any unedited extracted blocks to the right on the same line
            var collidingBlocks = ExtractedTextBlocks
                .Where(b => b.CanvasX > textItem.X && IsSameLine(b.BaseLineY, b.CanvasY) && b.CanvasX < rightEdge + 5.0)
                .OrderBy(b => b.CanvasX)
                .ToList();

            foreach (var block in collidingBlocks)
            {
                double requiredX = rightEdge + 5.0;
                string bgColor = SampleBackgroundColor(block.CanvasX, block.CanvasY, block.PdfWidth, block.PdfHeight);
                double padX = Math.Max(1.0, block.FontSizePt * 0.1);

                var whiteout = new PdfWhiteoutItem
                {
                    PageIndex = CurrentPageIndex,
                    X = Math.Max(0, block.CanvasX - padX),
                    Y = Math.Max(0, block.CanvasY - 1.0),
                    Width = block.PdfWidth + padX * 2 + 2.0,
                    Height = block.PdfHeight + 2.0,
                    FillColorHex = bgColor,
                    IsHitTestVisible = false
                };
                CurrentPageEdits.Add(whiteout);

                string fontFam = block.FontFamily;
                if (BengaliTextHelper.ContainsBengali(block.OriginalText) &&
                    (fontFam == "Arial" || fontFam == "Times New Roman" || string.IsNullOrWhiteSpace(fontFam)))
                {
                    fontFam = BengaliTextHelper.PreferredBengaliFont;
                }

                var promoted = new PdfTextItem
                {
                    PageIndex = CurrentPageIndex,
                    X = requiredX,
                    Y = block.CanvasY,
                    BaseLineY = block.BaseLineY,
                    FontFamily = fontFam,
                    FontSizePt = block.FontSizePt,
                    IsBold = block.IsBold,
                    IsItalic = block.IsItalic,
                    TextColorHex = block.ColorHex,
                    BackgroundColorHex = bgColor,
                    HasOpaqueBackground = false,
                    LinkedWhiteoutId = whiteout.Id,
                    OriginalTextToRedact = block.OriginalText,
                    OriginalBlockWidth = block.PdfWidth,
                    OriginalBlockHeight = block.PdfHeight,
                    Text = block.OriginalText,
                    Height = Math.Max(block.PdfHeight, block.FontSizePt * 1.3)
                };
                CurrentPageEdits.Add(promoted);
                ExtractedTextBlocks.Remove(block);

                rightEdge = promoted.X + promoted.Width;
            }
        }
        finally
        {
            _isSuppressingItemPropertySync = false;
            _isShiftingLineItems = false;
        }
    }

    public void NotifyEditsChanged()
    {
        SaveCurrentEditsToStore(immediateDiskSave: true);
    }

    public async Task LoadDocumentAsync(string path, string? password = null)
    {
        if (!File.Exists(path)) return;

        IsLoading = true;
        StatusMessage = "Loading PDF document...";

        try
        {
            // Check if document is password protected / locked
            bool isLocked = _service.IsPasswordProtected(path, password);
            if (isLocked)
            {
                PendingProtectedPdfPath = path;
                _isPasswordProtected = true;
                _filePath = string.Empty;
                _fileName = Path.GetFileName(path);
                CurrentPagePreview = null;
                PageThumbnails.Clear();
                CurrentPageEdits.Clear();
                ExtractedTextBlocks.Clear();
                _allEdits.Clear();
                _undoStacks.Clear();
                _redoStacks.Clear();
                TotalPages = 0;
                PdfPassword = string.Empty;
                PasswordErrorMessage = string.Empty;
                CurrentPassword = null;
                StatusMessage = "🔒 This PDF is password protected. Enter password to unlock and edit.";

                OnPropertyChanged(nameof(FilePath));
                OnPropertyChanged(nameof(FileName));
                OnPropertyChanged(nameof(HasDocument));
                OnPropertyChanged(nameof(IsNormalDocumentReady));
                OnPropertyChanged(nameof(ShowEmptyState));
                OnPropertyChanged(nameof(IsPasswordProtected));
                IsLoading = false;
                return;
            }

            PendingProtectedPdfPath = null;
            _isPasswordProtected = false;
            _filePath = path;
            _fileName = Path.GetFileName(path);
            CurrentPassword = password;
            PasswordErrorMessage = string.Empty;

            OnPropertyChanged(nameof(FilePath));
            OnPropertyChanged(nameof(FileName));
            OnPropertyChanged(nameof(HasDocument));
            OnPropertyChanged(nameof(IsNormalDocumentReady));
            OnPropertyChanged(nameof(ShowEmptyState));
            OnPropertyChanged(nameof(IsPasswordProtected));

            _allEdits.Clear();
            _undoStacks.Clear();
            _redoStacks.Clear();
            PageThumbnails.Clear();

            _dimensions = _service.GetPageDimensions(path, CurrentPassword);
            TotalPages = _dimensions.Count;

            if (TotalPages == 0)
            {
                StatusMessage = "Warning: PDF contains 0 pages or could not be parsed.";
                IsLoading = false;
                return;
            }

            // Render thumbnails asynchronously in background
            _ = Task.Run(() =>
            {
                try
                {
                    var thumbs = PdfRendererService.RenderAllThumbnails(path, 180, CurrentPassword);
                    System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                    {
                        PageThumbnails.Clear();
                        for (int i = 0; i < thumbs.Count; i++)
                        {
                            PageThumbnails.Add(new PdfPageThumbnailItem
                            {
                                PageIndex = i,
                                Thumbnail = thumbs[i]
                            });
                        }
                    });
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Failed rendering thumbnails in PdfEditor");
                }
            });

            // Check if an in-progress draft session exists for this PDF
            var draft = PdfDraftService.Instance.LoadDraft(path);
            if (draft != null && draft.Value.Edits.Count > 0)
            {
                foreach (var kvp in draft.Value.Edits)
                {
                    _allEdits[kvp.Key] = kvp.Value;
                }
                _currentPageIndex = Math.Clamp(draft.Value.PageIndex, 0, Math.Max(0, TotalPages - 1));
                OnPropertyChanged(nameof(CurrentPageIndex));
                OnPropertyChanged(nameof(CurrentPageNumber));
                UpdatePageInfo();
                if (draft.Value.ZoomFactor >= 0.4 && draft.Value.ZoomFactor <= 4.0)
                {
                    ZoomFactor = draft.Value.ZoomFactor;
                }
                HasUnsavedEdits = true;
                await LoadCurrentPageAsync();
                int restoredCount = draft.Value.Edits.Values.Sum(v => v.Count);
                StatusMessage = $"⚡ Resumed draft session from {draft.Value.LastModified:g} ({restoredCount} edit(s) restored).";
            }
            else
            {
                _currentPageIndex = 0;
                OnPropertyChanged(nameof(CurrentPageIndex));
                OnPropertyChanged(nameof(CurrentPageNumber));
                UpdatePageInfo();
                HasUnsavedEdits = false;
                await LoadCurrentPageAsync();
                StatusMessage = $"Loaded {FileName} ({TotalPages} pages). Select a tool to start editing.";
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed loading document {Path} in PdfEditorViewModel", path);
            StatusMessage = $"Error: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task UnlockPdfWithPasswordAsync()
    {
        if (string.IsNullOrEmpty(PendingProtectedPdfPath) || !File.Exists(PendingProtectedPdfPath))
        {
            PasswordErrorMessage = "File no longer exists or path is invalid.";
            return;
        }

        string pwd = PdfPassword?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(pwd))
        {
            PasswordErrorMessage = "Please enter the document password.";
            return;
        }

        IsLoading = true;
        PasswordErrorMessage = string.Empty;
        StatusMessage = "Verifying password...";

        try
        {
            string path = PendingProtectedPdfPath;

            // 1. Try exact trimmed password
            bool stillLocked = _service.IsPasswordProtected(path, pwd);

            // 2. If locked and user typed lowercase, try uppercase fallback (standard for e-Aadhaar)
            if (stillLocked && pwd.Any(char.IsLower))
            {
                string upperPwd = pwd.ToUpperInvariant();
                if (!_service.IsPasswordProtected(path, upperPwd))
                {
                    pwd = upperPwd;
                    stillLocked = false;
                }
            }

            if (stillLocked)
            {
                PasswordErrorMessage = "❌ Incorrect password. Please verify and try again.";
                StatusMessage = "Incorrect password.";
                return;
            }

            // Successfully unlocked!
            CurrentPassword = pwd;
            IsPasswordProtected = false;
            PendingProtectedPdfPath = null;
            PasswordErrorMessage = string.Empty;

            await LoadDocumentAsync(path, pwd);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to unlock password-protected PDF");
            PasswordErrorMessage = $"Failed to unlock: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    public void CancelUnlock()
    {
        _isPasswordProtected = false;
        PendingProtectedPdfPath = null;
        PdfPassword = string.Empty;
        PasswordErrorMessage = string.Empty;
        _filePath = string.Empty;
        _fileName = "No Document";
        CurrentPagePreview = null;
        PageThumbnails.Clear();
        CurrentPageEdits.Clear();
        ExtractedTextBlocks.Clear();
        _allEdits.Clear();
        TotalPages = 0;
        StatusMessage = "Unlock cancelled.";

        OnPropertyChanged(nameof(FilePath));
        OnPropertyChanged(nameof(FileName));
        OnPropertyChanged(nameof(HasDocument));
        OnPropertyChanged(nameof(IsNormalDocumentReady));
        OnPropertyChanged(nameof(ShowEmptyState));
        OnPropertyChanged(nameof(IsPasswordProtected));
    }

    private async Task LoadCurrentPageAsync()
    {
        if (!HasDocument || CurrentPageIndex < 0 || CurrentPageIndex >= TotalPages) return;

        IsLoading = true;
        StatusMessage = $"Rendering Page {CurrentPageNumber}...";

        try
        {
            if (_dimensions.Count > CurrentPageIndex)
            {
                PageNativeWidth = _dimensions[CurrentPageIndex].WidthPoints;
                PageNativeHeight = _dimensions[CurrentPageIndex].HeightPoints;
            }

            var bmp = await _service.RenderPageAsync(FilePath, CurrentPageIndex, 150, CurrentPassword);
            CurrentPagePreview = bmp;

            _isSuppressingItemPropertySync = true;
            CurrentPageEdits.Clear();
            if (_allEdits.TryGetValue(CurrentPageIndex, out var list) && list != null)
            {
                foreach (var item in list)
                {
                    item.PageIndex = CurrentPageIndex;
                    CurrentPageEdits.Add(item);
                }
            }
            _isSuppressingItemPropertySync = false;

            SelectedItem = null;
            UpdatePageInfo();
            StatusMessage = $"Page {CurrentPageNumber} of {TotalPages} ready.";

            if (IsEditTextTool)
            {
                _ = LoadExtractedTextBlocksAsync();
            }
            else
            {
                ExtractedTextBlocks.Clear();
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error loading page {Page} in PdfEditorViewModel", CurrentPageIndex);
            StatusMessage = $"Error loading page: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void UpdatePageInfo()
    {
        if (_dimensions.Count > CurrentPageIndex)
        {
            var d = _dimensions[CurrentPageIndex];
            PageInfoText = $"Page {CurrentPageNumber} of {TotalPages} • {d.WidthMm:F0} × {d.HeightMm:F0} mm ({(int)d.WidthPoints} × {(int)d.HeightPoints} pt)";
        }
        else
        {
            PageInfoText = $"Page {CurrentPageNumber} of {TotalPages}";
        }
        OnPropertyChanged(nameof(PageNumberText));
        OnPropertyChanged(nameof(CurrentPageNumber));
    }

    public void AddEditItem(PdfEditItem item)
    {
        PushUndoState();
        item.PageIndex = CurrentPageIndex;
        CurrentPageEdits.Add(item);
        SelectedItem = item;
        SaveCurrentEditsToStore(immediateDiskSave: true);
        StatusMessage = $"Added {item.ItemType}. Drag to move or edit properties.";
    }

    public void DeleteSelectedItem()
    {
        if (SelectedItem == null) return;
        PushUndoState();
        var itemToDelete = SelectedItem;
        CurrentPageEdits.Remove(itemToDelete);

        // If this text item replaced an existing word, also remove the underlying whiteout box
        if (itemToDelete is PdfTextItem ti && !string.IsNullOrEmpty(ti.LinkedWhiteoutId))
        {
            var linkedWhiteout = CurrentPageEdits.FirstOrDefault(x => x.Id == ti.LinkedWhiteoutId);
            if (linkedWhiteout != null)
            {
                CurrentPageEdits.Remove(linkedWhiteout);
            }

            // Restore the original block to ExtractedTextBlocks so the user can immediately click to edit it again
            if (!string.IsNullOrEmpty(ti.OriginalTextToRedact) &&
                !ExtractedTextBlocks.Any(b => Math.Abs(b.CanvasX - ti.X) < 1.5 && Math.Abs(b.CanvasY - ti.Y) < 1.5))
            {
                ExtractedTextBlocks.Add(new SmartSaver.Models.PdfExtractedTextBlock
                {
                    OriginalText = ti.OriginalTextToRedact,
                    CanvasX      = ti.X,
                    CanvasY      = ti.Y,
                    BaseLineY    = ti.BaseLineY,
                    PdfWidth     = ti.OriginalBlockWidth > 0 ? ti.OriginalBlockWidth : ti.Width,
                    PdfHeight    = ti.OriginalBlockHeight > 0 ? ti.OriginalBlockHeight : ti.Height,
                    FontFamily   = ti.FontFamily,
                    FontSizePt   = ti.FontSizePt,
                    IsBold       = ti.IsBold,
                    IsItalic     = ti.IsItalic,
                    ColorHex     = ti.TextColorHex
                });
            }
        }

        SelectedItem = null;
        SaveCurrentEditsToStore(immediateDiskSave: true);
        StatusMessage = "Item removed.";
    }

    public void FlushDraftNow()
    {
        _draftDebounceTimer?.Stop();
        PersistDraftToDisk(runAsync: false);
    }

    private void SaveCurrentEditsToStore(bool immediateDiskSave = false)
    {
        _allEdits[CurrentPageIndex] = CurrentPageEdits.ToList();
        HasUnsavedEdits = _allEdits.Values.Any(list => list != null && list.Count > 0);

        if (immediateDiskSave || _draftDebounceTimer == null)
        {
            _draftDebounceTimer?.Stop();
            PersistDraftToDisk(runAsync: false);
        }
        else
        {
            // Reset debounce timer on keystroke / drag (typing is 100% in-memory & instantaneous)
            _draftDebounceTimer.Stop();
            _draftDebounceTimer.Start();
        }
    }

    private void PersistDraftToDisk(bool runAsync = true)
    {
        if (string.IsNullOrEmpty(FilePath)) return;

        string path = FilePath;
        int pageIndex = CurrentPageIndex;
        double zoom = ZoomFactor;
        bool hasEdits = HasUnsavedEdits;

        if (!hasEdits)
        {
            Action deleteWork = () =>
            {
                try
                {
                    PdfDraftService.Instance.DeleteDraft(path);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Failed to delete draft for {Path}", path);
                }
            };

            if (runAsync) Task.Run(deleteWork);
            else deleteWork();
            return;
        }

        // Snapshot DTOs on UI thread safely and rapidly (pure in-memory copy, < 0.1ms)
        var dtoSnapshot = new Dictionary<int, List<PdfEditItemDto>>();
        foreach (var kvp in _allEdits)
        {
            if (kvp.Value != null && kvp.Value.Count > 0)
            {
                dtoSnapshot[kvp.Key] = kvp.Value.Select(PdfDraftService.ToDto).ToList();
            }
        }

        Action saveWork = () =>
        {
            try
            {
                PdfDraftService.Instance.SaveDraftFromDtos(path, dtoSnapshot, pageIndex, zoom);
                OutputHistoryService.Instance.Record(path, "PDF In Progress (Draft)");
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Background draft persistence failed for {Path}", path);
            }
        };

        if (runAsync)
        {
            Task.Run(saveWork);
        }
        else
        {
            saveWork();
        }
    }

    private void PushUndoState()
    {
        if (!_undoStacks.ContainsKey(CurrentPageIndex))
            _undoStacks[CurrentPageIndex] = new Stack<PageUndoState>();

        // Deep copy of both current edits and extracted text blocks
        var snapshot = new PageUndoState
        {
            Edits = CurrentPageEdits.Select(CloneItem).ToList(),
            Blocks = ExtractedTextBlocks.Select(b => b.Clone()).ToList()
        };
        _undoStacks[CurrentPageIndex].Push(snapshot);

        if (_redoStacks.ContainsKey(CurrentPageIndex))
            _redoStacks[CurrentPageIndex].Clear();
    }

    public bool CanUndo() => _undoStacks.ContainsKey(CurrentPageIndex) && _undoStacks[CurrentPageIndex].Count > 0;
    public bool CanRedo() => _redoStacks.ContainsKey(CurrentPageIndex) && _redoStacks[CurrentPageIndex].Count > 0;

    public void Undo()
    {
        if (!CanUndo()) return;

        if (!_redoStacks.ContainsKey(CurrentPageIndex))
            _redoStacks[CurrentPageIndex] = new Stack<PageUndoState>();

        // Push current edits & blocks to redo stack
        _redoStacks[CurrentPageIndex].Push(new PageUndoState
        {
            Edits = CurrentPageEdits.Select(CloneItem).ToList(),
            Blocks = ExtractedTextBlocks.Select(b => b.Clone()).ToList()
        });

        var previous = _undoStacks[CurrentPageIndex].Pop();

        CurrentPageEdits.Clear();
        foreach (var item in previous.Edits) CurrentPageEdits.Add(item);

        ExtractedTextBlocks.Clear();
        foreach (var block in previous.Blocks) ExtractedTextBlocks.Add(block);

        SelectedItem = null;
        SaveCurrentEditsToStore(immediateDiskSave: true);
        StatusMessage = "Undo applied.";
    }

    public void Redo()
    {
        if (!CanRedo()) return;

        _undoStacks[CurrentPageIndex].Push(new PageUndoState
        {
            Edits = CurrentPageEdits.Select(CloneItem).ToList(),
            Blocks = ExtractedTextBlocks.Select(b => b.Clone()).ToList()
        });

        var next = _redoStacks[CurrentPageIndex].Pop();

        CurrentPageEdits.Clear();
        foreach (var item in next.Edits) CurrentPageEdits.Add(item);

        ExtractedTextBlocks.Clear();
        foreach (var block in next.Blocks) ExtractedTextBlocks.Add(block);

        SelectedItem = null;
        SaveCurrentEditsToStore(immediateDiskSave: true);
        StatusMessage = "Redo applied.";
    }

    private void SyncToolPropertiesFromSelected()
    {
        if (SelectedItem is PdfTextItem ti)
        {
            _selectedFontFamily = ti.FontFamily;
            _fontSizePt = ti.FontSizePt;
            _isBold = ti.IsBold;
            _isItalic = ti.IsItalic;
            _textColorHex = ti.TextColorHex;
            _hasOpaqueBackground = ti.HasOpaqueBackground;

            OnPropertyChanged(nameof(SelectedFontFamily));
            OnPropertyChanged(nameof(FontSizePt));
            OnPropertyChanged(nameof(IsBold));
            OnPropertyChanged(nameof(IsItalic));
            OnPropertyChanged(nameof(TextColorHex));
            OnPropertyChanged(nameof(HasOpaqueBackground));
        }
    }

    private void TriggerItemChanged()
    {
        SaveCurrentEditsToStore(immediateDiskSave: true);
    }

    private static PdfEditItem CloneItem(PdfEditItem original)
    {
        return original switch
        {
            PdfWhiteoutItem w => new PdfWhiteoutItem
            {
                Id = w.Id,
                PageIndex = w.PageIndex,
                X = w.X,
                Y = w.Y,
                Width = w.Width,
                Height = w.Height,
                FillColorHex = w.FillColorHex,
                IsHitTestVisible = w.IsHitTestVisible
            },
            PdfTextItem t => new PdfTextItem
            {
                Id = t.Id,
                PageIndex = t.PageIndex,
                X = t.X,
                Y = t.Y,
                BaseLineY = t.BaseLineY,
                FontFamily = t.FontFamily,
                FontSizePt = t.FontSizePt,
                IsBold = t.IsBold,
                IsItalic = t.IsItalic,
                TextColorHex = t.TextColorHex,
                HasOpaqueBackground = t.HasOpaqueBackground,
                BackgroundColorHex = t.BackgroundColorHex,
                LinkedWhiteoutId = t.LinkedWhiteoutId,
                OriginalTextToRedact = t.OriginalTextToRedact,
                OriginalBlockWidth = t.OriginalBlockWidth,
                OriginalBlockHeight = t.OriginalBlockHeight,
                IsHitTestVisible = t.IsHitTestVisible,
                Text = t.Text,
                Width = t.Width,
                Height = t.Height
            },
            PdfImageItem i => new PdfImageItem
            {
                Id = Guid.NewGuid().ToString("N"),
                PageIndex = i.PageIndex,
                X = i.X,
                Y = i.Y,
                Width = i.Width,
                Height = i.Height,
                SourceFilePath = i.SourceFilePath,
                ImageBytes = i.ImageBytes,
                MaintainAspectRatio = i.MaintainAspectRatio
            },
            PdfInkItem k => new PdfInkItem
            {
                Id = Guid.NewGuid().ToString("N"),
                PageIndex = k.PageIndex,
                Points = new List<(double X, double Y)>(k.Points),
                StrokeColorHex = k.StrokeColorHex,
                StrokeThickness = k.StrokeThickness
            },
            _ => throw new NotSupportedException()
        };
    }

    public void PasteImageFromClipboard()
    {
        try
        {
            string? temp = ClipboardHelper.SaveClipboardImageToFile();
            if (!string.IsNullOrEmpty(temp) && File.Exists(temp))
            {
                byte[] bytes = File.ReadAllBytes(temp);
                using var bmp = SmartSaver.Helpers.ImageHelper.LoadOrientedBitmap(temp);
                double aspect = bmp.Height > 0 ? (double)bmp.Width / bmp.Height : 1.0;

                // Center on page with sensible default size (e.g. 100 pt width)
                double defaultW = Math.Min(150.0, PageNativeWidth * 0.4);
                double defaultH = defaultW / aspect;

                double placeX = Math.Max(20, (PageNativeWidth - defaultW) / 2.0);
                double placeY = Math.Max(20, (PageNativeHeight - defaultH) / 2.0);

                var imgItem = new PdfImageItem
                {
                    SourceFilePath = temp,
                    ImageBytes = bytes,
                    X = placeX,
                    Y = placeY,
                    Width = defaultW,
                    Height = defaultH
                };

                AddEditItem(imgItem);
                SelectedTool = PdfEditorTool.Select;
                StatusMessage = "Pasted image from clipboard. Drag to position, drag handles to resize.";
            }
            else
            {
                WpfMessageBox.Show("No image found on the clipboard.", "Clipboard Empty", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed pasting image in PdfEditor");
            WpfMessageBox.Show($"Failed to paste image:\n{ex.Message}", "Paste Error", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    public void InsertImageFromFile()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Insert Photo / Stamp / Signature",
            Filter = "All Supported Images (*.jpg;*.jpeg;*.jpe;*.jfif;*.png;*.webp;*.bmp;*.gif;*.tiff;*.tif)|*.jpg;*.jpeg;*.jpe;*.jfif;*.png;*.webp;*.bmp;*.gif;*.tiff;*.tif|JPEG Images (*.jpg;*.jpeg;*.jfif;*.jpe)|*.jpg;*.jpeg;*.jfif;*.jpe|PNG Images (*.png)|*.png|WebP Images (*.webp)|*.webp|BMP Images (*.bmp)|*.bmp|TIFF Images (*.tiff;*.tif)|*.tiff;*.tif|All Files (*.*)|*.*"
        };
        if (dlg.ShowDialog() == true)
        {
            try
            {
                byte[] bytes = File.ReadAllBytes(dlg.FileName);
                using var bmp = SmartSaver.Helpers.ImageHelper.LoadOrientedBitmap(dlg.FileName);
                double aspect = bmp.Height > 0 ? (double)bmp.Width / bmp.Height : 1.0;

                double defaultW = Math.Min(120.0, PageNativeWidth * 0.35);
                double defaultH = defaultW / aspect;

                var imgItem = new PdfImageItem
                {
                    SourceFilePath = dlg.FileName,
                    ImageBytes = bytes,
                    X = 50,
                    Y = 50,
                    Width = defaultW,
                    Height = defaultH
                };

                AddEditItem(imgItem);
                SelectedTool = PdfEditorTool.Select;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to insert image in PdfEditor");
                WpfMessageBox.Show($"Could not open image:\n{ex.Message}", "Image Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    public async Task<bool> SavePdfAsync(bool saveAsNew, bool reloadAfterSave = true)
    {
        if (!HasDocument) return false;

        SelectedItem = null;
        SaveCurrentEditsToStore(immediateDiskSave: true);

        string outPath;
        if (saveAsNew)
        {
            string defaultName = $"{Path.GetFileNameWithoutExtension(FileName)}_edited.pdf";
            string dir = Path.GetDirectoryName(FilePath) ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

            if (RequestSaveFile != null)
            {
                string? chosen = RequestSaveFile("Save Edited PDF", defaultName);
                if (string.IsNullOrEmpty(chosen)) return false;
                outPath = chosen;
            }
            else
            {
                var sfd = new Microsoft.Win32.SaveFileDialog
                {
                    Title = "Save Edited PDF As",
                    Filter = "PDF Document (*.pdf)|*.pdf",
                    FileName = defaultName,
                    InitialDirectory = dir
                };
                if (sfd.ShowDialog() != true) return false;
                outPath = sfd.FileName;
            }
        }
        else
        {
            outPath = FilePath;
        }

        IsLoading = true;
        StatusMessage = "Saving edited PDF (preserving vector quality)...";

        try
        {
            var dict = _allEdits.ToDictionary(k => k.Key, v => (IReadOnlyList<PdfEditItem>)v.Value);
            bool success = await _service.SaveEditsToPdfAsync(FilePath, outPath, dict, CurrentPassword);

            if (success)
            {
                // Delete active draft sessions for this PDF since changes are now permanently saved
                PdfDraftService.Instance.DeleteDraft(FilePath);
                if (saveAsNew)
                {
                    PdfDraftService.Instance.DeleteDraft(outPath);
                }
                HasUnsavedEdits = false;

                try
                {
                    long origSize = File.Exists(FilePath) ? new FileInfo(FilePath).Length : 0;
                    long newSize = File.Exists(outPath) ? new FileInfo(outPath).Length : 0;
                    OutputHistoryService.Instance.Record(outPath, "PDF Edited", origSize, newSize);
                }
                catch { }

                StatusMessage = $"✅ Saved successfully to: {Path.GetFileName(outPath)}";
                if (ShowMessage != null)
                {
                    ShowMessage($"Edited PDF saved successfully!\n\nLocation: {outPath}", "PDF Saved");
                }
                else
                {
                    WpfMessageBox.Show($"Edited PDF saved successfully!\n\nLocation: {outPath}", "PDF Saved", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                // Only reload the document in WPF context when not closing — skip in headless/test mode or window close
                // (LoadCurrentPageAsync → RenderPageAsync → PDFium hangs in non-STA console)
                if (reloadAfterSave && System.Windows.Application.Current != null)
                {
                    await LoadDocumentAsync(outPath, CurrentPassword);
                }
                return true;
            }
            else
            {
                StatusMessage = "❌ Failed to save PDF.";
                if (ShowMessage != null)
                {
                    ShowMessage("Could not save PDF. Ensure the file is not open in another viewer.", "Save Error");
                }
                else
                {
                    WpfMessageBox.Show("Could not save PDF. Ensure the file is not open in another viewer.", "Save Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                return false;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error saving PDF in PdfEditorViewModel");
            StatusMessage = $"Save Error: {ex.Message}";
            return false;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void PrintCurrentPdf()
    {
        if (!HasDocument) return;

        try
        {
            // First save temp edited copy
            SaveCurrentEditsToStore(immediateDiskSave: true);
            string tempPrintFile = Path.Combine(Path.GetTempPath(), $"print_{Guid.NewGuid():N}.pdf");
            var dict = _allEdits.ToDictionary(k => k.Key, v => (IReadOnlyList<PdfEditItem>)v.Value);
            bool ok = _service.SaveEditsToPdfAsync(FilePath, tempPrintFile, dict, CurrentPassword).GetAwaiter().GetResult();

            if (ok && File.Exists(tempPrintFile))
            {
                var owner = System.Windows.Application.Current?.Windows.OfType<Views.PdfEditorWindow>().FirstOrDefault()
                            ?? System.Windows.Application.Current?.MainWindow;

                // Render the edited page at high print quality (300 DPI)
                var printBmp = _service.RenderPageAsync(tempPrintFile, CurrentPageIndex, 300).GetAwaiter().GetResult();
                var bmpToPrint = printBmp ?? CurrentPagePreview;

                if (bmpToPrint != null)
                {
                    string preferredPaper = (PageNativeWidth > 300 && PageNativeHeight > 450) ? "A4 (210 × 297 mm)" : "Letter";
                    string preferredOri = PageNativeWidth > PageNativeHeight ? "Landscape" : "Portrait";
                    Views.NativePrintDialog.ShowPrintDialog(bmpToPrint, $"{Path.GetFileNameWithoutExtension(FileName)} (Page {CurrentPageNumber})", owner, preferredPaper, preferredOri);
                }

                try { File.Delete(tempPrintFile); } catch { }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to launch native print from PdfEditor");
            WpfMessageBox.Show($"Print error: {ex.Message}", "Print Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BrowseAndOpenPdf()
    {
        var ofd = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Open PDF Document to Edit",
            Filter = "PDF Files (*.pdf)|*.pdf|All Files (*.*)|*.*"
        };
        if (ofd.ShowDialog() == true)
        {
            _ = LoadDocumentAsync(ofd.FileName);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // EDIT TEXT TOOL — PdfPig text extraction + in-place editing
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Loads all text blocks for the current page via PdfPig and populates
    /// <see cref="ExtractedTextBlocks"/> so the XAML overlay can show them.
    /// Runs on a background thread; updates UI via Dispatcher.
    /// </summary>
    public async Task LoadExtractedTextBlocksAsync()
    {
        if (!HasDocument) return;

        ExtractedTextBlocks.Clear();
        StatusMessage = "🔍 Reading PDF text content...";

        try
        {
            double pageH = PageNativeHeight;
            string path  = FilePath;
            int    page  = CurrentPageIndex;

            var blocks = await Task.Run(() =>
                _service.ExtractTextBlocks(path, page, pageH, CurrentPassword));

            // Guard against page flip while extracting in background
            if (page != CurrentPageIndex || path != FilePath) return;

            Action updateUi = () =>
            {
                ExtractedTextBlocks.Clear();
                foreach (var b in blocks)
                    ExtractedTextBlocks.Add(b);

                StatusMessage = blocks.Count > 0
                    ? $"✏️ {blocks.Count} text blocks found. Click any text to edit it in-place."
                    : "⚠️ No selectable text found on this page (may be a scanned image PDF).";
            };

            if (System.Windows.Application.Current?.Dispatcher != null &&
                !System.Windows.Application.Current.Dispatcher.CheckAccess())
            {
                System.Windows.Application.Current.Dispatcher.Invoke(updateUi);
            }
            else
            {
                updateUi();
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "LoadExtractedTextBlocksAsync failed");
            StatusMessage = $"Text extraction error: {ex.Message}";
        }
    }

    /// <summary>
    /// Samples the background color from the rendered page preview (<see cref="CurrentPagePreview"/>)
    /// around the perimeter and interior of a text block's bounding box.
    /// Filters out dark ink/text pixels to find the true underlying cell or page background color
    /// (e.g. #EFEFEF for shaded table cells or #FFFFFF for white document areas).
    /// </summary>
    public string SampleBackgroundColor(double canvasX, double canvasY, double canvasW, double canvasH)
    {
        try
        {
            if (CurrentPagePreview == null || PageNativeWidth <= 0 || PageNativeHeight <= 0)
                return "#FFFFFF";

            var bmp = CurrentPagePreview;
            int pw = bmp.PixelWidth;
            int ph = bmp.PixelHeight;
            if (pw <= 0 || ph <= 0) return "#FFFFFF";

            double scaleX = (double)pw / PageNativeWidth;
            double scaleY = (double)ph / PageNativeHeight;

            int px = (int)Math.Clamp(Math.Round(canvasX * scaleX), 0, pw - 1);
            int py = (int)Math.Clamp(Math.Round(canvasY * scaleY), 0, ph - 1);
            int pwBox = (int)Math.Clamp(Math.Round(canvasW * scaleX), 1, pw - px);
            int phBox = (int)Math.Clamp(Math.Round(canvasH * scaleY), 1, ph - py);

            BitmapSource source = bmp;
            if (bmp.Format != System.Windows.Media.PixelFormats.Bgra32 && bmp.Format != System.Windows.Media.PixelFormats.Bgr32)
            {
                var conv = new FormatConvertedBitmap();
                conv.BeginInit();
                conv.Source = bmp;
                conv.DestinationFormat = System.Windows.Media.PixelFormats.Bgra32;
                conv.EndInit();
                conv.Freeze();
                source = conv;
            }

            int padPx = Math.Max(2, (int)(3 * scaleX));
            int sampleX = Math.Max(0, px - padPx);
            int sampleY = Math.Max(0, py - padPx);
            int sampleW = Math.Min(pw - sampleX, pwBox + padPx * 2);
            int sampleH = Math.Min(ph - sampleY, phBox + padPx * 2);

            if (sampleW <= 0 || sampleH <= 0) return "#FFFFFF";

            int stride = sampleW * 4;
            byte[] pixels = new byte[stride * sampleH];
            var rect = new System.Windows.Int32Rect(sampleX, sampleY, sampleW, sampleH);
            source.CopyPixels(rect, pixels, stride, 0);

            var colorCounts = new Dictionary<int, int>();

            for (int y = 0; y < sampleH; y++)
            {
                bool isBorderY = (y < padPx || y >= sampleH - padPx);
                for (int x = 0; x < sampleW; x++)
                {
                    bool isBorderX = (x < padPx || x >= sampleW - padPx);
                    int idx = y * stride + x * 4;
                    byte b = pixels[idx];
                    byte g = pixels[idx + 1];
                    byte r = pixels[idx + 2];
                    byte a = pixels[idx + 3];

                    if (a < 50) continue;

                    // Perceived luminance: dark ink has lum < 0.45; light/grey/white backgrounds have lum > 0.6
                    double lum = (0.299 * r + 0.587 * g + 0.114 * b) / 255.0;
                    if (lum < 0.4) continue;

                    // Weight perimeter pixels higher since text rarely lies outside the bounding box
                    int weight = (isBorderX || isBorderY) ? 3 : 1;

                    int key = (r << 16) | (g << 8) | b;
                    if (!colorCounts.TryGetValue(key, out int count)) count = 0;
                    colorCounts[key] = count + weight;
                }
            }

            if (colorCounts.Count > 0)
            {
                int dominantKey = colorCounts.OrderByDescending(kvp => kvp.Value).First().Key;
                byte r = (byte)((dominantKey >> 16) & 0xFF);
                byte g = (byte)((dominantKey >> 8) & 0xFF);
                byte b = (byte)(dominantKey & 0xFF);
                return $"#{r:X2}{g:X2}{b:X2}";
            }

            return "#FFFFFF";
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to sample background color");
            return "#FFFFFF";
        }
    }

    /// <summary>
    /// Called when the user confirms an edit on a text block.
    /// Creates a matching <see cref="PdfWhiteoutItem"/> using sampled cell background color to cover the original text,
    /// then a <see cref="PdfTextItem"/> with the replacement text using matched font/size/color.
    /// </summary>
    public void CommitTextEdit(SmartSaver.Models.PdfExtractedTextBlock block, string newText)
    {
        if (block == null || string.IsNullOrEmpty(newText)) return;
        if (newText == block.OriginalText) return; // Nothing changed

        PushUndoState();

        // 1. Dynamic background color sampling from rendered page preview
        string bgColor = SampleBackgroundColor(block.CanvasX, block.CanvasY, block.PdfWidth, block.PdfHeight);

        // 2. Whiteout to erase the original text (use a slightly enlarged box for clean coverage)
        double pad = Math.Max(1.0, block.FontSizePt * 0.12);
        var whiteout = new PdfWhiteoutItem
        {
            PageIndex    = CurrentPageIndex,
            X            = block.CanvasX - pad,
            Y            = block.CanvasY - pad,
            Width        = block.PdfWidth  + pad * 2,
            Height       = block.PdfHeight + pad * 2,
            FillColorHex = bgColor
        };
        CurrentPageEdits.Add(whiteout);

        // 3. New text overlay with font matching
        string fontFam = block.FontFamily;
        if (BengaliTextHelper.ContainsBengali(newText) || BengaliTextHelper.ContainsBengali(block.OriginalText))
        {
            if (fontFam == "Arial" || fontFam == "Times New Roman" || string.IsNullOrWhiteSpace(fontFam))
                fontFam = BengaliTextHelper.PreferredBengaliFont;
        }

        var textItem = new PdfTextItem
        {
            PageIndex            = CurrentPageIndex,
            X                    = block.CanvasX,
            Y                    = block.CanvasY,
            Width                = Math.Max(block.PdfWidth + pad * 2, newText.Length * block.FontSizePt * 0.55),
            Height               = block.PdfHeight + pad,
            Text                 = newText,
            FontFamily           = fontFam,
            FontSizePt           = block.FontSizePt,
            IsBold               = block.IsBold,
            IsItalic             = block.IsItalic,
            TextColorHex         = block.ColorHex,
            BackgroundColorHex   = bgColor,
            HasOpaqueBackground  = false,  // transparent bg — whiteout is separate
            OriginalTextToRedact = block.OriginalText
        };
        CurrentPageEdits.Add(textItem);

        block.IsBeingEdited = false;
        SaveCurrentEditsToStore(immediateDiskSave: true);

        StatusMessage = $"✅ Text replaced: '{block.OriginalText}' → '{newText}'";
    }

    /// <summary>
    /// Starts in-place editing of an extracted text block:
    /// Creates a clean whiteout underneath to erase the original text (matching cell background),
    /// places a matching PdfTextItem at the exact location with matched font, size, weight, color,
    /// selects the item, and returns it so UI can focus its TextBox.
    /// </summary>
    public PdfTextItem? StartEditingExtractedBlock(SmartSaver.Models.PdfExtractedTextBlock block)
    {
        if (block == null) return null;

        PushUndoState();

        // 1. Dynamic background color sampling from rendered page preview
        string bgColor = SampleBackgroundColor(block.CanvasX, block.CanvasY, block.PdfWidth, block.PdfHeight);

        // 2. Clean whiteout box covering the original text matching background
        double padX = Math.Max(1.0, block.FontSizePt * 0.1);
        var whiteout = new PdfWhiteoutItem
        {
            PageIndex        = CurrentPageIndex,
            X                = Math.Max(0, block.CanvasX - padX),
            Y                = Math.Max(0, block.CanvasY - 1.0),
            Width            = block.PdfWidth + padX * 2 + 2.0,
            Height           = block.PdfHeight + 2.0,
            FillColorHex     = bgColor,
            IsHitTestVisible = false // Automatic whiteout is non-interactive; passes mouse through to text/canvas
        };
        CurrentPageEdits.Add(whiteout);

        // 3. New matching text item placed at the exact baseline coordinates
        string fontFam = block.FontFamily;
        if (BengaliTextHelper.ContainsBengali(block.OriginalText) || BengaliTextHelper.ContainsIndic(block.OriginalText))
        {
            if (fontFam == "Arial" || fontFam == "Times New Roman" || string.IsNullOrWhiteSpace(fontFam))
                fontFam = BengaliTextHelper.PreferredBengaliFont;
        }

        var textItem = new PdfTextItem
        {
            PageIndex            = CurrentPageIndex,
            X                    = block.CanvasX,
            Y                    = block.CanvasY,
            BaseLineY            = block.BaseLineY,
            FontFamily           = fontFam,
            FontSizePt           = block.FontSizePt,
            IsBold               = block.IsBold,
            IsItalic             = block.IsItalic,
            TextColorHex         = block.ColorHex,
            BackgroundColorHex   = bgColor,
            HasOpaqueBackground  = false,
            LinkedWhiteoutId     = whiteout.Id,
            OriginalTextToRedact = block.OriginalText,
            OriginalBlockWidth   = block.PdfWidth,
            OriginalBlockHeight  = block.PdfHeight,
            Height               = Math.Max(block.PdfHeight, block.FontSizePt * 1.3),
            Text                 = block.OriginalText
        };
        CurrentPageEdits.Add(textItem);
        SelectedItem = textItem;

        // Explicitly sync toolbar properties so B / I buttons and font selectors immediately reflect the extracted block
        _isBold = block.IsBold;
        _isItalic = block.IsItalic;
        _selectedFontFamily = fontFam;
        _fontSizePt = block.FontSizePt;
        _textColorHex = block.ColorHex;
        OnPropertyChanged(nameof(IsBold));
        OnPropertyChanged(nameof(IsItalic));
        OnPropertyChanged(nameof(SelectedFontFamily));
        OnPropertyChanged(nameof(FontSizePt));
        OnPropertyChanged(nameof(TextColorHex));

        SaveCurrentEditsToStore(immediateDiskSave: true);

        ExtractedTextBlocks.Remove(block);
        StatusMessage = $"✏️ Editing '{block.OriginalText}' ({fontFam} {block.FontSizePt:F1}pt, Bold={block.IsBold}). Type your replacement text.";

        return textItem;
    }
}
