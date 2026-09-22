using System.IO;
using Serilog;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;

namespace SmartSaver.Services;

public enum ScanFilterType
{
    MagicWhite,         // Whitens paper background while preserving color stamps & signatures
    XeroxBlackAndWhite, // High contrast crisp Black & White (like a Xerox photocopier)
    GrayscaleClean      // Clean grayscale document with boosted text contrast
}

public class ScanEnhancerService
{
    private readonly ImageCompressor _imageCompressor;

    public ScanEnhancerService(ImageCompressor imageCompressor)
    {
        _imageCompressor = imageCompressor;
    }

    /// <summary>
    /// Generates a fast in-memory preview JPEG for the UI live preview.
    /// </summary>
    public byte[]? GeneratePreviewBytes(string sourcePath, ScanFilterType filterType, int intensity = 80, int maxDimension = 900)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath)) return null;

        try
        {
            using var image = SixLabors.ImageSharp.Image.Load<Rgba32>(sourcePath);
            image.Mutate(ctx =>
            {
                ctx.AutoOrient();
                if (image.Width > maxDimension || image.Height > maxDimension)
                {
                    ctx.Resize(new ResizeOptions
                    {
                        Mode = ResizeMode.Max,
                        Size = new SixLabors.ImageSharp.Size(maxDimension, maxDimension)
                    });
                }
            });

            switch (filterType)
            {
                case ScanFilterType.MagicWhite:
                    ApplyMagicWhite(image, intensity);
                    break;
                case ScanFilterType.XeroxBlackAndWhite:
                    ApplyXeroxBlackAndWhite(image, intensity);
                    break;
                case ScanFilterType.GrayscaleClean:
                    ApplyGrayscaleClean(image, intensity);
                    break;
            }

            using var ms = new MemoryStream();
            image.Save(ms, new JpegEncoder { Quality = 85 });
            return ms.ToArray();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to generate enhancer preview for {Path}", sourcePath);
            return null;
        }
    }

    /// <summary>
    /// Enhances a camera-scanned document with background whitening, intensity control, and text clarity.
    /// </summary>
    public bool EnhanceDocument(
        string sourcePath,
        string outputPath,
        ScanFilterType filterType,
        int intensity = 80,
        long? targetBytes = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        if (!File.Exists(sourcePath)) return false;

        string tempPath = Path.Combine(Path.GetTempPath(), $"smartsaver_enhance_{Guid.NewGuid():N}{Path.GetExtension(outputPath)}");

        try
        {
            using (var image = SixLabors.ImageSharp.Image.Load<Rgba32>(sourcePath))
            {
                image.Mutate(ctx => ctx.AutoOrient());

                switch (filterType)
                {
                    case ScanFilterType.MagicWhite:
                        ApplyMagicWhite(image, intensity);
                        break;

                    case ScanFilterType.XeroxBlackAndWhite:
                        ApplyXeroxBlackAndWhite(image, intensity);
                        break;

                    case ScanFilterType.GrayscaleClean:
                        ApplyGrayscaleClean(image, intensity);
                        break;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(tempPath)!);
                string ext = Path.GetExtension(outputPath).ToLowerInvariant();

                if (ext == ".png")
                {
                    image.Save(tempPath, new PngEncoder());
                }
                else
                {
                    image.Save(tempPath, new JpegEncoder { Quality = 90 });
                }
            }

            if (!File.Exists(tempPath) || new FileInfo(tempPath).Length == 0)
                return false;

            FileWatcherService.IgnoreOutputFile(outputPath);

            if (targetBytes.HasValue && targetBytes.Value > 0)
            {
                bool compressed = _imageCompressor.CompressToTargetSize(tempPath, outputPath, targetBytes.Value);
                if (compressed && File.Exists(outputPath))
                {
                    Log.Information("Enhanced and compressed scan {Src} -> {Out}", sourcePath, outputPath);
                    return true;
                }
            }

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            File.Move(tempPath, outputPath, overwrite: true);
            Log.Information("Enhanced scan {Src} -> {Out} (Intensity={Int})", sourcePath, outputPath, intensity);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to enhance document scan: {Path}", sourcePath);
            return false;
        }
        finally
        {
            if (File.Exists(tempPath))
                try { File.Delete(tempPath); } catch { }
        }
    }

    private static void ApplyMagicWhite(Image<Rgba32> image, int intensity = 80)
    {
        // Clamp intensity 10-100%
        int clampedInt = Math.Clamp(intensity, 10, 100);
        int whiteThreshold = (int)Math.Clamp(235 - (clampedInt * 0.75f), 130, 230);
        int darkThreshold = (int)Math.Clamp(75 + (clampedInt * 0.45f), 70, 140);
        float contrastFactor = 0.5f + (clampedInt / 100.0f) * 0.5f;

        image.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < accessor.Height; y++)
            {
                var pixelRow = accessor.GetRowSpan(y);
                for (int x = 0; x < pixelRow.Length; x++)
                {
                    ref Rgba32 p = ref pixelRow[x];
                    byte r = p.R;
                    byte g = p.G;
                    byte b = p.B;

                    float lum = 0.299f * r + 0.587f * g + 0.114f * b;
                    int maxDiff = Math.Max(Math.Abs(r - g), Math.Max(Math.Abs(r - b), Math.Abs(g - b)));
                    bool isColored = maxDiff > 28; // Blue ink signature, red stamp

                    if (!isColored)
                    {
                        // Text or paper background
                        if (lum >= whiteThreshold)
                        {
                            // Whiten grayish background to pure white
                            p.R = 255;
                            p.G = 255;
                            p.B = 255;
                        }
                        else if (lum < darkThreshold)
                        {
                            // Deepen dark text
                            float darkFactor = (lum / (float)darkThreshold) * 0.70f * contrastFactor;
                            p.R = (byte)Math.Clamp(r * darkFactor, 0, 255);
                            p.G = (byte)Math.Clamp(g * darkFactor, 0, 255);
                            p.B = (byte)Math.Clamp(b * darkFactor, 0, 255);
                        }
                        else
                        {
                            // Transition curve between text and background
                            float range = Math.Max(1.0f, whiteThreshold - darkThreshold);
                            float norm = (lum - darkThreshold) / range;
                            float factor = 1.0f + norm * 0.9f;
                            p.R = (byte)Math.Min(255, (int)(r * factor));
                            p.G = (byte)Math.Min(255, (int)(g * factor));
                            p.B = (byte)Math.Min(255, (int)(b * factor));
                        }
                    }
                    else
                    {
                        // Boost saturation slightly and brighten color ink
                        if (lum >= 200)
                        {
                            p.R = 255;
                            p.G = 255;
                            p.B = 255;
                        }
                        else
                        {
                            p.R = (byte)Math.Min(255, (int)(r * 1.15f));
                            p.G = (byte)Math.Min(255, (int)(g * 1.15f));
                            p.B = (byte)Math.Min(255, (int)(b * 1.15f));
                        }
                    }
                }
            }
        });
    }

    private static void ApplyXeroxBlackAndWhite(Image<Rgba32> image, int intensity = 80)
    {
        int clampedInt = Math.Clamp(intensity, 10, 100);
        int threshold = (int)Math.Clamp(105 + (clampedInt * 0.70f), 90, 210);

        image.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < accessor.Height; y++)
            {
                var pixelRow = accessor.GetRowSpan(y);
                for (int x = 0; x < pixelRow.Length; x++)
                {
                    ref Rgba32 p = ref pixelRow[x];
                    float lum = 0.299f * p.R + 0.587f * p.G + 0.114f * p.B;

                    if (lum >= threshold)
                    {
                        p.R = 255;
                        p.G = 255;
                        p.B = 255;
                    }
                    else
                    {
                        p.R = 0;
                        p.G = 0;
                        p.B = 0;
                    }
                }
            }
        });
    }

    private static void ApplyGrayscaleClean(Image<Rgba32> image, int intensity = 80)
    {
        int clampedInt = Math.Clamp(intensity, 10, 100);
        int whiteThreshold = (int)Math.Clamp(235 - (clampedInt * 0.75f), 130, 230);
        int darkThreshold = (int)Math.Clamp(75 + (clampedInt * 0.40f), 60, 130);

        image.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < accessor.Height; y++)
            {
                var pixelRow = accessor.GetRowSpan(y);
                for (int x = 0; x < pixelRow.Length; x++)
                {
                    ref Rgba32 p = ref pixelRow[x];
                    float lum = 0.299f * p.R + 0.587f * p.G + 0.114f * p.B;

                    if (lum >= whiteThreshold)
                    {
                        p.R = 255;
                        p.G = 255;
                        p.B = 255;
                    }
                    else if (lum <= darkThreshold)
                    {
                        byte dark = (byte)(lum * 0.65f);
                        p.R = dark;
                        p.G = dark;
                        p.B = dark;
                    }
                    else
                    {
                        float range = Math.Max(1.0f, whiteThreshold - darkThreshold);
                        float norm = (lum - darkThreshold) / range;
                        byte mid = (byte)Math.Min(255, 60 + norm * 195);
                        p.R = mid;
                        p.G = mid;
                        p.B = mid;
                    }
                }
            }
        });
    }
}
