using System.IO;
using Serilog;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Processing;
using Image = SixLabors.ImageSharp.Image;
using Size = SixLabors.ImageSharp.Size;

namespace SmartSaver.Services;

/// <summary>
/// Compresses and resizes images using SixLabors.ImageSharp.
/// Supports JPEG quality reduction with progressive encoding and
/// PNG metadata stripping. Uses Lanczos resampling for dimension resizing.
/// </summary>
public sealed class ImageCompressor
{
    private const int DefaultStartQuality = 85;
    private const int QualityStepDown = 8;
    private const int MaxAttempts = 10;
    private const int AbsoluteMinQuality = 10;
    private const long MinimumFloorBytes = 5 * 1024; // 5 KB

    /// <summary>
    /// Compresses an image file to meet a target file size using iterative quality reduction.
    /// </summary>
    /// <param name="sourcePath">Absolute path to the source image.</param>
    /// <param name="outputPath">Absolute path for the compressed output.</param>
    /// <param name="targetBytes">Target file size in bytes.</param>
    /// <param name="minQuality">Minimum acceptable JPEG quality (clamped to ≥10).</param>
    /// <returns><c>true</c> if the image was compressed to at or below the target size.</returns>
    public bool CompressToTargetSize(string sourcePath, string outputPath, long targetBytes, int minQuality = 40)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        minQuality = Math.Max(minQuality, AbsoluteMinQuality);

