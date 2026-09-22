using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Serilog;
using SmartSaver.Models;
using SmartSaver.Services;

namespace SmartSaver.ViewModels;

public class QuickPeekViewModel : ViewModelBase
{
    private readonly string _filePath;

    public string FilePath => _filePath;
    public string FileName => Path.GetFileName(_filePath);
    public string ExtensionUpper => Path.GetExtension(_filePath).TrimStart('.').ToUpperInvariant();
    public string FileSizeFormatted => CompressionResult.FormatFileSize(new FileInfo(_filePath).Length);

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

    public string CurrentPageDisplay => PdfPages.Count > 0 ? $"Page {CurrentPageIndex + 1} of {PdfPages.Count}" : string.Empty;

    public bool CanGoPrevPage => CurrentPageIndex > 0;
    public bool CanGoNextPage => CurrentPageIndex < PdfPages.Count - 1;

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
        ".jpg", ".jpeg", ".png", ".bmp", ".webp", ".tiff", ".tif"
    };

    public QuickPeekViewModel(string filePath, int initialPageIndex = 0, int initialRotation = 0)
    {
        _filePath = filePath;
        _initialPageIndex = Math.Max(0, initialPageIndex);
        _pageRotation = (initialRotation % 360 + 360) % 360;

        string ext = Path.GetExtension(filePath).ToLowerInvariant();

        IsPdf = ext == ".pdf";
        IsImage = ImageExts.Contains(ext);
        IsText = ext == ".txt" || ext == ".log" || ext == ".json" || ext == ".csv" || ext == ".xml";

        PrevPageCommand = new RelayCommand(_ => CurrentPageIndex--, _ => CanGoPrevPage);
        NextPageCommand = new RelayCommand(_ => CurrentPageIndex++, _ => CanGoNextPage);
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

        _ = LoadPreviewAsync();
    }

    private async Task LoadPreviewAsync()
    {
        IsLoading = true;
        try
        {
            if (IsPdf)
            {
                int loadLimit = Math.Max(50, _initialPageIndex + 25);
                var res = await PdfRendererService.RenderPdfPagesResultAsync(_filePath, password: null, maxPages: loadLimit);
                if (res.IsPasswordProtected)
                {
                    IsPasswordProtected = true;
                }
                else if (res.Success && res.Pages.Count > 0)
                {
                    PdfPages.Clear();
                    foreach (var page in res.Pages) PdfPages.Add(page);
                    int targetIdx = Math.Clamp(_initialPageIndex, 0, Math.Max(0, PdfPages.Count - 1));
                    _currentPageIndex = targetIdx;
                    IsPasswordProtected = false;
                    // Explicitly notify UI of all dependent properties
                    OnPropertyChanged(nameof(CurrentPageIndex));
                    OnPropertyChanged(nameof(SelectedPdfPage));
                    OnPropertyChanged(nameof(CurrentPageDisplay));
                    OnPropertyChanged(nameof(CanGoPrevPage));
                    OnPropertyChanged(nameof(CanGoNextPage));
                    OnPropertyChanged(nameof(PageRotation));
                }
            }
            else if (IsImage)
            {
                BitmapSource? loadedBitmap = null;
                await Task.Run(() =>
                {
                    byte[] bytes = File.ReadAllBytes(_filePath);
                    using var ms = new MemoryStream(bytes);
                    var bitmap = BitmapFrame.Create(ms, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                    bitmap.Freeze();
                    loadedBitmap = bitmap;
                });
                ImageSource = loadedBitmap;
            }
            else if (IsText)
            {
                TextContent = await File.ReadAllTextAsync(_filePath);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to load peek preview for: {FilePath}", _filePath);
        }
        finally
        {
            IsLoading = false;
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
                _currentPageIndex = 0; // Set backing field directly to avoid SetProperty short-circuit
                IsPasswordProtected = false;
                // Explicitly notify UI of all dependent properties
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
