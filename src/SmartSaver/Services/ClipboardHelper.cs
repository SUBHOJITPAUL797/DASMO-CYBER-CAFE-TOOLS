using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media.Imaging;
using Serilog;
using WpfClipboard = System.Windows.Clipboard;
using WpfApplication = System.Windows.Application;
using WfClipboard = System.Windows.Forms.Clipboard;
using IDataObject = System.Windows.IDataObject;
using DataFormats = System.Windows.DataFormats;

namespace SmartSaver.Services;

/// <summary>
/// Resilient multi-format clipboard helper that extracts images without throwing
/// COMException 0x800401D3 (CLIPBRD_E_BAD_DATA) from Phone Link, browsers, or UWP apps.
/// </summary>
public static class ClipboardHelper
{
    /// <summary>
    /// Safely extracts an image from the Windows clipboard, handling modern PNG/JPEG streams,
    /// Phone Link temporary file drops, and GDI+ DIBv5 headers with automatic retry protection.
    /// Saves the image to a temporary PNG file and returns the file path, or null if no image found.
    /// </summary>
    public static string? SaveClipboardImageToFile()
    {
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                // 1. Check IDataObject for modern PNG stream (Phone Link / Edge / Chrome native lossless format)
                try
                {
                    IDataObject? dataObj = WpfClipboard.GetDataObject();
                    if (dataObj != null)
                    {
                        // Check for PNG stream
                        if (dataObj.GetDataPresent("PNG", true) || dataObj.GetDataPresent("image/png", true))
                        {
                            object? pngObj = dataObj.GetData("PNG") ?? dataObj.GetData("image/png");
                            if (pngObj is MemoryStream ms && ms.Length > 0)
                            {
                                string outPath = CreateTempPngPath("clipboard_png");
                                File.WriteAllBytes(outPath, ms.ToArray());
                                Log.Information("Extracted clipboard image via PNG stream: {Path}", outPath);
                                return outPath;
                            }
                        }

                        // Check for FileDrop (Phone Link often copies local cached files)
                        if (dataObj.GetDataPresent(DataFormats.FileDrop))
                        {
                            if (dataObj.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
                            {
                                foreach (var file in files)
                                {
                                    if (File.Exists(file))
                                    {
                                        string ext = Path.GetExtension(file).ToLowerInvariant();
                                        if (ext is ".jpg" or ".jpeg" or ".png" or ".webp" or ".bmp" or ".tiff")
                                        {
                                            Log.Information("Extracted clipboard image via FileDrop: {Path}", file);
                                            return file;
                                        }
                                    }
                                }
                            }
                        }

                        // Check for JFIF / JPEG stream
                        if (dataObj.GetDataPresent("JFIF", true) || dataObj.GetDataPresent("image/jpeg", true))
                        {
                            object? jfifObj = dataObj.GetData("JFIF") ?? dataObj.GetData("image/jpeg");
                            if (jfifObj is MemoryStream ms && ms.Length > 0)
                            {
                                string outPath = CreateTempPngPath("clipboard_jfif");
                                File.WriteAllBytes(outPath, ms.ToArray());
                                Log.Information("Extracted clipboard image via JPEG stream: {Path}", outPath);
                                return outPath;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Debug(ex, "Attempt {Attempt}: Failed to read WPF IDataObject stream", attempt);
                }

                // 2. Try Windows Forms Clipboard (GDI+ reader handles 32-bit DIBv5 headers cleanly without CLIPBRD_E_BAD_DATA)
                try
                {
                    if (WfClipboard.ContainsImage())
                    {
                        using var wfImage = WfClipboard.GetImage();
                        if (wfImage != null)
                        {
                            string outPath = CreateTempPngPath("clipboard_gdi");
                            wfImage.Save(outPath, System.Drawing.Imaging.ImageFormat.Png);
                            Log.Information("Extracted clipboard image via Windows Forms GDI+: {Path}", outPath);
                            return outPath;
                        }
                    }

                    if (WfClipboard.ContainsFileDropList())
                    {
                        var files = WfClipboard.GetFileDropList();
                        if (files != null && files.Count > 0)
                        {
                            foreach (string? file in files)
                            {
                                if (!string.IsNullOrEmpty(file) && File.Exists(file))
                                {
                                    string ext = Path.GetExtension(file).ToLowerInvariant();
                                    if (ext is ".jpg" or ".jpeg" or ".png" or ".webp" or ".bmp" or ".tiff")
                                    {
                                        Log.Information("Extracted clipboard image via WinForms FileDrop: {Path}", file);
                                        return file;
                                    }
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Debug(ex, "Attempt {Attempt}: Failed to read WinForms clipboard", attempt);
                }

                // 3. Fallback: Standard WPF Clipboard.GetImage()
                try
                {
                    if (WpfClipboard.ContainsImage())
                    {
                        var bmpSource = WpfClipboard.GetImage();
                        if (bmpSource != null)
                        {
                            string outPath = CreateTempPngPath("clipboard_wpf");
                            var encoder = new PngBitmapEncoder();
                            encoder.Frames.Add(BitmapFrame.Create(bmpSource));
                            using (var fs = File.OpenWrite(outPath))
                            {
                                encoder.Save(fs);
                            }
                            Log.Information("Extracted clipboard image via WPF BitmapSource: {Path}", outPath);
                            return outPath;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Debug(ex, "Attempt {Attempt}: Failed to read WPF Clipboard.GetImage()", attempt);
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Clipboard read attempt {Attempt} encountered exception", attempt);
            }

            // Brief pause before retry in case clipboard was momentarily locked by source app
            Thread.Sleep(50);
        }

        return null;
    }

    private static string CreateTempPngPath(string prefix)
    {
        string dir = Path.Combine(Path.GetTempPath(), "DASMO_Clipboard");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, $"{prefix}_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}.png");
    }

    /// <summary>
    /// Copies an image with true 32-bit alpha channel transparency to the Windows clipboard.
    /// Writes multiple clipboard formats to ensure 100% compatibility across all software:
    /// 1. "PNG" and "image/png" streams for modern browsers, Discord, Photoshop CC, and web apps.
    /// 2. DeviceIndependentBitmap (CF_DIB) with standard BITMAPV5HEADER and bV5AlphaMask = 0xFF000000
    ///    for desktop applications like MS Word, Photoshop CS6/CC, CorelDRAW, PageMaker, and PVC printer software.
    /// 3. Standard fallback BitmapSource for legacy Windows controls.
    /// When pasted anywhere, ONLY the pure isolated signature ink appears with ZERO white box!
    /// </summary>
    public static bool CopyTransparentBitmapToClipboard(BitmapSource? bmpSource, byte[]? pngBytes = null)
    {
        if (bmpSource == null && (pngBytes == null || pngBytes.Length == 0))
            return false;

        for (int attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                var dataObj = new System.Windows.DataObject();

                // 1. If pngBytes is null, encode bmpSource as PNG
                if ((pngBytes == null || pngBytes.Length == 0) && bmpSource != null)
                {
                    using var ms = new MemoryStream();
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bmpSource));
                    encoder.Save(ms);
                    pngBytes = ms.ToArray();
                }

                // 2. Set PNG stream formats
                if (pngBytes != null && pngBytes.Length > 0)
                {
                    dataObj.SetData("PNG", new MemoryStream(pngBytes), false);
                    dataObj.SetData("image/png", new MemoryStream(pngBytes), false);
                }

                // 3. Create 32-bit DIBv5 with Alpha Mask (0xFF000000) for Word, Photoshop, CorelDRAW
                if (bmpSource != null)
                {
                    try
                    {
                        var formatted = new FormatConvertedBitmap(bmpSource, System.Windows.Media.PixelFormats.Bgra32, null, 0);
                        int width = formatted.PixelWidth;
                        int height = formatted.PixelHeight;
                        int stride = width * 4;
                        byte[] pixels = new byte[stride * height];
                        formatted.CopyPixels(pixels, stride, 0);

                        var dibMs = new MemoryStream(124 + pixels.Length);
                        using (var writer = new BinaryWriter(dibMs, System.Text.Encoding.UTF8, leaveOpen: true))
                        {
                            writer.Write((uint)124); // bV5Size
                            writer.Write(width);     // bV5Width
                            writer.Write(height);    // bV5Height (positive = bottom-up)
                            writer.Write((ushort)1); // bV5Planes
                            writer.Write((ushort)32); // bV5BitCount
                            writer.Write((uint)3);   // bV5Compression = BI_BITFIELDS
                            writer.Write((uint)(width * height * 4)); // bV5SizeImage
                            writer.Write((int)Math.Round(bmpSource.DpiX * 39.3701)); // bV5XPelsPerMeter
                            writer.Write((int)Math.Round(bmpSource.DpiY * 39.3701)); // bV5YPelsPerMeter
                            writer.Write((uint)0);   // bV5ClrUsed
                            writer.Write((uint)0);   // bV5ClrImportant
                            writer.Write((uint)0x00FF0000); // bV5RedMask
                            writer.Write((uint)0x0000FF00); // bV5GreenMask
                            writer.Write((uint)0x000000FF); // bV5BlueMask
                            writer.Write((uint)0xFF000000); // bV5AlphaMask
                            writer.Write((uint)0x73524742); // bV5CSType = 'sRGB'
                            for (int i = 0; i < 9; i++) writer.Write((uint)0); // Endpoints
                            writer.Write((uint)0); // GammaRed
                            writer.Write((uint)0); // GammaGreen
                            writer.Write((uint)0); // GammaBlue
                            writer.Write((uint)4); // Intent = LCS_GM_IMAGES
                            writer.Write((uint)0); // ProfileData
                            writer.Write((uint)0); // ProfileSize
                            writer.Write((uint)0); // Reserved

                            // Bottom-up pixel rows
                            for (int y = height - 1; y >= 0; y--)
                            {
                                dibMs.Write(pixels, y * stride, stride);
                            }
                        }
                        dibMs.Position = 0;
                        dataObj.SetData(DataFormats.Dib, dibMs);
                    }
                    catch (Exception ex)
                    {
                        Log.Debug(ex, "Could not generate DIBv5 stream for clipboard");
                    }

                    // 4. Fallback standard image
                    dataObj.SetImage(bmpSource);
                }

                WpfClipboard.SetDataObject(dataObj, true);
                Log.Information("Copied transparent bitmap to clipboard with PNG and DIBv5 alpha formats");
                return true;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Attempt {Attempt}: Failed to set transparent clipboard data", attempt);
                Thread.Sleep(60);
            }
        }

        return false;
    }
}
