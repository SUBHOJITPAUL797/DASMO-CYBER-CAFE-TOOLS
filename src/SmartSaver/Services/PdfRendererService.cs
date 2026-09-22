using System;
using System.Collections.Generic;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using PdfiumViewer;
using Serilog;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;

namespace SmartSaver.Services;

public class PdfRenderResult
{
    public List<BitmapSource> Pages { get; set; } = new();
    public bool IsPasswordProtected { get; set; }
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
}

public static class PdfRendererService
{
    private static bool _dllResolverRegistered;

    static PdfRendererService()
    {
        EnsureDllResolver();
    }

    public static void EnsureDllResolver()
    {
        if (_dllResolverRegistered) return;
        try
        {
            NativeLibrary.SetDllImportResolver(typeof(PdfiumViewer.PdfDocument).Assembly, (libraryName, assembly, searchPath) =>
            {
                if (libraryName.Equals("pdfium", StringComparison.OrdinalIgnoreCase) ||
                    libraryName.Equals("pdfium.dll", StringComparison.OrdinalIgnoreCase) ||
                    libraryName.Contains("pdfium", StringComparison.OrdinalIgnoreCase))
                {
                    string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                    string path1 = Path.Combine(baseDir, "pdfium.dll");
                    if (File.Exists(path1) && NativeLibrary.TryLoad(path1, out IntPtr handle1))
                        return handle1;

                    string path2 = Path.Combine(baseDir, "x64", "pdfium.dll");
                    if (File.Exists(path2) && NativeLibrary.TryLoad(path2, out IntPtr handle2))
                        return handle2;

                    string path3 = Path.Combine(baseDir, "runtimes", "win-x64", "native", "pdfium.dll");
                    if (File.Exists(path3) && NativeLibrary.TryLoad(path3, out IntPtr handle3))
                        return handle3;
                }
                return IntPtr.Zero;
            });
            _dllResolverRegistered = true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not register NativeLibrary resolver for pdfium.dll");
        }
    }

    /// <summary>
    /// Converts a GDI+ Bitmap to a WPF BitmapSource by flattening it onto a white background
    /// (PDFium renders with transparent background which appears blank/black on dark UIs)
    /// and encoding via PNG MemoryStream (avoids GetHbitmap DPI/alpha issues).
    /// </summary>
    public static BitmapSource ToBitmapSource(System.Drawing.Bitmap bitmap)
    {
        // Step 1: Flatten onto a white background to eliminate transparency
        using var flattened = new System.Drawing.Bitmap(bitmap.Width, bitmap.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = System.Drawing.Graphics.FromImage(flattened))
        {
            g.Clear(System.Drawing.Color.White);
            g.DrawImage(bitmap, 0, 0, bitmap.Width, bitmap.Height);
        }

        // Step 2: Encode to PNG in memory and create BitmapSource from stream
        using var ms = new MemoryStream();
        flattened.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
        ms.Position = 0;

        var bitmapImage = new BitmapImage();
        bitmapImage.BeginInit();
        bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
        bitmapImage.StreamSource = ms;
        bitmapImage.EndInit();
        bitmapImage.Freeze();
        return bitmapImage;
    }

    /// <summary>
    /// Fast page thumbnail rendering at specified width (default 240px) on pure white background.
    /// Returns a frozen BitmapSource that can safely be bound directly to WPF Image controls.
    /// </summary>
    public static BitmapSource? RenderPageThumbnail(string pdfPath, int pageIndex, int targetWidth = 240, string? password = null)
    {
        try
        {
            if (!File.Exists(pdfPath)) return null;

            EnsureDllResolver();
            byte[] pdfBytes = File.ReadAllBytes(pdfPath);
            using var stream = new MemoryStream(pdfBytes);
            using var document = string.IsNullOrEmpty(password)
                ? PdfiumViewer.PdfDocument.Load(stream)
                : PdfiumViewer.PdfDocument.Load(stream, password);

            if (pageIndex < 0 || pageIndex >= document.PageCount) return null;

            var pageSize = document.PageSizes[pageIndex];
            int targetHeight = pageSize.Width > 0 ? (int)(targetWidth * (pageSize.Height / pageSize.Width)) : (int)(targetWidth * 1.414);
            if (targetHeight <= 0) targetHeight = (int)(targetWidth * 1.414);

            using var image = (System.Drawing.Bitmap)document.Render(pageIndex, targetWidth, targetHeight, 72, 72, PdfRenderFlags.Annotations);
            return ToBitmapSource(image);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not render thumbnail for page {Page} of {Path}", pageIndex, pdfPath);
            return null;
        }
    }

