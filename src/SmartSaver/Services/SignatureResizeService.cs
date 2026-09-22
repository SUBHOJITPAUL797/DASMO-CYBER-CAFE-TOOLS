using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using Serilog;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Metadata;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SmartSaver.Models;
using ImageSharpImage = SixLabors.ImageSharp.Image;
using ImageSharpRectangle = SixLabors.ImageSharp.Rectangle;
using ImageSharpSize = SixLabors.ImageSharp.Size;

namespace SmartSaver.Services;

public class SignatureResizeResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public string OutputPath { get; set; } = string.Empty;
    public byte[]? OutputBytes { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public long OriginalSizeBytes { get; set; }
    public long OutputSizeBytes { get; set; }
    public string OriginalSizeFormatted => CompressionResult.FormatFileSize(OriginalSizeBytes);
    public string OutputSizeFormatted => CompressionResult.FormatFileSize(OutputSizeBytes);
}

public class SignatureResizeService
{
    private readonly ImageCompressor _imageCompressor;
    private readonly SignatureCleanerService _cleanerService;

    public SignatureResizeService(ImageCompressor imageCompressor)
    {
        _imageCompressor = imageCompressor ?? throw new ArgumentNullException(nameof(imageCompressor));
        _cleanerService = new SignatureCleanerService(imageCompressor);
    }

