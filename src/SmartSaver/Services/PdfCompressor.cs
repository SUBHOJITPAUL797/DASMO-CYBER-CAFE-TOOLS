using System.IO;
using System.Drawing.Imaging;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf.Advanced;
using PdfiumViewer;
using Serilog;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;
using Image = SixLabors.ImageSharp.Image;
using PdfDocument = PdfSharpCore.Pdf.PdfDocument;
using SmartSaver.Models;

namespace SmartSaver.Services;

/// <summary>
/// Professional PDF compressor with two engines:
///
///   PRIMARY  — Ghostscript (gswin64c.exe)
///     • CCITT G4 Fax for B&amp;W (lossless)
///     • Bicubic JPEG for colour (Q90 → Q42 across 5 tiers)
///     • Font subsetting, duplicate image removal
///
///   FALLBACK — Iterative built-in engine (identical algorithm to pi7.org)
///     1. Quick pass: re-encode embedded images in-place (preserves vector text)
///     2. Iterative rasterization: render pages → JPEG, decreasing quality (90%→30%)
///        AND dimensions (−40px per iteration) until target size is met.
///        Same adaptive logic used by pi7.org, iLovePDF's JS engine, etc.
/// </summary>
public sealed class PdfCompressor
{
    private const long MinimumFloorBytes = 3 * 1024;   // 3 KB sanity floor
    private const int  MinQuality        = 30;          // Match pi7's 0.3 floor
    private const int  StartQuality      = 90;          // Start at 90% like pi7

    // ─── Public API ─────────────────────────────────────────────────────────

    /// <summary>
    /// Compresses a PDF to meet <paramref name="targetBytes"/>.
    /// Uses Ghostscript if available, otherwise the iterative built-in engine.
    /// </summary>
    public bool CompressToTargetSize(string sourcePath, string outputPath, long targetBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        if (!File.Exists(sourcePath)) return false;

        long originalSize = new FileInfo(sourcePath).Length;

        // ── PRIMARY: Ghostscript ─────────────────────────────────────────────
        if (GhostscriptService.IsAvailable)
        {
            try
            {
                int pageCount = GetPageCount(sourcePath);
                var result = new GhostscriptService()
                    .CompressToTargetSize(sourcePath, outputPath, targetBytes, pageCount);

                if (result.Success && result.MetTarget && File.Exists(outputPath))
                {
                    long sz = new FileInfo(outputPath).Length;
                    if (sz <= targetBytes)
                    {
                        Log.Information("GS: {Src} → {Size} bytes (tier:{Tier} met:{Met})",
                            Path.GetFileName(sourcePath), sz, result.TierUsed, result.MetTarget);
                        return true;
                    }
                }

                // If Ghostscript didn't reach target, clean up its temp output and fall through to built-in engine
                DeleteSafe(outputPath);
                Log.Information("GS did not meet target size {T} bytes for {Src} — using built-in engine", targetBytes, sourcePath);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "GS threw exception for {Path} — using built-in", sourcePath);
                DeleteSafe(outputPath);
            }
        }