        try
        {
            var extension = Path.GetExtension(sourcePath).ToLowerInvariant();

            if (extension is ".png")
            {
                return CompressPng(sourcePath, outputPath, targetBytes);
            }

            return CompressJpeg(sourcePath, outputPath, targetBytes, minQuality);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to compress image {SourcePath}", sourcePath);
            return false;
        }
    }

    /// <summary>
    /// Compresses an image to a target file size, optionally saving in a different format.
    /// </summary>
    public bool CompressToTargetSize(string sourcePath, string outputPath, long targetBytes, int minQuality, string outputExtension)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        minQuality = Math.Max(minQuality, AbsoluteMinQuality);

        try
        {
            string outExt = outputExtension.ToLowerInvariant();

            if (outExt == ".png")
                return CompressPng(sourcePath, outputPath, targetBytes);

            // For JPEG-family output, use the same intelligent binary-search algorithm
            return CompressJpeg(sourcePath, outputPath, targetBytes, minQuality);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to compress image {SourcePath} to format {Ext}", sourcePath, outputExtension);
            return false;
        }
    }

    /// <summary>
    /// Resizes an image to the specified dimensions using Lanczos resampling.
    /// </summary>
    /// <param name="sourcePath">Absolute path to the source image.</param>
    /// <param name="outputPath">Absolute path for the resized output.</param>
    /// <param name="targetWidth">Target width in pixels. Use 0 to auto-calculate from height.</param>
    /// <param name="targetHeight">Target height in pixels. Use 0 to auto-calculate from width.</param>
    /// <param name="maintainAspectRatio">Whether to preserve the original aspect ratio.</param>
    /// <returns><c>true</c> if the image was resized successfully.</returns>
    public bool ResizeToDimensions(string sourcePath, string outputPath,
        int targetWidth, int targetHeight, bool maintainAspectRatio = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        if (targetWidth <= 0 && targetHeight <= 0)
        {
            Log.Warning("Both width and height are zero or negative for {Path}. Skipping resize", sourcePath);
            return false;
        }

        try
        {
            using var image = Image.Load(sourcePath);
            image.Mutate(ctx => ctx.AutoOrient());

            // If exact dimensions are requested (maintainAspectRatio is false), stretch to the exact required size (e.g. 1500x1000 for portals)
            if (!maintainAspectRatio && targetWidth > 0 && targetHeight > 0)
            {
                image.Mutate(ctx => ctx.Resize(new ResizeOptions
                {
                    Size = new Size(targetWidth, targetHeight),
                    Sampler = KnownResamplers.Lanczos3,
                    Mode = ResizeMode.Stretch
                }));

                SaveWithFormat(image, outputPath, Path.GetExtension(sourcePath));
                Log.Information("Resized {Path} from {OW}x{OH} to EXACT {NW}x{NH} (Mode: Stretch)",
                    sourcePath, image.Width, image.Height, targetWidth, targetHeight);
                return true;
            }

            var (finalWidth, finalHeight) = CalculateDimensions(
                image.Width, image.Height, targetWidth, targetHeight, maintainAspectRatio);

            image.Mutate(ctx => ctx.Resize(new ResizeOptions
            {
                Size = new Size(finalWidth, finalHeight),
                Sampler = KnownResamplers.Lanczos3,
                Mode = maintainAspectRatio ? ResizeMode.Max : ResizeMode.Stretch
            }));

            SaveWithFormat(image, outputPath, Path.GetExtension(sourcePath));
            Log.Information("Resized {Path} from {OW}x{OH} to {NW}x{NH} (MaintainAspect={M})",
                sourcePath, image.Width, image.Height, finalWidth, finalHeight, maintainAspectRatio);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to resize image {SourcePath}", sourcePath);
            return false;
        }
    }

    /// <summary>
    /// Compresses image bytes to a target size. Used internally by <see cref="OfficeCompressor"/>.
    /// </summary>
    /// <param name="imageData">Raw image bytes.</param>
    /// <param name="targetBytes">Target size in bytes.</param>
    /// <param name="minQuality">Minimum JPEG quality.</param>
    /// <returns>Compressed image bytes, or the original if compression fails.</returns>
    public byte[] CompressImageBytes(byte[] imageData, long targetBytes, int minQuality = 20)
    {
        ArgumentNullException.ThrowIfNull(imageData);
        minQuality = Math.Max(minQuality, AbsoluteMinQuality);

        if (imageData.Length <= targetBytes)
            return imageData;

        try
        {
            var format = Image.DetectFormat(imageData);
            using var image = Image.Load(imageData);

            // Handle PNG format preservation (e.g. inside Office documents)
            if (format != null && format.Name.Equals("PNG", StringComparison.OrdinalIgnoreCase))
            {
                using var ms = new MemoryStream();
                var encoder = new SixLabors.ImageSharp.Formats.Png.PngEncoder
                {
                    CompressionLevel = SixLabors.ImageSharp.Formats.Png.PngCompressionLevel.BestCompression
                };
                image.SaveAsPng(ms, encoder);

                if (ms.Length < imageData.Length)
                {
                    Log.Debug("Compressed PNG image bytes from {Original} to {New}", imageData.Length, ms.Length);
                    return ms.ToArray();
                }
                return imageData; // Return original if PNG compression couldn't reduce size
            }

            // Default: JPEG encoding with quality step-down
            int quality = DefaultStartQuality;
            for (int attempt = 0; attempt < MaxAttempts && quality >= minQuality; attempt++)
            {
                using var ms = new MemoryStream();
                var encoder = new JpegEncoder
                {
                    Quality = quality
                };
                image.SaveAsJpeg(ms, encoder);

                if (ms.Length <= targetBytes && ms.Length >= MinimumFloorBytes)
                {
                    Log.Debug("Compressed image bytes from {Original} to {New} at quality {Q}",
                        imageData.Length, ms.Length, quality);
                    return ms.ToArray();
                }

                quality -= QualityStepDown;
            }

            // Return best effort at minimum quality
            using var finalMs = new MemoryStream();
            image.SaveAsJpeg(finalMs, new JpegEncoder { Quality = minQuality });
            return finalMs.ToArray();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to compress image bytes");
            return imageData;
        }
    }

    private bool CompressJpeg(string sourcePath, string outputPath, long targetBytes, int minQuality)
    {
        long originalSizeBytes = new FileInfo(sourcePath).Length;
        using var originalImage = Image.Load(sourcePath);

        // ---------------------------------------------------------------
        // SMART UPSCALE MODE: Target size > Original size (e.g. 200 KB -> 2 MB)
        // ---------------------------------------------------------------
        if (targetBytes > originalSizeBytes && targetBytes > 50 * 1024)
        {
            double sizeRatio = (double)targetBytes / Math.Max(1, originalSizeBytes);
            if (sizeRatio >= 1.25)
            {
                double dimScale = Math.Min(3.0, Math.Sqrt(sizeRatio));
                int upW = Math.Max(1, (int)(originalImage.Width * dimScale));
                int upH = Math.Max(1, (int)(originalImage.Height * dimScale));

                using var upscaled = originalImage.Clone(ctx => ctx.Resize(new ResizeOptions
                {
                    Size = new Size(upW, upH),
                    Sampler = KnownResamplers.Lanczos3,
                    Mode = ResizeMode.Max
                }));

                SaveJpegToDisk(upscaled, outputPath, 98);
                Log.Information("Smart Upscale ✓ — Enlarged {W}x{H} → {NW}x{NH} using Lanczos3 (Size: {Orig} → {New} bytes, target={Target})",
                    originalImage.Width, originalImage.Height, upW, upH, originalSizeBytes, new FileInfo(outputPath).Length, targetBytes);
                return true;
            }
        }

        // ---------------------------------------------------------------
        // INTELLIGENT SWEET-SPOT ALGORITHM (IN-MEMORY TEST)
        // ---------------------------------------------------------------

        int sweepLow = Math.Max(minQuality, 1);
        int sweepHigh = 95;

        // Phase 1: Binary search — find highest Q that fits under target
        int bsLow = sweepLow;
        int bsHigh = sweepHigh;
        int bestQ = -1;
        long bestSize = -1;

        while (bsLow <= bsHigh)
        {
            int midQ = bsLow + (bsHigh - bsLow) / 2;
            long size = TrySaveJpegToMemory(originalImage, midQ);

            if (size <= targetBytes)
            {
                if (bestQ == -1 || size > bestSize)
                {
                    bestQ = midQ;
                    bestSize = size;
                }
                bsLow = midQ + 1;
            }
            else
            {
                bsHigh = midQ - 1;
            }
        }

        if (bestQ != -1)
        {
            // Phase 2: Fine-tune ±3 quality steps around bestQ to get even closer to target
            int refLow  = Math.Max(sweepLow,  bestQ - 3);
            int refHigh = Math.Min(sweepHigh, bestQ + 3);
            int closestQ    = bestQ;
            long closestSize = bestSize;

            for (int q = refHigh; q >= refLow; q--)
            {
                if (q == bestQ) continue; // already measured
                long size = TrySaveJpegToMemory(originalImage, q);
                if (size <= targetBytes && size > closestSize)
                {
                    closestQ    = q;
                    closestSize = size;
                }
            }

            // Commit the winner to disk (Single write!)
            SaveJpegToDisk(originalImage, outputPath, closestQ);

            double utilizationPct = targetBytes > 0 ? 100.0 * closestSize / targetBytes : 0;
            Log.Information(
                "Smart Compression ✓ — Quality={Q}, Size={S:N0} bytes ({Util:F1}% of {T:N0} B target), Dimensions preserved",
                closestQ, closestSize, utilizationPct, targetBytes);
            return true;
        }

        // Phase 3: Even quality=minQuality on the original image is too big.
        // Scale down dimensions gradually at a reasonable fixed quality (70)
        // so we lose as little sharpness as possible.
        int scaleQuality = Math.Max(70, minQuality);
        double scale = 0.9;
        int scaleBestQ = -1;
        long scaleBestSize = -1;
        double scaleBestFactor = 1.0;

        while (scale > 0.25)
        {
            int newW = Math.Max(1, (int)(originalImage.Width  * scale));
            int newH = Math.Max(1, (int)(originalImage.Height * scale));

            using var resized = originalImage.Clone(ctx => ctx.Resize(new ResizeOptions
            {
                Size    = new Size(newW, newH),
                Sampler = KnownResamplers.Lanczos3,
                Mode    = ResizeMode.Max
            }));

            // At this scale, binary-search quality for a sweet-spot
            int sqBsLow  = Math.Max(minQuality, 1);
            int sqBsHigh = scaleQuality;
            int sqBestQ    = -1;
            long sqBestSize = -1;

            while (sqBsLow <= sqBsHigh)
            {
                int midQ = sqBsLow + (sqBsHigh - sqBsLow) / 2;
                long sz  = TrySaveJpegToMemory(resized, midQ);

                if (sz <= targetBytes)
                {
                    if (sqBestQ == -1 || sz > sqBestSize)
                    {
                        sqBestQ    = midQ;
                        sqBestSize = sz;
                    }
                    sqBsLow = midQ + 1;
                }
                else
                {
                    sqBsHigh = midQ - 1;
                }
            }

            if (sqBestQ != -1)
            {
                scaleBestQ      = sqBestQ;
                scaleBestSize   = sqBestSize;
                scaleBestFactor = scale;

                // Fine-tune ±3 steps at this scale
                int fLow  = Math.Max(Math.Max(minQuality, 1), sqBestQ - 3);
                int fHigh = Math.Min(scaleQuality, sqBestQ + 3);
                for (int q = fHigh; q >= fLow; q--)
                {
                    if (q == sqBestQ) continue;
                    long sz = TrySaveJpegToMemory(resized, q);
                    if (sz <= targetBytes && sz > scaleBestSize)
                    {
                        scaleBestQ    = q;
                        scaleBestSize = sz;
                    }
                }

                // Commit the best at this scale to disk (Single write!)
                SaveJpegToDisk(resized, outputPath, scaleBestQ);

                double utilizationPct = targetBytes > 0 ? 100.0 * scaleBestSize / targetBytes : 0;
                Log.Information(
                    "Smart Compression ✓ (scaled) — Scale={Scale:P0}, Quality={Q}, Size={S:N0} bytes ({Util:F1}% of {T:N0} B target)",
                    scale, scaleBestQ, scaleBestSize, utilizationPct, targetBytes);
                return true;
            }

            scale -= 0.1;
        }

        // Absolute last resort — minimum quality, minimum dimensions
        int lastW = Math.Max(1, originalImage.Width / 4);
        int lastH = Math.Max(1, originalImage.Height / 4);
        using var lastResort = originalImage.Clone(ctx => ctx.Resize(new ResizeOptions
        {
            Size    = new Size(lastW, lastH),
            Sampler = KnownResamplers.Lanczos3,
            Mode    = ResizeMode.Max
        }));
        int lastQuality = Math.Max(minQuality, AbsoluteMinQuality);
        SaveJpegToDisk(lastResort, outputPath, lastQuality);
        long lastSize = new FileInfo(outputPath).Length;
        Log.Warning("Smart Compression last-resort: Size={S} bytes (target={T} bytes)", lastSize, targetBytes);
        return lastSize <= targetBytes;
    }

    private static long TrySaveJpegToMemory(Image image, int quality)
    {
        var encoder = new JpegEncoder
        {
            Quality = quality,
            ColorType = JpegColorType.YCbCrRatio444 // 4:4:4 subsampling preserves 100% text edge clarity!
        };
        using var ms = new MemoryStream();
        image.SaveAsJpeg(ms, encoder);
        return ms.Length;
    }

    private static void SaveJpegToDisk(Image image, string outputPath, int quality)
    {
        var encoder = new JpegEncoder
        {
            Quality = quality,
            ColorType = JpegColorType.YCbCrRatio444 // 4:4:4 subsampling preserves 100% text edge clarity!
        };
        using var fs = File.Create(outputPath);
        image.SaveAsJpeg(fs, encoder);
    }

    private static bool CompressPng(string sourcePath, string outputPath, long targetBytes)
    {
        long originalSizeBytes = new FileInfo(sourcePath).Length;
        using var image = Image.Load(sourcePath);

        // ---------------------------------------------------------------
        // SMART PNG UPSCALE MODE: Target size > Original size (e.g. 200 KB -> 2 MB)
        // ---------------------------------------------------------------
        if (targetBytes > originalSizeBytes && targetBytes > 50 * 1024)
        {
            double sizeRatio = (double)targetBytes / Math.Max(1, originalSizeBytes);
            if (sizeRatio >= 1.25)
            {
                double dimScale = Math.Min(3.0, Math.Sqrt(sizeRatio));
                int upW = Math.Max(1, (int)(image.Width * dimScale));
                int upH = Math.Max(1, (int)(image.Height * dimScale));

                using var upscaled = image.Clone(ctx => ctx.Resize(new ResizeOptions
                {
                    Size = new Size(upW, upH),
                    Sampler = KnownResamplers.Lanczos3,
                    Mode = ResizeMode.Max
                }));

                using var fs = File.Create(outputPath);
                upscaled.SaveAsPng(fs, new PngEncoder
                {
                    CompressionLevel = PngCompressionLevel.NoCompression,
                    ColorType = PngColorType.RgbWithAlpha
                });

                Log.Information("Smart PNG Upscale ✓ — Enlarged {W}x{H} → {NW}x{NH} using Lanczos3 ({Orig} → {New} bytes, target={Target})",
                    image.Width, image.Height, upW, upH, originalSizeBytes, new FileInfo(outputPath).Length, targetBytes);
                return true;
            }
        }

        // Strip all metadata for maximum size reduction
        image.Metadata.ExifProfile = null;
        image.Metadata.IccProfile = null;
        image.Metadata.IptcProfile = null;
        image.Metadata.XmpProfile = null;

        var encoder = new PngEncoder
        {
            CompressionLevel = PngCompressionLevel.BestCompression,
            BitDepth = PngBitDepth.Bit8,
            ColorType = PngColorType.RgbWithAlpha,
            FilterMethod = PngFilterMethod.Adaptive
        };

        long compressedSize = TrySavePngToMemory(image, encoder);
        Log.Debug("PNG compressed in-memory to {Size} bytes (target={Target})",
            compressedSize, targetBytes);

        if (compressedSize <= targetBytes)
        {
            // Fits! Save to file
            using (var fs = File.Create(outputPath))
            {
                image.SaveAsPng(fs, encoder);
            }
            return true;
        }

        // Try converting to 8-bit Palette PNG first to preserve dimensions
        var paletteEncoder = new PngEncoder
        {
            CompressionLevel = PngCompressionLevel.BestCompression,
            ColorType = PngColorType.Palette
        };

        long paletteSize = TrySavePngToMemory(image, paletteEncoder);
        Log.Debug("PNG palette compressed in-memory to {Size} bytes", paletteSize);

        if (paletteSize <= targetBytes)
        {
            // Fits! Save to file
            using (var fs = File.Create(outputPath))
            {
                image.SaveAsPng(fs, paletteEncoder);
            }
            return true;
        }

        // Try reducing dimensions progressively in-memory using the palette encoder
        double scale = 0.9;
        for (int i = 0; i < MaxAttempts && scale > 0.2; i++)
        {
            int newWidth = Math.Max(1, (int)(image.Width * scale));
            int newHeight = Math.Max(1, (int)(image.Height * scale));

            using var resized = image.Clone(ctx => ctx.Resize(new ResizeOptions
            {
                Size = new Size(newWidth, newHeight),
                Sampler = KnownResamplers.Lanczos3,
                Mode = ResizeMode.Max
            }));

            long size = TrySavePngToMemory(resized, paletteEncoder);
            if (size <= targetBytes && size >= MinimumFloorBytes)
            {
                // Fits! Save this resized version to file and return true
                using (var fs = File.Create(outputPath))
                {
                    resized.SaveAsPng(fs, paletteEncoder);
                }
                return true;
            }

            scale -= 0.1;
        }

        // If nothing else worked, save the smallest version (scale = 0.2)
        int lastW = Math.Max(1, (int)(image.Width * 0.2));
        int lastH = Math.Max(1, (int)(image.Height * 0.2));
        using var lastResized = image.Clone(ctx => ctx.Resize(new ResizeOptions
        {
            Size = new Size(lastW, lastH),
            Sampler = KnownResamplers.Lanczos3,
            Mode = ResizeMode.Max
        }));
        using (var fs = File.Create(outputPath))
        {
            lastResized.SaveAsPng(fs, paletteEncoder);
        }
        return new FileInfo(outputPath).Length <= targetBytes;
    }

    private static long TrySavePngToMemory(Image image, PngEncoder encoder)
    {
        using var ms = new MemoryStream();
        image.SaveAsPng(ms, encoder);
        return ms.Length;
    }

    private static (int width, int height) CalculateDimensions(
        int originalWidth, int originalHeight,
        int targetWidth, int targetHeight,
        bool maintainAspectRatio)
    {
        if (!maintainAspectRatio)
        {
            return (
                targetWidth > 0 ? targetWidth : originalWidth,
                targetHeight > 0 ? targetHeight : originalHeight
            );
        }

        double aspectRatio = (double)originalWidth / originalHeight;

        if (targetWidth > 0 && targetHeight > 0)
        {
            // Fit within bounding box
            double scaleW = (double)targetWidth / originalWidth;
            double scaleH = (double)targetHeight / originalHeight;
            double scale = Math.Min(scaleW, scaleH);
            return (
                Math.Max(1, (int)Math.Round(originalWidth * scale)),
                Math.Max(1, (int)Math.Round(originalHeight * scale))
            );
        }

        if (targetWidth > 0)
        {
            return (targetWidth, Math.Max(1, (int)Math.Round(targetWidth / aspectRatio)));
        }

        // targetHeight > 0
        return (Math.Max(1, (int)Math.Round(targetHeight * aspectRatio)), targetHeight);
    }

    private static void SaveWithFormat(Image image, string outputPath, string extension)
    {
        switch (extension.ToLowerInvariant())
        {
            case ".png":
                image.SaveAsPng(outputPath, new PngEncoder
                {
                    CompressionLevel = PngCompressionLevel.BestCompression,
                    FilterMethod = PngFilterMethod.Adaptive
                });
                break;
            case ".jpg":
            case ".jpeg":
            default:
                image.SaveAsJpeg(outputPath, new JpegEncoder
                {
                    Quality = DefaultStartQuality
                });
                break;
        }
    }

    /// <summary>
    /// Upscales an image to MEET A MINIMUM file size (target is LARGER than original).
    /// Useful for portals that require a minimum file size.
    /// Progressively increases JPEG quality to 97, then scales up resolution if needed.
    /// </summary>
    public bool UpscaleToTargetSize(string sourcePath, string outputPath, long targetBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        if (!File.Exists(sourcePath)) return false;

        try
        {
            using var img = Image.Load(sourcePath);
            var ext = Path.GetExtension(sourcePath).ToLowerInvariant();

            // Step 1: Try increasing JPEG quality to 95, 97 to push file size up
            int[] qualities = { 85, 90, 93, 95, 97 };
            foreach (int q in qualities)
            {
                using var ms = new MemoryStream();
                img.SaveAsJpeg(ms, new JpegEncoder { Quality = q, ColorType = JpegColorType.YCbCrRatio444 });
                if (ms.Length >= targetBytes)
                {
                    File.WriteAllBytes(outputPath, ms.ToArray());
                    Log.Information("Image upscaled to Q={Q}: {Old} → {New} bytes", q,
                        new FileInfo(sourcePath).Length, ms.Length);
                    return true;
                }
            }

            // Step 2: Scale up resolution (2×, 3×, 4×) at max quality
            int[] scales = { 2, 3, 4 };
            foreach (int scale in scales)
            {
                int newW = img.Width * scale;
                int newH = img.Height * scale;

                using var upscaled = img.Clone(ctx =>
                    ctx.Resize(newW, newH, KnownResamplers.Lanczos3));

                using var ms = new MemoryStream();
                upscaled.SaveAsJpeg(ms, new JpegEncoder { Quality = 97, ColorType = JpegColorType.YCbCrRatio444 });
                if (ms.Length >= targetBytes)
                {
                    File.WriteAllBytes(outputPath, ms.ToArray());
                    Log.Information("Image upscaled {S}× resolution: {Old} → {New} bytes", scale,
                        new FileInfo(sourcePath).Length, ms.Length);
                    return true;
                }

                // Best effort at this scale anyway — keep track
                File.WriteAllBytes(outputPath, ms.ToArray());
            }

            // Return best effort (4× scale at Q97) — it's the biggest we can produce
            Log.Warning("Image upscale best effort: could not reach target {Target}", targetBytes);
            return File.Exists(outputPath) && new FileInfo(outputPath).Length > 0;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "UpscaleToTargetSize failed for {Path}", sourcePath);
            return false;
        }
    }
}
