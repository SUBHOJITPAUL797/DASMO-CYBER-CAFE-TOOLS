using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using Serilog;

namespace SmartSaver.Services;

public class PhotoStampService
{
    private readonly ImageCompressor _imageCompressor;

    public PhotoStampService(ImageCompressor imageCompressor)
    {
        _imageCompressor = imageCompressor;
    }

    /// <summary>
    /// Overlays candidate name and date stamp onto the bottom of a passport photo.
    /// Standard format required by SSC, Railway, State PSC, and Central Govt job portals.
    /// </summary>
    public bool StampPhoto(
        string sourcePath,
        string outputPath,
        string candidateName,
        string dateText,
        string datePrefix = "DOP: ",
        bool standardPassportSize = true,
        long? targetBytes = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        if (!File.Exists(sourcePath)) return false;

        string tempPath = Path.Combine(Path.GetTempPath(), $"smartsaver_stamp_{Guid.NewGuid():N}.jpg");

        try
        {
            using (var srcBmp = SmartSaver.Helpers.ImageHelper.LoadOrientedBitmap(sourcePath))
            {
                int targetWidth = standardPassportSize ? 413 : srcBmp.Width;
                int targetHeight = standardPassportSize ? 531 : srcBmp.Height;

                using var canvas = new Bitmap(targetWidth, targetHeight, PixelFormat.Format24bppRgb);
                using (var g = Graphics.FromImage(canvas))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.SmoothingMode = SmoothingMode.HighQuality;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

                    // Draw base photo
                    g.DrawImage(srcBmp, 0, 0, targetWidth, targetHeight);

                    // Standard Govt Exam banner height (~17% of image height)
                    int bannerHeight = Math.Max(36, (int)Math.Round(targetHeight * 0.17f));
                    int bannerY = targetHeight - bannerHeight;

                    // Draw White Banner Background
                    using (var whiteBrush = new SolidBrush(Color.White))
                    {
                        g.FillRectangle(whiteBrush, 0, bannerY, targetWidth, bannerHeight);
                    }

                    // Draw thin top border for the banner
                    using (var borderPen = new Pen(Color.FromArgb(190, 190, 190), 1.5f))
                    {
                        g.DrawLine(borderPen, 0, bannerY, targetWidth, bannerY);
                    }

                    // Format text
                    string line1 = (candidateName ?? string.Empty).Trim().ToUpperInvariant();
                    string line2;
                    if (string.IsNullOrWhiteSpace(dateText))
                    {
                        line2 = string.Empty;
                    }
                    else
                    {
                        string trimmedDate = dateText.Trim();
                        if (trimmedDate.StartsWith("DOP:", StringComparison.OrdinalIgnoreCase) ||
                            trimmedDate.StartsWith("DOB:", StringComparison.OrdinalIgnoreCase) ||
                            trimmedDate.StartsWith("DATE:", StringComparison.OrdinalIgnoreCase))
                        {
                            line2 = trimmedDate.ToUpperInvariant();
                        }
                        else
                        {
                            string prefix = string.IsNullOrWhiteSpace(datePrefix) ? "DOP:" : datePrefix.Trim();
                            line2 = $"{prefix} {trimmedDate}".ToUpperInvariant();
                        }
                    }

                    using var sf = new StringFormat
                    {
                        Alignment = StringAlignment.Center,
                        LineAlignment = StringAlignment.Center,
                        FormatFlags = StringFormatFlags.NoWrap
                    };

                    using var textBrush = new SolidBrush(Color.FromArgb(15, 15, 15));

                    if (!string.IsNullOrEmpty(line1) && !string.IsNullOrEmpty(line2))
                    {
                        // 2 lines: Name strictly in top half, Date strictly in bottom half
                        float halfH = bannerHeight / 2.0f;

                        // Use GraphicsUnit.Pixel so font is not multiplied by DPI/72
                        float nameFontPx = Math.Clamp(halfH * 0.54f, 11f, 24f);
                        Font nameFont = new Font("Arial", nameFontPx, FontStyle.Bold, GraphicsUnit.Pixel);
                        while (g.MeasureString(line1, nameFont).Width > (targetWidth - 14) && nameFontPx > 6.5f)
                        {
                            nameFontPx -= 0.5f;
                            nameFont.Dispose();
                            nameFont = new Font("Arial", nameFontPx, FontStyle.Bold, GraphicsUnit.Pixel);
                        }

                        float dateFontPx = Math.Clamp(halfH * 0.48f, 10f, 21f);
                        Font dateFont = new Font("Arial", dateFontPx, FontStyle.Bold, GraphicsUnit.Pixel);
                        while (g.MeasureString(line2, dateFont).Width > (targetWidth - 14) && dateFontPx > 6.5f)
                        {
                            dateFontPx -= 0.5f;
                            dateFont.Dispose();
                            dateFont = new Font("Arial", dateFontPx, FontStyle.Bold, GraphicsUnit.Pixel);
                        }

                        var rectName = new RectangleF(4, bannerY + 1, targetWidth - 8, halfH - 2);
                        var rectDate = new RectangleF(4, bannerY + halfH + 1, targetWidth - 8, halfH - 2);

                        g.DrawString(line1, nameFont, textBrush, rectName, sf);
                        g.DrawString(line2, dateFont, textBrush, rectDate, sf);

                        nameFont.Dispose();
                        dateFont.Dispose();
                    }
                    else
                    {
                        // 1 line of text
                        string singleLine = !string.IsNullOrEmpty(line1) ? line1 : line2;
                        float singleFontPx = Math.Clamp(bannerHeight * 0.45f, 12f, 26f);
                        Font singleFont = new Font("Arial", singleFontPx, FontStyle.Bold, GraphicsUnit.Pixel);
                        while (g.MeasureString(singleLine, singleFont).Width > (targetWidth - 14) && singleFontPx > 7f)
                        {
                            singleFontPx -= 0.5f;
                            singleFont.Dispose();
                            singleFont = new Font("Arial", singleFontPx, FontStyle.Bold, GraphicsUnit.Pixel);
                        }

                        var rectSingle = new RectangleF(4, bannerY + 2, targetWidth - 8, bannerHeight - 4);
                        g.DrawString(singleLine, singleFont, textBrush, rectSingle, sf);
                        singleFont.Dispose();
                    }
                }

                // Save JPEG to temp
                var jpegEncoder = ImageCodecInfo.GetImageDecoders()
                    .FirstOrDefault(c => c.FormatID == ImageFormat.Jpeg.Guid);
                if (jpegEncoder != null)
                {
                    var encoderParams = new EncoderParameters(1);
                    encoderParams.Param[0] = new EncoderParameter(Encoder.Quality, 92L);
                    canvas.Save(tempPath, jpegEncoder, encoderParams);
                }
                else
                {
                    canvas.Save(tempPath, ImageFormat.Jpeg);
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
                    Log.Information("Stamped and compressed photo {Src} -> {Out}", sourcePath, outputPath);
                    return true;
                }
            }

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            File.Move(tempPath, outputPath, overwrite: true);
            Log.Information("Stamped candidate photo {Src} -> {Out}", sourcePath, outputPath);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to stamp photo: {Path}", sourcePath);
            return false;
        }
        finally
        {
            if (File.Exists(tempPath))
                try { File.Delete(tempPath); } catch { }
        }
    }
}
