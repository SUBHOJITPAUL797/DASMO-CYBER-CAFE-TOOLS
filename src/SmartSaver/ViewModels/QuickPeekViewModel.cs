using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Serilog;
using PdfiumViewer;
using SmartSaver.Helpers;
using SmartSaver.Models;
using SmartSaver.Services;

namespace SmartSaver.ViewModels;

public class QuickPeekViewModel : ViewModelBase
{
    private string _filePath;

    public string FilePath => _filePath;
    public string FileName => Path.GetFileName(_filePath);
    public string ExtensionUpper => Path.GetExtension(_filePath).TrimStart('.').ToUpperInvariant();
    public string FileSizeFormatted => File.Exists(_filePath) ? CompressionResult.FormatFileSize(new FileInfo(_filePath).Length) : string.Empty;

    private bool _isPdf;
    public bool IsPdf { get => _isPdf; set => SetProperty(ref _isPdf, value); }

    private bool _isImage;
    public bool IsImage { get => _isImage; set => SetProperty(ref _isImage, value); }

    private bool _isText;
    public bool IsText { get => _isText; set => SetProperty(ref _isText, value); }

    private BitmapSource? _imageSource;
    public BitmapSource? ImageSource { get => _imageSource; set => SetProperty(ref _imageSource, value); }

    private string _textContent = string.Empty;
    public string TextContent { get => _textContent; set => SetProperty(ref _textContent, value); }

    public ObservableCollection<BitmapSource> PdfPages { get; } = new();

    private int _currentPageIndex;
    public int CurrentPageIndex
    {
        get => _currentPageIndex;
        set
        {
            if (SetProperty(ref _currentPageIndex, value))
            {
                OnPropertyChanged(nameof(CurrentPageDisplay));
                OnPropertyChanged(nameof(SelectedPdfPage));
                OnPropertyChanged(nameof(CanGoPrevPage));
                OnPropertyChanged(nameof(CanGoNextPage));
            }
        }
    }

    public BitmapSource? SelectedPdfPage => PdfPages.Count > 0 && CurrentPageIndex >= 0 && CurrentPageIndex < PdfPages.Count
        ? PdfPages[CurrentPageIndex]
        : null;

    private int _totalPagesCount;
    public int TotalPagesCount
    {
        get => _totalPagesCount > 0 ? _totalPagesCount : Math.Max(1, PdfPages.Count);
        set
        {
            if (SetProperty(ref _totalPagesCount, value))
            {
                OnPropertyChanged(nameof(CurrentPageDisplay));
                OnPropertyChanged(nameof(CanGoNextPage));
            }
        }
    }

    public string CurrentPageDisplay => IsPdf
        ? $"Page {CurrentPageIndex + 1} of {TotalPagesCount}"
        : string.Empty;

    public bool CanGoPrevPage => CurrentPageIndex > 0;
    public bool CanGoNextPage => CurrentPageIndex < TotalPagesCount - 1;

    #region Folder File Navigation (Next / Prev File)

    private List<string> _folderFiles = new();
    private int _currentFileIndex = -1;

    public bool HasMultipleFiles => _folderFiles.Count > 1;
    public bool CanGoPrevFile => _currentFileIndex > 0;
    public bool CanGoNextFile => _currentFileIndex >= 0 && _currentFileIndex < _folderFiles.Count - 1;
    public int TotalFilesCount => _folderFiles.Count;
    public int CurrentFileIndex => _currentFileIndex;

    public string FileNavigationDisplay => _folderFiles.Count > 1
        ? $"File {_currentFileIndex + 1} of {_folderFiles.Count}"
        : string.Empty;

    public string PrevFileName => CanGoPrevFile ? Path.GetFileName(_folderFiles[_currentFileIndex - 1]) : string.Empty;
    public string NextFileName => CanGoNextFile ? Path.GetFileName(_folderFiles[_currentFileIndex + 1]) : string.Empty;