    /// <summary>
    /// Resizes a signature image from a file path to exact pixel or centimeter dimensions,
    /// cleans paper background, and compresses to strict target KB.
    /// </summary>
    public async Task<SignatureResizeResult> ResizeSignatureAsync(
        string inputPath,
        string outputPath,
        int targetWidthPx,
        int targetHeightPx,
        long targetBytes,
        long minBytes = 0,
        bool cleanBackground = true,
        bool autoCrop = true,
        bool maintainAspectRatio = false,
        string outputExtension = ".jpg",
        int dpi = 300,
        bool convertBlueToBlack = true)
    {
        if (!File.Exists(inputPath))
        {
            return new SignatureResizeResult { Success = false, Message = "Source image not found." };
        }

        byte[] inputBytes = await File.ReadAllBytesAsync(inputPath);
        var result = await ResizeSignatureFromBytesAsync(inputBytes, targetWidthPx, targetHeightPx, targetBytes, minBytes, cleanBackground, autoCrop, maintainAspectRatio, outputExtension, dpi, convertBlueToBlack);

        if (result.Success && result.OutputBytes != null && !string.IsNullOrWhiteSpace(outputPath))
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                await File.WriteAllBytesAsync(outputPath, result.OutputBytes);
                result.OutputPath = outputPath;
                result.OutputSizeBytes = new FileInfo(outputPath).Length;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to save resized signature to {Path}", outputPath);
                return new SignatureResizeResult { Success = false, Message = $"Failed to save: {ex.Message}" };
            }
        }

        return result;
    }

    /// <summary>
    /// Resizes signature from in-memory byte array (supports clipboard paste & drag-drop).
    /// </summary>
    public async Task<SignatureResizeResult> ResizeSignatureFromBytesAsync(
        byte[] inputBytes,
        int targetWidthPx,
        int targetHeightPx,
        long targetBytes,
        long minBytes = 0,
        bool cleanBackground = true,
        bool autoCrop = true,
        bool maintainAspectRatio = false,
        string outputExtension = ".jpg",
        int dpi = 300,
        bool convertBlueToBlack = true)
    {
        return await Task.Run(() =>
        {
            try
            {
                if (inputBytes == null || inputBytes.Length == 0)
                {
                    return new SignatureResizeResult { Success = false, Message = "No image data provided." };
                }

                long originalSize = inputBytes.Length;

                // 1. Load image and auto-orient EXIF camera rotation
                using var image = ImageSharpImage.Load<Rgba32>(inputBytes);
                image.Mutate(ctx => ctx.AutoOrient());

                // 2. Optional: Clean paper shadows, enhance dark ink, and auto-crop to ink
                if (cleanBackground)
                {
                    CleanPaperAndBoostInk(image, autoCrop: autoCrop, convertBlueToBlack: convertBlueToBlack);
                }

                // 3. Resize to exact dimensions
                SixLabors.ImageSharp.Image canvas;

                if (maintainAspectRatio)
                {
                    // Fit inside target dimensions and center on pure white canvas
                    double scaleX = (double)targetWidthPx / image.Width;
                    double scaleY = (double)targetHeightPx / image.Height;
                    double scale = Math.Min(scaleX, scaleY);

                    int scaledW = Math.Max(1, (int)Math.Round(image.Width * scale));
                    int scaledH = Math.Max(1, (int)Math.Round(image.Height * scale));

                    image.Mutate(ctx => ctx.Resize(new ResizeOptions
                    {
                        Size = new ImageSharpSize(scaledW, scaledH),
                        Sampler = KnownResamplers.Lanczos3,
                        Mode = ResizeMode.Stretch
                    }));

                    canvas = new SixLabors.ImageSharp.Image<Rgba32>(targetWidthPx, targetHeightPx);
                    canvas.Mutate(ctx =>
                    {
                        ctx.BackgroundColor(SixLabors.ImageSharp.Color.White);
                        int posX = (targetWidthPx - scaledW) / 2;
                        int posY = (targetHeightPx - scaledH) / 2;
                        ctx.DrawImage(image, new SixLabors.ImageSharp.Point(posX, posY), 1.0f);
                    });
                }
                else
                {
                    // Exact stretch to form dimensions (standard online portal requirement)
                    image.Mutate(ctx => ctx.Resize(new ResizeOptions
                    {
                        Size = new ImageSharpSize(targetWidthPx, targetHeightPx),
                        Sampler = KnownResamplers.Lanczos3,
                        Mode = ResizeMode.Stretch
                    }));
                    canvas = image.CloneAs<Rgba32>();
                }

                using (canvas)
                {
                    // Set physical DPI metadata
                    canvas.Metadata.HorizontalResolution = dpi;
                    canvas.Metadata.VerticalResolution = dpi;
                    canvas.Metadata.ResolutionUnits = PixelResolutionUnit.PixelsPerInch;

                    // 4. Compress to target bytes using binary search quality passes
                    byte[] finalBytes = CompressCanvasToBytes(canvas, targetBytes, outputExtension, minBytes);

                    return new SignatureResizeResult
                    {
                        Success = true,
                        OutputBytes = finalBytes,
                        Width = targetWidthPx,
                        Height = targetHeightPx,
                        OriginalSizeBytes = originalSize,
                        OutputSizeBytes = finalBytes.Length
                    };
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed in ResizeSignatureFromBytesAsync");
                return new SignatureResizeResult { Success = false, Message = $"Resize failed: {ex.Message}" };
            }
        });
    }

    /// <summary>
    /// Removes scanner/camera shadows and paper grayness, boosting dark ink to sharp black,
    /// and optionally crops tightly around the signature ink to eliminate useless blank margins.
    /// </summary>
    private static void CleanPaperAndBoostInk(SixLabors.ImageSharp.Image<Rgba32> image, int threshold = 185, bool autoCrop = true, bool convertBlueToBlack = true)
    {
        int minX = image.Width, minY = image.Height, maxX = 0, maxY = 0;
        bool foundInk = false;

        image.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (int x = 0; x < row.Length; x++)
                {
                    ref Rgba32 p = ref row[x];
                    int lum = (int)(0.299f * p.R + 0.587f * p.G + 0.114f * p.B);

                    // Check for blue ink detection (ballpoint or gel pen)
                    bool isBlueInk = (p.B > p.R + 15 && p.B > p.G + 10) || (lum < 220 && (p.B - Math.Min(p.R, p.G) > 20));

                    if (lum > threshold && !isBlueInk)
                    {
                        // Background paper shadow -> make pure crisp white
                        p = new Rgba32(255, 255, 255, 255);
                    }
                    else
                    {
                        if (convertBlueToBlack || isBlueInk)
                        {
                            // Convert ink (including pale/royal blue ink) to pitch black
                            float factor = Math.Min(1.0f, (float)lum / threshold);
                            byte darkVal = (byte)Math.Clamp(factor * 25, 0, 255);
                            p = new Rgba32(darkVal, darkVal, darkVal, 255);
                        }
                        else
                        {
                            // Ink pixel -> enhance contrast (darken to rich dark tone)
                            float factor = (float)lum / threshold;
                            byte darkVal = (byte)Math.Clamp(factor * 35, 0, 255);
                            p = new Rgba32(darkVal, darkVal, darkVal, 255);
                        }

                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;
                        foundInk = true;
                    }
                }
            }
        });

        // Crop tightly around the signature if ink detected with clean margin
        if (autoCrop && foundInk && maxX > minX && maxY > minY)
        {
            int pad = 12;
            int cropX = Math.Max(0, minX - pad);
            int cropY = Math.Max(0, minY - pad);
            int cropW = Math.Min(image.Width - cropX, (maxX - minX) + pad * 2);
            int cropH = Math.Min(image.Height - cropY, (maxY - minY) + pad * 2);

            if (cropW > 15 && cropH > 15 && (cropW < image.Width || cropH < image.Height))
            {
                image.Mutate(ctx => ctx.Crop(new ImageSharpRectangle(cropX, cropY, cropW, cropH)));
            }
        }
    }

    /// <summary>
    /// Compresses canvas in memory using binary-search JPEG/PNG quality reduction to clamp under targetBytes,
    /// and optionally pads JPEG to satisfy minimum portal requirements (e.g. 10-20 KB).
    /// </summary>
    private static byte[] CompressCanvasToBytes(ImageSharpImage canvas, long targetBytes, string outputExtension, long minBytes = 0)
    {
        bool isPng = outputExtension.Equals(".png", StringComparison.OrdinalIgnoreCase);

        if (isPng)
        {
            using var ms = new MemoryStream();
            canvas.Save(ms, new PngEncoder { CompressionLevel = PngCompressionLevel.BestCompression });
            return ms.ToArray();
        }

        // JPEG compression loop
        if (targetBytes <= 0) targetBytes = 20 * 1024; // Default 20 KB

        int lowQuality = 15;
        int highQuality = 98;
        int bestQuality = 85;
        byte[]? bestBytes = null;

        // Binary search up to 8 iterations
        for (int i = 0; i < 8; i++)
        {
            int midQuality = (lowQuality + highQuality) / 2;
            using var ms = new MemoryStream();
            canvas.Save(ms, new JpegEncoder { Quality = midQuality });
            byte[] bytes = ms.ToArray();

            if (bytes.Length <= targetBytes)
            {
                bestBytes = bytes;
                bestQuality = midQuality;
                lowQuality = midQuality + 1; // Try to get higher quality while staying under target
            }
            else
            {
                highQuality = midQuality - 1; // Exceeded limit, reduce quality
            }

            if (lowQuality > highQuality) break;
        }

        byte[] resultBytes;
        if (bestBytes != null)
        {
            resultBytes = bestBytes;
        }
        else
        {
            // Fallback to lowest acceptable quality
            using var fallbackMs = new MemoryStream();
            canvas.Save(fallbackMs, new JpegEncoder { Quality = 15 });
            resultBytes = fallbackMs.ToArray();
        }

        // If minBytes specified and output is smaller, safely pad using JPEG comment marker
        if (minBytes > 0 && resultBytes.Length < minBytes)
        {
            resultBytes = PadJpegToMinSize(resultBytes, minBytes, targetBytes);
        }

        return resultBytes;
    }

    /// <summary>
    /// Pads a JPEG file using standard JPEG COM (Comment) marker so it strictly meets
    /// portal minimum file size requirements (e.g. 10 KB - 20 KB) without changing pixels.
    /// </summary>
    private static byte[] PadJpegToMinSize(byte[] jpegBytes, long minBytes, long maxBytes)
    {
        if (jpegBytes.Length >= minBytes || jpegBytes.Length < 4) return jpegBytes;
        if (jpegBytes[0] != 0xFF || jpegBytes[1] != 0xD8) return jpegBytes;

        long needed = (minBytes - jpegBytes.Length) + 256; // Aim slightly above minimum
        long maxAllowedPad = maxBytes - jpegBytes.Length - 100;
        if (maxAllowedPad <= 0) return jpegBytes;
        needed = Math.Min(needed, maxAllowedPad);
        if (needed < 4) return jpegBytes;

        int padLength = (int)Math.Min(needed, 65500);
        byte[] paddingMarker = new byte[padLength];
        paddingMarker[0] = 0xFF;
        paddingMarker[1] = 0xFE;
        int payloadLen = padLength - 2;
        paddingMarker[2] = (byte)(payloadLen >> 8);
        paddingMarker[3] = (byte)(payloadLen & 0xFF);
        for (int i = 4; i < padLength; i++)
        {
            paddingMarker[i] = 0x20; // Safe ASCII spaces
        }

        using var ms = new MemoryStream(jpegBytes.Length + padLength);
        ms.Write(jpegBytes, 0, 2); // SOI
        ms.Write(paddingMarker, 0, padLength); // COM segment
        ms.Write(jpegBytes, 2, jpegBytes.Length - 2); // Rest of JPEG
        return ms.ToArray();
    }

    /// <summary>
    /// Helper to convert byte array to WPF BitmapSource for UI preview and clipboard.
    /// </summary>
    public static BitmapSource? BytesToBitmapSource(byte[]? bytes)
    {
        if (bytes == null || bytes.Length == 0) return null;

        try
        {
            using var ms = new MemoryStream(bytes);
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.StreamSource = ms;
            bi.EndInit();
            bi.Freeze();
            return bi;
        }
        catch
        {
            try
            {
                using var isImg = SixLabors.ImageSharp.Image.Load<SixLabors.ImageSharp.PixelFormats.Rgba32>(bytes);
                using var pngMs = new MemoryStream();
                isImg.SaveAsPng(pngMs);
                pngMs.Position = 0;

                var bi = new BitmapImage();
                bi.BeginInit();
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.StreamSource = pngMs;
                bi.EndInit();
                bi.Freeze();
                return bi;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to convert bytes to BitmapSource");
                return null;
            }
        }
    }
}
