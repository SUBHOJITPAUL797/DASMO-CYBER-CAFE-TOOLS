using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Serilog;

namespace SmartSaver.Services;

public class ImageCropService
{
    private static readonly Lazy<ImageCropService> _instance = new(() => new ImageCropService());
    public static ImageCropService Instance => _instance.Value;

    private readonly string _pythonScriptPath;

    public ImageCropService()
    {
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        _pythonScriptPath = Path.Combine(baseDir, "Services", "SmartCardDetector.py");
        if (!File.Exists(_pythonScriptPath))
        {
            _pythonScriptPath = Path.Combine(baseDir, "SmartCardDetector.py");
        }
    }

    /// <summary>
    /// Performs smart on-device card detection on a photo (Aadhaar, PAN, Voter, Ration in hands or on table)
    /// and returns the cropped card file path, guaranteed to be in horizontal landscape.
    /// </summary>
    public async Task<string?> AutoCropCardAsync(string imagePath, bool autoWhiten = true)
    {
        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath)) return null;

        try
        {
            string outDir = Path.Combine(Path.GetTempPath(), "DASMO_AutoCrop");
            Directory.CreateDirectory(outDir);

            string? finalPath = null;

            // Attempt Python computer vision detector first
            var pyResult = await RunPythonAutoCropAsync(imagePath, outDir, autoWhiten);
            if (pyResult != null && !string.IsNullOrEmpty(pyResult.FrontImage) && File.Exists(pyResult.FrontImage))
            {
                Log.Information("On-device Python smart auto-crop succeeded: {Path}", pyResult.FrontImage);
                finalPath = pyResult.FrontImage;
            }
            else
            {
                // Fallback: Pure C# GDI+ edge / contrast card detection
                finalPath = await Task.Run(() => FallbackCsAutoCrop(imagePath, outDir));
            }

            // GUARANTEE: If the resulting card has Height > Width (vertical/portrait), rotate it to landscape!
            if (!string.IsNullOrEmpty(finalPath) && File.Exists(finalPath))
            {
                finalPath = EnsureLandscapeOrientation(finalPath);
            }

            return finalPath;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to auto-crop image: {Path}", imagePath);
            return null;
        }
    }

    /// <summary>
    /// Checks image dimensions and automatically rotates 90 degrees if height > width (portrait).
    /// </summary>
    public static string EnsureLandscapeOrientation(string imagePath)
    {
        try
        {
            using var img = SmartSaver.Helpers.ImageHelper.LoadOrientedBitmap(imagePath);
            if (img.Height > img.Width)
            {
                string dir = Path.GetDirectoryName(imagePath)!;
                string rotatedPath = Path.Combine(dir, $"rot_land_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}.png");
                img.RotateFlip(RotateFlipType.Rotate270FlipNone);
                img.Save(rotatedPath, ImageFormat.Png);
                Log.Information("Auto-rotated vertical card to landscape: {Path} ({W}x{H})", rotatedPath, img.Width, img.Height);
                return rotatedPath;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not verify/rotate landscape orientation for {Path}", imagePath);
        }
        return imagePath;
    }

    /// <summary>
    /// Detects card bounds [x, y, w, h] without saving crop, for snapping handles in Manual Crop dialog.
    /// </summary>
    public async Task<Rectangle?> DetectCardBoundsAsync(string imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath)) return null;

        try
        {
            string outDir = Path.Combine(Path.GetTempPath(), "DASMO_AutoCrop");
            Directory.CreateDirectory(outDir);

            var pyResult = await RunPythonAutoCropAsync(imagePath, outDir, autoWhiten: false);
            if (pyResult != null && pyResult.CropRect != null && pyResult.CropRect.Length == 4)
            {
                return new Rectangle(pyResult.CropRect[0], pyResult.CropRect[1], pyResult.CropRect[2], pyResult.CropRect[3]);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to detect card bounds for {Path}", imagePath);
        }

        return null;
    }

    /// <summary>
    /// Manually crops an image to exact pixel rectangle [x, y, width, height].
    /// </summary>
    public async Task<string?> CropImageToRectAsync(string imagePath, Rectangle cropRect)
    {
        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath)) return null;

        return await Task.Run(() =>
        {
            try
            {
                byte[] bytes = File.ReadAllBytes(imagePath);
                using var ms = new MemoryStream(bytes);
                using var src = System.Drawing.Image.FromStream(ms);

                // Clamp cropRect within image bounds
                int rx = Math.Max(0, Math.Min(src.Width - 1, cropRect.X));
                int ry = Math.Max(0, Math.Min(src.Height - 1, cropRect.Y));
                int rw = Math.Max(10, Math.Min(src.Width - rx, cropRect.Width));
                int rh = Math.Max(10, Math.Min(src.Height - ry, cropRect.Height));

                using var target = new Bitmap(rw, rh, PixelFormat.Format24bppRgb);
                target.SetResolution(src.HorizontalResolution > 0 ? src.HorizontalResolution : 300,
                                     src.VerticalResolution > 0 ? src.VerticalResolution : 300);

                using (var g = Graphics.FromImage(target))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.SmoothingMode = SmoothingMode.HighQuality;
                    g.Clear(Color.White);

                    g.DrawImage(src, new Rectangle(0, 0, rw, rh), new Rectangle(rx, ry, rw, rh), GraphicsUnit.Pixel);
                }

                string outDir = Path.Combine(Path.GetTempPath(), "DASMO_ManualCrop");
                Directory.CreateDirectory(outDir);
                string outPath = Path.Combine(outDir, $"crop_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}.png");

                target.Save(outPath, ImageFormat.Png);
                Log.Information("Manual crop saved: {Path} ({W}x{H})", outPath, rw, rh);
                return outPath;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to crop image {Path} to rect {Rect}", imagePath, cropRect);
                return null;
            }
        });
    }

    /// <summary>
    /// Rotates an image by 90 degrees (clockwise or counter-clockwise) and saves to a new temporary file.
    /// </summary>
    public async Task<string?> RotateImage90Async(string imagePath, bool clockwise = true)
    {
        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath)) return null;

        return await Task.Run(() =>
        {
            try
            {
                byte[] bytes = File.ReadAllBytes(imagePath);
                using var ms = new MemoryStream(bytes);
                using var src = System.Drawing.Image.FromStream(ms);

                src.RotateFlip(clockwise ? RotateFlipType.Rotate90FlipNone : RotateFlipType.Rotate270FlipNone);

                string outDir = Path.Combine(Path.GetTempPath(), "DASMO_ManualCrop");
                Directory.CreateDirectory(outDir);
                string outPath = Path.Combine(outDir, $"rotated_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}.png");

                src.Save(outPath, ImageFormat.Png);
                return outPath;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to rotate image: {Path}", imagePath);
                return null;
            }
        });
    }

    private async Task<PythonCropResult?> RunPythonAutoCropAsync(string imagePath, string outDir, bool autoWhiten)
    {
        var pythonCandidates = new List<string>();

        // 1. Check user local python installations
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string[] localPyPaths = {
            Path.Combine(localAppData, "Programs", "Python", "Python314", "python.exe"),
            Path.Combine(localAppData, "Programs", "Python", "Python313", "python.exe"),
            Path.Combine(localAppData, "Programs", "Python", "Python312", "python.exe"),
            Path.Combine(localAppData, "Programs", "Python", "Python311", "python.exe"),
            @"C:\Program Files\Python314\python.exe",
            @"C:\Program Files\Python313\python.exe",
            @"C:\Program Files\Python312\python.exe",
            @"C:\Program Files\Python311\python.exe"
        };
        foreach (var p in localPyPaths)
        {
            if (File.Exists(p)) pythonCandidates.Add(p);
        }

        // 2. Add standard PATH names
        pythonCandidates.AddRange(new[] { "python", "py", "python3" });

        foreach (var py in pythonCandidates)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = py,
                    Arguments = $"\"{_pythonScriptPath}\" --input \"{imagePath}\" --outdir \"{outDir}\" --mode autocrop --autowhiten {autoWhiten.ToString().ToLower()}",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using var proc = Process.Start(psi);
                if (proc == null) continue;

                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(25));
                string stdout = await proc.StandardOutput.ReadToEndAsync(cts.Token);
                await proc.WaitForExitAsync(cts.Token);

                if (proc.ExitCode == 0 && !string.IsNullOrWhiteSpace(stdout))
                {
                    var options = new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true,
                        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
                    };
                    var res = JsonSerializer.Deserialize<PythonCropResult>(stdout.Trim(), options);
                    if (res != null && res.Success && !string.IsNullOrEmpty(res.FrontImage))
                    {
                        return res;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Python executable {Py} failed for auto-crop", py);
            }
        }

        return null;
    }

    private static string? FallbackCsAutoCrop(string imagePath, string outDir)
    {
        try
        {
            using var bmp = SmartSaver.Helpers.ImageHelper.LoadOrientedBitmap(imagePath);

            // If image is vertical portrait (height > width), rotate 90 degrees CCW first
            // to align with standard landscape ID card format (85.6mm x 54mm)
            if (bmp.Height > bmp.Width)
            {
                bmp.RotateFlip(RotateFlipType.Rotate270FlipNone);
            }

            // Center-crop with standard ID card aspect ratio (1.58:1)
            int w = bmp.Width;
            int h = bmp.Height;

            int targetW = (int)(w * 0.90);
            int targetH = (int)(targetW / 1.58);
            if (targetH > h)
            {
                targetH = (int)(h * 0.90);
                targetW = (int)(targetH * 1.58);
            }

            int x = (w - targetW) / 2;
            int y = (h - targetH) / 2;

            using var cropped = new Bitmap(targetW, targetH);
            using (var g = Graphics.FromImage(cropped))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(bmp, new Rectangle(0, 0, targetW, targetH), new Rectangle(x, y, targetW, targetH), GraphicsUnit.Pixel);
            }

            string outPath = Path.Combine(outDir, $"cs_crop_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}.png");
            cropped.Save(outPath, ImageFormat.Png);
            return outPath;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Unwarps an arbitrary quadrilateral defined by 4 corners (TL, TR, BR, BL) into a flat rectangle.
    /// Perfectly straightens tilted/skewed photos of cards or documents taken on tables or bedsheets.
    /// </summary>
    public async Task<string?> WarpPerspectiveQuadAsync(
        string imagePath,
        PointF[] quadCorners,
        int targetWidth = 0,
        int targetHeight = 0,
        bool removeShadows = true)
    {
        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath) || quadCorners == null || quadCorners.Length != 4)
            return null;

        return await Task.Run(() =>
        {
            try
            {
                using var src = SmartSaver.Helpers.ImageHelper.LoadOrientedBitmap(imagePath);

                PointF tl = quadCorners[0];
                PointF tr = quadCorners[1];
                PointF br = quadCorners[2];
                PointF bl = quadCorners[3];

                // Determine target dimensions if not explicitly provided
                if (targetWidth <= 0 || targetHeight <= 0)
                {
                    double topDist = Math.Sqrt(Math.Pow(tr.X - tl.X, 2) + Math.Pow(tr.Y - tl.Y, 2));
                    double botDist = Math.Sqrt(Math.Pow(br.X - bl.X, 2) + Math.Pow(br.Y - bl.Y, 2));
                    double leftDist = Math.Sqrt(Math.Pow(bl.X - tl.X, 2) + Math.Pow(bl.Y - tl.Y, 2));
                    double rightDist = Math.Sqrt(Math.Pow(br.X - tr.X, 2) + Math.Pow(br.Y - tr.Y, 2));

                    int w = (int)Math.Round(Math.Max(topDist, botDist));
                    int h = (int)Math.Round(Math.Max(leftDist, rightDist));
                    targetWidth = Math.Max(100, w);
                    targetHeight = Math.Max(100, h);
                }

                // Warp perspective using closed-form Paul Heckbert projective mapping
                using var unwarped = WarpQuadInternal(src, tl, tr, br, bl, targetWidth, targetHeight);

                // If removeShadows is requested, apply illumination normalization
                using var finalBmp = removeShadows ? RemoveUnevenShadowsInternal(unwarped) : (Bitmap)unwarped.Clone();

                string outDir = Path.Combine(Path.GetTempPath(), "DASMO_ManualCrop");
                Directory.CreateDirectory(outDir);
                string outPath = Path.Combine(outDir, $"unwarped_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}.png");

                finalBmp.Save(outPath, ImageFormat.Png);
                Log.Information("Perspective unwarp saved: {Path} ({W}x{H})", outPath, targetWidth, targetHeight);
                return outPath;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to warp quad for {Path}", imagePath);
                return null;
            }
        });
    }

    /// <summary>
    /// Closed-form Paul Heckbert projective mapping from destination rectangle to source quadrilateral.
    /// </summary>
    public static Bitmap WarpQuadInternal(Bitmap src, PointF s0, PointF s1, PointF s2, PointF s3, int destW, int destH)
    {
        var dest = new Bitmap(destW, destH, PixelFormat.Format32bppArgb);
        dest.SetResolution(src.HorizontalResolution > 0 ? src.HorizontalResolution : 300,
                           src.VerticalResolution > 0 ? src.VerticalResolution : 300);

        double dx1 = s1.X - s2.X;
        double dx2 = s3.X - s2.X;
        double dx3 = s0.X - s1.X + s2.X - s3.X;
        double dy1 = s1.Y - s2.Y;
        double dy2 = s3.Y - s2.Y;
        double dy3 = s0.Y - s1.Y + s2.Y - s3.Y;

        double a31, a32, a11, a12, a13, a21, a22, a23;

        double det = dx1 * dy2 - dx2 * dy1;
        if (Math.Abs(det) < 1e-7 || (Math.Abs(dx3) < 1e-7 && Math.Abs(dy3) < 1e-7))
        {
            a11 = s1.X - s0.X;
            a12 = s3.X - s0.X;
            a13 = s0.X;
            a21 = s1.Y - s0.Y;
            a22 = s3.Y - s0.Y;
            a23 = s0.Y;
            a31 = 0;
            a32 = 0;
        }
        else
        {
            a31 = (dx3 * dy2 - dx2 * dy3) / det;
            a32 = (dx1 * dy3 - dx3 * dy1) / det;
            a11 = s1.X - s0.X + a31 * s1.X;
            a12 = s3.X - s0.X + a32 * s3.X;
            a13 = s0.X;
            a21 = s1.Y - s0.Y + a31 * s1.Y;
            a22 = s3.Y - s0.Y + a32 * s3.Y;
            a23 = s0.Y;
        }

        int srcW = src.Width;
        int srcH = src.Height;

        var srcData = src.LockBits(new Rectangle(0, 0, srcW, srcH), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var destData = dest.LockBits(new Rectangle(0, 0, destW, destH), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);

        try
        {
            unsafe
            {
                byte* pSrc = (byte*)srcData.Scan0;
                byte* pDest = (byte*)destData.Scan0;
                int srcStride = srcData.Stride;
                int destStride = destData.Stride;

                double invDestW = 1.0 / destW;
                double invDestH = 1.0 / destH;

                for (int y = 0; y < destH; y++)
                {
                    double v = y * invDestH;
                    byte* rowDest = pDest + y * destStride;

                    for (int x = 0; x < destW; x++)
                    {
                        double u = x * invDestW;
                        double w = a31 * u + a32 * v + 1.0;
                        if (Math.Abs(w) < 1e-7) w = 1e-7;

                        double sx = (a11 * u + a12 * v + a13) / w;
                        double sy = (a21 * u + a22 * v + a23) / w;

                        if (sx >= 0 && sx < srcW - 1 && sy >= 0 && sy < srcH - 1)
                        {
                            int x0 = (int)sx;
                            int y0 = (int)sy;
                            int x1 = x0 + 1;
                            int y1 = y0 + 1;

                            double fx = sx - x0;
                            double fy = sy - y0;
                            double fx1 = 1.0 - fx;
                            double fy1 = 1.0 - fy;

                            byte* c00 = pSrc + y0 * srcStride + x0 * 4;
                            byte* c10 = pSrc + y0 * srcStride + x1 * 4;
                            byte* c01 = pSrc + y1 * srcStride + x0 * 4;
                            byte* c11 = pSrc + y1 * srcStride + x1 * 4;

                            double b = (c00[0] * fx1 + c10[0] * fx) * fy1 + (c01[0] * fx1 + c11[0] * fx) * fy;
                            double g = (c00[1] * fx1 + c10[1] * fx) * fy1 + (c01[1] * fx1 + c11[1] * fx) * fy;
                            double r = (c00[2] * fx1 + c10[2] * fx) * fy1 + (c01[2] * fx1 + c11[2] * fx) * fy;

                            rowDest[x * 4 + 0] = (byte)Math.Clamp(b, 0, 255);
                            rowDest[x * 4 + 1] = (byte)Math.Clamp(g, 0, 255);
                            rowDest[x * 4 + 2] = (byte)Math.Clamp(r, 0, 255);
                            rowDest[x * 4 + 3] = 255;
                        }
                        else
                        {
                            rowDest[x * 4 + 0] = 255;
                            rowDest[x * 4 + 1] = 255;
                            rowDest[x * 4 + 2] = 255;
                            rowDest[x * 4 + 3] = 255;
                        }
                    }
                }
            }
        }
        finally
        {
            src.UnlockBits(srcData);
            dest.UnlockBits(destData);
        }

        return dest;
    }

    /// <summary>
    /// Removes mobile camera shadows, uneven room lighting, and yellowish paper casts.
    /// Normalizes background to pure 100% white while keeping text and photos crisp and high-contrast.
    /// </summary>
    public static Bitmap RemoveUnevenShadowsInternal(Bitmap src)
    {
        int w = src.Width;
        int h = src.Height;

        var result = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        result.SetResolution(src.HorizontalResolution, src.VerticalResolution);

        int blockSize = Math.Max(16, Math.Min(48, Math.Min(w, h) / 25));
        int gridCols = (w + blockSize - 1) / blockSize;
        int gridRows = (h + blockSize - 1) / blockSize;
        float[,] bgGrid = new float[gridRows, gridCols];

        var srcData = src.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var resData = result.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);

        try
        {
            unsafe
            {
                byte* pSrc = (byte*)srcData.Scan0;
                int stride = srcData.Stride;

                for (int gy = 0; gy < gridRows; gy++)
                {
                    int startY = gy * blockSize;
                    int endY = Math.Min(h, startY + blockSize);

                    for (int gx = 0; gx < gridCols; gx++)
                    {
                        int startX = gx * blockSize;
                        int endX = Math.Min(w, startX + blockSize);

                        int count = 0;
                        float sum = 0;
                        float maxLum = 0;

                        for (int py = startY; py < endY; py += 2)
                        {
                            byte* row = pSrc + py * stride;
                            for (int px = startX; px < endX; px += 2)
                            {
                                byte b = row[px * 4 + 0];
                                byte g = row[px * 4 + 1];
                                byte r = row[px * 4 + 2];
                                float lum = 0.299f * r + 0.587f * g + 0.114f * b;
                                if (lum > maxLum) maxLum = lum;
                                sum += lum;
                                count++;
                            }
                        }

                        float bgLum = (maxLum * 0.7f + (count > 0 ? sum / count : 200f) * 0.3f);
                        bgGrid[gy, gx] = Math.Max(80f, bgLum);
                    }
                }

                byte* pRes = (byte*)resData.Scan0;
                int resStride = resData.Stride;

                for (int y = 0; y < h; y++)
                {
                    float gy = (float)y / blockSize;
                    int gy0 = (int)gy;
                    int gy1 = Math.Min(gridRows - 1, gy0 + 1);
                    float fy = gy - gy0;

                    byte* srcRow = pSrc + y * stride;
                    byte* resRow = pRes + y * resStride;

                    for (int x = 0; x < w; x++)
                    {
                        float gx = (float)x / blockSize;
                        int gx0 = (int)gx;
                        int gx1 = Math.Min(gridCols - 1, gx0 + 1);
                        float fx = gx - gx0;

                        float top = bgGrid[gy0, gx0] * (1f - fx) + bgGrid[gy0, gx1] * fx;
                        float bot = bgGrid[gy1, gx0] * (1f - fx) + bgGrid[gy1, gx1] * fx;
                        float bgEst = top * (1f - fy) + bot * fy;
                        if (bgEst < 50f) bgEst = 50f;

                        float scale = 255f / bgEst;

                        byte b = srcRow[x * 4 + 0];
                        byte g = srcRow[x * 4 + 1];
                        byte r = srcRow[x * 4 + 2];

                        float newR = r * scale;
                        float newG = g * scale;
                        float newB = b * scale;

                        float newLum = 0.299f * newR + 0.587f * newG + 0.114f * newB;
                        if (newLum >= 210f)
                        {
                            newR = 255f;
                            newG = 255f;
                            newB = 255f;
                        }
                        else if (newLum <= 90f)
                        {
                            newR *= 0.85f;
                            newG *= 0.85f;
                            newB *= 0.85f;
                        }

                        resRow[x * 4 + 0] = (byte)Math.Clamp(newB, 0, 255);
                        resRow[x * 4 + 1] = (byte)Math.Clamp(newG, 0, 255);
                        resRow[x * 4 + 2] = (byte)Math.Clamp(newR, 0, 255);
                        resRow[x * 4 + 3] = 255;
                    }
                }
            }
        }
        finally
        {
            src.UnlockBits(srcData);
            result.UnlockBits(resData);
        }

        return result;
    }

    /// <summary>
    /// Automatically detects the 4 corner points (TL, TR, BR, BL) of a document/card in a photo.
    /// </summary>
    public async Task<PointF[]?> DetectCardCornersAsync(string imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath)) return null;

        var rect = await DetectCardBoundsAsync(imagePath);
        if (rect != null && rect.Value.Width > 50 && rect.Value.Height > 50)
        {
            var r = rect.Value;
            return new PointF[]
            {
                new PointF(r.Left, r.Top),
                new PointF(r.Right, r.Top),
                new PointF(r.Right, r.Bottom),
                new PointF(r.Left, r.Bottom)
            };
        }

        // Fallback: 85% center quad
        try
        {
            using var img = SmartSaver.Helpers.ImageHelper.LoadOrientedBitmap(imagePath);
            float w = img.Width;
            float h = img.Height;
            float mx = w * 0.08f;
            float my = h * 0.08f;
            return new PointF[]
            {
                new PointF(mx, my),
                new PointF(w - mx, my),
                new PointF(w - mx, h - my),
                new PointF(mx, h - my)
            };
        }
        catch
        {
            return null;
        }
    }

    private class PythonCropResult
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("front_image")]
        public string? FrontImage { get; set; }

        [JsonPropertyName("back_image")]
        public string? BackImage { get; set; }

        [JsonPropertyName("crop_rect")]
        public int[]? CropRect { get; set; }

        [JsonPropertyName("rotated")]
        public bool Rotated { get; set; }

        [JsonPropertyName("width")]
        public int Width { get; set; }

        [JsonPropertyName("height")]
        public int Height { get; set; }

        [JsonPropertyName("aspect_ratio")]
        public double AspectRatio { get; set; }

        [JsonPropertyName("method")]
        public string? Method { get; set; }
    }
}
