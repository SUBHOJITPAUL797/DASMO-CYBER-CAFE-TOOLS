using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using SmartSaver.ViewModels;

namespace SmartSaver.Models;

/// <summary>
/// Represents a text block extracted directly from the PDF's content stream by PdfPig.
/// Used by the "✏️ Edit Text" tool to show clickable overlays over real PDF text.
/// </summary>
public class PdfExtractedTextBlock : ViewModelBase
{
    /// <summary>Original text content from the PDF.</summary>
    public string OriginalText { get; set; } = string.Empty;

    /// <summary>X position in PDF points (bottom-left origin, needs Y-flip for WPF).</summary>
    public double PdfX { get; set; }

    /// <summary>Y position in PDF points (bottom-left origin, needs Y-flip for WPF).</summary>
    public double PdfY { get; set; }

    /// <summary>Width in PDF points.</summary>
    public double PdfWidth { get; set; }

    /// <summary>Height in PDF points.</summary>
    public double PdfHeight { get; set; }

    /// <summary>Font size in PDF points — used to match replacement text size.</summary>
    public double FontSizePt { get; set; } = 10.0;

    /// <summary>Best-matching Windows font family name.</summary>
    public string FontFamily { get; set; } = "Arial";

    /// <summary>Whether the extracted font is bold.</summary>
    public bool IsBold { get; set; }

    /// <summary>Whether the extracted font is italic.</summary>
    public bool IsItalic { get; set; }

    /// <summary>Text color in hex (e.g. "#000000"). Defaults to black.</summary>
    public string ColorHex { get; set; } = "#000000";

    // ── WPF canvas coordinates (set after Y-flip) ─────────────────────────────
    /// <summary>Canvas X position (WPF top-left origin).</summary>
    public double CanvasX { get; set; }

    /// <summary>Canvas Y position (WPF top-left origin).</summary>
    public double CanvasY { get; set; }

    /// <summary>Top-down baseline Y position in points (pageHeightPt - StartBaseLine.Y).</summary>
    public double BaseLineY { get; set; }

    // ── Observable editing state ───────────────────────────────────────────────
    private bool _isHighlighted;
    /// <summary>True when the user hovers over this block in Edit Text mode.</summary>
    public bool IsHighlighted
    {
        get => _isHighlighted;
        set => SetProperty(ref _isHighlighted, value);
    }

    private bool _isBeingEdited;
    /// <summary>True when the user has clicked this block and the inline editor is open.</summary>
    public bool IsBeingEdited
    {
        get => _isBeingEdited;
        set => SetProperty(ref _isBeingEdited, value);
    }

    private string _editedText = string.Empty;
    /// <summary>The replacement text the user is currently typing.</summary>
    public string EditedText
    {
        get => _editedText;
        set => SetProperty(ref _editedText, value);
    }

    /// <summary>Creates an exact independent clone of this extracted text block for Undo/Redo state snapshots.</summary>
    public PdfExtractedTextBlock Clone()
    {
        return new PdfExtractedTextBlock
        {
            OriginalText   = this.OriginalText,
            PdfX           = this.PdfX,
            PdfY           = this.PdfY,
            PdfWidth       = this.PdfWidth,
            PdfHeight      = this.PdfHeight,
            FontSizePt     = this.FontSizePt,
            FontFamily     = this.FontFamily,
            IsBold         = this.IsBold,
            IsItalic       = this.IsItalic,
            ColorHex       = this.ColorHex,
            CanvasX        = this.CanvasX,
            CanvasY        = this.CanvasY,
            BaseLineY      = this.BaseLineY,
            IsHighlighted  = false,
            IsBeingEdited  = false,
            EditedText     = this.EditedText
        };
    }
}

public enum PdfEditItemType
{
    Whiteout,
    Text,
    Image,
    Ink
}

public abstract class PdfEditItem : ViewModelBase
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public int PageIndex { get; set; }

    private double _x;
    public double X
    {
        get => _x;
        set => SetProperty(ref _x, Math.Round(value, 2));
    }

    private double _y;
    public double Y
    {
        get => _y;
        set => SetProperty(ref _y, Math.Round(value, 2));
    }

    private double _width = 100;
    public double Width
    {
        get => _width;
        set => SetProperty(ref _width, Math.Max(5, Math.Round(value, 2)));
    }

    private double _height = 30;
    public double Height
    {
        get => _height;
        set => SetProperty(ref _height, Math.Max(5, Math.Round(value, 2)));
    }

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    private bool _isHitTestVisible = true;
    public bool IsHitTestVisible
    {
        get => _isHitTestVisible;
        set => SetProperty(ref _isHitTestVisible, value);
    }

    public abstract PdfEditItemType ItemType { get; }
}

