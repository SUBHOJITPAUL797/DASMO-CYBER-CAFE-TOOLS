using System;
using System.Drawing.Imaging;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using PdfSharpCore.Pdf;
using PdfSharpCore.Drawing;
using PdfiumViewer;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Serilog;
using SmartSaver.Models;

namespace SmartSaver.Services;

public enum StackerLayoutMode
{
    StandardIdCard, // Exact Indian ID Card size: 8.56 cm x 5.4 cm (Front Top, Back Bottom, Same Size)
    HalfA4,         // 2 Equal Halves of A4 (for Certificates, Marksheets, Passbook)
    FullA4Sheet     // Entire A4 Paper (Single Full Page Document)
}

/// <summary>
/// Combines documents or images onto an A4 sheet (ID card stack, Half-A4, or Full A4),
/// saving the result as a PDF or an Image, and compresses it.
/// </summary>
public sealed class DocumentStackerService
{
    private readonly CompressionEngine _compressionEngine;

    public DocumentStackerService(CompressionEngine compressionEngine)
    {
        _compressionEngine = compressionEngine ?? throw new ArgumentNullException(nameof(compressionEngine));
        PdfRendererService.EnsureDllResolver();
    }

    /// <summary>
    /// Stacks documents/images onto a single A4 PDF page.
    /// </summary>
    public async Task<CompressionResult> StackToPdfAsync(
        string frontPath, int frontPageIndex, double frontScale,
        string backPath, int backPageIndex, double backScale,
        string outputPath, long targetBytes,
        StackerLayoutMode layoutMode = StackerLayoutMode.StandardIdCard,
        bool autoWhiten = false,
        bool pitchBlackText = false)
    {
        return await Task.Run(async () =>
        {
            try
            {
                using (var document = new PdfSharpCore.Pdf.PdfDocument())
                {
                    var page = document.AddPage();
                    page.Size = PdfSharpCore.PageSize.A4;

                    using (var gfx = XGraphics.FromPdfPage(page))
                    {
                        // A4 is 595.28 x 841.89 points at 72 DPI (1 cm = ~28.346 points)
                        double a4W = page.Width.Point;
                        double a4H = page.Height.Point;

                        if (layoutMode == StackerLayoutMode.StandardIdCard)
                        {
                            // Standard ID card: exactly 8.5 cm length x 5.5 cm width -> 85.0 mm x 55.0 mm
                            // Scale factor applied uniformly to BOTH cards so both cards are always identical size!
                            double cardW = 8.50 * 28.3464567 * (frontScale / 100.0);
                            double cardH = 5.50 * 28.3464567 * (frontScale / 100.0);
                            double centerX = (a4W - cardW) / 2.0;

                            // Top card centered in upper half, Bottom card centered in lower half
                            double topY = (a4H * 0.5 - cardH) / 2.0;
                            double bottomY = a4H * 0.5 + (a4H * 0.5 - cardH) / 2.0;

                            DrawSourceItemExact(gfx, frontPath, frontPageIndex, centerX, topY, cardW, cardH, autoWhiten, pitchBlackText);
                            DrawSourceItemExact(gfx, backPath, backPageIndex, centerX, bottomY, cardW, cardH, autoWhiten, pitchBlackText);

                            // Middle fold / cut guide line
                            var pen = new XPen(XColor.FromArgb(210, 210, 210), 0.5) { DashStyle = XDashStyle.Dash };
                            gfx.DrawLine(pen, 35, a4H * 0.5, a4W - 35, a4H * 0.5);
                        }
                        else if (layoutMode == StackerLayoutMode.HalfA4)
                        {
                            // 2 Equal Halves of A4 (each A5 horizontal)
                            double marginX = 28.35; // 10 mm
                            double marginY = 24.0;
                            double maxWidth = a4W - (marginX * 2);
                            double maxHeight = (a4H * 0.5) - (marginY * 1.5);

                            // Draw Top Half (Front)
                            DrawSourceItemFit(gfx, frontPath, frontPageIndex, frontScale, marginX, marginY, maxWidth, maxHeight, autoWhiten, pitchBlackText);

                            // Draw Bottom Half (Back, if provided)
                            if (!string.IsNullOrWhiteSpace(backPath) && File.Exists(backPath))
                            {
                                DrawSourceItemFit(gfx, backPath, backPageIndex, backScale, marginX, a4H * 0.5 + marginY * 0.5, maxWidth, maxHeight, autoWhiten, pitchBlackText);
                            }

                            // Middle fold / cut guide line
                            var pen = new XPen(XColor.FromArgb(210, 210, 210), 0.5) { DashStyle = XDashStyle.Dash };
                            gfx.DrawLine(pen, 30, a4H * 0.5, a4W - 30, a4H * 0.5);
                        }
                        else // FullA4Sheet
                        {
                            // Entire A4 Paper (Single Full Document)
                            string docPath = !string.IsNullOrEmpty(frontPath) && File.Exists(frontPath) ? frontPath : backPath;
                            int docPage = !string.IsNullOrEmpty(frontPath) && File.Exists(frontPath) ? frontPageIndex : backPageIndex;
                            double docScale = !string.IsNullOrEmpty(frontPath) && File.Exists(frontPath) ? frontScale : backScale;

                            double marginX = 20.0; // ~7mm printable margin
                            double marginY = 20.0;
                            double maxWidth = a4W - (marginX * 2);
                            double maxHeight = a4H - (marginY * 2);

                            DrawSourceItemFit(gfx, docPath, docPage, docScale, marginX, marginY, maxWidth, maxHeight, autoWhiten, pitchBlackText);
                        }
                    }

                    string tempPath = Path.Combine(
                        Path.GetDirectoryName(outputPath)!,
                        $".smartsaver_stack_tmp_{Guid.NewGuid():N}.pdf");

                    document.Save(tempPath);

                    Log.Information("A4 PDF stacked, compressing to target size: {TempPath}", tempPath);
                    var result = await _compressionEngine.CompressFileAsync(tempPath, outputPath, targetBytes, ".pdf", keepBackup: false);

                    try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
                    return result;
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to stack documents to PDF");
                return new CompressionResult
                {
                    FilePath = outputPath,
                    Success = false,
                    Message = $"Stacking failed: {ex.Message}"
                };
            }
        });
    }

    /// <summary>
    /// Stacks documents/images onto a single A4 Image (JPEG or PNG).
    /// </summary>
    public async Task<CompressionResult> StackToImageAsync(
        string frontPath, int frontPageIndex, double frontScale,
        string backPath, int backPageIndex, double backScale,
        string outputPath, long targetBytes, string outputExtension,
        StackerLayoutMode layoutMode = StackerLayoutMode.StandardIdCard,
        bool autoWhiten = false,
        bool pitchBlackText = false)
    {
        return await Task.Run(async () =>
        {
            string tempPath = Path.Combine(
                Path.GetDirectoryName(outputPath)!,
                $".smartsaver_stack_tmp_{Guid.NewGuid():N}{outputExtension}");

            try
            {
                // A4 dimensions at 300 DPI: 2480 x 3508 pixels
                int canvasW = 2480;
                int canvasH = 3508;

                using (var canvas = new SixLabors.ImageSharp.Image<Rgba32>(canvasW, canvasH))
                {
                    canvas.Metadata.HorizontalResolution = 300;
                    canvas.Metadata.VerticalResolution = 300;
                    canvas.Metadata.ResolutionUnits = SixLabors.ImageSharp.Metadata.PixelResolutionUnit.PixelsPerInch;
                    canvas.Mutate(ctx => ctx.BackgroundColor(SixLabors.ImageSharp.Color.White));

                    if (layoutMode == StackerLayoutMode.StandardIdCard)
                    {
                        // Exactly 8.5 cm length x 5.5 cm width at 300 DPI: ~1004 x 650 pixels (1 cm = ~118.110236 px)
                        // Both cards rendered at identical exact dimensions
                        int cardW = (int)Math.Round(8.50 * 118.110236 * (frontScale / 100.0));
                        int cardH = (int)Math.Round(5.50 * 118.110236 * (frontScale / 100.0));
                        int posX = (canvasW - cardW) / 2;

                        int topY = (int)((canvasH * 0.5 - cardH) / 2);
                        int bottomY = (int)(canvasH * 0.5 + (canvasH * 0.5 - cardH) / 2);

                        DrawImageOnCanvasExact(canvas, frontPath, frontPageIndex, posX, topY, cardW, cardH, autoWhiten, pitchBlackText);
                        DrawImageOnCanvasExact(canvas, backPath, backPageIndex, posX, bottomY, cardW, cardH, autoWhiten, pitchBlackText);

                        // Middle dashed line
                        DrawDashedLine(canvas, canvasH / 2);
                    }
                    else if (layoutMode == StackerLayoutMode.HalfA4)
                    {
                        // 2 Equal Halves of A4 (Passbook / Certificate)
                        int marginX = 120;
                        int marginY = 120;
                        int maxW = canvasW - (marginX * 2);
                        int maxH = (canvasH / 2) - (marginY * 2);

                        DrawImageOnCanvasFit(canvas, frontPath, frontPageIndex, frontScale, marginX, marginY, maxW, maxH, autoWhiten, pitchBlackText);

                        if (!string.IsNullOrWhiteSpace(backPath) && File.Exists(backPath))
                        {
                            DrawImageOnCanvasFit(canvas, backPath, backPageIndex, backScale, marginX, (canvasH / 2) + marginY, maxW, maxH, autoWhiten, pitchBlackText);
                        }

                        // Middle dashed line
                        DrawDashedLine(canvas, canvasH / 2);
                    }
                    else // FullA4Sheet
                    {
                        // Entire A4 Paper (Single Document)
                        string docPath = !string.IsNullOrEmpty(frontPath) && File.Exists(frontPath) ? frontPath : backPath;
                        int docPage = !string.IsNullOrEmpty(frontPath) && File.Exists(frontPath) ? frontPageIndex : backPageIndex;
                        double docScale = !string.IsNullOrEmpty(frontPath) && File.Exists(frontPath) ? frontScale : backScale;

                        int marginX = 80; // ~6.8mm printable margin
                        int marginY = 80;
                        int maxW = canvasW - (marginX * 2);
                        int maxH = canvasH - (marginY * 2);

                        DrawImageOnCanvasFit(canvas, docPath, docPage, docScale, marginX, marginY, maxW, maxH, autoWhiten, pitchBlackText);
                    }

                    canvas.Save(tempPath);
                }

                Log.Information("A4 Image stacked, compressing to target: {TempPath}", tempPath);
                var result = await _compressionEngine.CompressFileAsync(tempPath, outputPath, targetBytes, outputExtension, keepBackup: false);

                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
                return result;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to stack documents to Image");
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
                return new CompressionResult
                {
                    FilePath = outputPath,
                    Success = false,
                    Message = $"Stacking failed: {ex.Message}"
                };
            }
        });
    }

    /// <summary>
    /// Legacy overload for StackToImageAsync with default page index 0.
    /// </summary>
    public Task<CompressionResult> StackToImageAsync(
        string frontPath, double frontScale,
        string backPath, double backScale,
        string outputPath, long targetBytes, string outputExtension,
        StackerLayoutMode layoutMode = StackerLayoutMode.StandardIdCard,
        bool autoWhiten = false,
        bool pitchBlackText = false)
    {
        return StackToImageAsync(frontPath, 0, frontScale, backPath, 0, backScale, outputPath, targetBytes, outputExtension, layoutMode, autoWhiten, pitchBlackText);
    }

    /// <summary>
    /// Renders the complete A4 sheet layout directly into memory as a 300 DPI BitmapSource for instant printing.
    /// Completely eliminates disk writes and slow compression loops, achieving sub-second print dialog launch.
    /// </summary>
    public async Task<BitmapSource?> RenderA4SheetBitmapAsync(
        string frontPath, int frontPageIndex, double frontScale,
        string backPath, int backPageIndex, double backScale,
        StackerLayoutMode layoutMode = StackerLayoutMode.StandardIdCard,
        bool autoWhiten = false,
        bool pitchBlackText = false)
    {
        return await Task.Run(() =>
        {
            try
            {
                // A4 dimensions at 300 DPI: 2480 x 3508 pixels
                int canvasW = 2480;
                int canvasH = 3508;

                using var canvas = new SixLabors.ImageSharp.Image<Rgba32>(canvasW, canvasH);
                canvas.Metadata.HorizontalResolution = 300;
                canvas.Metadata.VerticalResolution = 300;
                canvas.Metadata.ResolutionUnits = SixLabors.ImageSharp.Metadata.PixelResolutionUnit.PixelsPerInch;
                canvas.Mutate(ctx => ctx.BackgroundColor(SixLabors.ImageSharp.Color.White));

                if (layoutMode == StackerLayoutMode.StandardIdCard)
                {
                    // Exactly 8.5 cm length x 5.5 cm width at 300 DPI: ~1004 x 650 pixels (1 cm = ~118.110236 px)
                    int cardW = (int)Math.Round(8.50 * 118.110236 * (frontScale / 100.0));
                    int cardH = (int)Math.Round(5.50 * 118.110236 * (frontScale / 100.0));
                    int posX = (canvasW - cardW) / 2;

                    int topY = (int)((canvasH * 0.5 - cardH) / 2);
                    int bottomY = (int)(canvasH * 0.5 + (canvasH * 0.5 - cardH) / 2);

                    DrawImageOnCanvasExact(canvas, frontPath, frontPageIndex, posX, topY, cardW, cardH, autoWhiten, pitchBlackText);
                    DrawImageOnCanvasExact(canvas, backPath, backPageIndex, posX, bottomY, cardW, cardH, autoWhiten, pitchBlackText);

                    // Middle dashed fold/cut guide line
                    DrawDashedLine(canvas, canvasH / 2);
                }
                else if (layoutMode == StackerLayoutMode.HalfA4)
                {
                    // 2 Equal Halves of A4
                    int marginX = 120;
                    int marginY = 120;
                    int maxW = canvasW - (marginX * 2);
                    int maxH = (canvasH / 2) - (marginY * 2);

                    DrawImageOnCanvasFit(canvas, frontPath, frontPageIndex, frontScale, marginX, marginY, maxW, maxH, autoWhiten, pitchBlackText);

                    if (!string.IsNullOrWhiteSpace(backPath) && File.Exists(backPath))
                    {
                        DrawImageOnCanvasFit(canvas, backPath, backPageIndex, backScale, marginX, (canvasH / 2) + marginY, maxW, maxH, autoWhiten, pitchBlackText);
                    }

                    DrawDashedLine(canvas, canvasH / 2);
                }
                else // FullA4Sheet
                {
                    string docPath = !string.IsNullOrEmpty(frontPath) && File.Exists(frontPath) ? frontPath : backPath;
                    int docPage = !string.IsNullOrEmpty(frontPath) && File.Exists(frontPath) ? frontPageIndex : backPageIndex;
                    double docScale = !string.IsNullOrEmpty(frontPath) && File.Exists(frontPath) ? frontScale : backScale;

                    int marginX = 80;
                    int marginY = 80;
                    int maxW = canvasW - (marginX * 2);
                    int maxH = canvasH - (marginY * 2);

                    DrawImageOnCanvasFit(canvas, docPath, docPage, docScale, marginX, marginY, maxW, maxH, autoWhiten, pitchBlackText);
                }

                // Fast in-memory single-pass encoding (no disk I/O, no multi-pass compression)
                using var ms = new MemoryStream();
                var encoder = new SixLabors.ImageSharp.Formats.Png.PngEncoder
                {
                    CompressionLevel = SixLabors.ImageSharp.Formats.Png.PngCompressionLevel.BestSpeed
                };
                canvas.Save(ms, encoder);
                ms.Position = 0;

                var bi = new BitmapImage();
                bi.BeginInit();
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.StreamSource = ms;
                bi.EndInit();
                bi.Freeze();

                return (BitmapSource)bi;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "RenderA4SheetBitmapAsync failed");
                return null;
            }
        });
    }

