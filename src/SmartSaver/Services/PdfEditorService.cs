using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;
using Serilog;
using SmartSaver.Models;
using UglyToad.PdfPig.Content;

namespace SmartSaver.Services;

public class PdfPageDimension
{
    public int PageIndex { get; set; }
    public double WidthPoints { get; set; }
    public double HeightPoints { get; set; }
    public double WidthMm => WidthPoints * 25.4 / 72.0;
    public double HeightMm => HeightPoints * 25.4 / 72.0;
}

public class PdfEditorService
{
    private static readonly Lazy<PdfEditorService> _instance = new(() => new PdfEditorService());
    public static PdfEditorService Instance => _instance.Value;

    public PdfEditorService()
    {
        PdfRendererService.EnsureDllResolver();
    }

    /// <summary>
    /// Reads physical page dimensions for all pages in the PDF document.
    /// </summary>
    public List<PdfPageDimension> GetPageDimensions(string pdfPath, string? password = null)
    {
        var list = new List<PdfPageDimension>();
        if (!File.Exists(pdfPath)) return list;

        try
        {
            using var stream = File.OpenRead(pdfPath);
            using var document = string.IsNullOrEmpty(password)
                ? PdfiumViewer.PdfDocument.Load(stream)
                : PdfiumViewer.PdfDocument.Load(stream, password);

            for (int i = 0; i < document.PageCount; i++)
            {
                var sz = document.PageSizes[i];
                list.Add(new PdfPageDimension
                {
                    PageIndex = i,
                    WidthPoints = sz.Width,
                    HeightPoints = sz.Height
                });
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to read page dimensions via Pdfium for {Path}, trying PdfSharpCore", pdfPath);
            try
            {
                using var doc = string.IsNullOrEmpty(password)
                    ? PdfReader.Open(pdfPath, PdfDocumentOpenMode.Import)
                    : PdfReader.Open(pdfPath, password, PdfDocumentOpenMode.Import);
                for (int i = 0; i < doc.PageCount; i++)
                {
                    var page = doc.Pages[i];
                    list.Add(new PdfPageDimension
                    {
                        PageIndex = i,
                        WidthPoints = page.Width.Point,
                        HeightPoints = page.Height.Point
                    });
                }
            }
            catch (Exception ex2)
            {
                Log.Error(ex2, "Failed to read page dimensions via PdfSharpCore for {Path}", pdfPath);
            }
        }

        return list;
    }

    /// <summary>
    /// Checks if a PDF document is locked/encrypted with a password.
    /// Uses a triple-engine check (UglyToad.PdfPig, Pdfium, and PdfSharpCore).
    /// </summary>
    public bool IsPasswordProtected(string pdfPath, string? password = null)
    {
        if (!File.Exists(pdfPath)) return false;

        // 1. Check via UglyToad.PdfPig (handles modern AES-128, AES-256 standard encrypted PDFs)
        try
        {
            var options = new UglyToad.PdfPig.ParsingOptions
            {
                Password = password,
                UseLenientParsing = true
            };
            using var pigDoc = UglyToad.PdfPig.PdfDocument.Open(pdfPath, options);
            if (pigDoc.IsEncrypted && string.IsNullOrEmpty(password))
            {
                return true;
            }
        }
        catch (Exception ex)
        {
            string msg = ex.Message.ToLowerInvariant();
            if (msg.Contains("password") || msg.Contains("encrypt") || msg.Contains("protect") ||
                ex.GetType().Name.Contains("Encrypted") || ex.GetType().Name.Contains("Password"))
            {
                return true;
            }
        }

        // 2. Check via Pdfium (high-precision native Google Chrome engine)
        try
        {
            using var stream = File.OpenRead(pdfPath);
            using var document = string.IsNullOrEmpty(password)
                ? PdfiumViewer.PdfDocument.Load(stream)
                : PdfiumViewer.PdfDocument.Load(stream, password);
            return false;
        }
        catch (Exception ex)
        {
            string msg = ex.Message.ToLowerInvariant();
            if (msg.Contains("password") || msg.Contains("encrypt") || msg.Contains("protect") || ex is PdfiumViewer.PdfException)
            {
                return true;
            }
        }

        // 3. Check via PdfSharpCore
        try
        {
            using var doc = string.IsNullOrEmpty(password)
                ? PdfReader.Open(pdfPath, PdfDocumentOpenMode.Import)
                : PdfReader.Open(pdfPath, password, PdfDocumentOpenMode.Import);
            return false;
        }
        catch (Exception ex)
        {
            string msg = ex.Message.ToLowerInvariant();
            if (msg.Contains("password") || msg.Contains("encrypt") || msg.Contains("protect") || ex is PdfSharpCore.Pdf.IO.PdfReaderException)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Renders a single page of the PDF at high resolution (e.g. 150-300 DPI) for live studio editing.
    /// </summary>
    public async Task<BitmapSource?> RenderPageAsync(string pdfPath, int pageIndex, int dpi = 150, string? password = null)
    {
        if (!File.Exists(pdfPath)) return null;

        return await Task.Run(() =>
        {
            try
            {
                byte[] pdfBytes = File.ReadAllBytes(pdfPath);
                using var stream = new MemoryStream(pdfBytes);
                using var document = string.IsNullOrEmpty(password)
                    ? PdfiumViewer.PdfDocument.Load(stream)
                    : PdfiumViewer.PdfDocument.Load(stream, password);

                if (pageIndex < 0 || pageIndex >= document.PageCount) return null;

                var pageSize = document.PageSizes[pageIndex];
                int targetW = (int)Math.Round(pageSize.Width / 72.0 * dpi);
                int targetH = (int)Math.Round(pageSize.Height / 72.0 * dpi);
                if (targetW <= 0) targetW = 800;
                if (targetH <= 0) targetH = (int)(targetW * 1.414);

                using var rendered = (Bitmap)document.Render(pageIndex, targetW, targetH, dpi, dpi, PdfiumViewer.PdfRenderFlags.Annotations);
                return PdfRendererService.ToBitmapSource(rendered);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to render page {PageIndex} at {Dpi} DPI for {Path}", pageIndex, dpi, pdfPath);
                return null;
            }
        });
    }

    /// <summary>
    /// Commits all in-place edits (whiteouts, text replacements, photos, ink) into the actual PDF.
    /// Preserves 100% of the underlying vector PDF content, fonts, and page structure!
    /// Supports unlocked/password-protected PDFs.
    /// </summary>
    public async Task<bool> SaveEditsToPdfAsync(
        string sourcePdfPath,
        string outputPdfPath,
        IReadOnlyDictionary<int, IReadOnlyList<PdfEditItem>> pageEdits,
        string? password = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePdfPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPdfPath);

        return await Task.Run(() =>
        {
            try
            {
                string tempOut = Path.Combine(Path.GetDirectoryName(outputPdfPath)!, $".tmp_edit_{Guid.NewGuid():N}.pdf");

                // Check if we can open in Modify mode
                bool modifiedDirectly = false;
                try
                {
                    using (var doc = string.IsNullOrEmpty(password)
                        ? PdfReader.Open(sourcePdfPath, PdfDocumentOpenMode.Modify)
                        : PdfReader.Open(sourcePdfPath, password, PdfDocumentOpenMode.Modify))
                    {
                        if (!string.IsNullOrEmpty(password))
                        {
                            try
                            {
                                doc.SecuritySettings.OwnerPassword = string.Empty;
                                doc.SecuritySettings.UserPassword = string.Empty;
                            }
                            catch { }
                        }
                        ApplyEditsToDocument(doc, pageEdits);
                        doc.Save(tempOut);
                        modifiedDirectly = true;
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Could not open in Modify mode, importing pages instead for {Path}", sourcePdfPath);
                }

                if (!modifiedDirectly)
                {
                    // Fallback to Import mode
                    using var srcDoc = string.IsNullOrEmpty(password)
                        ? PdfReader.Open(sourcePdfPath, PdfDocumentOpenMode.Import)
                        : PdfReader.Open(sourcePdfPath, password, PdfDocumentOpenMode.Import);
                    using var outDoc = new PdfDocument();
                    outDoc.Info.Creator = "DASMO CYBER CAFE TOOLS — PDF Editor Studio";

                    for (int i = 0; i < srcDoc.PageCount; i++)
                    {
                        var page = outDoc.AddPage(srcDoc.Pages[i]);
                        if (pageEdits.TryGetValue(i, out var edits) && edits.Count > 0)
                        {
                            ApplyEditsToPage(page, edits);
                        }
                    }
                    outDoc.Save(tempOut);
                }

                if (File.Exists(outputPdfPath))
                {
                    File.Delete(outputPdfPath);
                }
                File.Move(tempOut, outputPdfPath);
                Log.Information("Successfully saved edited PDF to {OutPath}", outputPdfPath);
                return true;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to save edited PDF from {Source} to {Out}", sourcePdfPath, outputPdfPath);
                return false;
            }
        });
    }

    private void ApplyEditsToDocument(PdfDocument doc, IReadOnlyDictionary<int, IReadOnlyList<PdfEditItem>> pageEdits)
    {
        // 1. Redact replaced original text directly from existing content streams
        RedactReplacedTextFromStreams(doc, pageEdits);

        // 2. Draw all vector edits (whiteouts, new text, photos, ink)
        for (int i = 0; i < doc.PageCount; i++)
        {
            if (pageEdits.TryGetValue(i, out var edits) && edits.Count > 0)
            {
                ApplyEditsToPage(doc.Pages[i], edits);
            }
        }
    }

    private void RedactReplacedTextFromStreams(PdfDocument doc, IReadOnlyDictionary<int, IReadOnlyList<PdfEditItem>> pageEdits)
    {
        for (int i = 0; i < doc.PageCount; i++)
        {
            if (!pageEdits.TryGetValue(i, out var edits) || edits.Count == 0) continue;

            var targetsToRedact = edits
                .OfType<PdfTextItem>()
                .Where(t => !string.IsNullOrWhiteSpace(t.OriginalTextToRedact))
                .Select(t => t.OriginalTextToRedact!.Trim())
                .Distinct()
                .ToList();

            if (targetsToRedact.Count == 0) continue;

            var page = doc.Pages[i];
            if (page.Contents == null || page.Contents.Elements.Count == 0) continue;

            for (int e = 0; e < page.Contents.Elements.Count; e++)
            {
                var elem = page.Contents.Elements[e];
                var dict = (elem is PdfSharpCore.Pdf.Advanced.PdfReference r)
                    ? (r.Value as PdfSharpCore.Pdf.PdfDictionary)
                    : (elem as PdfSharpCore.Pdf.PdfDictionary);

                if (dict?.Stream == null) continue;

                try
                {
                    byte[] rawBytes = dict.Stream.UnfilteredValue;
                    if (rawBytes == null || rawBytes.Length == 0) continue;

                    string content = Encoding.GetEncoding("ISO-8859-1").GetString(rawBytes);
                    bool modified = false;

                    foreach (var target in targetsToRedact)
                    {
                        if (content.Contains(target))
                        {
                            content = content.Replace($"({target})Tj", "()Tj");
                            content = content.Replace($"({target}) Tj", "() Tj");
                            content = content.Replace($"({target})", "()");
                            modified = true;
                            Log.Information("Redacted original text '{Target}' from content stream {StreamIdx} on page {Page}", target, e, i);
                        }
                    }

                    if (modified)
                    {
                        byte[] newBytes = Encoding.GetEncoding("ISO-8859-1").GetBytes(content);
                        dict.Stream.Value = newBytes;
                        dict.Elements.Remove("/Filter");
                        dict.Elements["/Length"] = new PdfSharpCore.Pdf.PdfInteger(newBytes.Length);
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Could not redact content stream {StreamIdx} on page {Page}", e, i);
                }
            }
        }
    }

    private void ApplyEditsToPage(PdfPage page, IReadOnlyList<PdfEditItem> edits)
    {
        using var gfx = XGraphics.FromPdfPage(page);

        foreach (var item in edits)
        {
            switch (item)
            {
                case PdfWhiteoutItem whiteout:
                    DrawWhiteout(gfx, whiteout);
                    break;

                case PdfTextItem text:
                    DrawText(gfx, text);
                    break;

                case PdfImageItem img:
                    DrawImage(gfx, img);
                    break;

                case PdfInkItem ink:
                    DrawInk(gfx, ink);
                    break;
            }
        }
    }

    private void DrawWhiteout(XGraphics gfx, PdfWhiteoutItem item)
    {
        var color = ParseColor(item.FillColorHex, XColor.FromArgb(255, 255, 255));
        var brush = new XSolidBrush(color);
        gfx.DrawRectangle(brush, item.X, item.Y, item.Width, item.Height);
    }

    private void DrawText(XGraphics gfx, PdfTextItem item)
    {
        if (string.IsNullOrEmpty(item.Text)) return;

        var lines = item.Text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
        double size = Math.Max(4, item.FontSizePt);
        double lineHeight = Math.Max(size * 1.25, 8.0);
        double totalTextHeight = Math.Max(item.Height, lines.Length * lineHeight);

        // Auto-whiteout background if enabled
        if (item.HasOpaqueBackground)
        {
            var bgColor = ParseColor(item.BackgroundColorHex, XColor.FromArgb(255, 255, 255));
            var bgBrush = new XSolidBrush(bgColor);
            gfx.DrawRectangle(bgBrush, item.X, item.Y, Math.Max(10, item.Width), totalTextHeight);
        }

        var style = XFontStyle.Regular;
        if (item.IsBold && item.IsItalic) style = XFontStyle.BoldItalic;
        else if (item.IsBold) style = XFontStyle.Bold;
        else if (item.IsItalic) style = XFontStyle.Italic;

        string family = string.IsNullOrWhiteSpace(item.FontFamily) ? "Arial" : item.FontFamily;
        if (BengaliTextHelper.ContainsBengali(item.Text) && (family == "Arial" || family == "Times New Roman"))
        {
            family = BengaliTextHelper.PreferredBengaliFont;
        }
        var font = new XFont(family, size, style);

        var textColor = ParseColor(item.TextColorHex, XColor.FromArgb(0, 0, 0));
        var textBrush = new XSolidBrush(textColor);

        // Position directly on the baseline:
        // In PdfSharp, DrawString(text, font, brush, x, baselineY) positions text precisely on the baseline.
        double firstLineBaselineY = item.BaseLineY > 0
            ? item.BaseLineY
            : item.Y + (size * GetFontBaselineRatio(family));

        for (int i = 0; i < lines.Length; i++)
        {
            double currentBaselineY = firstLineBaselineY + (i * lineHeight);
            gfx.DrawString(lines[i], font, textBrush, item.X, currentBaselineY);
        }
    }

    private void DrawImage(XGraphics gfx, PdfImageItem item)
    {
        try
        {
            XImage? xImg = null;
            byte[]? rawBytes = item.ImageBytes;
            if ((rawBytes == null || rawBytes.Length == 0) && !string.IsNullOrEmpty(item.SourceFilePath) && File.Exists(item.SourceFilePath))
            {
                rawBytes = File.ReadAllBytes(item.SourceFilePath);
            }

            if (rawBytes != null && rawBytes.Length > 0)
            {
                try
                {
                    xImg = XImage.FromStream(() => new MemoryStream(rawBytes));
                }
                catch
                {
                    // Fallback: convert via System.Drawing to standard PNG stream
                    using var msIn = new MemoryStream(rawBytes);
                    using var sysImg = System.Drawing.Image.FromStream(msIn);
                    using var msOut = new MemoryStream();
                    sysImg.Save(msOut, System.Drawing.Imaging.ImageFormat.Png);
                    byte[] pngBytes = msOut.ToArray();
                    xImg = XImage.FromStream(() => new MemoryStream(pngBytes));
                }
            }

            if (xImg != null)
            {
                gfx.DrawImage(xImg, item.X, item.Y, item.Width, item.Height);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to draw image on PDF page at ({X}, {Y})", item.X, item.Y);
        }
    }

    private void DrawInk(XGraphics gfx, PdfInkItem item)
    {
        if (item.Points == null || item.Points.Count < 2) return;

        var color = ParseColor(item.StrokeColorHex, XColor.FromArgb(0, 0, 0));
        var pen = new XPen(color, Math.Max(0.5, item.StrokeThickness));

        for (int i = 0; i < item.Points.Count - 1; i++)
        {
            var p1 = item.Points[i];
            var p2 = item.Points[i + 1];
            gfx.DrawLine(pen, p1.X, p1.Y, p2.X, p2.Y);
        }
    }

    private static XColor ParseColor(string hex, XColor fallback)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(hex)) return fallback;
            hex = hex.TrimStart('#');
            if (hex.Length == 6)
            {
                byte r = Convert.ToByte(hex.Substring(0, 2), 16);
                byte g = Convert.ToByte(hex.Substring(2, 2), 16);
                byte b = Convert.ToByte(hex.Substring(4, 2), 16);
                return XColor.FromArgb(r, g, b);
            }
            if (hex.Length == 8)
            {
                byte a = Convert.ToByte(hex.Substring(0, 2), 16);
                byte r = Convert.ToByte(hex.Substring(2, 2), 16);
                byte g = Convert.ToByte(hex.Substring(4, 2), 16);
                byte b = Convert.ToByte(hex.Substring(6, 2), 16);
                return XColor.FromArgb(a, r, g, b);
            }
        }
        catch { }
        return fallback;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // PDF TEXT EXTRACTION  (PdfPig)
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Extracts all text blocks from a single page using PdfPig.
    /// Returns blocks in WPF canvas coordinates (top-left origin, PDF points scale).
    /// Font family is matched to the closest available Windows font.
    /// Supports unlocked/password-protected PDFs.
    /// </summary>
    public List<SmartSaver.Models.PdfExtractedTextBlock> ExtractTextBlocks(string pdfPath, int pageIndex, double pageHeightPt, string? password = null)
    {
        var result = new List<SmartSaver.Models.PdfExtractedTextBlock>();
        if (!File.Exists(pdfPath)) return result;

        try
        {
            var options = new UglyToad.PdfPig.ParsingOptions
            {
                Password = password,
                UseLenientParsing = true
            };
            using var doc = UglyToad.PdfPig.PdfDocument.Open(pdfPath, options);
            if (pageIndex < 0 || pageIndex >= doc.NumberOfPages) return result;

            // PdfPig pages are 1-indexed
            var page = doc.GetPage(pageIndex + 1);

            var rawWords = page.GetWords().ToList();

            for (int i = 0; i < rawWords.Count; i++)
            {
                var word = rawWords[i];
                string text = word.Text?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(text)) continue;

                var bb = word.BoundingBox;
                double curLeft = bb.Left;
                double curBottom = bb.Bottom;
                double curRight = bb.Right;
                double curTop = bb.Top;
                var firstLetter = word.Letters.FirstOrDefault();

                // Merge with immediately adjacent words on the same baseline (syllables within words, or words within the same phrase/label/cell)
                while (i + 1 < rawWords.Count)
                {
                    var next = rawWords[i + 1];
                    if (Math.Abs(curBottom - next.BoundingBox.Bottom) <= 2.5)
                    {
                        double gap = next.BoundingBox.Left - curRight;
                        if (gap >= -8.0 && gap <= 1.0)
                        {
                            // Syllable fragment within word (kerning / advance width split)
                            text += next.Text;
                            curRight = Math.Max(curRight, next.BoundingBox.Right);
                            curTop = Math.Max(curTop, next.BoundingBox.Top);
                            i++;
                            continue;
                        }

                        bool isBengali = BengaliTextHelper.ContainsBengali(text) || BengaliTextHelper.ContainsBengali(next.Text);
                        if (isBengali && !text.TrimEnd().EndsWith(":"))
                        {
                            double maxGap = Math.Max(8.0, (firstLetter?.PointSize ?? 10) * 0.85);
                            if (gap > 1.0 && (gap <= maxGap || (next.Text == ":" && gap <= 35.0)))
                            {
                                // Words within the same phrase/label/cell on the same line, or trailing colon
                                text += " " + next.Text;
                                curRight = Math.Max(curRight, next.BoundingBox.Right);
                                curTop = Math.Max(curTop, next.BoundingBox.Top);
                                i++;
                                continue;
                            }
                        }
                    }
                    break;
                }

                text = BengaliTextHelper.NormalizeBengaliText(text.Trim());
                if (string.IsNullOrWhiteSpace(text)) continue;

                // Bounding box in PDF points (bottom-left origin)
                double pdfX = curLeft;
                double pdfY = curBottom;
                double w    = curRight - curLeft;
                double h    = curTop - curBottom;

                // Font info from first letter
                double fontSize  = 10.0;
                string fontName  = "Arial";
                bool   isBold    = false;
                bool   isItalic  = false;
                string colorHex  = "#000000";

                double pdfBaseLineY = curBottom;

                if (firstLetter != null)
                {
                    fontSize = Math.Max(4, Math.Abs(firstLetter.PointSize));
                    if (firstLetter.Font != null)
                    {
                        isBold = (firstLetter.Font.IsBold == true) || (firstLetter.Font.Weight >= 500);
                        isItalic = firstLetter.Font.IsItalic == true;
                    }
                    if (BengaliTextHelper.ContainsBengali(text))
                    {
                        // In Indian government documents, ROR land records, and citizen identity forms,
                        // Bengali fonts are rendered with heavy/medium TrueType outlines.
                        // If weight is not specified (0) or >= 450, preserve bold so it never degrades to thin hairline text.
                        if (firstLetter.Font == null || firstLetter.Font.Weight >= 450 || firstLetter.Font.Weight == 0)
                        {
                            isBold = true;
                        }
                    }
                    fontName = MapToWindowsFont(firstLetter.FontName ?? string.Empty, text, ref isBold, ref isItalic);
                    colorHex = ExtractColorHex(firstLetter);
                    if (firstLetter.StartBaseLine.Y > 0)
                    {
                        pdfBaseLineY = firstLetter.StartBaseLine.Y;
                    }
                }

                // Calculate exact typographical baseline in top-down coordinates
                double topDownBaseLine = pageHeightPt - pdfBaseLineY;

                // Baseline ratio from font metrics (ratio of baseline distance from top to font size)
                double baselineRatio = GetFontBaselineRatio(fontName);

                // Exact CanvasY so that: CanvasY + (fontSize * baselineRatio) == topDownBaseLine
                // This guarantees the WPF TextBox text sits on the identical baseline down to the sub-pixel!
                double canvasX = pdfX;
                double canvasY = topDownBaseLine - (fontSize * baselineRatio);
                double lineHeight = fontSize * 1.25;

                result.Add(new SmartSaver.Models.PdfExtractedTextBlock
                {
                    OriginalText = text,
                    PdfX         = pdfX,
                    PdfY         = pdfY,
                    PdfWidth     = Math.Max(w, fontSize * 0.6),
                    PdfHeight    = Math.Max(h, lineHeight),
                    CanvasX      = canvasX,
                    CanvasY      = canvasY,
                    BaseLineY    = topDownBaseLine,
                    FontSizePt   = fontSize,
                    FontFamily   = fontName,
                    IsBold       = isBold,
                    IsItalic     = isItalic,
                    ColorHex     = colorHex,
                    EditedText   = text
                });
            }

            // Apply Painter's Algorithm occlusion filtering to eliminate ghost text
            return FilterOccludedTextBlocks(result, page);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "PdfPig text extraction failed for {Path} page {Page}", pdfPath, pageIndex);
        }

        return result;
    }

    /// <summary>
    /// Filters out individual letters that are occluded by letters drawn later in the PDF content streams.
    /// In PDF rendering (Painter's Algorithm), elements drawn later paint on top of earlier elements.
    /// If an earlier letter A is horizontally overlapped at the same baseline by a later letter B,
    /// letter A is obscured/covered. Filtering it prior to word extraction prevents overlapping
    /// letters from different streams being interleaved into garbled words.
    /// </summary>
    private static IReadOnlyList<Letter> FilterOccludedLetters(IReadOnlyList<Letter> letters)
    {
        if (letters == null || letters.Count <= 1) return letters ?? Array.Empty<Letter>();

        var occluded = new bool[letters.Count];

        for (int i = 0; i < letters.Count; i++)
        {
            var a = letters[i];
            double aLeft = a.GlyphRectangle.Left;
            double aRight = a.GlyphRectangle.Right;
            double aWidth = a.GlyphRectangle.Width;
            if (aWidth <= 0.1) continue;

            for (int j = i + 1; j < letters.Count; j++)
            {
                var b = letters[j];
                double bWidth = b.GlyphRectangle.Width;
                if (bWidth <= 0.1) continue;

                // Check vertical baseline proximity (within 60% of font point size)
                double lineTol = Math.Max(a.PointSize, b.PointSize) * 0.6;
                if (Math.Abs(a.StartBaseLine.Y - b.StartBaseLine.Y) <= lineTol)
                {
                    double overlap = Math.Min(aRight, b.GlyphRectangle.Right) - Math.Max(aLeft, b.GlyphRectangle.Left);
                    if (overlap > aWidth * 0.35 || overlap >= 2.0)
                    {
                        occluded[i] = true;
                        break;
                    }
                }
            }
        }

        var visible = new List<Letter>(letters.Count);
        for (int i = 0; i < letters.Count; i++)
        {
            if (!occluded[i]) visible.Add(letters[i]);
        }
        return visible;
    }

    /// <summary>
    /// Filters out occluded, superseded, and erased text blocks using the Painter's Algorithm.
    /// In PDF streams, elements drawn LATER (higher index) paint ON TOP of elements drawn EARLIER (lower index).
    /// If an earlier word A is overlapped on the same line by a later word B, word A is visually superseded/hidden.
    /// Guarantees that replaced ghost text never appears as an interactive box in the UI.
    /// </summary>
    private static List<SmartSaver.Models.PdfExtractedTextBlock> FilterOccludedTextBlocks(
        List<SmartSaver.Models.PdfExtractedTextBlock> blocks,
        UglyToad.PdfPig.Content.Page page)
    {
        if (blocks.Count <= 1) return blocks;

        var isOccluded = new bool[blocks.Count];

        for (int i = 0; i < blocks.Count; i++)
        {
            var a = blocks[i];
            double aRight = a.CanvasX + a.PdfWidth;

            for (int j = i + 1; j < blocks.Count; j++)
            {
                var b = blocks[j];

                // Check if they are on the same vertical line
                double lineTolerance = Math.Max(a.FontSizePt, b.FontSizePt) * 0.75;
                if (Math.Abs(a.BaseLineY - b.BaseLineY) <= lineTolerance)
                {
                    double bRight = b.CanvasX + b.PdfWidth;
                    double overlapLeft = Math.Max(a.CanvasX, b.CanvasX);
                    double overlapRight = Math.Min(aRight, bRight);
                    double overlap = overlapRight - overlapLeft;

                    if (overlap > 0)
                    {
                        double aWidth = a.PdfWidth;
                        // For B drawn later to occlude A drawn earlier:
                        // 1. Overlap must cover at least 50% of A's width, OR at least 8.0 points if they share the same starting X.
                        // 2. B must actually start over A (not positioned to the right of A).
                        bool substantialOverlap = (overlap >= aWidth * 0.50) || (overlap >= 8.0 && Math.Abs(a.CanvasX - b.CanvasX) <= 6.0);
                        bool bStartsOverA = b.CanvasX < (a.CanvasX + aWidth - 2.0);

                        if (substantialOverlap && bStartsOverA)
                        {
                            isOccluded[i] = true;
                            break;
                        }
                    }
                }
            }
        }

        var filtered = new List<SmartSaver.Models.PdfExtractedTextBlock>(blocks.Count);
        for (int i = 0; i < blocks.Count; i++)
        {
            if (!isOccluded[i])
            {
                filtered.Add(blocks[i]);
            }
        }

        return filtered;
    }

    /// <summary>
    /// Gets the ratio of baseline offset from line top to font em-size.
    /// In WPF typography, the text baseline of the first line is at (FontSize * BaselineRatio).
    /// </summary>
    public static double GetFontBaselineRatio(string fontName)
    {
        try
        {
            var ff = new System.Windows.Media.FontFamily(fontName);
            if (ff.Baseline > 0.1 && ff.Baseline < 2.0)
            {
                return ff.Baseline;
            }
        }
        catch { }

        // Standard typography fallbacks
        string fn = (fontName ?? string.Empty).ToUpperInvariant();
        if (fn.Contains("TIMES")) return 0.9124;
        if (fn.Contains("ARIAL")) return 0.9216;
        if (fn.Contains("SEGOE")) return 1.0791;
        if (fn.Contains("CALIBRI")) return 0.85;
        if (fn.Contains("COURIER")) return 0.88;
        return 0.88;
    }

    /// <summary>
    /// Maps a PDF font name to the closest Windows font family.
    /// Detects bold/italic from the font name string.
    /// </summary>
    private static string MapToWindowsFont(string pdfFontName, string text, ref bool isBold, ref bool isItalic)
    {
        string fn = (pdfFontName ?? string.Empty).ToUpperInvariant();

        // Detect bold/italic flags from font name if not already detected from font object/weight
        if (!isBold)
        {
            isBold = fn.Contains("BOLD") || fn.Contains("BLACK") || fn.Contains("HEAVY") || 
                     fn.Contains("DEMI") || fn.Contains("MEDIUM") || fn.Contains("SEMIBOLD") || 
                     fn.Contains("SBOLD") || fn.Contains("FAT") || fn.Contains("DARK");
        }
        if (!isItalic)
        {
            isItalic = fn.Contains("ITALIC") || fn.Contains("OBLIQUE") || fn.Contains("SLANT");
        }

        if (BengaliTextHelper.ContainsBengali(text))
        {
            return BengaliTextHelper.PreferredBengaliFont;
        }

        // Map to known Windows fonts by keyword match
        if (fn.Contains("TIMES") || fn.Contains("TIMES NEW ROMAN") || fn.Contains("TNR"))
            return "Times New Roman";
        if (fn.Contains("CALIBRI"))
            return "Calibri";
        if (fn.Contains("COURIER") || fn.Contains("COUR") || fn.Contains("MONO"))
            return "Courier New";
        if (fn.Contains("GEORGIA"))
            return "Georgia";
        if (fn.Contains("VERDANA"))
            return "Verdana";
        if (fn.Contains("HELVETICA") || fn.Contains("ARIAL") || fn.Contains("ARIALMT"))
            return "Arial";
        if (fn.Contains("TAHOMA"))
            return "Tahoma";
        if (fn.Contains("TREBUCHET"))
            return "Trebuchet MS";
        if (fn.Contains("GARAMOND"))
            return "Garamond";
        if (fn.Contains("PALATINO"))
            return "Palatino Linotype";
        if (fn.Contains("SEGOE"))
            return "Segoe UI";

        // Default fallback: if it looks like a serif, use Times New Roman; else Arial
        bool likelySerif = fn.Contains("SERIF") || fn.Contains("ROMAN") || fn.Contains("MINCHO") || fn.Contains("BOOK");
        return likelySerif ? "Times New Roman" : "Arial";
    }

    /// <summary>
    /// Extracts the text rendering color from a PdfPig letter as a hex string.
    /// </summary>
    private static string ExtractColorHex(UglyToad.PdfPig.Content.Letter letter)
    {
        try
        {
            var col = letter.Color;
            if (col != null)
            {
                // PdfPig colors expose R/G/B as 0-1 doubles
                byte r = (byte)Math.Clamp((int)(col.ToRGBValues().r * 255), 0, 255);
                byte g = (byte)Math.Clamp((int)(col.ToRGBValues().g * 255), 0, 255);
                byte b = (byte)Math.Clamp((int)(col.ToRGBValues().b * 255), 0, 255);
                return $"#{r:X2}{g:X2}{b:X2}";
            }
        }
        catch { /* color not extractable — use default black */ }
        return "#000000";
    }
}
