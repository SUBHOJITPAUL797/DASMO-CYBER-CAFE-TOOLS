using System;
using System.IO;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Jpeg;
using Serilog;
using ImageSharpImage = SixLabors.ImageSharp.Image;
using ImageSharpRectangle = SixLabors.ImageSharp.Rectangle;

namespace SmartSaver.Services;

public sealed class SignatureCleanerService
{
    private readonly ImageCompressor _imageCompressor;

    public SignatureCleanerService(ImageCompressor imageCompressor)
    {
        _imageCompressor = imageCompressor ?? throw new ArgumentNullException(nameof(imageCompressor));
    }

    /// <summary>
    /// Cleans a photographed signature: removes paper background, lines, and shadows,
    /// boosts dark ink, and crops tightly around signature ink.
    /// </summary>
    public byte[] CleanSignature(byte[] inputBytes, bool transparentBackground = false, int threshold = 180)
    {
        using var image = ImageSharpImage.Load<Rgba32>(inputBytes);
        image.Mutate(ctx => ctx.AutoOrient());

        int minX = image.Width, minY = image.Height, maxX = 0, maxY = 0;
        bool foundInk = false;

        // Process pixels
        image.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (int x = 0; x < row.Length; x++)
                {
                    ref Rgba32 p = ref row[x];
                    // Calculate luminance
                    int lum = (int)(0.299f * p.R + 0.587f * p.G + 0.114f * p.B);

                    if (lum > threshold)
                    {
                        // Background paper / shadow
                        if (transparentBackground)
                        {
                            p = new Rgba32(255, 255, 255, 0);
                        }
                        else
                        {
                            p = new Rgba32(255, 255, 255, 255);
                        }
                    }
                    else
                    {
                        // Ink pixel -> enhance contrast (make dark/crisp)
                        float inkFactor = (float)lum / threshold;
                        byte darkValue = (byte)Math.Clamp(inkFactor * 40, 0, 255);
                        p = new Rgba32(darkValue, darkValue, darkValue, 255);

                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;
                        foundInk = true;
                    }
                }
            }
        });

        // Crop tightly around the signature if ink detected with padding
        if (foundInk && maxX > minX && maxY > minY)
        {
            int pad = 12;
            int cropX = Math.Max(0, minX - pad);
            int cropY = Math.Max(0, minY - pad);
            int cropW = Math.Min(image.Width - cropX, (maxX - minX) + pad * 2);
            int cropH = Math.Min(image.Height - cropY, (maxY - minY) + pad * 2);

            image.Mutate(ctx => ctx.Crop(new ImageSharpRectangle(cropX, cropY, cropW, cropH)));
        }

        using var ms = new MemoryStream();
        if (transparentBackground)
        {
            image.Save(ms, new PngEncoder());
        }
        else
        {
            image.Save(ms, new JpegEncoder { Quality = 92 });
        }

        return ms.ToArray();
    }

    /// <summary>
    /// Processes and compresses signature to target file size (e.g. 10 KB to 20 KB).
    /// </summary>
    public bool ProcessAndSave(string inputPath, string outputPath, bool transparentBackground, long targetBytes)
    {
        try
        {
            if (!File.Exists(inputPath)) return false;
            byte[] inputBytes = File.ReadAllBytes(inputPath);
            byte[] cleanedBytes = CleanSignature(inputBytes, transparentBackground);

            if (targetBytes > 0)
            {
                string temp = Path.Combine(Path.GetTempPath(), $"sig_clean_{Guid.NewGuid():N}.{(transparentBackground ? "png" : "jpg")}");
                File.WriteAllBytes(temp, cleanedBytes);
                bool success = _imageCompressor.CompressToTargetSize(temp, outputPath, targetBytes);
                try { File.Delete(temp); } catch { }
                return success;
            }

            File.WriteAllBytes(outputPath, cleanedBytes);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to clean signature: {Path}", inputPath);
            return false;
        }
    }
}
