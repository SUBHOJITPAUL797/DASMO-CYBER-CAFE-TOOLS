using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Threading.Tasks;
using Serilog;
using SmartSaver.Models;

namespace SmartSaver.Services;

public class PhotoSignatureCombinedResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public string OutputPath { get; set; } = string.Empty;
    public byte[]? OutputBytes { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public long OutputSizeBytes { get; set; }
    public string OutputSizeFormatted => CompressionResult.FormatFileSize(OutputSizeBytes);
}

public class PhotoSignatureCombinerService
{
    private readonly SignatureResizeService _signatureResizeService;

    public PhotoSignatureCombinerService(ImageCompressor imageCompressor, SignatureResizeService signatureResizeService)
    {
        _signatureResizeService = signatureResizeService ?? throw new ArgumentNullException(nameof(signatureResizeService));
    }

    /// <summary>
    /// Combines a candidate photo (top) and signature (bottom) into a single unified image.
    /// Perfect for online exam portals requiring joint photo+signature (e.g., under 50 KB).
    /// </summary>
    public async Task<PhotoSignatureCombinedResult> CombineAsync(
        byte[] photoBytes,
        byte[] signatureBytes,
        int totalWidthPx = 350,
        int totalHeightPx = 500,
        long targetBytes = 50 * 1024,
        long minBytes = 10 * 1024,
        bool cleanSignature = true,
        bool convertBlueInkToBlack = true,
        string candidateName = "",
        string photoDate = "",
        string outputExtension = ".jpg",
        int dpi = 300)
    {
        return await Task.Run(() =>
        {
            try
            {
                if (photoBytes == null || photoBytes.Length == 0)
                    return new PhotoSignatureCombinedResult { Success = false, Message = "Photo image is required." };
                if (signatureBytes == null || signatureBytes.Length == 0)
                    return new PhotoSignatureCombinedResult { Success = false, Message = "Signature image is required." };

                int photoAreaH = (int)Math.Round(totalHeightPx * 0.72);
                int signAreaH = totalHeightPx - photoAreaH;

                byte[] cleanSignBytes = signatureBytes;
                if (cleanSignature)
                {
                    var signResult = _signatureResizeService.ResizeSignatureFromBytesAsync(
                        signatureBytes,
                        totalWidthPx - 16,
                        signAreaH - 12,
                        100 * 1024,
                        0,
                        cleanBackground: true,
                        autoCrop: true,
                        maintainAspectRatio: true,
                        outputExtension: ".png",
                        dpi: dpi,
                        convertBlueToBlack: convertBlueInkToBlack).GetAwaiter().GetResult();

                    if (signResult.Success && signResult.OutputBytes != null)
                    {
                        cleanSignBytes = signResult.OutputBytes;
                    }
                }

                using var photoBmp = SmartSaver.Helpers.ImageHelper.LoadOrientedBitmap(photoBytes, "candidate photo");
                using var signBmp = SmartSaver.Helpers.ImageHelper.LoadOrientedBitmap(cleanSignBytes, "candidate signature");

                using var canvas = new Bitmap(totalWidthPx, totalHeightPx, PixelFormat.Format24bppRgb);
                canvas.SetResolution(dpi, dpi);

                using (var g = Graphics.FromImage(canvas))
                {
                    g.Clear(Color.White);
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.SmoothingMode = SmoothingMode.HighQuality;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;

                    // Draw candidate photo scaled to fill photo area
                    g.DrawImage(photoBmp, new Rectangle(0, 0, totalWidthPx, photoAreaH));

                    // Thin divider line
                    using var divPen = new Pen(Color.FromArgb(210, 210, 210), 1f);
                    g.DrawLine(divPen, 0, photoAreaH, totalWidthPx, photoAreaH);

                    // Fit signature inside sign area maintaining aspect ratio
                    double scaleX = (double)(totalWidthPx - 16) / signBmp.Width;
                    double scaleY = (double)(signAreaH - 12) / signBmp.Height;
                    double scale = Math.Min(scaleX, scaleY);
                    int fitSignW = Math.Max(10, (int)Math.Round(signBmp.Width * scale));
                    int fitSignH = Math.Max(10, (int)Math.Round(signBmp.Height * scale));

                    int signX = (totalWidthPx - fitSignW) / 2;
                    int signY = photoAreaH + (signAreaH - fitSignH) / 2;
                    g.DrawImage(signBmp, new Rectangle(signX, signY, fitSignW, fitSignH));

                    // Draw optional Candidate Name / Date overlay on photo bottom
                    if (!string.IsNullOrWhiteSpace(candidateName) || !string.IsNullOrWhiteSpace(photoDate))
                    {
                        string label = !string.IsNullOrWhiteSpace(candidateName) && !string.IsNullOrWhiteSpace(photoDate)
                            ? $"{candidateName.Trim().ToUpper()}  |  DOP: {photoDate.Trim()}"
                            : (!string.IsNullOrWhiteSpace(candidateName) ? candidateName.Trim().ToUpper() : $"DOP: {photoDate.Trim()}");

                        int tagH = 26;
                        int tagY = photoAreaH - tagH;
                        using var tagBrush = new SolidBrush(Color.FromArgb(220, 255, 255, 255));
                        g.FillRectangle(tagBrush, 0, tagY, totalWidthPx, tagH);

                        float fontPx = 13f;
                        Font font = new Font("Segoe UI", fontPx, FontStyle.Bold, GraphicsUnit.Pixel);
                        while (g.MeasureString(label, font).Width > (totalWidthPx - 10) && fontPx > 8.5f)
                        {
                            fontPx -= 0.5f;
                            font.Dispose();
                            font = new Font("Segoe UI", fontPx, FontStyle.Bold, GraphicsUnit.Pixel);
                        }
                        using var textBrush = new SolidBrush(Color.FromArgb(20, 20, 20));
                        var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap };
                        g.DrawString(label, font, textBrush, new RectangleF(0, tagY, totalWidthPx, tagH), sf);
                        font.Dispose();
                    }

                    // Outer neat border
                    using var borderPen = new Pen(Color.FromArgb(180, 180, 180), 1f);
                    g.DrawRectangle(borderPen, 0, 0, totalWidthPx - 1, totalHeightPx - 1);
                }

                // Compress canvas
                byte[] finalBytes = CompressToTargetSize(canvas, targetBytes, minBytes, outputExtension);

                return new PhotoSignatureCombinedResult
                {
                    Success = true,
                    OutputBytes = finalBytes,
                    Width = totalWidthPx,
                    Height = totalHeightPx,
                    OutputSizeBytes = finalBytes.Length
                };
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed in PhotoSignatureCombinerService.CombineAsync");
                return new PhotoSignatureCombinedResult { Success = false, Message = $"Combination failed: {ex.Message}" };
            }
        });
    }

    private static byte[] CompressToTargetSize(Bitmap bitmap, long targetBytes, long minBytes, string outputExtension)
    {
        bool isPng = outputExtension.Equals(".png", StringComparison.OrdinalIgnoreCase);
        if (isPng)
        {
            using var ms = new MemoryStream();
            bitmap.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        }

        ImageCodecInfo? jpegCodec = null;
        foreach (var codec in ImageCodecInfo.GetImageDecoders())
        {
            if (codec.FormatID == ImageFormat.Jpeg.Guid)
            {
                jpegCodec = codec;
                break;
            }
        }

        if (jpegCodec == null)
        {
            using var ms = new MemoryStream();
            bitmap.Save(ms, ImageFormat.Jpeg);
            return ms.ToArray();
        }

        if (targetBytes <= 0) targetBytes = 50 * 1024;

        int lowQ = 25, highQ = 98;
        byte[] best = Array.Empty<byte>();

        while (lowQ <= highQ)
        {
            int midQ = (lowQ + highQ) / 2;
            using var ms = new MemoryStream();
            using var encoderParams = new EncoderParameters(1);
            encoderParams.Param[0] = new EncoderParameter(Encoder.Quality, (long)midQ);
            bitmap.Save(ms, jpegCodec, encoderParams);
            byte[] trial = ms.ToArray();

            if (trial.Length <= targetBytes)
            {
                best = trial;
                lowQ = midQ + 1; // Try higher quality
            }
            else
            {
                highQ = midQ - 1; // Try lower quality
            }
        }

        if (best.Length == 0)
        {
            using var ms = new MemoryStream();
            using var encoderParams = new EncoderParameters(1);
            encoderParams.Param[0] = new EncoderParameter(Encoder.Quality, 25L);
            bitmap.Save(ms, jpegCodec, encoderParams);
            best = ms.ToArray();
        }

        // Check if output needs padding to satisfy minimum portal size
        if (minBytes > 0 && best.Length > 4 && best.Length < minBytes && best[0] == 0xFF && best[1] == 0xD8)
        {
            long needed = (minBytes - best.Length) + 256;
            long maxAllowed = targetBytes - best.Length - 100;
            if (maxAllowed > 0)
            {
                needed = Math.Min(needed, maxAllowed);
                if (needed >= 4)
                {
                    int padLen = (int)Math.Min(needed, 65500);
                    byte[] marker = new byte[padLen];
                    marker[0] = 0xFF;
                    marker[1] = 0xFE;
                    int pLen = padLen - 2;
                    marker[2] = (byte)(pLen >> 8);
                    marker[3] = (byte)(pLen & 0xFF);
                    for (int i = 4; i < padLen; i++) marker[i] = 0x20;

                    using var msPad = new MemoryStream(best.Length + padLen);
                    msPad.Write(best, 0, 2);
                    msPad.Write(marker, 0, padLen);
                    msPad.Write(best, 2, best.Length - 2);
                    best = msPad.ToArray();
                }
            }
        }

        return best;
    }
}