    private void DrawSourceItemExact(XGraphics gfx, string path, int pageIndex, double x, double y, double targetW, double targetH, bool autoWhiten, bool pitchBlackText)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
        byte[]? imgBytes = GetImageBytes(path, pageIndex, autoWhiten, pitchBlackText);
        if (imgBytes == null) return;

        using var image = XImage.FromStream(() => new MemoryStream(imgBytes));
        gfx.DrawImage(image, x, y, targetW, targetH);

        // Optional card border
        var borderPen = new XPen(XColor.FromArgb(200, 200, 200), 0.5);
        gfx.DrawRectangle(borderPen, x, y, targetW, targetH);
    }

    private void DrawSourceItemFit(XGraphics gfx, string path, int pageIndex, double scaleFactor, double x, double y, double maxWidth, double maxHeight, bool autoWhiten, bool pitchBlackText)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
        byte[]? imgBytes = GetImageBytes(path, pageIndex, autoWhiten, pitchBlackText);
        if (imgBytes == null) return;

        using var image = XImage.FromStream(() => new MemoryStream(imgBytes));
        double formW = image.PointWidth;
        double formH = image.PointHeight;
        if (formW <= 0 || formH <= 0) return;
        double aspect = formW / formH;

        double w = maxWidth;
        double h = maxWidth / aspect;
        if (h > maxHeight)
        {
            h = maxHeight;
            w = maxHeight * aspect;
        }

        double scale = Math.Clamp(scaleFactor / 100.0, 0.1, 1.0);
        w *= scale;
        h *= scale;

        double posX = x + (maxWidth - w) / 2.0;
        double posY = y + (maxHeight - h) / 2.0;

        gfx.DrawImage(image, posX, posY, w, h);
    }

    private void DrawImageOnCanvasExact(Image<Rgba32> canvas, string path, int pageIndex, int x, int y, int targetW, int targetH, bool autoWhiten, bool pitchBlackText)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;

        try
        {
            byte[]? imgBytes = GetImageBytes(path, pageIndex, autoWhiten, pitchBlackText);
            if (imgBytes == null) return;

            using var itemImg = SixLabors.ImageSharp.Image.Load<Rgba32>(imgBytes);
            itemImg.Mutate(ctx =>
            {
                ctx.Resize(new ResizeOptions
                {
                    Mode = ResizeMode.Stretch,
                    Size = new SixLabors.ImageSharp.Size(targetW, targetH)
                });
            });

            canvas.Mutate(ctx => ctx.DrawImage(itemImg, new SixLabors.ImageSharp.Point(x, y), 1.0f));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to draw exact image on canvas: {Path}", path);
        }
    }

    private void DrawImageOnCanvasFit(Image<Rgba32> canvas, string path, int pageIndex, double scaleFactor, int x, int y, int maxW, int maxH, bool autoWhiten, bool pitchBlackText)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;

        try
        {
            byte[]? imgBytes = GetImageBytes(path, pageIndex, autoWhiten, pitchBlackText);
            if (imgBytes == null) return;

            using var itemImg = SixLabors.ImageSharp.Image.Load<Rgba32>(imgBytes);
            double aspect = (double)itemImg.Width / itemImg.Height;
            double w = maxW;
            double h = maxW / aspect;
            if (h > maxH)
            {
                h = maxH;
                w = maxH * aspect;
            }

            double scale = Math.Clamp(scaleFactor / 100.0, 0.1, 1.0);
            w *= scale;
            h *= scale;

            int finalW = Math.Max(1, (int)w);
            int finalH = Math.Max(1, (int)h);

            itemImg.Mutate(ctx => ctx.Resize(finalW, finalH));

            int posX = x + (maxW - finalW) / 2;
            int posY = y + (maxH - finalH) / 2;

            canvas.Mutate(ctx => ctx.DrawImage(itemImg, new SixLabors.ImageSharp.Point(posX, posY), 1.0f));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to draw fit image on canvas: {Path}", path);
        }
    }

    private static void DrawDashedLine(Image<Rgba32> canvas, int lineY)
    {
        int dashLen = 20;
        int gapLen = 15;
        var lineColor = new Rgba32(200, 200, 200, 255);

        canvas.ProcessPixelRows(accessor =>
        {
            for (int dy = -1; dy <= 1; dy++)
            {
                int cy = lineY + dy;
                if (cy < 0 || cy >= accessor.Height) continue;

                var row = accessor.GetRowSpan(cy);
                int startX = 100;
                int endX = accessor.Width - 100;

                for (int x = startX; x < endX; x++)
                {
                    int mod = (x - startX) % (dashLen + gapLen);
                    if (mod < dashLen)
                    {
                        row[x] = lineColor;
                    }
                }
            }
        });
    }

    private static void ApplyPitchBlackText(Image<Rgba32> image)
    {
        image.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < accessor.Height; y++)
            {
                var pixelRow = accessor.GetRowSpan(y);
                for (int x = 0; x < pixelRow.Length; x++)
                {
                    ref Rgba32 p = ref pixelRow[x];
                    float lum = 0.299f * p.R + 0.587f * p.G + 0.114f * p.B;
                    int maxDiff = Math.Max(Math.Abs(p.R - p.G), Math.Max(Math.Abs(p.R - p.B), Math.Abs(p.G - p.B)));
                    if (maxDiff <= 35) // Neutral text or grayscale document background
                    {
                        if (lum >= 175)
                        {
                            p.R = 255;
                            p.G = 255;
                            p.B = 255;
                        }
                        else if (lum < 135)
                        {
                            p.R = 0;
                            p.G = 0;
                            p.B = 0;
                        }
                        else
                        {
                            float factor = (lum - 135.0f) / 40.0f;
                            byte val = (byte)Math.Clamp(factor * 255.0f, 0, 255);
                            p.R = val;
                            p.G = val;
                            p.B = val;
                        }
                    }
                }
            }
        });
    }

    private static void WhitenImage(Image<Rgba32> image)
    {
        image.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < accessor.Height; y++)
            {
                var pixelRow = accessor.GetRowSpan(y);
                for (int x = 0; x < pixelRow.Length; x++)
                {
                    ref Rgba32 p = ref pixelRow[x];
                    float lum = 0.299f * p.R + 0.587f * p.G + 0.114f * p.B;
                    int maxDiff = Math.Max(Math.Abs(p.R - p.G), Math.Max(Math.Abs(p.R - p.B), Math.Abs(p.G - p.B)));
                    if (maxDiff <= 28)
                    {
                        if (lum >= 170)
                        {
                            p.R = 255;
                            p.G = 255;
                            p.B = 255;
                        }
                        else if (lum < 110)
                        {
                            float factor = (lum / 110.0f) * 0.70f;
                            p.R = (byte)Math.Clamp(p.R * factor, 0, 255);
                            p.G = (byte)Math.Clamp(p.G * factor, 0, 255);
                            p.B = (byte)Math.Clamp(p.B * factor, 0, 255);
                        }
                    }
                }
            }
        });
    }

    private byte[]? GetImageBytes(string path, int pageIndex, bool autoWhiten, bool pitchBlackText)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;

        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".pdf")
        {
            try
            {
                byte[] pdfBytes = File.ReadAllBytes(path);
                using var pdfStream = new MemoryStream(pdfBytes);
                using var doc = PdfiumViewer.PdfDocument.Load(pdfStream);
                int safeIndex = Math.Clamp(pageIndex, 0, Math.Max(0, doc.PageCount - 1));
                using var renderedImg = doc.Render(safeIndex, 300, 300, PdfRenderFlags.Annotations | PdfRenderFlags.CorrectFromDpi);

                using var ms = new MemoryStream();
                renderedImg.Save(ms, ImageFormat.Png);
                byte[] rawPng = ms.ToArray();

                if (autoWhiten || pitchBlackText)
                {
                    using var sharpImg = SixLabors.ImageSharp.Image.Load<Rgba32>(rawPng);
                    if (pitchBlackText)
                        ApplyPitchBlackText(sharpImg);
                    else if (autoWhiten)
                        WhitenImage(sharpImg);

                    using var outMs = new MemoryStream();
                    sharpImg.Save(outMs, new SixLabors.ImageSharp.Formats.Png.PngEncoder());
                    return outMs.ToArray();
                }

                return rawPng;
            }
            catch
            {
                var bmp = PdfRendererService.RenderPdfPageAsync(path, pageIndex).GetAwaiter().GetResult();
                if (bmp != null)
                {
                    using var ms = new MemoryStream();
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bmp));
                    encoder.Save(ms);
                    byte[] rawPng = ms.ToArray();

                    if (autoWhiten || pitchBlackText)
                    {
                        using var sharpImg = SixLabors.ImageSharp.Image.Load<Rgba32>(rawPng);
                        if (pitchBlackText)
                            ApplyPitchBlackText(sharpImg);
                        else if (autoWhiten)
                            WhitenImage(sharpImg);

                        using var outMs = new MemoryStream();
                        sharpImg.Save(outMs, new SixLabors.ImageSharp.Formats.Png.PngEncoder());
                        return outMs.ToArray();
                    }

                    return rawPng;
                }
                return null;
            }
        }
        else
        {
            try
            {
                using var img = SixLabors.ImageSharp.Image.Load<Rgba32>(path);
                img.Mutate(ctx => ctx.AutoOrient());
                if (pitchBlackText)
                    ApplyPitchBlackText(img);
                else if (autoWhiten)
                    WhitenImage(img);

                using var ms = new MemoryStream();
                img.Save(ms, new SixLabors.ImageSharp.Formats.Png.PngEncoder());
                return ms.ToArray();
            }
            catch
            {
                return File.Exists(path) ? File.ReadAllBytes(path) : null;
            }
        }
    }

    public static BitmapSource ApplyPitchBlackToBitmapSource(BitmapSource source)
    {
        var formatted = new FormatConvertedBitmap(source, System.Windows.Media.PixelFormats.Bgra32, null, 0);
        int width = formatted.PixelWidth;
        int height = formatted.PixelHeight;
        int stride = width * 4;
        byte[] pixels = new byte[height * stride];
        formatted.CopyPixels(pixels, stride, 0);

        for (int i = 0; i < pixels.Length; i += 4)
        {
            byte b = pixels[i];
            byte g = pixels[i + 1];
            byte r = pixels[i + 2];

            float lum = 0.299f * r + 0.587f * g + 0.114f * b;
            int maxDiff = Math.Max(Math.Abs(r - g), Math.Max(Math.Abs(r - b), Math.Abs(g - b)));

            if (maxDiff <= 35)
            {
                if (lum >= 175)
                {
                    pixels[i] = 255;
                    pixels[i + 1] = 255;
                    pixels[i + 2] = 255;
                }
                else if (lum < 135)
                {
                    pixels[i] = 0;
                    pixels[i + 1] = 0;
                    pixels[i + 2] = 0;
                }
                else
                {
                    float factor = (lum - 135.0f) / 40.0f;
                    byte val = (byte)Math.Clamp(factor * 255.0f, 0, 255);
                    pixels[i] = val;
                    pixels[i + 1] = val;
                    pixels[i + 2] = val;
                }
            }
        }

        var result = BitmapSource.Create(width, height, formatted.DpiX, formatted.DpiY, System.Windows.Media.PixelFormats.Bgra32, null, pixels, stride);
        result.Freeze();
        return result;
    }
}