    /// <summary>
    /// Renders all page thumbnails for a PDF document efficiently in a single document load.
    /// </summary>
    public static List<BitmapSource?> RenderAllThumbnails(string pdfPath, int targetWidth = 240, string? password = null)
    {
        var list = new List<BitmapSource?>();
        try
        {
            if (!File.Exists(pdfPath)) return list;

            EnsureDllResolver();
            byte[] pdfBytes = File.ReadAllBytes(pdfPath);
            using var stream = new MemoryStream(pdfBytes);
            using var document = string.IsNullOrEmpty(password)
                ? PdfiumViewer.PdfDocument.Load(stream)
                : PdfiumViewer.PdfDocument.Load(stream, password);

            for (int i = 0; i < document.PageCount; i++)
            {
                var pageSize = document.PageSizes[i];
                int targetHeight = pageSize.Width > 0 ? (int)(targetWidth * (pageSize.Height / pageSize.Width)) : (int)(targetWidth * 1.414);
                if (targetHeight <= 0) targetHeight = (int)(targetWidth * 1.414);

                using var image = (System.Drawing.Bitmap)document.Render(i, targetWidth, targetHeight, 72, 72, PdfRenderFlags.Annotations);
                list.Add(ToBitmapSource(image));
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not render all thumbnails for {Path}", pdfPath);
        }

        if (list.Count == 0 && File.Exists(pdfPath))
        {
            try
            {
                var winRtTask = Task.Run(() => RenderPdfPagesWinRTResultAsync(pdfPath, password, 500));
                if (winRtTask.Wait(TimeSpan.FromSeconds(2)))
                {
                    var winRtRes = winRtTask.Result;
                    if (winRtRes.Success && winRtRes.Pages.Count > 0)
                    {
                        foreach (var p in winRtRes.Pages) list.Add(p);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "WinRT thumbnail fallback also failed for {Path}", pdfPath);
            }
        }

        return list;
    }

    public static async Task<PdfRenderResult> RenderPdfPagesResultAsync(string pdfPath, string? password = null, int maxPages = 20)
    {
        var result = new PdfRenderResult();
        try
        {
            if (!File.Exists(pdfPath)) return result;

            // Primary attempt: PDFium
            try
            {
                await Task.Run(() =>
                {
                    byte[] pdfBytes = File.ReadAllBytes(pdfPath);
                    Log.Information("PDFium: Read {ByteCount} bytes from {PdfPath}", pdfBytes.Length, pdfPath);
                    using var stream = new MemoryStream(pdfBytes);
                    using var document = string.IsNullOrEmpty(password)
                        ? PdfiumViewer.PdfDocument.Load(stream)
                        : PdfiumViewer.PdfDocument.Load(stream, password);

                    int count = Math.Min(document.PageCount, maxPages);
                    Log.Information("PDFium: Loaded document with {PageCount} pages, rendering {RenderCount} pages", document.PageCount, count);
                    for (int i = 0; i < count; i++)
                    {
                        // Use fixed-width rendering (max 1600px wide) to avoid enormous bitmaps on high-DPI displays
                        var pageSize = document.PageSizes[i];
                        int targetWidth = 1600;
                        int targetHeight = pageSize.Width > 0 ? (int)(targetWidth * (pageSize.Height / pageSize.Width)) : 2262;
                        if (targetHeight <= 0) targetHeight = 2262;
                        using var image = (System.Drawing.Bitmap)document.Render(i, targetWidth, targetHeight, 96, 96, PdfRenderFlags.Annotations);
                        Log.Information("PDFium: Page {PageIndex} rendered as {Width}x{Height} bitmap (target {TargetW}x{TargetH})", i, image.Width, image.Height, targetWidth, targetHeight);
                        var bmp = ToBitmapSource(image);
                        Log.Information("PDFium: Page {PageIndex} converted to BitmapSource {Width}x{Height}", i, bmp.PixelWidth, bmp.PixelHeight);
                        result.Pages.Add(bmp);
                    }
                });

                if (result.Pages.Count > 0)
                {
                    result.Success = true;
                    Log.Information("PDFium: Successfully rendered {PageCount} pages for {PdfPath}", result.Pages.Count, pdfPath);
                    return result;
                }
            }
            catch (Exception pdfiumEx)
            {
                Log.Warning(pdfiumEx, "PDFium failed to render pages for {PdfPath}", pdfPath);
                string msg = pdfiumEx.Message.ToLowerInvariant();
                if (msg.Contains("password") || msg.Contains("encrypt") || msg.Contains("protected") || pdfiumEx is PdfException)
                {
                    result.IsPasswordProtected = true;
                }
            }

            // Fallback attempt: WinRT Windows.Data.Pdf
            return await RenderPdfPagesWinRTResultAsync(pdfPath, password, maxPages, result.IsPasswordProtected);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to render PDF pages for: {PdfPath}", pdfPath);
        }
        return result;
    }

    public static async Task<List<BitmapSource>> RenderPdfPagesAsync(string pdfPath, int maxPages = 10)
    {
        var res = await RenderPdfPagesResultAsync(pdfPath, null, maxPages);
        return res.Pages;
    }

    public static async Task<BitmapSource?> RenderPdfPageAsync(string pdfPath, int pageIndex = 0)
    {
        try
        {
            if (!File.Exists(pdfPath)) return null;

            // Primary attempt: PDFium
            try
            {
                var bitmap = await Task.Run(() =>
                {
                    byte[] pdfBytes = File.ReadAllBytes(pdfPath);
                    using var stream = new MemoryStream(pdfBytes);
                    using var document = PdfiumViewer.PdfDocument.Load(stream);
                    if (document.PageCount == 0) return null;

                    int safeIndex = Math.Clamp(pageIndex, 0, document.PageCount - 1);
                    // Use fixed-width rendering (max 1600px wide) to avoid enormous bitmaps on high-DPI displays
                    var pageSize = document.PageSizes[safeIndex];
                    int targetWidth = 1600;
                    int targetHeight = pageSize.Width > 0 ? (int)(targetWidth * (pageSize.Height / pageSize.Width)) : 2262;
                    if (targetHeight <= 0) targetHeight = 2262;
                    using var image = (System.Drawing.Bitmap)document.Render(safeIndex, targetWidth, targetHeight, 96, 96, PdfRenderFlags.Annotations);
                    return ToBitmapSource(image);
                });

                if (bitmap != null) return bitmap;
            }
            catch (Exception pdfiumEx)
            {
                Log.Warning(pdfiumEx, "PDFium failed single page render for {PdfPath}, attempting WinRT PDF fallback", pdfPath);
            }

            // Fallback attempt: WinRT Windows.Data.Pdf
            return await RenderPdfPageWinRTAsync(pdfPath, pageIndex);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to render single PDF page {PageIndex} for: {PdfPath}", pageIndex, pdfPath);
            return null;
        }
    }

    private static async Task<PdfRenderResult> RenderPdfPagesWinRTResultAsync(string pdfPath, string? password = null, int maxPages = 20, bool initialPasswordProtected = false)
    {
        var result = new PdfRenderResult { IsPasswordProtected = initialPasswordProtected };
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(pdfPath));
            var pdfDoc = string.IsNullOrEmpty(password)
                ? await Windows.Data.Pdf.PdfDocument.LoadFromFileAsync(file)
                : await Windows.Data.Pdf.PdfDocument.LoadFromFileAsync(file, password);

            if (pdfDoc.IsPasswordProtected && string.IsNullOrEmpty(password))
            {
                result.IsPasswordProtected = true;
                return result;
            }

            uint count = Math.Min(pdfDoc.PageCount, (uint)maxPages);
            for (uint i = 0; i < count; i++)
            {
                using var page = pdfDoc.GetPage(i);
                using var stream = new InMemoryRandomAccessStream();
                await page.RenderToStreamAsync(stream);

                using var ms = new MemoryStream();
                await stream.AsStreamForRead().CopyToAsync(ms);
                ms.Position = 0;

                var bitmap = System.Windows.Media.Imaging.BitmapFrame.Create(
                    ms,
                    BitmapCreateOptions.None,
                    BitmapCacheOption.OnLoad);
                bitmap.Freeze();

                result.Pages.Add(bitmap);
            }

            if (result.Pages.Count > 0)
            {
                result.Success = true;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "WinRT PDF multi-page rendering failed for {PdfPath}", pdfPath);
            string msg = ex.Message.ToLowerInvariant();
            if (msg.Contains("password") || msg.Contains("encrypt") || ex.HResult == unchecked((int)0x80070005) || ex.HResult == unchecked((int)0x8007052B))
            {
                result.IsPasswordProtected = true;
            }
        }
        return result;
    }

    private static async Task<BitmapSource?> RenderPdfPageWinRTAsync(string pdfPath, int pageIndex)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(pdfPath));
            var pdfDoc = await Windows.Data.Pdf.PdfDocument.LoadFromFileAsync(file);
            if (pdfDoc.PageCount == 0) return null;

            uint safeIndex = (uint)Math.Clamp(pageIndex, 0, (int)pdfDoc.PageCount - 1);
            using var page = pdfDoc.GetPage(safeIndex);

            using var stream = new InMemoryRandomAccessStream();
            await page.RenderToStreamAsync(stream);

            using var ms = new MemoryStream();
            await stream.AsStreamForRead().CopyToAsync(ms);
            ms.Position = 0;

            var bitmap = System.Windows.Media.Imaging.BitmapFrame.Create(
                ms,
                BitmapCreateOptions.None,
                BitmapCacheOption.OnLoad);
            bitmap.Freeze();

            return bitmap;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "WinRT PDF single-page rendering failed for {PdfPath}", pdfPath);
            return null;
        }
    }
}