public class PdfWhiteoutItem : PdfEditItem
{
    public override PdfEditItemType ItemType => PdfEditItemType.Whiteout;

    private string _fillColorHex = "#FFFFFF";
    public string FillColorHex
    {
        get => _fillColorHex;
        set => SetProperty(ref _fillColorHex, value);
    }
}

public class PdfTextItem : PdfEditItem
{
    public override PdfEditItemType ItemType => PdfEditItemType.Text;

    private static readonly ConcurrentDictionary<(string font, bool isBold), System.Windows.Media.Typeface> _typefaceCache = new();

    public static System.Windows.Media.Typeface GetCachedTypeface(string fontFamily, bool isBold)
    {
        string fontKey = string.IsNullOrWhiteSpace(fontFamily) ? "Arial" : fontFamily.Trim();
        return _typefaceCache.GetOrAdd((fontKey, isBold), key =>
            new System.Windows.Media.Typeface(
                new System.Windows.Media.FontFamily(key.font),
                System.Windows.FontStyles.Normal,
                key.isBold ? System.Windows.FontWeights.Bold : System.Windows.FontWeights.Normal,
                System.Windows.FontStretches.Normal));
    }

    public static double EstimateTextWidth(string text, string fontFamily, double fontSizePt, bool isBold)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        try
        {
            var typeface = GetCachedTypeface(fontFamily, isBold);

            var ft = new System.Windows.Media.FormattedText(
                text,
                System.Globalization.CultureInfo.CurrentCulture,
                System.Windows.FlowDirection.LeftToRight,
                typeface,
                fontSizePt,
                System.Windows.Media.Brushes.Black,
                1.0);

            return ft.WidthIncludingTrailingWhitespace;
        }
        catch
        {
            double charWidthFactor = isBold ? 0.75 : 0.65;
            return text.Length * fontSizePt * charWidthFactor;
        }
    }

    private string _text = "New Text";
    public string Text
    {
        get => _text;
        set
        {
            if (SetProperty(ref _text, value))
            {
                UpdateAutoWidth();
            }
        }
    }

    private string _fontFamily = "Arial";
    public string FontFamily
    {
        get => _fontFamily;
        set
        {
            if (SetProperty(ref _fontFamily, value))
            {
                UpdateAutoWidth();
            }
        }
    }

    private double _fontSizePt = 12.0;
    public double FontSizePt
    {
        get => _fontSizePt;
        set
        {
            if (SetProperty(ref _fontSizePt, value))
            {
                UpdateAutoWidth();
            }
        }
    }

    private bool _isBold;
    public bool IsBold
    {
        get => _isBold;
        set
        {
            if (SetProperty(ref _isBold, value))
            {
                OnPropertyChanged(nameof(FontWeightValue));
                UpdateAutoWidth();
            }
        }
    }

    /// <summary>Direct WPF FontWeight for zero-latency, deterministic bold rendering in TextBox.</summary>
    public System.Windows.FontWeight FontWeightValue => _isBold ? System.Windows.FontWeights.Bold : System.Windows.FontWeights.Normal;

    private bool _isItalic;
    public bool IsItalic
    {
        get => _isItalic;
        set
        {
            if (SetProperty(ref _isItalic, value))
            {
                OnPropertyChanged(nameof(FontStyleValue));
            }
        }
    }

    /// <summary>Direct WPF FontStyle for zero-latency, deterministic italic rendering in TextBox.</summary>
    public System.Windows.FontStyle FontStyleValue => _isItalic ? System.Windows.FontStyles.Italic : System.Windows.FontStyles.Normal;

    /// <summary>
    /// Recalculates snug width to perfectly enclose the typed text.
    /// Dynamically expands as text is added, and shrinks as text is backspaced or shortened.
    /// </summary>
    public void UpdateAutoWidth()
    {
        if (!string.IsNullOrEmpty(_text))
        {
            double estimatedWidth = EstimateTextWidth(_text, _fontFamily, _fontSizePt, _isBold);
            Width = Math.Max(Math.Round(estimatedWidth + 6.0, 2), 15.0);
        }
    }

    private string _textColorHex = "#000000";
    public string TextColorHex
    {
        get => _textColorHex;
        set => SetProperty(ref _textColorHex, value);
    }

    private bool _hasOpaqueBackground = true;
    public bool HasOpaqueBackground
    {
        get => _hasOpaqueBackground;
        set => SetProperty(ref _hasOpaqueBackground, value);
    }

    private string _backgroundColorHex = "#FFFFFF";
    public string BackgroundColorHex
    {
        get => _backgroundColorHex;
        set => SetProperty(ref _backgroundColorHex, value);
    }

    /// <summary>
    /// If this text item was created by 'Edit Text' to replace an existing word,
    /// this links to the underlying whiteout item so they can be deleted/undone together.
    /// </summary>
    public string? LinkedWhiteoutId { get; set; }

    /// <summary>
    /// If this text item replaces an existing extracted text block,
    /// stores the original text to be redacted from the PDF content stream upon save.
    /// </summary>
    public string? OriginalTextToRedact { get; set; }

    private double _originalBlockWidth;
    /// <summary>Original PDF width of the extracted text block in points.</summary>
    public double OriginalBlockWidth
    {
        get => _originalBlockWidth;
        set => SetProperty(ref _originalBlockWidth, Math.Round(value, 2));
    }

    private double _originalBlockHeight;
    /// <summary>Original PDF height of the extracted text block in points.</summary>
    public double OriginalBlockHeight
    {
        get => _originalBlockHeight;
        set => SetProperty(ref _originalBlockHeight, Math.Round(value, 2));
    }

    private double _baseLineY;
    /// <summary>
    /// Top-down baseline Y in points. When > 0, specifies the exact typographical baseline
    /// so the replacement text sits on the identical baseline down to the sub-pixel.
    /// </summary>
    public double BaseLineY
    {
        get => _baseLineY;
        set => SetProperty(ref _baseLineY, Math.Round(value, 2));
    }
}