        // ── FALLBACK: Iterative built-in ─────────────────────────────────────
        return CompressBuiltIn(sourcePath, outputPath, targetBytes, originalSize);
    }

    /// <summary>
    /// Merges multiple PDF files into one output file in the specified order.
    /// Optionally compresses the merged output to meet targetBytes if specified (>0).
    /// </summary>
    public bool MergePdfs(IReadOnlyList<string> sourceFiles, string outputPath, long? targetBytes = null)
    {
        ArgumentNullException.ThrowIfNull(sourceFiles);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        var validFiles = sourceFiles.Where(File.Exists).ToList();
        if (validFiles.Count == 0) return false;

        string tempMergedPath = Path.Combine(Path.GetTempPath(), $"smartsaver_merge_{Guid.NewGuid():N}.pdf");

        try
        {
            using (var outDoc = new PdfDocument())
            {
                foreach (var file in validFiles)
                {
                    try
                    {
                        using var inDoc = PdfReader.Open(file, PdfDocumentOpenMode.Import);
                        for (int i = 0; i < inDoc.PageCount; i++)
                        {
                            outDoc.AddPage(inDoc.Pages[i]);
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "Failed to import pages from {File} during merge", file);
                    }
                }

                if (outDoc.PageCount == 0) return false;

                outDoc.Save(tempMergedPath);
            }

            if (!File.Exists(tempMergedPath) || new FileInfo(tempMergedPath).Length == 0)
                return false;

            FileWatcherService.IgnoreOutputFile(outputPath);

            if (targetBytes.HasValue && targetBytes.Value > 0)
            {
                bool compressed = CompressToTargetSize(tempMergedPath, outputPath, targetBytes.Value);
                if (compressed && File.Exists(outputPath))
                {
                    Log.Information("Merged and compressed {Count} PDFs -> {Out} (Target: {Target})",
                        validFiles.Count, outputPath, targetBytes.Value);
                    return true;
                }
            }

            // If no target size or target not met, copy uncompressed merged file
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            File.Move(tempMergedPath, outputPath, overwrite: true);
            Log.Information("Merged {Count} PDFs -> {Out} ({Size} bytes)",
                validFiles.Count, outputPath, new FileInfo(outputPath).Length);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to merge {Count} PDFs into {Out}", sourceFiles.Count, outputPath);
            return false;
        }
        finally
        {
            DeleteSafe(tempMergedPath);
        }
    }

    /// <summary>
    /// Extracts specified 1-based page numbers from a PDF into an output file.
    /// Optionally compresses output to targetBytes if specified.
    /// </summary>
    public bool ExtractPdfPages(string sourcePath, string outputPath, IEnumerable<int> pageNumbers, long? targetBytes = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        if (!File.Exists(sourcePath)) return false;

        var pagesToExtract = pageNumbers.Distinct().OrderBy(p => p).ToList();
        if (pagesToExtract.Count == 0) return false;

        string tempPath = Path.Combine(Path.GetTempPath(), $"smartsaver_extract_{Guid.NewGuid():N}.pdf");
        try
        {
            using (var inDoc = PdfReader.Open(sourcePath, PdfDocumentOpenMode.Import))
            using (var outDoc = new PdfDocument())
            {
                foreach (int p in pagesToExtract)
                {
                    if (p >= 1 && p <= inDoc.PageCount)
                    {
                        outDoc.AddPage(inDoc.Pages[p - 1]);
                    }
                }

                if (outDoc.PageCount == 0) return false;
                outDoc.Save(tempPath);
            }

            if (!File.Exists(tempPath) || new FileInfo(tempPath).Length == 0)
                return false;

            FileWatcherService.IgnoreOutputFile(outputPath);

            if (targetBytes.HasValue && targetBytes.Value > 0)
            {
                bool compressed = CompressToTargetSize(tempPath, outputPath, targetBytes.Value);
                if (compressed && File.Exists(outputPath))
                {
                    Log.Information("Extracted {Pages} pages from {Src} and compressed -> {Out}",
                        pagesToExtract.Count, sourcePath, outputPath);
                    return true;
                }
            }

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            File.Move(tempPath, outputPath, overwrite: true);
            Log.Information("Extracted {Pages} pages from {Src} -> {Out}", pagesToExtract.Count, sourcePath, outputPath);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to extract pages from {Path}", sourcePath);
            return false;
        }
        finally
        {
            DeleteSafe(tempPath);
        }
    }

    /// <summary>
    /// Extracts pages in exact custom order and rotations from one or more source PDF files.
    /// Optionally compresses output to targetBytes if specified.
    /// </summary>
    public bool ExtractPdfPagesOrdered(string outputPath, IEnumerable<PdfPageExtractionItem> pageItems, long? targetBytes = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        var items = pageItems.ToList();
        if (items.Count == 0) return false;

        string tempPath = Path.Combine(Path.GetTempPath(), $"smartsaver_ordered_{Guid.NewGuid():N}.pdf");
        var openDocs = new Dictionary<string, PdfDocument>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using (var outDoc = new PdfDocument())
            {
                foreach (var item in items)
                {
                    if (string.IsNullOrWhiteSpace(item.SourcePdfPath) || !File.Exists(item.SourcePdfPath)) continue;

                    if (!openDocs.TryGetValue(item.SourcePdfPath, out var inDoc))
                    {
                        inDoc = PdfReader.Open(item.SourcePdfPath, PdfDocumentOpenMode.Import);
                        openDocs[item.SourcePdfPath] = inDoc;
                    }

                    if (item.PageIndex >= 1 && item.PageIndex <= inDoc.PageCount)
                    {
                        var importedPage = outDoc.AddPage(inDoc.Pages[item.PageIndex - 1]);
                        if (item.Rotation != 0)
                        {
                            importedPage.Rotate = (importedPage.Rotate + item.Rotation) % 360;
                        }
                    }
                }

                if (outDoc.PageCount == 0) return false;
                outDoc.Save(tempPath);
            }

            if (!File.Exists(tempPath) || new FileInfo(tempPath).Length == 0)
                return false;

            FileWatcherService.IgnoreOutputFile(outputPath);

            if (targetBytes.HasValue && targetBytes.Value > 0)
            {
                bool compressed = CompressToTargetSize(tempPath, outputPath, targetBytes.Value);
                if (compressed && File.Exists(outputPath))
                {
                    Log.Information("Extracted {Pages} ordered pages and compressed -> {Out}", items.Count, outputPath);
                    return true;
                }
            }

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            File.Move(tempPath, outputPath, overwrite: true);
            Log.Information("Extracted {Pages} ordered pages -> {Out}", items.Count, outputPath);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to extract ordered pages to {Path}", outputPath);
            return false;
        }
        finally
        {
            foreach (var doc in openDocs.Values)
            {
                try { doc.Dispose(); } catch { }
            }
            DeleteSafe(tempPath);
        }
    }

    /// <summary>
    /// Converts a list of images into a single PDF with A4 page fit.
    /// Optionally compresses output to targetBytes.
    /// </summary>
    public bool ImagesToPdf(IReadOnlyList<string> imagePaths, string outputPath, bool fitA4 = true, long? targetBytes = null)
    {
        ArgumentNullException.ThrowIfNull(imagePaths);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        var validImages = imagePaths.Where(File.Exists).ToList();
        if (validImages.Count == 0) return false;

        string tempPdf = Path.Combine(Path.GetTempPath(), $"smartsaver_img2pdf_{Guid.NewGuid():N}.pdf");

        try
        {
            using (var doc = new PdfDocument())
            {
                foreach (var imgPath in validImages)
                {
                    try
                    {
                        var page = doc.AddPage();
                        using var xImg = XImage.FromFile(imgPath);

                        if (fitA4)
                        {
                            bool isLandscape = xImg.PixelWidth > xImg.PixelHeight;
                            page.Orientation = isLandscape ? PdfSharpCore.PageOrientation.Landscape : PdfSharpCore.PageOrientation.Portrait;
                            page.Size = PdfSharpCore.PageSize.A4;

                            using var gfx = XGraphics.FromPdfPage(page);
                            double pageWidth = page.Width.Point;
                            double pageHeight = page.Height.Point;

                            double margin = 20;
                            double maxW = pageWidth - (margin * 2);
                            double maxH = pageHeight - (margin * 2);

                            double ratio = Math.Min(maxW / xImg.PixelWidth, maxH / xImg.PixelHeight);
                            double drawW = xImg.PixelWidth * ratio;
                            double drawH = xImg.PixelHeight * ratio;
                            double posX = (pageWidth - drawW) / 2;
                            double posY = (pageHeight - drawH) / 2;

                            gfx.DrawImage(xImg, posX, posY, drawW, drawH);
                        }
                        else
                        {
                            page.Width = XUnit.FromPoint(xImg.PointWidth);
                            page.Height = XUnit.FromPoint(xImg.PointHeight);
                            using var gfx = XGraphics.FromPdfPage(page);
                            gfx.DrawImage(xImg, 0, 0, page.Width.Point, page.Height.Point);
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "Failed to render image {Path} into PDF", imgPath);
                    }
                }

                if (doc.PageCount == 0) return false;
                doc.Save(tempPdf);
            }

            if (!File.Exists(tempPdf) || new FileInfo(tempPdf).Length == 0)
                return false;

            FileWatcherService.IgnoreOutputFile(outputPath);

            if (targetBytes.HasValue && targetBytes.Value > 0)
            {
                bool compressed = CompressToTargetSize(tempPdf, outputPath, targetBytes.Value);
                if (compressed && File.Exists(outputPath))
                {
                    Log.Information("Converted {Count} images to PDF and compressed -> {Out}", validImages.Count, outputPath);
                    return true;
                }
            }

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            File.Move(tempPdf, outputPath, overwrite: true);
            Log.Information("Converted {Count} images to PDF -> {Out}", validImages.Count, outputPath);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to convert images to PDF: {Out}", outputPath);
            return false;
        }
        finally
        {
            DeleteSafe(tempPdf);
        }
    }

    /// <summary>
    /// Converts each page of a PDF into high-res images (JPEG or PNG).
    /// </summary>
    public List<string> PdfToImages(string pdfPath, string outputDirectory, string format = ".jpg", int dpi = 300)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pdfPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);

        var outputFiles = new List<string>();
        if (!File.Exists(pdfPath)) return outputFiles;

        try
        {
            Directory.CreateDirectory(outputDirectory);
            string baseName = Path.GetFileNameWithoutExtension(pdfPath);

            byte[] pdfBytes = File.ReadAllBytes(pdfPath);
            using var pdfiumDoc = PdfiumViewer.PdfDocument.Load(new MemoryStream(pdfBytes));

            for (int i = 0; i < pdfiumDoc.PageCount; i++)
            {
                var pageSize = pdfiumDoc.PageSizes[i];
                int renderWidth = (int)(pageSize.Width * (dpi / 72.0));
                int renderHeight = (int)(pageSize.Height * (dpi / 72.0));

                using var bmp = pdfiumDoc.Render(i, renderWidth, renderHeight, dpi, dpi, PdfRenderFlags.Annotations);
                string outPath = Path.Combine(outputDirectory, $"{baseName}_page_{i + 1}{format}");

                FileWatcherService.IgnoreOutputFile(outPath);

                if (format.Equals(".png", StringComparison.OrdinalIgnoreCase))
                {
                    bmp.Save(outPath, System.Drawing.Imaging.ImageFormat.Png);
                }
                else if (format.Equals(".bmp", StringComparison.OrdinalIgnoreCase))
                {
                    bmp.Save(outPath, System.Drawing.Imaging.ImageFormat.Bmp);
                }
                else if (format.Equals(".tiff", StringComparison.OrdinalIgnoreCase) || format.Equals(".tif", StringComparison.OrdinalIgnoreCase))
                {
                    bmp.Save(outPath, System.Drawing.Imaging.ImageFormat.Tiff);
                }
                else if (format.Equals(".webp", StringComparison.OrdinalIgnoreCase))
                {
                    using var memStream = new MemoryStream();
                    bmp.Save(memStream, System.Drawing.Imaging.ImageFormat.Png);
                    memStream.Position = 0;
                    using var imgSharp = SixLabors.ImageSharp.Image.Load(memStream);
                    imgSharp.Save(outPath, new SixLabors.ImageSharp.Formats.Webp.WebpEncoder { Quality = 90 });
                }
                else
                {
                    var jpegEncoder = System.Drawing.Imaging.ImageCodecInfo.GetImageDecoders()
                        .FirstOrDefault(c => c.FormatID == System.Drawing.Imaging.ImageFormat.Jpeg.Guid);
                    if (jpegEncoder != null)
                    {
                        var encoderParams = new System.Drawing.Imaging.EncoderParameters(1);
                        encoderParams.Param[0] = new System.Drawing.Imaging.EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 92L);
                        bmp.Save(outPath, jpegEncoder, encoderParams);
                    }
                    else
                    {
                        bmp.Save(outPath, System.Drawing.Imaging.ImageFormat.Jpeg);
                    }
                }

                if (File.Exists(outPath))
                {
                    outputFiles.Add(outPath);
                }
            }

            Log.Information("Converted PDF {Src} to {Count} images in {Dir}", pdfPath, outputFiles.Count, outputDirectory);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to convert PDF to images: {Path}", pdfPath);
        }

        return outputFiles;
    }

    /// <summary>
    /// Upscales a PDF to meet a MINIMUM file size (for government portals
    /// that require a minimum file size, not a maximum).
    /// </summary>
    public bool UpscaleToTargetSize(string sourcePath, string outputPath, long targetBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        if (!File.Exists(sourcePath)) return false;

        try
        {
            byte[] pdfBytes = File.ReadAllBytes(sourcePath);
            using var pdfiumDoc = PdfiumViewer.PdfDocument.Load(new MemoryStream(pdfBytes));
            if (pdfiumDoc.PageCount == 0) return false;

            // Try progressively higher DPI until size >= target
            int[] widths = [1200, 1600, 2000, 2400, 3000];
            int[] qualities = [85, 90, 93, 95, 97];

            byte[]? bestBytes = null;
            long bestSize = 0;

            for (int i = 0; i < widths.Length; i++)
            {
                byte[]? tryBytes = RasterizeToJpegBytes(pdfiumDoc, widths[i], qualities[i]);
                if (tryBytes != null)
                {
                    long size = tryBytes.Length;
                    if (size > bestSize)
                    {
                        bestBytes = tryBytes;
                        bestSize = size;
                    }

                    if (bestSize >= targetBytes) break;
                }
            }

            if (bestBytes != null)
            {
                File.WriteAllBytes(outputPath, bestBytes);
                Log.Information("Upscaled: {Src} → {Size} bytes", sourcePath, bestSize);
                return true;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "UpscaleToTargetSize failed for {Path}", sourcePath);
        }

        return false;
    }

    // ─── Built-in Iterative Engine ───────────────────────────────────────────

    /// <summary>
    /// Iterative compression engine identical in approach to pi7.org:
    ///   1. Quick pass: re-compress embedded JPEG images in-place
    ///   2. Iterative: render pages → JPEG in RAM, reducing quality (90→20%) and
    ///      canvas size until target size is achieved.
    /// </summary>
    private bool CompressBuiltIn(string sourcePath, string outputPath, long targetBytes, long originalSize)
    {
        try
        {
            return CompressBuiltInCore(sourcePath, outputPath, targetBytes, originalSize);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "CompressBuiltIn threw unhandled exception for {Path}", sourcePath);
            DeleteSafe(outputPath);
            return false;
        }
    }

    private bool CompressBuiltInCore(string sourcePath, string outputPath, long targetBytes, long originalSize)
    {
        // ── Pass 1: Quick in-place re-encode (no rasterisation, preserves vector text) ──
        string inplacePath = Path.Combine(Path.GetTempPath(), $"smartsaver_inplace_{Guid.NewGuid():N}.pdf");
        try
        {
            if (TryInPlaceReencode(sourcePath, inplacePath))
            {
                byte[] inPlaceBytes = File.ReadAllBytes(inplacePath);
                long sz = inPlaceBytes.Length;
                if (sz <= targetBytes && sz >= MinimumFloorBytes)
                {
                    File.WriteAllBytes(outputPath, inPlaceBytes);
                    Log.Information("Built-in pass 1 (in-place): {Src} → {Size} bytes", sourcePath, sz);
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "In-place re-encode skipped for {Path}", sourcePath);
        }
        finally
        {
            DeleteSafe(inplacePath);
        }

        // ── Pass 2+: Iterative in-memory rasterisation ──
        byte[] pdfBytes;
        try { pdfBytes = File.ReadAllBytes(sourcePath); }
        catch (Exception ex)
        {
            Log.Error(ex, "Cannot read source PDF {Path}", sourcePath);
            return false;
        }

        PdfiumViewer.PdfDocument? pdfiumDoc = null;
        try
        {
            pdfiumDoc = PdfiumViewer.PdfDocument.Load(new MemoryStream(pdfBytes));
            int numPages = pdfiumDoc.PageCount;
            if (numPages == 0) return false;

            long effectiveTarget = targetBytes;
            long kbPerPage = Math.Max(1, effectiveTarget / 1024 / numPages);

            var (startW, startH) = GetInitialCanvasSize(kbPerPage);

            int quality = StartQuality;
            byte[]? bestBytes = null;
            long bestSize = long.MaxValue;
            int lastW = startW;
            int lastQ = quality;

            for (int iter = 0; iter <= 25; iter++)
            {
                int renderWidth  = Math.Max(100, startW - iter * 40);
                int renderHeight = Math.Max(150, startH - iter * 55);
                int jpegQ = Math.Max(20, quality - iter * 3);

                byte[]? rendered = RasterizeToJpegBytes(pdfiumDoc, renderWidth, jpegQ, renderHeight);
                if (rendered != null)
                {
                    long trySize = rendered.Length;

                    if (trySize <= effectiveTarget && trySize >= MinimumFloorBytes)
                    {
                        File.WriteAllBytes(outputPath, rendered);
                        Log.Information(
                            "Built-in iter {N} (W={W} Q={Q}%): {Src} → {Size} bytes (target {T})",
                            iter, renderWidth, jpegQ, Path.GetFileName(sourcePath), trySize, effectiveTarget);
                        return true; // ✅ Target strictly met!
                    }

                    if (trySize < bestSize)
                    {
                        bestBytes = rendered;
                        bestSize = trySize;
                        lastW = renderWidth;
                        lastQ = jpegQ;
                    }
                }

                if (jpegQ <= 20 && renderWidth <= 120) break;
            }

            // ── Mandatory Force-Trim: if bestSize > effectiveTarget, scale resolution down until <= targetBytes ──
            if (bestSize > effectiveTarget)
            {
                int curW = lastW;
                int curQ = lastQ;

                for (int forcePass = 1; forcePass <= 15; forcePass++)
                {
                    double ratio = bestSize > 0 ? (double)effectiveTarget / bestSize : 0.65;
                    double scale = Math.Clamp(Math.Sqrt(ratio) * 0.90, 0.35, 0.85);

                    curW = Math.Max(50, (int)(curW * scale));
                    int curH = (int)(curW * 1.414);
                    curQ = Math.Max(10, curQ - 5);

                    byte[]? fBytes = RasterizeToJpegBytes(pdfiumDoc, curW, curQ, curH);
                    if (fBytes != null)
                    {
                        long fSize = fBytes.Length;
                        if (fSize <= effectiveTarget && fSize >= MinimumFloorBytes)
                        {
                            File.WriteAllBytes(outputPath, fBytes);
                            Log.Information("Force-trim pass {N} (W={W} Q={Q}): {Src} → {Size} bytes (target {T})",
                                forcePass, curW, curQ, Path.GetFileName(sourcePath), fSize, effectiveTarget);
                            return true; // ✅ Target strictly met!
                        }

                        if (fSize < bestSize)
                        {
                            bestBytes = fBytes;
                            bestSize = fSize;
                        }
                    }

                    if (curW <= 50 && curQ <= 10) break;
                }
            }

            // Always write the best compressed result to outputPath
            if (bestBytes != null && bestBytes.Length >= MinimumFloorBytes)
            {
                File.WriteAllBytes(outputPath, bestBytes);
                Log.Information("Built-in final output: {Src} → {Size} bytes (target {T})",
                    Path.GetFileName(sourcePath), bestBytes.Length, effectiveTarget);
                return true;
            }

            return false;
        }
        finally
        {
            pdfiumDoc?.Dispose();
        }
    }

    // ─── Canvas Size Calculation (replicates pi7.org's getSize() exactly) ───

    /// <summary>
    /// Maps a per-page KB budget to an initial canvas size.
    /// Ported directly from pi7.org's <c>getSize()</c> JavaScript function.
    /// </summary>
    private static (int width, int height) GetInitialCanvasSize(long kbPerPage)
    {
        if (kbPerPage < 15) return (350, 550);

        if (kbPerPage <= 35)
        {
            double r = (kbPerPage - 15.0) / 20.0;
            return ((int)(350 + r * 200), (int)(550 + r * 300));
        }
        if (kbPerPage <= 50)
        {
            double r = (kbPerPage - 35.0) / 15.0;
            return ((int)(550 + r * 200), (int)(850 + r * 300));
        }
        if (kbPerPage <= 100)
        {
            double r = (kbPerPage - 50.0) / 50.0;
            return ((int)(750 + r * 500), (int)(1150 + r * 600));
        }
        if (kbPerPage <= 200)
        {
            double r = (kbPerPage - 100.0) / 100.0;
            return ((int)(1250 + r * 500), (int)(1750 + r * 600));
        }

        return (1800, 2400); // > 200 KB/page — full resolution
    }

    // ─── Rasterisation ──────────────────────────────────────────────────────

    /// <summary>
    /// Renders each PDF page via PDFium (clean lossless bitmap) → JPEG directly into memory.
    /// No intermediate encoding needed — PDFium renders from the original vector/image
    /// data, so there is no pre-existing JPEG to stack artifacts on.
    /// </summary>
    private static byte[]? RasterizeToJpegBytes(
        PdfiumViewer.PdfDocument pdfiumDoc,
        int maxWidth,
        int jpegQuality,
        int maxHeight = 0)
    {
        jpegQuality = Math.Max(MinQuality, jpegQuality);

        var jpegCodec = ImageCodecInfo.GetImageEncoders()
            .FirstOrDefault(c => c.FormatID == ImageFormat.Jpeg.Guid);
        if (jpegCodec == null)
        {
            Log.Warning("JPEG codec not found — cannot rasterize");
            return null;
        }

        var encParams = new EncoderParameters(1);
        encParams.Param[0] = new EncoderParameter(
            System.Drawing.Imaging.Encoder.Quality, (long)jpegQuality);

        var xImagesToDispose = new List<XImage>();
        var streamsToDispose = new List<MemoryStream>();

        try
        {
            using var outDoc = new PdfDocument();

            for (int i = 0; i < pdfiumDoc.PageCount; i++)
            {
                var pageSize = pdfiumDoc.PageSizes[i];
                if (pageSize.Width <= 0 || pageSize.Height <= 0) continue;

                double aspect = pageSize.Height / pageSize.Width;

                int renderWidth = maxWidth;
                int renderHeight = (int)(renderWidth * aspect);

                if (maxHeight > 0 && renderHeight > maxHeight)
                {
                    renderHeight = maxHeight;
                    renderWidth  = (int)(renderHeight / aspect);
                }

                renderWidth  = Math.Max(50, renderWidth);
                renderHeight = Math.Max(50, renderHeight);

                int dpi = Math.Clamp((int)(renderWidth / (pageSize.Width / 72.0)), 40, 300);

                // Render page to 32-bit ARGB bitmap via PDFium
                using var bmp = pdfiumDoc.Render(i, renderWidth, renderHeight, dpi, dpi,
                    PdfRenderFlags.Annotations);

                var jpegMs = new MemoryStream();
                streamsToDispose.Add(jpegMs);
                bmp.Save(jpegMs, jpegCodec, encParams);

                byte[] jpegBytes = jpegMs.ToArray();
                var pageMs = new MemoryStream(jpegBytes);
                streamsToDispose.Add(pageMs);

                var page = outDoc.AddPage();
                page.Width  = XUnit.FromPoint(pageSize.Width);
                page.Height = XUnit.FromPoint(pageSize.Height);

                var xImg = XImage.FromStream(() => pageMs);
                xImagesToDispose.Add(xImg);

                using var gfx = XGraphics.FromPdfPage(page);
                gfx.DrawImage(xImg, 0, 0, page.Width.Point, page.Height.Point);
            }

            using var outMs = new MemoryStream();
            outDoc.Save(outMs);
            byte[] finalBytes = outMs.ToArray();

            return finalBytes.Length >= MinimumFloorBytes ? finalBytes : null;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "RasterizeToJpegBytes failed W={W} Q={Q}", maxWidth, jpegQuality);
            return null;
        }
        finally
        {
            encParams.Dispose();
            foreach (var img in xImagesToDispose) try { img.Dispose(); } catch { }
            foreach (var ms in streamsToDispose) try { ms.Dispose(); } catch { }
        }
    }

    // ─── In-Place Re-Encode ──────────────────────────────────────────────────

    /// <summary>
    /// Re-compresses embedded bitmap images inside the PDF at lower quality
    /// WITHOUT rasterizing the whole page — vector text and fonts are untouched.
    /// </summary>
    private static bool TryInPlaceReencode(string sourcePath, string outputPath)
    {
        try
        {
            using var document = PdfReader.Open(sourcePath, PdfDocumentOpenMode.Modify);

            // Strip metadata
            document.Info.Author   = string.Empty;
            document.Info.Creator  = string.Empty;
            document.Info.Keywords = string.Empty;
            document.Info.Subject  = string.Empty;

            // Re-compress embedded images
            for (int i = 0; i < document.PageCount; i++)
            {
                var page      = document.Pages[i];
                var resources = page.Elements.GetDictionary("/Resources");
                if (resources == null) continue;
                var xObjects  = resources.Elements.GetDictionary("/XObject");
                if (xObjects  == null) continue;

                foreach (var key in xObjects.Elements.Keys.ToList())
                {
                    try
                    {
                        if (xObjects.Elements[key] is not PdfReference rf) continue;
                        if (rf.Value is not PdfDictionary xObj) continue;
                        if (xObj.Elements.GetString("/Subtype") != "/Image") continue;

                        var stream = xObj.Stream;
                        if (stream?.Value == null || stream.Value.Length < 4 * 1024) continue;

                        int w = xObj.Elements.GetInteger("/Width");
                        int h = xObj.Elements.GetInteger("/Height");
                        if (w <= 0 || h <= 0) continue;

                        byte[] rawBytes = stream.UnfilteredValue ?? stream.Value;
                        using var img = Image.Load(rawBytes);

                        // Downscale if very large
                        if (w > 1200)
                        {
                            int newW = 1200;
                            int newH = (int)(h * (1200.0 / w));
                            img.Mutate(ctx => ctx.Resize(newW, newH, KnownResamplers.Lanczos3));
                            w = newW; h = newH;
                        }

                        using var ms = new MemoryStream();
                        img.SaveAsJpeg(ms, new JpegEncoder { Quality = 75 });
                        byte[] compressed = ms.ToArray();

                        if (compressed.Length < stream.Value.Length * 0.85)
                        {
                            stream.Value = compressed;
                            xObj.Elements.SetInteger("/Width",  w);
                            xObj.Elements.SetInteger("/Height", h);
                            xObj.Elements.SetName("/Filter", "/DCTDecode");
                            xObj.Elements.Remove("/DecodeParms");
                        }
                    }
                    catch { }
                }
            }

            document.Save(outputPath);
            return File.Exists(outputPath) && new FileInfo(outputPath).Length > MinimumFloorBytes;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "TryInPlaceReencode failed for {Path}", sourcePath);
            return false;
        }
    }

    // ─── Helpers ────────────────────────────────────────────────────────────

    public int GetPageCount(string sourcePath)
    {
        try
        {
            using var doc = PdfiumViewer.PdfDocument.Load(sourcePath);
            return doc.PageCount;
        }
        catch { return 1; }
    }

    private static void DeleteSafe(string? path)
    {
        if (!string.IsNullOrEmpty(path))
            try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
