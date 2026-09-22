using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Threading.Tasks;
using Serilog;

namespace SmartSaver.Services;

public enum StudioBackgroundType
{
    Original,
    StudioWhite,
    SkyBlue,
    RoyalBlue,
    SoftGray,
    CrimsonRed,
    LightGreen,
    LightCream,
    CustomColor
}

public class PassportBatchItem
{
    public string Name { get; set; } = "Passport";
    public double WidthCm { get; set; } = 3.5;
    public double HeightCm { get; set; } = 4.5;
    public int Count { get; set; } = 8;

    public PassportBatchItem() { }
    public PassportBatchItem(string name, double widthCm, double heightCm, int count)
    {
        Name = name;
        WidthCm = widthCm;
        HeightCm = heightCm;
        Count = count;
    }
}

public class PassportSheetConfig
{
    public string PaperSize { get; set; } = "A4"; // "A4" or "4x6"
    public bool IsLandscape { get; set; } = false;
    public double MarginMm { get; set; } = 6.0;
    public double TopMarginMm { get; set; } = 6.0;
    public double GapMm { get; set; } = 3.0;
    public int BorderThicknessPx { get; set; } = 1;
    public Color BorderColor { get; set; } = Color.FromArgb(180, 180, 180);
    public List<PassportBatchItem> Batches { get; set; } = new();
    public StudioBackgroundType BackgroundType { get; set; } = StudioBackgroundType.Original;
    public Color CustomColor { get; set; } = Color.FromArgb(74, 144, 226);
    public float BackgroundTolerance { get; set; } = 45f;
    public float Brightness { get; set; } = 0f;
    public float Contrast { get; set; } = 0f;
    public float Warmth { get; set; } = 0f;
    public bool AutoEnhance { get; set; } = true;
}

public class SinglePhotoExportConfig
{
    public double WidthCm { get; set; } = 3.5;
    public double HeightCm { get; set; } = 4.5;
    public int Dpi { get; set; } = 300;
    public int MinKb { get; set; } = 20;
    public int MaxKb { get; set; } = 50;
    public bool AddCandidateStamp { get; set; } = false;
    public string CandidateName { get; set; } = string.Empty;
    public string DateOfPhoto { get; set; } = string.Empty;
    public string DatePrefix { get; set; } = "DOP: ";
}

public class SinglePhotoExportResult
{
    public bool Success { get; set; }
    public byte[]? ImageBytes { get; set; }
    public int WidthPx { get; set; }
    public int HeightPx { get; set; }
    public long FileSizeBytes { get; set; }
    public string FormattedSize => $"{FileSizeBytes / 1024.0:F1} KB";
    public string ErrorMessage { get; set; } = string.Empty;
}

public class PassportStudioService
{
    private static readonly Lazy<PassportStudioService> _instance = new(() => new PassportStudioService());
    public static PassportStudioService Instance => _instance.Value;

    public static Color GetBackgroundColor(StudioBackgroundType bgType, Color customColor = default) => bgType switch
    {
        StudioBackgroundType.StudioWhite => Color.FromArgb(255, 255, 255),
        StudioBackgroundType.SkyBlue     => Color.FromArgb(74, 144, 226), // #4A90E2 Official Govt Sky Blue
        StudioBackgroundType.RoyalBlue   => Color.FromArgb(13, 71, 161),  // #0D47A1 Deep Royal Blue
        StudioBackgroundType.SoftGray    => Color.FromArgb(224, 224, 224), // #E0E0E0 Clean Studio Gray
        StudioBackgroundType.CrimsonRed  => Color.FromArgb(183, 28, 28),   // #B71C1C Red (Gulf/Passes)
        StudioBackgroundType.LightGreen  => Color.FromArgb(165, 214, 167), // #A5D6A7 Soft Green
        StudioBackgroundType.LightCream  => Color.FromArgb(253, 251, 247), // #FDFBF7 Soft Off-White
        StudioBackgroundType.CustomColor => (customColor.IsEmpty || customColor.A == 0) ? Color.FromArgb(74, 144, 226) : customColor,
        _ => Color.Transparent
    };

    /// <summary>
    /// Processes a single portrait: background replacement, studio lighting, warmth, sharpening.
    /// Completely decouples unmanaged GDI+ bitmap memory to prevent stream disposal crashes.
    /// </summary>
    public Bitmap ProcessPortrait(Bitmap src, PassportSheetConfig config)
    {
        // Safe deep copy in guaranteed Format32bppArgb to break any stream locks
        Bitmap working;
        lock (src)
        {
            working = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
            working.SetResolution(src.HorizontalResolution, src.VerticalResolution);
            using (var g = Graphics.FromImage(working))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.SmoothingMode = SmoothingMode.HighQuality;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(src, 0, 0, src.Width, src.Height);
            }
        }