    public string PrevFileToolTip => CanGoPrevFile
        ? $"Previous File: {PrevFileName} (↑ / P)"
        : "No previous file";

    public string NextFileToolTip => CanGoNextFile
        ? $"Next File: {NextFileName} (↓ / N)"
        : "No next file";

    public ICommand PrevFileCommand { get; }
    public ICommand NextFileCommand { get; }

    public static readonly HashSet<string> SupportedPeekExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf",
        ".jpg", ".jpeg", ".png", ".bmp", ".webp", ".tiff", ".tif", ".ico", ".gif",
        ".txt", ".log", ".json", ".csv", ".xml", ".ini", ".cfg", ".md"
    };

    public void RefreshFolderFiles(string currentPath)
    {
        string? dir = Path.GetDirectoryName(currentPath);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        {
            _folderFiles = new List<string> { currentPath };
            _currentFileIndex = 0;
            NotifyFileNavigationProperties();
            return;
        }

        try
        {
            string curExt = Path.GetExtension(currentPath);
            bool IsSupported(string f)
            {
                string ext = Path.GetExtension(f);
                return SupportedPeekExtensions.Contains(ext) || string.Equals(ext, curExt, StringComparison.OrdinalIgnoreCase);
            }

            // 1. Fast filesystem enumeration first (completes in 1-2 ms without blocking UI thread!)
            string userDownloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            bool isDownloadsFolder = string.Equals(dir, userDownloads, StringComparison.OrdinalIgnoreCase) ||
                                     dir.EndsWith("\\Downloads", StringComparison.OrdinalIgnoreCase);

            var rawFiles = Directory.EnumerateFiles(dir).Where(IsSupported);

            List<string> files;
            if (isDownloadsFolder)
            {
                // Newest files at the top, matching Windows Explorer's default Downloads view
                files = rawFiles
                    .OrderByDescending(f =>
                    {
                        try { return File.GetLastWriteTimeUtc(f); } catch { return DateTime.MinValue; }
                    })
                    .ToList();
            }
            else
            {
                // Natural alphabetical order for regular folders
                files = rawFiles
                    .OrderBy(f => Path.GetFileName(f), NaturalStringComparer.Instance)
                    .ToList();
            }

            // Ensure current file is present in the list
            if (!files.Contains(currentPath, StringComparer.OrdinalIgnoreCase))
            {
                files.Insert(0, currentPath);
            }

            _folderFiles = files;
            _currentFileIndex = _folderFiles.FindIndex(f => string.Equals(f, currentPath, StringComparison.OrdinalIgnoreCase));
            if (_currentFileIndex < 0) _currentFileIndex = 0;

            // 2. Query active Explorer view order in the background so COM never freezes or delays the UI thread!
            _ = Task.Run(() =>
            {
                try
                {
                    var fromExplorer = ExplorerSelectionHelper.GetFolderFilesFromExplorer(dir);
                    if (fromExplorer != null && fromExplorer.Count > 0)
                    {
                        var ordered = fromExplorer.Where(IsSupported).ToList();
                        if (ordered.Count > 0 && !ordered.SequenceEqual(_folderFiles, StringComparer.OrdinalIgnoreCase))
                        {
                            if (!ordered.Contains(currentPath, StringComparer.OrdinalIgnoreCase))
                            {
                                ordered.Insert(0, currentPath);
                            }
                            System.Windows.Application.Current?.Dispatcher.InvokeAsync(() =>
                            {
                                if (string.Equals(_filePath, currentPath, StringComparison.OrdinalIgnoreCase))
                                {
                                    _folderFiles = ordered;
                                    _currentFileIndex = _folderFiles.FindIndex(f => string.Equals(f, currentPath, StringComparison.OrdinalIgnoreCase));
                                    if (_currentFileIndex < 0) _currentFileIndex = 0;
                                    NotifyFileNavigationProperties();
                                }
                            });
                        }
                    }
                }
                catch { }
            });
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Failed to enumerate folder files for {Path}", currentPath);
            _folderFiles = new List<string> { currentPath };
            _currentFileIndex = 0;
        }

        NotifyFileNavigationProperties();
    }

    private void NotifyFileNavigationProperties()
    {
        OnPropertyChanged(nameof(HasMultipleFiles));
        OnPropertyChanged(nameof(CanGoPrevFile));
        OnPropertyChanged(nameof(CanGoNextFile));
        OnPropertyChanged(nameof(FileNavigationDisplay));
        OnPropertyChanged(nameof(PrevFileName));
        OnPropertyChanged(nameof(NextFileName));
        OnPropertyChanged(nameof(PrevFileToolTip));
        OnPropertyChanged(nameof(NextFileToolTip));
    }

    public void NavigateToPrevFile()
    {
        if (!CanGoPrevFile) return;
        _ = NavigateToFileIndexAsync(_currentFileIndex - 1);
    }

    public void NavigateToNextFile()
    {
        if (!CanGoNextFile) return;
        _ = NavigateToFileIndexAsync(_currentFileIndex + 1);
    }

    public async Task NavigateToFileIndexAsync(int targetIndex)
    {
        if (targetIndex < 0 || targetIndex >= _folderFiles.Count) return;
        string targetPath = _folderFiles[targetIndex];
        if (!File.Exists(targetPath))
        {
            RefreshFolderFiles(_filePath);
            return;
        }
        await NavigateToFileAsync(targetPath);
    }

    private CancellationTokenSource? _loadCts;

    public async Task NavigateToFileAsync(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return;

        // Cancel previous load in flight
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = new CancellationTokenSource();
        var token = _loadCts.Token;

        _filePath = filePath;

        // Update folder file index
        int idx = _folderFiles.FindIndex(f => string.Equals(f, filePath, StringComparison.OrdinalIgnoreCase));
        if (idx >= 0)
        {
            _currentFileIndex = idx;
        }
        else
        {
            RefreshFolderFiles(filePath);
        }

        // Reset zoom & rotation
        ZoomLevel = 1.0;
        PageRotation = 0;
        IsPasswordProtected = false;
        PdfPassword = string.Empty;
        PasswordErrorMessage = string.Empty;

        string ext = Path.GetExtension(filePath).ToLowerInvariant();
        IsPdf = ext == ".pdf";
        IsImage = ImageExts.Contains(ext);
        IsText = ext == ".txt" || ext == ".log" || ext == ".json" || ext == ".csv" || ext == ".xml" || ext == ".ini" || ext == ".cfg" || ext == ".md";

        // FAST PATH: Check memory preview cache for instant display (0ms!)
        if (_previewCache.TryGetValue(filePath, out var cached))
        {
            if (IsPdf)
            {
                PdfPages.Clear();
                PdfPages.Add(cached.FirstPage);
                TotalPagesCount = cached.TotalPages;
                _currentPageIndex = 0;
                ImageSource = null;
                TextContent = string.Empty;
                IsLoading = false; // NO PROGRESS BAR! INSTANT!

                if (cached.TotalPages > 1)
                {
                    _ = LoadRemainingPdfPagesAsync(filePath, cached.TotalPages, token);
                }
            }
            else if (IsImage)
            {
                PdfPages.Clear();
                ImageSource = cached.FirstPage;
                TextContent = string.Empty;
                IsLoading = false;
            }

            OnPropertyChanged(nameof(FilePath));
            OnPropertyChanged(nameof(FileName));
            OnPropertyChanged(nameof(ExtensionUpper));
            OnPropertyChanged(nameof(FileSizeFormatted));
            OnPropertyChanged(nameof(CurrentPageIndex));
            OnPropertyChanged(nameof(SelectedPdfPage));
            OnPropertyChanged(nameof(CurrentPageDisplay));
            OnPropertyChanged(nameof(CanGoPrevPage));
            OnPropertyChanged(nameof(CanGoNextPage));
            NotifyFileNavigationProperties();

            PrefetchAdjacentFiles();
            _ = Task.Run(() => ExplorerSelectionHelper.SelectFileInExplorer(filePath));
            return;
        }

        // Cache miss: clear previous view and load Page 1 with priority
        PdfPages.Clear();
        ImageSource = null;
        TextContent = string.Empty;
        _currentPageIndex = 0;

        OnPropertyChanged(nameof(FilePath));
        OnPropertyChanged(nameof(FileName));
        OnPropertyChanged(nameof(ExtensionUpper));
        OnPropertyChanged(nameof(FileSizeFormatted));
        OnPropertyChanged(nameof(CurrentPageIndex));
        OnPropertyChanged(nameof(SelectedPdfPage));
        OnPropertyChanged(nameof(CurrentPageDisplay));
        OnPropertyChanged(nameof(CanGoPrevPage));
        OnPropertyChanged(nameof(CanGoNextPage));
        NotifyFileNavigationProperties();

        await LoadPreviewAsync(token);
        _ = Task.Run(() => ExplorerSelectionHelper.SelectFileInExplorer(filePath));
    }

    #endregion

    #region In-Memory Instant Preview Cache & Prefetching

    private static readonly ConcurrentDictionary<string, CachedPreview> _previewCache = new(StringComparer.OrdinalIgnoreCase);

    private class CachedPreview
    {
        public BitmapSource FirstPage { get; set; } = null!;
        public int TotalPages { get; set; }
        public DateTime CachedTime { get; set; } = DateTime.UtcNow;
    }

    private static void AddToCache(string path, BitmapSource bmp, int totalPages)
    {
        // Limit cache size to 25 documents to keep memory lean
        if (_previewCache.Count > 25)
        {
            var oldest = _previewCache.OrderBy(kv => kv.Value.CachedTime).Take(10).ToList();
            foreach (var item in oldest)
            {
                _previewCache.TryRemove(item.Key, out _);
            }
        }

        _previewCache[path] = new CachedPreview
        {
            FirstPage = bmp,
            TotalPages = totalPages,
            CachedTime = DateTime.UtcNow
        };
    }

    private void PrefetchAdjacentFiles()
    {
        if (_folderFiles.Count <= 1) return;

        var filesToPrefetch = new List<string>();

        // Prefetch next 2 files and previous 1 file
        if (_currentFileIndex + 1 < _folderFiles.Count)
            filesToPrefetch.Add(_folderFiles[_currentFileIndex + 1]);
        if (_currentFileIndex + 2 < _folderFiles.Count)
            filesToPrefetch.Add(_folderFiles[_currentFileIndex + 2]);
        if (_currentFileIndex - 1 >= 0)
            filesToPrefetch.Add(_folderFiles[_currentFileIndex - 1]);

        Task.Run(async () =>
        {
            foreach (var file in filesToPrefetch)
            {
                if (_previewCache.ContainsKey(file) || !File.Exists(file)) continue;

                string ext = Path.GetExtension(file).ToLowerInvariant();
                if (ext == ".pdf")
                {
                    try
                    {
                        var res = await PdfRendererService.RenderPdfFirstPageAsync(file, password: null, targetWidth: 1200);
                        if (res.Success && res.FirstPage != null)
                        {
                            AddToCache(file, res.FirstPage, res.TotalPages);
                        }
                    }
                    catch { }
                }
                else if (ImageExts.Contains(ext))
                {
                    try
                    {
                        byte[] bytes = await File.ReadAllBytesAsync(file);
                        using var ms = new MemoryStream(bytes);
                        var bitmap = BitmapFrame.Create(ms, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                        bitmap.Freeze();
                        AddToCache(file, bitmap, 1);
                    }
                    catch { }
                }
            }
        });
    }

    #endregion

    private bool _isLoading = true;
    public bool IsLoading { get => _isLoading; set => SetProperty(ref _isLoading, value); }

    private double _zoomLevel = 1.0;
    public double ZoomLevel
    {
        get => _zoomLevel;
        set
        {
            double val = Math.Clamp(value, 0.25, 4.0);
            if (SetProperty(ref _zoomLevel, val))
            {
                OnPropertyChanged(nameof(ZoomPercentageDisplay));
            }
        }
    }

    public string ZoomPercentageDisplay => $"{Math.Round(_zoomLevel * 100)}%";

    private bool _isPasswordProtected;
    public bool IsPasswordProtected { get => _isPasswordProtected; set => SetProperty(ref _isPasswordProtected, value); }

    private string _pdfPassword = string.Empty;
    public string PdfPassword { get => _pdfPassword; set => SetProperty(ref _pdfPassword, value); }

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

    public bool HasPasswordError => !string.IsNullOrEmpty(_passwordErrorMessage);

    public ICommand PrevPageCommand { get; }
    public ICommand NextPageCommand { get; }
    public ICommand ZoomInCommand { get; }
    public ICommand ZoomOutCommand { get; }
    public ICommand ResetZoomCommand { get; }
    public ICommand RotateCwCommand { get; }
    public ICommand RotateCcwCommand { get; }
    public ICommand UnlockPdfCommand { get; }
    public ICommand CompressCommand { get; }
    public ICommand StackCommand { get; }
    public ICommand GovtCardPrintCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public ICommand CloseCommand { get; }

    private int _pageRotation;
    public int PageRotation
    {
        get => _pageRotation;
        set => SetProperty(ref _pageRotation, (value % 360 + 360) % 360);
    }

    private readonly int _initialPageIndex;

    public Action? RequestClose { get; set; }
    public Action<string>? RequestCompress { get; set; }
    public Action<string>? RequestStack { get; set; }
    public Action<string>? RequestGovtCardPrint { get; set; }

    private static readonly HashSet<string> ImageExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".bmp", ".webp", ".tiff", ".tif", ".ico", ".gif"
    };

    public QuickPeekViewModel(string filePath, int initialPageIndex = 0, int initialRotation = 0)
    {
        _filePath = filePath;
        _initialPageIndex = Math.Max(0, initialPageIndex);
        _pageRotation = (initialRotation % 360 + 360) % 360;

        string ext = Path.GetExtension(filePath).ToLowerInvariant();

        IsPdf = ext == ".pdf";
        IsImage = ImageExts.Contains(ext);
        IsText = ext == ".txt" || ext == ".log" || ext == ".json" || ext == ".csv" || ext == ".xml" || ext == ".ini" || ext == ".cfg" || ext == ".md";

        PrevPageCommand = new RelayCommand(_ => CurrentPageIndex--, _ => CanGoPrevPage);
        NextPageCommand = new RelayCommand(_ => CurrentPageIndex++, _ => CanGoNextPage);
        PrevFileCommand = new RelayCommand(_ => NavigateToPrevFile(), _ => CanGoPrevFile);
        NextFileCommand = new RelayCommand(_ => NavigateToNextFile(), _ => CanGoNextFile);
        ZoomInCommand = new RelayCommand(_ => ZoomLevel += 0.15);
        ZoomOutCommand = new RelayCommand(_ => ZoomLevel -= 0.15);
        ResetZoomCommand = new RelayCommand(_ => ZoomLevel = 1.0);
        RotateCwCommand = new RelayCommand(_ => PageRotation = (PageRotation + 90) % 360);
        RotateCcwCommand = new RelayCommand(_ => PageRotation = (PageRotation + 270) % 360);
        UnlockPdfCommand = new RelayCommand(_ => UnlockPdfWithPassword());
        CompressCommand = new RelayCommand(_ => { RequestClose?.Invoke(); RequestCompress?.Invoke(_filePath); });
        StackCommand = new RelayCommand(_ => { RequestClose?.Invoke(); RequestStack?.Invoke(_filePath); });
        GovtCardPrintCommand = new RelayCommand(_ => { RequestClose?.Invoke(); RequestGovtCardPrint?.Invoke(_filePath); });
        OpenFolderCommand = new RelayCommand(_ => OpenInExplorer());
        CloseCommand = new RelayCommand(_ => RequestClose?.Invoke());

        RefreshFolderFiles(_filePath);
        _ = LoadPreviewAsync();
    }

    private async Task LoadPreviewAsync(CancellationToken token = default)
    {
        try
        {
            if (IsPdf)
            {
                // FAST PATH: Instant display from cache (0ms)
                if (_previewCache.TryGetValue(_filePath, out var cached))
                {
                    PdfPages.Clear();
                    PdfPages.Add(cached.FirstPage);
                    TotalPagesCount = cached.TotalPages;
                    _currentPageIndex = 0;
                    IsPasswordProtected = false;
                    IsLoading = false; // Zero delay!

                    OnPropertyChanged(nameof(CurrentPageIndex));
                    OnPropertyChanged(nameof(SelectedPdfPage));
                    OnPropertyChanged(nameof(CurrentPageDisplay));
                    OnPropertyChanged(nameof(CanGoPrevPage));
                    OnPropertyChanged(nameof(CanGoNextPage));

                    if (cached.TotalPages > 1)
                    {
                        _ = LoadRemainingPdfPagesAsync(_filePath, cached.TotalPages, token);
                    }

                    PrefetchAdjacentFiles();
                    return;
                }

                // Cache Miss: Render Page 1 first with high-speed memory mapping
                IsLoading = true;
                var res = await PdfRendererService.RenderPdfFirstPageAsync(_filePath, password: null, targetWidth: 1200);
                if (token.IsCancellationRequested) return;

                if (res.IsPasswordProtected)
                {
                    IsPasswordProtected = true;
                    IsLoading = false;
                    return;
                }

                if (res.Success && res.FirstPage != null)
                {
                    PdfPages.Clear();
                    PdfPages.Add(res.FirstPage);
                    TotalPagesCount = res.TotalPages;
                    _currentPageIndex = 0;
                    IsPasswordProtected = false;
                    IsLoading = false; // Instant display!

                    AddToCache(_filePath, res.FirstPage, res.TotalPages);

                    OnPropertyChanged(nameof(CurrentPageIndex));
                    OnPropertyChanged(nameof(SelectedPdfPage));
                    OnPropertyChanged(nameof(CurrentPageDisplay));
                    OnPropertyChanged(nameof(CanGoPrevPage));
                    OnPropertyChanged(nameof(CanGoNextPage));
                    OnPropertyChanged(nameof(PageRotation));

                    // Load remaining pages lazily in background
                    if (res.TotalPages > 1)
                    {
                        _ = LoadRemainingPdfPagesAsync(_filePath, res.TotalPages, token);
                    }

                    PrefetchAdjacentFiles();
                }
                else
                {
                    IsLoading = false;
                }
            }
            else if (IsImage)
            {
                if (_previewCache.TryGetValue(_filePath, out var cached))
                {
                    ImageSource = cached.FirstPage;
                    IsLoading = false;
                    PrefetchAdjacentFiles();
                    return;
                }

                IsLoading = true;
                BitmapSource? loadedBitmap = null;
                await Task.Run(() =>
                {
                    if (token.IsCancellationRequested) return;
                    byte[] bytes = File.ReadAllBytes(_filePath);
                    using var ms = new MemoryStream(bytes);
                    var bitmap = BitmapFrame.Create(ms, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                    bitmap.Freeze();
                    loadedBitmap = bitmap;
                }, token);

                if (token.IsCancellationRequested) return;
                ImageSource = loadedBitmap;
                IsLoading = false;

                if (loadedBitmap != null)
                {
                    AddToCache(_filePath, loadedBitmap, 1);
                }

                PrefetchAdjacentFiles();
            }
            else if (IsText)
            {
                IsLoading = true;
                string text = await File.ReadAllTextAsync(_filePath, token);
                if (token.IsCancellationRequested) return;
                TextContent = text;
                IsLoading = false;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to load peek preview for: {FilePath}", _filePath);
            IsLoading = false;
        }
    }

    private async Task LoadRemainingPdfPagesAsync(string filePath, int totalPages, CancellationToken token)
    {
        if (totalPages <= 1 || token.IsCancellationRequested) return;

        try
        {
            await Task.Run(() =>
            {
                if (token.IsCancellationRequested) return;
                PdfRendererService.EnsureDllResolver();
                using var document = PdfiumViewer.PdfDocument.Load(filePath);
                int limit = Math.Min(document.PageCount, 50);

                for (int i = 1; i < limit; i++)
                {
                    if (token.IsCancellationRequested) return;
                    var pageSize = document.PageSizes[i];
                    double widthPt = pageSize.Width > 0 ? pageSize.Width : 595.28;
                    double heightPt = pageSize.Height > 0 ? pageSize.Height : 841.89;

                    int targetWidth = 1200;
                    float renderDpi = (float)((targetWidth / widthPt) * 72.0);
                    int targetHeight = (int)Math.Round(heightPt * (renderDpi / 72.0));
                    if (targetHeight <= 0) targetHeight = (int)(targetWidth * 1.414);

                    using var image = (System.Drawing.Bitmap)document.Render(i, targetWidth, targetHeight, renderDpi, renderDpi, PdfRenderFlags.Annotations);
                    var bmp = PdfRendererService.ToBitmapSource(image);

                    if (token.IsCancellationRequested) return;

                    System.Windows.Application.Current?.Dispatcher.InvokeAsync(() =>
                    {
                        if (token.IsCancellationRequested || !string.Equals(_filePath, filePath, StringComparison.OrdinalIgnoreCase))
                            return;

                        PdfPages.Add(bmp);
                        OnPropertyChanged(nameof(CanGoNextPage));
                        OnPropertyChanged(nameof(CurrentPageDisplay));
                        OnPropertyChanged(nameof(SelectedPdfPage));
                    });
                }
            }, token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Debug(ex, "Background remaining pages load cancelled or failed for {Path}", filePath);
        }
    }

    private async void UnlockPdfWithPassword()
    {
        if (string.IsNullOrWhiteSpace(PdfPassword))
        {
            PasswordErrorMessage = "Please enter password";
            return;
        }

        IsLoading = true;
        PasswordErrorMessage = string.Empty;
        try
        {
            var res = await PdfRendererService.RenderPdfPagesResultAsync(_filePath, password: PdfPassword.Trim(), maxPages: 20);
            if (res.Success && res.Pages.Count > 0)
            {
                PdfPages.Clear();
                foreach (var page in res.Pages) PdfPages.Add(page);
                _currentPageIndex = 0;
                IsPasswordProtected = false;

                OnPropertyChanged(nameof(CurrentPageIndex));
                OnPropertyChanged(nameof(SelectedPdfPage));
                OnPropertyChanged(nameof(CurrentPageDisplay));
                OnPropertyChanged(nameof(CanGoPrevPage));
                OnPropertyChanged(nameof(CanGoNextPage));
            }
            else
            {
                PasswordErrorMessage = "❌ Incorrect password. Please try again.";
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Unlock PDF failed for {FilePath}", _filePath);
            PasswordErrorMessage = "❌ Incorrect password or corrupt file.";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void OpenInExplorer()
    {
        try
        {
            Process.Start("explorer.exe", $"/select,\"{_filePath}\"");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to open folder in explorer for {FilePath}", _filePath);
        }
    }
}
