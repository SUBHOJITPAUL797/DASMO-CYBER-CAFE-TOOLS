using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Windows.Media.Imaging;
using Serilog;
using SixLabors.ImageSharp.Processing;

namespace SmartSaver.Helpers;

public static class ImageHelper
{
    public const int ExifOrientationId = 0x0112;

    /// <summary>
    /// Checks EXIF orientation tag (0x0112) commonly produced by smartphones (iPhone, Samsung, Android)
    /// and permanently normalizes pixel rotation so that the image is rendered right-side up.
    /// Removes the EXIF tag after applying rotation so subsequent readers do not rotate it again.
    /// </summary>
    public static bool NormalizeExifOrientation(Image img)
    {
        if (img == null) return false;

        try
        {
            if (!img.PropertyIdList.Contains(ExifOrientationId))
                return false;

            var prop = img.GetPropertyItem(ExifOrientationId);
            if (prop?.Value == null || prop.Value.Length < 2)
                return false;

            ushort orientation = BitConverter.ToUInt16(prop.Value, 0);
            RotateFlipType flip = orientation switch
            {
                2 => RotateFlipType.RotateNoneFlipX,
                3 => RotateFlipType.Rotate180FlipNone,
                4 => RotateFlipType.Rotate180FlipX,
                5 => RotateFlipType.Rotate90FlipX,
                6 => RotateFlipType.Rotate90FlipNone,
                7 => RotateFlipType.Rotate270FlipX,
                8 => RotateFlipType.Rotate270FlipNone,
                _ => RotateFlipType.RotateNoneFlipNone
            };

            if (flip != RotateFlipType.RotateNoneFlipNone)
            {
                img.RotateFlip(flip);
                try
                {
                    img.RemovePropertyItem(ExifOrientationId);
                }
                catch
                {
                    // Some encoders may throw if read-only property item; safely ignore
                }
                return true;
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not normalize EXIF orientation");
        }

        return false;
    }

    /// <summary>
    /// Loads an image file completely decoupled from file handles, corrects EXIF orientation,
    /// and returns a clean 32bppArgb Bitmap.
    /// Supports ALL photo formats: standard JPG/JPEG, progressive/CMYK JPEG, JFIF, PNG, WebP, BMP, GIF, TIFF.
    /// </summary>
    public static Bitmap LoadOrientedBitmap(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            throw new FileNotFoundException($"Image file not found: {filePath}");

        byte[] bytes = File.ReadAllBytes(filePath);
        if (bytes.Length == 0)
            throw new InvalidOperationException($"Image file is empty (0 bytes): {filePath}");

        return LoadOrientedBitmap(bytes, filePath);
    }

    /// <summary>
    /// Decodes image bytes with automatic fallback to SixLabors.ImageSharp universal decoder.
    /// Decodes any format (including WebP, CMYK JPEGs, TIFF, etc.) into a clean 32bppArgb GDI+ Bitmap.
    /// </summary>
    public static Bitmap LoadOrientedBitmap(byte[] bytes, string? sourcePathForLogging = null)
    {
        if (bytes == null || bytes.Length == 0)
            throw new ArgumentException("Image byte array cannot be null or empty", nameof(bytes));

        // 1. First attempt fast GDI+ load
        try
        {
            using var ms = new MemoryStream(bytes);
            using var rawBmp = new Bitmap(ms);

            NormalizeExifOrientation(rawBmp);

            var safeBmp = new Bitmap(rawBmp.Width, rawBmp.Height, PixelFormat.Format32bppArgb);
            safeBmp.SetResolution(rawBmp.HorizontalResolution, rawBmp.VerticalResolution);
            using (var g = Graphics.FromImage(safeBmp))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.SmoothingMode = SmoothingMode.HighQuality;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(rawBmp, 0, 0, rawBmp.Width, rawBmp.Height);
            }
            return safeBmp;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Standard GDI+ load failed for {Path}. Falling back to SixLabors.ImageSharp universal decoder...", sourcePathForLogging ?? "bytes");
        }

        // 2. Universal ImageSharp decoder fallback:
        // Flawlessly decodes: WebP, CMYK JPEGs, progressive JPEGs, JFIF, BMP, TIFF, GIF, etc.
        try
        {
            using var isImg = SixLabors.ImageSharp.Image.Load<SixLabors.ImageSharp.PixelFormats.Rgba32>(bytes);
            isImg.Mutate(ctx => SixLabors.ImageSharp.Processing.AutoOrientExtensions.AutoOrient(ctx));

            using var pngMs = new MemoryStream();
            SixLabors.ImageSharp.ImageExtensions.SaveAsPng(isImg, pngMs);
            pngMs.Position = 0;

            using var decodedBmp = new Bitmap(pngMs);
            var safeBmp = new Bitmap(decodedBmp.Width, decodedBmp.Height, PixelFormat.Format32bppArgb);
            
            float dpiX = isImg.Metadata.HorizontalResolution > 0 ? (float)isImg.Metadata.HorizontalResolution : 300f;
            float dpiY = isImg.Metadata.VerticalResolution > 0 ? (float)isImg.Metadata.VerticalResolution : 300f;
            safeBmp.SetResolution(dpiX, dpiY);

            using (var g = Graphics.FromImage(safeBmp))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.SmoothingMode = SmoothingMode.HighQuality;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(decodedBmp, 0, 0, decodedBmp.Width, decodedBmp.Height);
            }
            return safeBmp;
        }
        catch (Exception isEx)
        {
            Log.Error(isEx, "Both GDI+ and ImageSharp universal decoder failed to open {Path}", sourcePathForLogging ?? "bytes");
            throw new InvalidOperationException($"Could not open photo format ({isEx.Message}). Please verify the file is a valid image.", isEx);
        }
    }

    /// <summary>
    /// Loads an image file into a WPF BitmapImage with EXIF orientation corrected.
    /// </summary>
    public static BitmapImage LoadOrientedBitmapImage(string filePath)
    {
        using var orientedBmp = LoadOrientedBitmap(filePath);
        using var ms = new MemoryStream();
        orientedBmp.Save(ms, ImageFormat.Png);
        ms.Seek(0, SeekOrigin.Begin);

        var bi = new BitmapImage();
        bi.BeginInit();
        bi.CacheOption = BitmapCacheOption.OnLoad;
        bi.StreamSource = ms;
        bi.EndInit();
        bi.Freeze();
        return bi;
    }
}