        try
        {
            // 1. Studio Lighting & Tone fixes
            if (config.AutoEnhance || Math.Abs(config.Brightness) > 0.01f || Math.Abs(config.Contrast) > 0.01f || Math.Abs(config.Warmth) > 0.01f)
            {
                var enhanced = EnhanceLightingAndTone(working, config.Brightness, config.Contrast, config.Warmth, config.AutoEnhance);
                working.Dispose();
                working = enhanced;
            }

            // 2. Background replacement if selected
            if (config.BackgroundType != StudioBackgroundType.Original)
            {
                Color targetBg = GetBackgroundColor(config.BackgroundType, config.CustomColor);
                var replaced = ReplacePortraitBackground(working, targetBg, config.BackgroundTolerance);
                working.Dispose();
                working = replaced;
            }

            return working;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to process portrait");
            return working;
        }
    }

    /// <summary>
    /// Replaces the photo background with smooth anti-aliased edge blending and boundary-connected protection.
    /// Intelligently analyzes all 4 border zones (top, bottom, left, right) using color variance to determine
    /// the true background orientation, even if the photo has been rotated 90°, 180° (upside down), or 270°.
    /// Employs edge-connected flood-fill masking so clothing, shirt patterns, and white checks
    /// inside the subject's silhouette are never bleached or eroded.
    /// </summary>
    public unsafe Bitmap ReplacePortraitBackground(Bitmap src, Color newBg, float tolerance = 45f)
    {
        int w = src.Width;
        int h = src.Height;

        if (w < 4 || h < 4) return (Bitmap)src.Clone();

        int boxW = Math.Max(10, Math.Min(w / 8, 80));
        int boxH = Math.Max(10, Math.Min(h / 8, 80));

        // 4 corner regions: 0=Top-Left, 1=Top-Right, 2=Bottom-Right, 3=Bottom-Left
        Rectangle[] corners = new Rectangle[]
        {
            new Rectangle(0, 0, boxW, boxH),
            new Rectangle(w - boxW, 0, boxW, boxH),
            new Rectangle(w - boxW, h - boxH, boxW, boxH),
            new Rectangle(0, h - boxH, boxW, boxH)
        };

        BitmapData data = src.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        byte* ptr = (byte*)data.Scan0;
        int stride = data.Stride;

        float[] cornerStds = new float[4];
        int[] cornerR = new int[4], cornerG = new int[4], cornerB = new int[4];

        for (int c = 0; c < 4; c++)
        {
            List<byte> rL = new List<byte>(boxW * boxH / 4);
            List<byte> gL = new List<byte>(boxW * boxH / 4);
            List<byte> bL = new List<byte>(boxW * boxH / 4);
            Rectangle rect = corners[c];

            for (int y = rect.Top; y < rect.Bottom; y += 2)
            {
                byte* row = ptr + y * stride;
                for (int x = rect.Left; x < rect.Right; x += 2)
                {
                    bL.Add(row[x * 4 + 0]);
                    gL.Add(row[x * 4 + 1]);
                    rL.Add(row[x * 4 + 2]);
                }
            }

            if (rL.Count == 0) continue;

            double sumR = 0, sumG = 0, sumB = 0;
            for (int i = 0; i < rL.Count; i++) { sumR += rL[i]; sumG += gL[i]; sumB += bL[i]; }
            double avgR = sumR / rL.Count, avgG = sumG / rL.Count, avgB = sumB / rL.Count;
            double varSum = 0;
            for (int i = 0; i < rL.Count; i++)
            {
                double dR = rL[i] - avgR, dG = gL[i] - avgG, dB = bL[i] - avgB;
                varSum += dR * dR + dG * dG + dB * dB;
            }
            cornerStds[c] = (float)Math.Sqrt(varSum / rL.Count);
            cornerR[c] = (int)Math.Round(avgR);
            cornerG[c] = (int)Math.Round(avgG);
            cornerB[c] = (int)Math.Round(avgB);
        }

        // Find corner with lowest variance (background has uniform color, clothing has high variance/texture)
        int bestCorner = 0;
        float minStd = cornerStds[0];
        for (int c = 1; c < 4; c++)
        {
            if (cornerStds[c] < minStd)
            {
                minStd = cornerStds[c];
                bestCorner = c;
            }
        }

        // Check adjacent corners
        int adj1 = (bestCorner + 1) % 4;
        int adj2 = (bestCorner + 3) % 4;
        int otherCorner = cornerStds[adj1] < cornerStds[adj2] ? adj1 : adj2;

        int bgR = cornerR[bestCorner];
        int bgG = cornerG[bestCorner];
        int bgB = cornerB[bestCorner];

        // If other corner is also low variance and similar color, average them
        float cornerDiff = (float)Math.Sqrt(Math.Pow(cornerR[bestCorner] - cornerR[otherCorner], 2) +
                                            Math.Pow(cornerG[bestCorner] - cornerG[otherCorner], 2) +
                                            Math.Pow(cornerB[bestCorner] - cornerB[otherCorner], 2));
        if (cornerStds[otherCorner] < minStd * 3.5f + 15f && cornerDiff < 60f)
        {
            bgR = (bgR + cornerR[otherCorner]) / 2;
            bgG = (bgG + cornerG[otherCorner]) / 2;
            bgB = (bgB + cornerB[otherCorner]) / 2;
        }

        // Determine which side is the clothing side (opposite the low variance background)
        // 0=Top-Left, 1=Top-Right -> Background is at Top -> Clothing is at Bottom
        // 2=Bottom-Right, 3=Bottom-Left -> Background is at Bottom -> Clothing is at Top
        bool clothingAtBottom = (bestCorner == 0 || bestCorner == 1);
        bool clothingAtTop = (bestCorner == 2 || bestCorner == 3);

        // Sensitivity threshold calculation
        float tol = Math.Clamp(tolerance, 15f, 120f);
        float thresholdLow = tol * 0.70f;
        float thresholdHigh = tol * 1.30f;

        // BFS flood-fill mask: 0 = unvisited/subject, 1 = background
        byte[] mask = new byte[w * h];
        int[] q = new int[w * h];
        int qHead = 0, qTail = 0;

        // Seed along image borders (excluding the clothing side)
        if (!clothingAtTop)
        {
            for (int x = 0; x < w; x++)
            {
                byte* p = ptr + 0 * stride + x * 4;
                float dr = p[2] - bgR, dg = p[1] - bgG, db = p[0] - bgB;
                if ((float)Math.Sqrt(2 * dr * dr + 4 * dg * dg + 3 * db * db) < thresholdHigh)
                {
                    mask[x] = 1;
                    q[qTail++] = x;
                }
            }
        }

        if (!clothingAtBottom)
        {
            int botRowOffset = (h - 1) * w;
            for (int x = 0; x < w; x++)
            {
                byte* p = ptr + (h - 1) * stride + x * 4;
                float dr = p[2] - bgR, dg = p[1] - bgG, db = p[0] - bgB;
                if ((float)Math.Sqrt(2 * dr * dr + 4 * dg * dg + 3 * db * db) < thresholdHigh)
                {
                    int idx = botRowOffset + x;
                    if (mask[idx] == 0) { mask[idx] = 1; q[qTail++] = idx; }
                }
            }
        }

        // Left & Right borders (seed only upper 75% if clothing is at bottom, or lower 75% if clothing is at top)
        int yStart = clothingAtTop ? (int)(h * 0.25) : 0;
        int yEnd = clothingAtBottom ? (int)(h * 0.75) : h;

        for (int y = yStart; y < yEnd; y++)
        {
            // Left
            byte* pL = ptr + y * stride + 0 * 4;
            float drL = pL[2] - bgR, dgL = pL[1] - bgG, dbL = pL[0] - bgB;
            if ((float)Math.Sqrt(2 * drL * drL + 4 * dgL * dgL + 3 * dbL * dbL) < thresholdHigh)
            {
                int idx = y * w;
                if (mask[idx] == 0) { mask[idx] = 1; q[qTail++] = idx; }
            }

            // Right
            byte* pR = ptr + y * stride + (w - 1) * 4;
            float drR = pR[2] - bgR, dgR = pR[1] - bgG, dbR = pR[0] - bgB;
            if ((float)Math.Sqrt(2 * drR * drR + 4 * dgR * dgR + 3 * dbR * dbR) < thresholdHigh)
            {
                int idx = y * w + (w - 1);
                if (mask[idx] == 0) { mask[idx] = 1; q[qTail++] = idx; }
            }
        }

        // BFS traversal: expands background only through connected background pixels
        while (qHead < qTail)
        {
            int curr = q[qHead++];
            int cy = curr / w;
            int cx = curr % w;

            // 4-way neighbors checked directly without stackalloc
            if (cy > 0)
            {
                int nIdx = curr - w;
                if (mask[nIdx] == 0)
                {
                    byte* nPtr = ptr + (cy - 1) * stride + cx * 4;
                    float dr = nPtr[2] - bgR, dg = nPtr[1] - bgG, db = nPtr[0] - bgB;
                    if ((float)Math.Sqrt(2 * dr * dr + 4 * dg * dg + 3 * db * db) < thresholdHigh)
                    {
                        mask[nIdx] = 1;
                        q[qTail++] = nIdx;
                    }
                    else mask[nIdx] = 2;
                }
            }
            if (cy < h - 1)
            {
                int nIdx = curr + w;
                if (mask[nIdx] == 0)
                {
                    byte* nPtr = ptr + (cy + 1) * stride + cx * 4;
                    float dr = nPtr[2] - bgR, dg = nPtr[1] - bgG, db = nPtr[0] - bgB;
                    if ((float)Math.Sqrt(2 * dr * dr + 4 * dg * dg + 3 * db * db) < thresholdHigh)
                    {
                        mask[nIdx] = 1;
                        q[qTail++] = nIdx;
                    }
                    else mask[nIdx] = 2;
                }
            }
            if (cx > 0)
            {
                int nIdx = curr - 1;
                if (mask[nIdx] == 0)
                {
                    byte* nPtr = ptr + cy * stride + (cx - 1) * 4;
                    float dr = nPtr[2] - bgR, dg = nPtr[1] - bgG, db = nPtr[0] - bgB;
                    if ((float)Math.Sqrt(2 * dr * dr + 4 * dg * dg + 3 * db * db) < thresholdHigh)
                    {
                        mask[nIdx] = 1;
                        q[qTail++] = nIdx;
                    }
                    else mask[nIdx] = 2;
                }
            }
            if (cx < w - 1)
            {
                int nIdx = curr + 1;
                if (mask[nIdx] == 0)
                {
                    byte* nPtr = ptr + cy * stride + (cx + 1) * 4;
                    float dr = nPtr[2] - bgR, dg = nPtr[1] - bgG, db = nPtr[0] - bgB;
                    if ((float)Math.Sqrt(2 * dr * dr + 4 * dg * dg + 3 * db * db) < thresholdHigh)
                    {
                        mask[nIdx] = 1;
                        q[qTail++] = nIdx;
                    }
                    else mask[nIdx] = 2;
                }
            }
        }

        // Render destination with anti-aliased edge-feathering
        Bitmap dest = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        dest.SetResolution(src.HorizontalResolution, src.VerticalResolution);

        BitmapData dstData = dest.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        byte* dstPtr = (byte*)dstData.Scan0;
        int dStride = dstData.Stride;

        for (int y = 0; y < h; y++)
        {
            byte* sRow = ptr + y * stride;
            byte* dRow = dstPtr + y * dStride;
            int rowOffset = y * w;

            for (int x = 0; x < w; x++)
            {
                byte b = sRow[x * 4 + 0];
                byte g = sRow[x * 4 + 1];
                byte r = sRow[x * 4 + 2];
                byte a = sRow[x * 4 + 3];

                int m = mask[rowOffset + x];
                if (m == 1)
                {
                    float dr = r - bgR, dg = g - bgG, db = b - bgB;
                    float dist = (float)Math.Sqrt(2 * dr * dr + 4 * dg * dg + 3 * db * db);

                    if (dist < thresholdLow)
                    {
                        dRow[x * 4 + 0] = newBg.B;
                        dRow[x * 4 + 1] = newBg.G;
                        dRow[x * 4 + 2] = newBg.R;
                        dRow[x * 4 + 3] = 255;
                    }
                    else
                    {
                        // Transition edge: smooth anti-aliased alpha blend
                        float alpha = (dist - thresholdLow) / (thresholdHigh - thresholdLow);
                        dRow[x * 4 + 0] = (byte)Math.Clamp(b * alpha + newBg.B * (1f - alpha), 0, 255);
                        dRow[x * 4 + 1] = (byte)Math.Clamp(g * alpha + newBg.G * (1f - alpha), 0, 255);
                        dRow[x * 4 + 2] = (byte)Math.Clamp(r * alpha + newBg.R * (1f - alpha), 0, 255);
                        dRow[x * 4 + 3] = 255;
                    }
                }
                else
                {
                    // 100% protected subject (clothing, face, hair)
                    dRow[x * 4 + 0] = b;
                    dRow[x * 4 + 1] = g;
                    dRow[x * 4 + 2] = r;
                    dRow[x * 4 + 3] = a;
                }
            }
        }

        src.UnlockBits(data);
        dest.UnlockBits(dstData);
        return dest;
    }

    /// <summary>
    /// Studio lighting & tone enhancements: brightens shadows, adjusts contrast, and warms skin tones.
    /// Single LockBits pass ensures zero GDI+ lock collisions.
    /// </summary>
    public unsafe Bitmap EnhanceLightingAndTone(Bitmap src, float brightness, float contrast, float warmth, bool autoEnhance)
    {
        int w = src.Width;
        int h = src.Height;

        Bitmap dest = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        dest.SetResolution(src.HorizontalResolution, src.VerticalResolution);

        BitmapData srcData = src.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        BitmapData dstData = dest.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);

        try
        {
            byte* srcPtr = (byte*)srcData.Scan0;
            byte* dstPtr = (byte*)dstData.Scan0;
            int sStride = srcData.Stride;
            int dStride = dstData.Stride;

            // Pre-calculate auto-brightness boost in the same pass if enabled
            float autoBoost = 0f;
            if (autoEnhance)
            {
                long lumSum = 0;
                int count = 0;
                int step = Math.Max(1, Math.Min(w, h) / 50);

                for (int y = h / 4; y < 3 * h / 4; y += step)
                {
                    byte* row = srcPtr + y * sStride;
                    for (int x = w / 4; x < 3 * w / 4; x += step)
                    {
                        lumSum += (long)(0.299f * row[x * 4 + 2] + 0.587f * row[x * 4 + 1] + 0.114f * row[x * 4 + 0]);
                        count++;
                    }
                }

                if (count > 0)
                {
                    float avgLum = (float)lumSum / count;
                    if (avgLum < 125)
                    {
                        autoBoost = Math.Min(35f, (135f - avgLum) * 0.6f);
                    }
                }
            }

            float totalBrightness = brightness + autoBoost;
            float cFactor = (259f * (contrast + 100f)) / (100f * (259f - contrast));

            for (int y = 0; y < h; y++)
            {
                byte* sRow = srcPtr + y * sStride;
                byte* dRow = dstPtr + y * dStride;

                for (int x = 0; x < w; x++)
                {
                    float b = sRow[x * 4 + 0];
                    float g = sRow[x * 4 + 1];
                    float r = sRow[x * 4 + 2];
                    byte a = sRow[x * 4 + 3];

                    // Brightness
                    r += totalBrightness;
                    g += totalBrightness;
                    b += totalBrightness;

                    // Contrast
                    r = cFactor * (r - 128f) + 128f;
                    g = cFactor * (g - 128f) + 128f;
                    b = cFactor * (b - 128f) + 128f;

                    // Skin Warmth
                    if (warmth > 0)
                    {
                        r += warmth * 0.8f;
                        g += warmth * 0.3f;
                        b -= warmth * 0.4f;
                    }
                    else if (warmth < 0)
                    {
                        b -= warmth * 0.6f;
                    }

                    dRow[x * 4 + 0] = (byte)Math.Clamp(b, 0f, 255f);
                    dRow[x * 4 + 1] = (byte)Math.Clamp(g, 0f, 255f);
                    dRow[x * 4 + 2] = (byte)Math.Clamp(r, 0f, 255f);
                    dRow[x * 4 + 3] = a;
                }
            }
        }
        finally
        {
            src.UnlockBits(srcData);
            dest.UnlockBits(dstData);
        }

        return dest;
    }

    /// <summary>
    /// Calculates maximum photos that can fit on a sheet with zero paper waste.
    /// Uses adaptive margin down to physical printer limit (3.0 mm) so extra columns fit smartly.
    /// </summary>
    public (int cols, int rows, int total) CalculateCapacity(string paperSize, double photoWidthCm, double photoHeightCm, double gapMm, double marginMm = 6.0, bool isLandscape = false, double topMarginMm = 6.0)
    {
        bool is4x6 = paperSize.Equals("4x6", StringComparison.OrdinalIgnoreCase);
        double sheetW = is4x6 ? 10.16 : 21.0;
        double sheetH = is4x6 ? 15.24 : 29.7;
        if (isLandscape)
        {
            (sheetW, sheetH) = (sheetH, sheetW);
        }

        double sheetWMm = sheetW * 10.0;
        double sheetHMm = sheetH * 10.0;
        double photoWMm = photoWidthCm * 10.0;
        double photoHMm = photoHeightCm * 10.0;

        if (photoWMm <= 0 || photoHMm <= 0) return (0, 0, 0);

        // Standard modern inkjet/laser printable margin limit: 3.0 mm
        double minMarginMm = 3.0;
        double availW = Math.Max(0, sheetWMm - 2.0 * minMarginMm);
        // Vertical space takes into account top margin starting position
        double effectiveTopMargin = Math.Max(minMarginMm, topMarginMm);
        double availH = Math.Max(0, sheetHMm - effectiveTopMargin - minMarginMm);

        int cols = (int)Math.Floor((availW + gapMm + 0.001) / (photoWMm + gapMm));
        int rows = (int)Math.Floor((availH + gapMm + 0.001) / (photoHMm + gapMm));
        cols = Math.Max(0, cols);
        rows = Math.Max(0, rows);
        return (cols, rows, cols * rows);
    }

    /// <summary>
    /// Draws the portrait into the target destination rectangle while strictly preserving aspect ratio.
    /// Centers the photo within the slot and crops uniformly (UniformToFill) so the entire slot is cleanly filled
    /// with zero squishing or facial distortion.
    /// </summary>
    public static void DrawPortraitAspectFilled(Graphics g, Bitmap portraitBmp, Rectangle destRect)
    {
        if (portraitBmp.Width <= 0 || portraitBmp.Height <= 0 || destRect.Width <= 0 || destRect.Height <= 0) return;

        double srcRatio = (double)portraitBmp.Width / portraitBmp.Height;
        double destRatio = (double)destRect.Width / destRect.Height;

        int srcX = 0, srcY = 0;
        int srcW = portraitBmp.Width;
        int srcH = portraitBmp.Height;

        if (srcRatio > destRatio)
        {
            // Source is wider than destination: crop equal horizontal margins from left and right
            srcW = (int)Math.Round(portraitBmp.Height * destRatio);
            srcX = Math.Max(0, (portraitBmp.Width - srcW) / 2);
        }
        else if (srcRatio < destRatio)
        {
            // Source is taller than destination: crop equal vertical margins from top and bottom
            srcH = (int)Math.Round(portraitBmp.Width / destRatio);
            srcY = Math.Max(0, (portraitBmp.Height - srcH) / 2);
        }

        // Clamp crop rectangle within image boundaries
        srcW = Math.Min(srcW, portraitBmp.Width - srcX);
        srcH = Math.Min(srcH, portraitBmp.Height - srcY);

        g.DrawImage(portraitBmp, destRect, new Rectangle(srcX, srcY, srcW, srcH), GraphicsUnit.Pixel);
    }

    /// <summary>
    /// Generates a sheet containing single-size or multi-size combo batches.
    /// Supports configurable DPI: 100 DPI for lightning-fast live screen preview,
    /// and full 300 DPI for printing and saving.
    /// Implements smart space detection: fits maximum columns and utilizes available row space smartly.
    /// </summary>
    public async Task<Bitmap> GenerateSheetAsync(Bitmap portraitBmp, PassportSheetConfig config, int dpi = 300)
    {
        return await Task.Run(() =>
        {
            int renderDpi = Math.Clamp(dpi, 72, 600);
            bool is4x6 = config.PaperSize.Equals("4x6", StringComparison.OrdinalIgnoreCase);

            // Sheet physical size in cm
            double sheetWidthCm = is4x6 ? 10.16 : 21.0;
            double sheetHeightCm = is4x6 ? 15.24 : 29.7;
            if (config.IsLandscape)
            {
                (sheetWidthCm, sheetHeightCm) = (sheetHeightCm, sheetWidthCm);
            }

            // Sheet dimensions in pixels at requested DPI
            int sheetW = (int)Math.Round(sheetWidthCm / 2.54 * renderDpi);
            int sheetH = (int)Math.Round(sheetHeightCm / 2.54 * renderDpi);

            Bitmap sheet = new Bitmap(sheetW, sheetH, PixelFormat.Format24bppRgb);
            sheet.SetResolution(renderDpi, renderDpi);

            using (var g = Graphics.FromImage(sheet))
            {
                // Pure photo-paper white background
                g.Clear(Color.White);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.SmoothingMode = SmoothingMode.HighQuality;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;

                int minMarginPx = (int)Math.Round(3.0 / 10.0 / 2.54 * renderDpi);
                int userMarginPx = (int)Math.Round(config.MarginMm / 10.0 / 2.54 * renderDpi);
                int userTopMarginPx = (int)Math.Round(config.TopMarginMm / 10.0 / 2.54 * renderDpi);
                int gapPx = (int)Math.Round(config.GapMm / 10.0 / 2.54 * renderDpi);

                int borderPx = (int)Math.Max(1, Math.Round(config.BorderThicknessPx * (renderDpi / 300.0)));
                using var borderPen = new Pen(config.BorderColor, borderPx);

                // Flatten all batch items into a work list
                var pendingItems = new List<(string Name, int WidthPx, int HeightPx)>();
                foreach (var batch in config.Batches)
                {
                    if (batch.Count <= 0 || batch.WidthCm <= 0 || batch.HeightCm <= 0) continue;
                    int wPx = (int)Math.Round(batch.WidthCm / 2.54 * renderDpi);
                    int hPx = (int)Math.Round(batch.HeightCm / 2.54 * renderDpi);
                    for (int i = 0; i < batch.Count; i++)
                    {
                        pendingItems.Add((batch.Name, wPx, hPx));
                    }
                }

                if (pendingItems.Count == 0) return sheet;

                // Check if all items have identical dimensions (Single-Size Grid)
                bool isSingleSize = pendingItems.All(item => item.WidthPx == pendingItems[0].WidthPx && item.HeightPx == pendingItems[0].HeightPx);

                if (isSingleSize)
                {
                    // SMART GRID ALIGNMENT: Calculate max columns using physical margin floor (3.0 mm)
                    int itemWPx = pendingItems[0].WidthPx;
                    int itemHPx = pendingItems[0].HeightPx;

                    int maxCols = Math.Max(1, (int)Math.Floor((sheetW - 2 * minMarginPx + gapPx + 0.5) / (itemWPx + gapPx)));
                    int maxRows = Math.Max(1, (int)Math.Floor((sheetH - 2 * minMarginPx + gapPx + 0.5) / (itemHPx + gapPx)));

                    // Center the full grid horizontally across the page
                    int gridWidthPx = maxCols * itemWPx + (maxCols - 1) * gapPx;
                    int startX = Math.Max(minMarginPx, (sheetW - gridWidthPx) / 2);

                    // Starting Y respects user top margin while ensuring it fits on sheet
                    int startY = Math.Max(0, userTopMarginPx);

                    int placed = 0;
                    int totalToPlace = pendingItems.Count;

                    for (int r = 0; r < maxRows && placed < totalToPlace; r++)
                    {
                        int y = startY + r * (itemHPx + gapPx);
                        if (y + itemHPx > sheetH - minMarginPx) break;

                        for (int c = 0; c < maxCols && placed < totalToPlace; c++)
                        {
                            int x = startX + c * (itemWPx + gapPx);

                            // Draw passport photo preserving exact aspect ratio without facial distortion
                            DrawPortraitAspectFilled(g, portraitBmp, new Rectangle(x, y, itemWPx, itemHPx));

                            // Draw cutting border line
                            if (config.BorderThicknessPx > 0)
                            {
                                g.DrawRectangle(borderPen, x, y, itemWPx - 1, itemHPx - 1);
                            }

                            placed++;
                        }
                    }
                }
                else
                {
                    // SMART 2D BIN PACKING (Multi-Size Combo):
                    // Lookahead shelf packer - if an item does not fit in remaining row space,
                    // check if ANY subsequent smaller item can fit there, and paste it!
                    int currentY = Math.Max(0, userTopMarginPx);
                    int currentX = minMarginPx;
                    int shelfHeight = 0;

                    while (pendingItems.Count > 0)
                    {
                        int remainingWidthInRow = sheetW - minMarginPx - currentX;

                        // Look for an item that fits in the available space
                        int candidateIndex = -1;
                        for (int i = 0; i < pendingItems.Count; i++)
                        {
                            if (pendingItems[i].WidthPx <= remainingWidthInRow)
                            {
                                candidateIndex = i;
                                break;
                            }
                        }

                        if (candidateIndex >= 0)
                        {
                            // An item fits in this row! Paste it right here!
                            var item = pendingItems[candidateIndex];
                            pendingItems.RemoveAt(candidateIndex);

                            // Check vertical fit
                            if (currentY + item.HeightPx > sheetH - minMarginPx)
                            {
                                break; // Reached bottom of sheet
                            }

                            // Draw passport photo preserving exact aspect ratio without facial distortion
                            DrawPortraitAspectFilled(g, portraitBmp, new Rectangle(currentX, currentY, item.WidthPx, item.HeightPx));
                            if (config.BorderThicknessPx > 0)
                            {
                                g.DrawRectangle(borderPen, currentX, currentY, item.WidthPx - 1, item.HeightPx - 1);
                            }

                            shelfHeight = Math.Max(shelfHeight, item.HeightPx);
                            currentX += item.WidthPx + gapPx;
                        }
                        else
                        {
                            // Nothing fits in remaining space of this row -> start new row
                            currentX = minMarginPx;
                            currentY += (shelfHeight > 0 ? shelfHeight : 100) + gapPx;
                            shelfHeight = 0;

                            // Check if next row is off page
                            if (currentY + pendingItems[0].HeightPx > sheetH - minMarginPx)
                            {
                                break; // Sheet full
                            }
                        }
                    }
                }
            }

            return sheet;
        });
    }

    /// <summary>
    /// Exports a single processed passport photo meeting strict government portal rules
    /// (e.g. SSC, UPSC, IBPS, Railways, PAN Card, Passport Seva).
    /// Handles exact sizing, optional candidate Name & Date of Photo (DOP) stamp,
    /// 300 DPI header metadata, and binary-search JPEG size clamping (e.g. 20 KB - 50 KB).
    /// </summary>
    public SinglePhotoExportResult ExportSinglePassportPhoto(
        Bitmap src,
        PassportSheetConfig sheetConfig,
        SinglePhotoExportConfig exportConfig)
    {
        if (src == null)
        {
            return new SinglePhotoExportResult { Success = false, ErrorMessage = "Source portrait is null." };
        }

        try
        {
            // 1. Process portrait with user's selected background color, lighting, warmth, auto-enhance
            using var processedPortrait = ProcessPortrait(src, sheetConfig);

            // 2. Calculate pixel dimensions based on cm and DPI
            int targetW = Math.Max(50, (int)Math.Round((exportConfig.WidthCm / 2.54) * exportConfig.Dpi));
            int targetH = Math.Max(50, (int)Math.Round((exportConfig.HeightCm / 2.54) * exportConfig.Dpi));

            // 3. Render onto target canvas with exact DPI
            using var canvas = new Bitmap(targetW, targetH, PixelFormat.Format24bppRgb);
            canvas.SetResolution(exportConfig.Dpi, exportConfig.Dpi);

            using (var g = Graphics.FromImage(canvas))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.SmoothingMode = SmoothingMode.HighQuality;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

                // Fill canvas with background color
                Color bg = GetBackgroundColor(sheetConfig.BackgroundType, sheetConfig.CustomColor);
                if (bg.IsEmpty || bg.A == 0) bg = Color.White;
                using (var bgBrush = new SolidBrush(bg))
                {
                    g.FillRectangle(bgBrush, 0, 0, targetW, targetH);
                }

                // Draw processed portrait scaled to fill
                g.DrawImage(processedPortrait, 0, 0, targetW, targetH);

                // 4. Optional: Candidate Name and Date of Photo (DOP) Stamp
                if (exportConfig.AddCandidateStamp &&
                    (!string.IsNullOrWhiteSpace(exportConfig.CandidateName) || !string.IsNullOrWhiteSpace(exportConfig.DateOfPhoto)))
                {
                    // Standard Govt Exam banner height (~17% of photo height, ~90px at 300 DPI)
                    int bannerHeight = Math.Max(36, (int)Math.Round(targetH * 0.17f));
                    int bannerY = targetH - bannerHeight;

                    // Pure solid white background
                    using (var whiteBrush = new SolidBrush(Color.White))
                    {
                        g.FillRectangle(whiteBrush, 0, bannerY, targetW, bannerHeight);
                    }

                    // Neat top border line separating banner from candidate shirt
                    using (var borderPen = new Pen(Color.FromArgb(190, 190, 190), 1.5f))
                    {
                        g.DrawLine(borderPen, 0, bannerY, targetW, bannerY);
                    }

                    string line1 = (exportConfig.CandidateName ?? string.Empty).Trim().ToUpperInvariant();
                    string line2;
                    if (string.IsNullOrWhiteSpace(exportConfig.DateOfPhoto))
                    {
                        line2 = string.Empty;
                    }
                    else
                    {
                        string trimmedDate = exportConfig.DateOfPhoto.Trim();
                        if (trimmedDate.StartsWith("DOP:", StringComparison.OrdinalIgnoreCase) ||
                            trimmedDate.StartsWith("DOB:", StringComparison.OrdinalIgnoreCase) ||
                            trimmedDate.StartsWith("DATE:", StringComparison.OrdinalIgnoreCase))
                        {
                            line2 = trimmedDate.ToUpperInvariant();
                        }
                        else
                        {
                            string prefix = string.IsNullOrWhiteSpace(exportConfig.DatePrefix) ? "DOP:" : exportConfig.DatePrefix.Trim();
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

                        // Use GraphicsUnit.Pixel so font is not multiplied by (300 DPI / 72)
                        float nameFontPx = Math.Clamp(halfH * 0.54f, 11f, 24f);
                        Font nameFont = new Font("Arial", nameFontPx, FontStyle.Bold, GraphicsUnit.Pixel);
                        while (g.MeasureString(line1, nameFont).Width > (targetW - 14) && nameFontPx > 6.5f)
                        {
                            nameFontPx -= 0.5f;
                            nameFont.Dispose();
                            nameFont = new Font("Arial", nameFontPx, FontStyle.Bold, GraphicsUnit.Pixel);
                        }

                        float dateFontPx = Math.Clamp(halfH * 0.48f, 10f, 21f);
                        Font dateFont = new Font("Arial", dateFontPx, FontStyle.Bold, GraphicsUnit.Pixel);
                        while (g.MeasureString(line2, dateFont).Width > (targetW - 14) && dateFontPx > 6.5f)
                        {
                            dateFontPx -= 0.5f;
                            dateFont.Dispose();
                            dateFont = new Font("Arial", dateFontPx, FontStyle.Bold, GraphicsUnit.Pixel);
                        }

                        var rectName = new RectangleF(4, bannerY + 1, targetW - 8, halfH - 2);
                        var rectDate = new RectangleF(4, bannerY + halfH + 1, targetW - 8, halfH - 2);

                        g.DrawString(line1, nameFont, textBrush, rectName, sf);
                        g.DrawString(line2, dateFont, textBrush, rectDate, sf);

                        nameFont.Dispose();
                        dateFont.Dispose();
                    }
                    else
                    {
                        // 1 line only: Centered vertically in entire banner
                        string singleLine = !string.IsNullOrEmpty(line1) ? line1 : line2;
                        float singleFontPx = Math.Clamp(bannerHeight * 0.45f, 12f, 26f);
                        Font singleFont = new Font("Arial", singleFontPx, FontStyle.Bold, GraphicsUnit.Pixel);
                        while (g.MeasureString(singleLine, singleFont).Width > (targetW - 14) && singleFontPx > 7f)
                        {
                            singleFontPx -= 0.5f;
                            singleFont.Dispose();
                            singleFont = new Font("Arial", singleFontPx, FontStyle.Bold, GraphicsUnit.Pixel);
                        }

                        var rectSingle = new RectangleF(4, bannerY + 2, targetW - 8, bannerHeight - 4);
                        g.DrawString(singleLine, singleFont, textBrush, rectSingle, sf);
                        singleFont.Dispose();
                    }
                }
            }

            // 5. Compress to target bytes strictly within [MinKb, MaxKb]
            long minBytes = exportConfig.MinKb * 1024L;
            long maxBytes = exportConfig.MaxKb * 1024L;
            if (maxBytes <= 0) maxBytes = 50 * 1024L;
            if (minBytes > maxBytes) minBytes = maxBytes / 2;

            byte[] finalBytes = CompressGdiToJpegBytes(canvas, minBytes, maxBytes);

            return new SinglePhotoExportResult
            {
                Success = true,
                ImageBytes = finalBytes,
                WidthPx = targetW,
                HeightPx = targetH,
                FileSizeBytes = finalBytes.Length
            };
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to export single passport photo");
            return new SinglePhotoExportResult { Success = false, ErrorMessage = ex.Message };
        }
    }

    private static byte[] CompressGdiToJpegBytes(Bitmap bmp, long minBytes, long maxBytes)
    {
        ImageCodecInfo? jpegCodec = GetEncoder(ImageFormat.Jpeg);
        if (jpegCodec == null)
        {
            using var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Jpeg);
            return ms.ToArray();
        }

        int lowQuality = 20;
        int highQuality = 98;
        byte[]? bestBytes = null;

        // Binary search up to 8 passes
        for (int i = 0; i < 8; i++)
        {
            int midQuality = (lowQuality + highQuality) / 2;
            using var encParams = new EncoderParameters(1);
            encParams.Param[0] = new EncoderParameter(Encoder.Quality, (long)midQuality);

            using var ms = new MemoryStream();
            bmp.Save(ms, jpegCodec, encParams);
            byte[] bytes = ms.ToArray();

            if (bytes.Length <= maxBytes)
            {
                bestBytes = bytes;
                lowQuality = midQuality + 1; // Try higher quality
            }
            else
            {
                highQuality = midQuality - 1; // Exceeded max, reduce quality
            }

            if (lowQuality > highQuality) break;
        }

        byte[] resultBytes = bestBytes ?? Array.Empty<byte>();
        if (resultBytes.Length == 0)
        {
            using var encParams = new EncoderParameters(1);
            encParams.Param[0] = new EncoderParameter(Encoder.Quality, 20L);
            using var ms = new MemoryStream();
            bmp.Save(ms, jpegCodec, encParams);
            resultBytes = ms.ToArray();
        }

        // Pad with standard JPEG comment if under minBytes
        if (minBytes > 0 && resultBytes.Length < minBytes)
        {
            resultBytes = PadJpegBytes(resultBytes, minBytes, maxBytes);
        }

        return resultBytes;
    }

    private static ImageCodecInfo? GetEncoder(ImageFormat format)
    {
        var codecs = ImageCodecInfo.GetImageDecoders();
        foreach (var codec in codecs)
        {
            if (codec.FormatID == format.Guid) return codec;
        }
        return null;
    }

    private static byte[] PadJpegBytes(byte[] jpegBytes, long minBytes, long maxBytes)
    {
        if (jpegBytes.Length >= minBytes || jpegBytes.Length < 4) return jpegBytes;
        if (jpegBytes[0] != 0xFF || jpegBytes[1] != 0xD8) return jpegBytes;

        long needed = (minBytes - jpegBytes.Length) + 256;
        long maxAllowedPad = maxBytes - jpegBytes.Length - 100;
        if (maxAllowedPad <= 0) return jpegBytes;
        needed = Math.Min(needed, maxAllowedPad);
        if (needed < 4) return jpegBytes;

        int padLength = (int)Math.Min(needed, 65500);
        byte[] paddingMarker = new byte[padLength];
        paddingMarker[0] = 0xFF;
        paddingMarker[1] = 0xFE; // COM (Comment marker)
        int payloadLen = padLength - 2;
        paddingMarker[2] = (byte)(payloadLen >> 8);
        paddingMarker[3] = (byte)(payloadLen & 0xFF);
        for (int i = 4; i < padLength; i++)
        {
            paddingMarker[i] = 0x20; // safe space byte
        }

        using var ms = new MemoryStream(jpegBytes.Length + padLength);
        ms.Write(jpegBytes, 0, 2); // SOI (0xFF, 0xD8)
        ms.Write(paddingMarker, 0, paddingMarker.Length); // COM block
        ms.Write(jpegBytes, 2, jpegBytes.Length - 2); // remaining JPEG payload
        return ms.ToArray();
    }
}