public class PdfImageItem : PdfEditItem
{
    public override PdfEditItemType ItemType => PdfEditItemType.Image;

    private System.Windows.Media.ImageSource? _cachedDisplaySource;

    private string? _sourceFilePath;
    public string? SourceFilePath
    {
        get => _sourceFilePath;
        set
        {
            if (SetProperty(ref _sourceFilePath, value))
            {
                _cachedDisplaySource = null;
                OnPropertyChanged(nameof(DisplaySource));
            }
        }
    }

    private byte[]? _imageBytes;
    public byte[]? ImageBytes
    {
        get => _imageBytes;
        set
        {
            if (SetProperty(ref _imageBytes, value))
            {
                _cachedDisplaySource = null;
                OnPropertyChanged(nameof(DisplaySource));
            }
        }
    }

    public System.Windows.Media.ImageSource? DisplaySource
    {
        get
        {
            if (_cachedDisplaySource != null) return _cachedDisplaySource;

            try
            {
                if (ImageBytes != null && ImageBytes.Length > 0)
                {
                    using var ms = new System.IO.MemoryStream(ImageBytes);
                    var bmp = new System.Windows.Media.Imaging.BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    bmp.StreamSource = ms;
                    bmp.EndInit();
                    bmp.Freeze();
                    _cachedDisplaySource = bmp;
                    return _cachedDisplaySource;
                }
                if (!string.IsNullOrEmpty(SourceFilePath) && System.IO.File.Exists(SourceFilePath))
                {
                    var bmp = new System.Windows.Media.Imaging.BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    bmp.UriSource = new Uri(SourceFilePath);
                    bmp.EndInit();
                    bmp.Freeze();
                    _cachedDisplaySource = bmp;
                    return _cachedDisplaySource;
                }
            }
            catch { }
            return null;
        }
    }

    private bool _maintainAspectRatio = true;
    public bool MaintainAspectRatio
    {
        get => _maintainAspectRatio;
        set => SetProperty(ref _maintainAspectRatio, value);
    }
}

public class PdfInkItem : PdfEditItem
{
    public override PdfEditItemType ItemType => PdfEditItemType.Ink;
    public List<(double X, double Y)> Points { get; set; } = new();

    public System.Windows.Media.PointCollection WpfPoints
    {
        get
        {
            var pc = new System.Windows.Media.PointCollection();
            foreach (var pt in Points)
            {
                pc.Add(new System.Windows.Point(pt.X, pt.Y));
            }
            return pc;
        }
    }

    public void AddPoint(double x, double y)
    {
        Points.Add((x, y));
        OnPropertyChanged(nameof(WpfPoints));
    }

    public void NotifyPointsChanged()
    {
        OnPropertyChanged(nameof(WpfPoints));
    }

    private string _strokeColorHex = "#000000";
    public string StrokeColorHex
    {
        get => _strokeColorHex;
        set => SetProperty(ref _strokeColorHex, value);
    }

    private double _strokeThickness = 2.0;
    public double StrokeThickness
    {
        get => _strokeThickness;
        set => SetProperty(ref _strokeThickness, value);
    }
}
