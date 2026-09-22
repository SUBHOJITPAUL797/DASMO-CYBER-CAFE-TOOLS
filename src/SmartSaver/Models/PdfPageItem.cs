using System;
using System.IO;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using SmartSaver.ViewModels;

namespace SmartSaver.Models;

public class PdfPageExtractionItem
{
    public string SourcePdfPath { get; set; } = string.Empty;
    public int PageIndex { get; set; } = 1; // 1-based original page index
    public int Rotation { get; set; } = 0;   // 0, 90, 180, 270 degrees
}

public class PdfPageItem : ViewModelBase
{
    private string _sourcePdfPath = string.Empty;
    public string SourcePdfPath
    {
        get => _sourcePdfPath;
        set
        {
            if (SetProperty(ref _sourcePdfPath, value))
            {
                OnPropertyChanged(nameof(SourceFileName));
                OnPropertyChanged(nameof(SourceInfo));
            }
        }
    }

    public string SourceFileName => string.IsNullOrEmpty(SourcePdfPath) ? "" : Path.GetFileName(SourcePdfPath);

    private int _originalPageIndex = 1;
    public int OriginalPageIndex
    {
        get => _originalPageIndex;
        set
        {
            if (SetProperty(ref _originalPageIndex, value))
            {
                OnPropertyChanged(nameof(SourceInfo));
            }
        }
    }

    private int _displayIndex = 1;
    public int DisplayIndex
    {
        get => _displayIndex;
        set
        {
            if (SetProperty(ref _displayIndex, value))
            {
                OnPropertyChanged(nameof(PageTitle));
                OnPropertyChanged(nameof(SourceInfo));
            }
        }
    }

    public string PageTitle => $"Page {DisplayIndex}";

    private bool _hasMultipleSources;
    public bool HasMultipleSources
    {
        get => _hasMultipleSources;
        set
        {
            if (SetProperty(ref _hasMultipleSources, value))
            {
                OnPropertyChanged(nameof(SourceInfo));
            }
        }
    }

    public string SourceInfo => HasMultipleSources
        ? $"{SourceFileName} (P.{OriginalPageIndex})"
        : $"Original Page {OriginalPageIndex}";

    private BitmapSource? _thumbnail;
    public BitmapSource? Thumbnail
    {
        get => _thumbnail;
        set => SetProperty(ref _thumbnail, value);
    }

    private int _rotation = 0;
    public int Rotation
    {
        get => _rotation;
        set
        {
            int normalized = (value % 360 + 360) % 360;
            if (SetProperty(ref _rotation, normalized))
            {
                OnPropertyChanged(nameof(RotationLabel));
                OnPropertyChanged(nameof(HasRotation));
            }
        }
    }

    public string RotationLabel => Rotation == 0 ? "" : $"{Rotation}°";
    public bool HasRotation => Rotation != 0;

    private bool _isSelected = true;
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value))
            {
                OnSelectionChanged?.Invoke();
            }
        }
    }

    private bool _isDraggingOver;
    public bool IsDraggingOver
    {
        get => _isDraggingOver;
        set => SetProperty(ref _isDraggingOver, value);
    }

    public ICommand MoveLeftCommand { get; }
    public ICommand MoveRightCommand { get; }
    public ICommand RotateCwCommand { get; }
    public ICommand RotateCcwCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand PreviewCommand { get; }

    public Action<PdfPageItem>? OnMoveLeftRequested { get; set; }
    public Action<PdfPageItem>? OnMoveRightRequested { get; set; }
    public Action<PdfPageItem>? OnDeleteRequested { get; set; }
    public Action<PdfPageItem>? OnPreviewRequested { get; set; }
    public Action? OnSelectionChanged { get; set; }

    public PdfPageItem()
    {
        MoveLeftCommand = new RelayCommand(_ => OnMoveLeftRequested?.Invoke(this));
        MoveRightCommand = new RelayCommand(_ => OnMoveRightRequested?.Invoke(this));
        RotateCwCommand = new RelayCommand(_ => Rotation = (Rotation + 90) % 360);
        RotateCcwCommand = new RelayCommand(_ => Rotation = (Rotation + 270) % 360);
        DeleteCommand = new RelayCommand(_ => OnDeleteRequested?.Invoke(this));
        PreviewCommand = new RelayCommand(_ => OnPreviewRequested?.Invoke(this));
    }
}
