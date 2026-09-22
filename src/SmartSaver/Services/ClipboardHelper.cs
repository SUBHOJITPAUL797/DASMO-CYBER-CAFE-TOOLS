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
}
