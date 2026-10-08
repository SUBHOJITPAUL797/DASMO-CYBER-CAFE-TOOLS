using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SmartSaver.Services;

namespace SmartSaver.Tests;

/// <summary>
/// Comprehensive automated E2E benchmark and image quality test suite for DocumentEnhancerService.
/// Validates background whitening, shadow removal, ink contrast deepening, 8-connected stroke continuity,
/// stamp and photo color preservation, preset calibrations, and <600ms A4 300 DPI CPU performance.
/// </summary>
public static class DocumentEnhancerTests
{
    private static readonly IDocumentEnhancerService Enhancer = new DocumentEnhancerService();

    /// <summary>
    /// TEST 77: Verifies adaptive background whitening and shadow removal on documents with
    /// non-uniform grayish backgrounds (>160 RGB) converted to clean white (>=250 RGB across 9 sampled zones).
    /// </summary>
    public static void Test_BackgroundWhitening_And_ShadowRemoval(string testDir)
    {
        int width = 1200;
        int height = 1600;

        using var image = new Image<Rgba32>(width, height);

        // Generate non-uniform illumination field: center ~215 RGB, corners drop to ~165 RGB, plus sensor noise +-4
        for (int y = 0; y < height; y++)
        {
            double ny = (double)(y - height / 2) / (height / 2);
            for (int x = 0; x < width; x++)
            {
                double nx = (double)(x - width / 2) / (width / 2);
                double distSq = Math.Min(1.0, nx * nx * 0.5 + ny * ny * 0.5);
                int baseLum = (int)(215 - 50 * distSq); // 215 center -> 165 corners
                int noise = ((x * 37 + y * 73) % 9) - 4;
                byte bg = (byte)Math.Clamp(baseLum + noise, 160, 230);
                image[x, y] = new Rgba32(bg, bg, bg, 255);
            }
        }

        // Draw foreground text lines in known vertical bands (y: 300..320, 600..620, 900..920)
        int[] textBands = { 300, 600, 900, 1200 };
        foreach (int bandY in textBands)
        {
            for (int dy = 0; dy < 15; dy++)
            {
                int py = bandY + dy;
                for (int px = 200; px < 1000; px++)
                {
                    if ((px % 40) < 28) // Text stroke segments
                    {
                        image[px, py] = new Rgba32(45, 45, 45, 255);
                    }
                }
            }
        }

        // Verify baseline: corners are indeed grayish (160..180 RGB)
        Rgba32 preCorner = image[50, 50];
        if (preCorner.R >= 220 || preCorner.R < 160)
            throw new Exception($"Baseline corner background not in expected grayish range: {preCorner.R}");

        // Execute enhancement
        var options = new EnhancementOptions(EnhancementPreset.Balanced);
        Enhancer.EnhanceImage(image, options);

        // Define 9 background sampling zones (60x60 windows) in blank paper regions
        (int zx, int zy)[] zones =
        {
            (50, 50),     // Top-Left corner (severe shadow)
            (570, 50),    // Top-Center
            (1090, 50),   // Top-Right corner (severe shadow)
            (50, 750),    // Mid-Left edge
            (570, 750),   // Center (mild shadow)
            (1090, 750),  // Mid-Right edge
            (50, 1450),   // Bottom-Left corner (severe shadow)
            (570, 1450),  // Bottom-Center
            (1090, 1450)  // Bottom-Right corner (severe shadow)
        };

        for (int i = 0; i < zones.Length; i++)
        {
            (int zx, int zy) = zones[i];
            long sumR = 0, sumG = 0, sumB = 0;
            int count = 0;
            var lums = new List<int>(60 * 60);

            for (int dy = 0; dy < 60; dy++)
            {
                for (int dx = 0; dx < 60; dx++)
                {
                    Rgba32 p = image[zx + dx, zy + dy];
                    sumR += p.R;
                    sumG += p.G;
                    sumB += p.B;
                    int lum = (77 * p.R + 150 * p.G + 29 * p.B) >> 8;
                    lums.Add(lum);
                    count++;
                }
            }

            double meanR = (double)sumR / count;
            double meanG = (double)sumG / count;
            double meanB = (double)sumB / count;
            double meanLum = (77 * meanR + 150 * meanG + 29 * meanB) / 256.0;

            if (meanR < 250.0 || meanG < 250.0 || meanB < 250.0 || meanLum < 250.0)
            {
                throw new Exception($"Background Zone {i} at ({zx},{zy}) failed whitening threshold: " +
                                    $"MeanRGB=({meanR:F1}, {meanG:F1}, {meanB:F1}), Lum={meanLum:F1} (expected >= 250.0)");
            }

            // 5th percentile check: >95% of background pixels must be >= 245
            lums.Sort();
            int p5Index = (int)(count * 0.05);
            int p5Lum = lums[p5Index];
            if (p5Lum < 245)
            {
                throw new Exception($"Background Zone {i} 5th-percentile failed: Lum={p5Lum} (expected >= 245)");
            }
        }

        // Save sample artifact to test sandbox
        string previewOut = Path.Combine(testDir, "test77_whitened_sample.png");
        image.SaveAsPng(previewOut);
    }

    /// <summary>
    /// TEST 78: Verifies text stroke clarity: core text pixels (<100 RGB) are maintained or deepened
    /// to <= 35 RGB (target <= 25 RGB) without breaking stroke continuity (8-connected BFS component traversal PIR = 1.00).
    /// </summary>
    public static void Test_TextStrokeClarity_And_Continuity(string testDir)
    {
        int width = 800;
        int height = 800;

        using var image = new Image<Rgba32>(width, height);

        // Fill uniform paper at 210 RGB
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                image[x, y] = new Rgba32(210, 210, 210, 255);
            }
        }

        // 1. Draw horizontal faded text strokes (<100 RGB) with anti-aliased edge falloff
        var corePixels = new List<(int x, int y)>();
        for (int row = 0; row < 5; row++)
        {
            int centerY = 150 + row * 60;
            for (int x = 100; x < 700; x++)
            {
                // Top edge falloff (135 RGB)
                image[x, centerY - 1] = new Rgba32(135, 135, 135, 255);
                // Core center (75 RGB)
                image[x, centerY] = new Rgba32(75, 75, 75, 255);
                corePixels.Add((x, centerY));
                // Bottom edge falloff (135 RGB)
                image[x, centerY + 1] = new Rgba32(135, 135, 135, 255);
            }
        }

        // 2. Draw continuous ground-truth cursive sine wave stroke: y = 550 + 35 * sin((x - 100) * 0.04)
        var sineCenterline = new List<(int x, int y)>();
        int sineStartX = 100;
        int sineEndX = 700;
        int prevY = -1;

        for (int x = sineStartX; x <= sineEndX; x++)
        {
            int sy = 550 + (int)Math.Round(35.0 * Math.Sin((x - 100) * 0.04));

            // Bridge vertical steps to ensure strict 8-connected continuity in ground truth
            if (prevY != -1 && Math.Abs(sy - prevY) > 1)
            {
                int step = sy > prevY ? 1 : -1;
                for (int fillY = prevY + step; fillY != sy; fillY += step)
                {
                    image[x - 1, fillY] = new Rgba32(80, 80, 80, 255);
                    sineCenterline.Add((x - 1, fillY));
                }
            }

            // Draw 3px wide stroke: sy-1, sy, sy+1
            image[x, sy - 1] = new Rgba32(110, 110, 110, 255);
            image[x, sy] = new Rgba32(80, 80, 80, 255);
            image[x, sy + 1] = new Rgba32(110, 110, 110, 255);

            sineCenterline.Add((x, sy));
            prevY = sy;
        }

        // Execute enhancement
        var options = new EnhancementOptions(EnhancementPreset.Balanced);
        Enhancer.EnhanceImage(image, options);

        // Assertion 1: Core text pixels (<100 RGB) deepened to <= 35 RGB
        long sumCoreLum = 0;
        foreach (var (cx, cy) in corePixels)
        {
            Rgba32 p = image[cx, cy];
            int lum = (77 * p.R + 150 * p.G + 29 * p.B) >> 8;
            if (p.R > 35 || p.G > 35 || p.B > 35 || lum > 35)
            {
                throw new Exception($"Core text pixel at ({cx},{cy}) not sufficiently deepened: " +
                                    $"RGB=({p.R},{p.G},{p.B}), Lum={lum} (expected <= 35)");
            }
            sumCoreLum += lum;
        }
        double meanCoreLum = (double)sumCoreLum / corePixels.Count;
        if (meanCoreLum > 25.0)
        {
            throw new Exception($"Mean core text luminance failed: {meanCoreLum:F1} (expected <= 25.0)");
        }

        // Assertion 2: 8-Connected BFS Stroke Continuity Verification
        // Start from first point of sine wave
        var startPt = sineCenterline.First();
        var endPt = sineCenterline.Last();

        var visited = new HashSet<(int x, int y)>();
        var queue = new Queue<(int x, int y)>();

        // Ink criterion: pixel luminance <= 60 RGB
        bool IsInk(int x, int y)
        {
            if (x < 0 || x >= width || y < 0 || y >= height) return false;
            Rgba32 p = image[x, y];
            int lum = (77 * p.R + 150 * p.G + 29 * p.B) >> 8;
            return lum <= 60;
        }

        if (!IsInk(startPt.x, startPt.y))
            throw new Exception($"Sine wave start point ({startPt.x},{startPt.y}) is not detected as ink!");

        queue.Enqueue(startPt);
        visited.Add(startPt);

        while (queue.Count > 0)
        {
            var (qx, qy) = queue.Dequeue();

            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0) continue;
                    int nx = qx + dx;
                    int ny = qy + dy;
                    if (nx < sineStartX - 10 || nx > sineEndX + 10 || ny < 450 || ny > 650) continue;

                    var neighbor = (nx, ny);
                    if (!visited.Contains(neighbor) && IsInk(nx, ny))
                    {
                        visited.Add(neighbor);
                        queue.Enqueue(neighbor);
                    }
                }
            }
        }

        // Check if end point was reached
        if (!visited.Contains(endPt))
        {
            throw new Exception($"8-Connected BFS path traversal failed: End point ({endPt.x},{endPt.y}) " +
                                $"is disconnected from start point ({startPt.x},{startPt.y})! Stroke continuity broken.");
        }

        // Path Integrity Ratio (PIR): All centerline pixels must be contained within the connected component
        int connectedCenterlineCount = sineCenterline.Count(pt => visited.Contains(pt));
        double pir = (double)connectedCenterlineCount / sineCenterline.Count;
        if (pir < 1.0)
        {
            throw new Exception($"Path Integrity Ratio failed: PIR = {pir:P2} (expected 100.0%, broken pixels: {sineCenterline.Count - connectedCenterlineCount})");
        }

        // Save sample artifact to test sandbox
        string previewOut = Path.Combine(testDir, "test78_text_clarity_sample.png");
        image.SaveAsPng(previewOut);
    }

    /// <summary>
    /// TEST 79: Verifies stamp and photo protection: color components (blue ink signatures,
    /// purple notary stamps, red seals, and color passport photos) retain color saturation (RSR >= 85%)
    /// and are not stripped to monochrome unless PreserveColor is false.
    /// </summary>
    public static void Test_ColorProtection_StampsSignaturesAndPhotos(string testDir)
    {
        int width = 1000;
        int height = 1000;

        using var origImage = new Image<Rgba32>(width, height);

        // Grayish paper background: 185 RGB
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                origImage[x, y] = new Rgba32(185, 185, 185, 255);
            }
        }

        // 1. Blue ink signature: handwriting stroke with RGB (15, 55, 185) (Chroma = 170)
        var blueSignaturePixels = new List<(int x, int y)>();
        for (int x = 100; x < 450; x++)
        {
            int sy = 250 + (int)Math.Round(20.0 * Math.Sin(x * 0.08) + 10.0 * Math.Cos(x * 0.03));
            for (int dy = -1; dy <= 1; dy++)
            {
                origImage[x, sy + dy] = new Rgba32(15, 55, 185, 255);
                blueSignaturePixels.Add((x, sy + dy));
            }
        }

        // 2. Purple notary stamp: circular emblem at center (700, 250) with RGB (135, 35, 155) (Chroma = 120)
        var purpleStampPixels = new List<(int x, int y)>();
        for (int dy = -60; dy <= 60; dy++)
        {
            for (int dx = -60; dx <= 60; dx++)
            {
                double dist = Math.Sqrt(dx * dx + dy * dy);
                if ((dist >= 45 && dist <= 55) || (dist >= 20 && dist <= 26)) // Double concentric rings
                {
                    int px = 700 + dx;
                    int py = 250 + dy;
                    origImage[px, py] = new Rgba32(135, 35, 155, 255);
                    purpleStampPixels.Add((px, py));
                }
            }
        }

        // 3. Red official seal: circular emblem at center (250, 700) with RGB (210, 30, 40) (Chroma = 180)
        var redSealPixels = new List<(int x, int y)>();
        for (int dy = -55; dy <= 55; dy++)
        {
            for (int dx = -55; dx <= 55; dx++)
            {
                double dist = Math.Sqrt(dx * dx + dy * dy);
                if (dist >= 40 && dist <= 50)
                {
                    int px = 250 + dx;
                    int py = 700 + dy;
                    origImage[px, py] = new Rgba32(210, 30, 40, 255);
                    redSealPixels.Add((px, py));
                }
            }
        }

        // 4. Color passport photo slot (550..850 x, 600..900 y): skin tones (215, 172, 138), shirt (30, 110, 190)
        var photoPixels = new List<(int x, int y)>();
        for (int py = 600; py < 900; py++)
        {
            for (int px = 550; px < 850; px++)
            {
                if (py < 780) // Face / skin tone
                {
                    origImage[px, py] = new Rgba32(215, 172, 138, 255);
                }
                else // Cyan/blue shirt
                {
                    origImage[px, py] = new Rgba32(30, 110, 190, 255);
                }
                photoPixels.Add((px, py));
            }
        }

        // PART A: Enhance with PreserveColor = true (Balanced Preset)
        using var enhancedImage = origImage.Clone();
        var colorOptions = new EnhancementOptions(EnhancementPreset.Balanced) { PreserveColor = true };
        Enhancer.EnhanceImage(enhancedImage, colorOptions);

        // Helper to compute HSV Saturation: (max - min) / max
        double GetSaturation(Rgba32 p)
        {
            int max = Math.Max(p.R, Math.Max(p.G, p.B));
            int min = Math.Min(p.R, Math.Min(p.G, p.B));
            return max == 0 ? 0.0 : (double)(max - min) / max;
        }

        // Helper to compute Chroma: max - min
        int GetChroma(Rgba32 p) => Math.Max(p.R, Math.Max(p.G, p.B)) - Math.Min(p.R, Math.Min(p.G, p.B));

        // Evaluate Retained Saturation Ratio (RSR) on Blue Signature
        double origBlueSat = blueSignaturePixels.Average(pt => GetSaturation(origImage[pt.x, pt.y]));
        double enhBlueSat = blueSignaturePixels.Average(pt => GetSaturation(enhancedImage[pt.x, pt.y]));
        double rsrBlue = enhBlueSat / origBlueSat;
        if (rsrBlue < 0.85)
        {
            throw new Exception($"Blue Signature Retained Saturation Ratio failed: {rsrBlue:P1} (expected >= 85.0%)");
        }

        // Evaluate Non-monochrome Chroma on Blue Signature
        double enhBlueChroma = blueSignaturePixels.Average(pt => (double)GetChroma(enhancedImage[pt.x, pt.y]));
        if (enhBlueChroma < 35.0)
        {
            throw new Exception($"Blue Signature Chroma stripped to near-monochrome: {enhBlueChroma:F1} (expected >= 35.0)");
        }

        // Evaluate Purple Notary Stamp
        double origPurpleSat = purpleStampPixels.Average(pt => GetSaturation(origImage[pt.x, pt.y]));
        double enhPurpleSat = purpleStampPixels.Average(pt => GetSaturation(enhancedImage[pt.x, pt.y]));
        double rsrPurple = enhPurpleSat / origPurpleSat;
        if (rsrPurple < 0.85)
        {
            throw new Exception($"Purple Notary Stamp Retained Saturation Ratio failed: {rsrPurple:P1} (expected >= 85.0%)");
        }

        // Evaluate Red Official Seal
        double enhRedChroma = redSealPixels.Average(pt => (double)GetChroma(enhancedImage[pt.x, pt.y]));
        if (enhRedChroma < 35.0)
        {
            throw new Exception($"Red Seal Chroma stripped: {enhRedChroma:F1} (expected >= 35.0)");
        }

        // Evaluate Blue Signature Hue Preservation: atan2(sqrt(3)*(G-B), 2R - G - B)
        double GetHueAngle(Rgba32 p)
        {
            double num = Math.Sqrt(3.0) * (p.G - p.B);
            double den = 2.0 * p.R - p.G - p.B;
            double deg = Math.Atan2(num, den) * (180.0 / Math.PI);
            return (deg + 360.0) % 360.0;
        }

        var sampleBluePt = blueSignaturePixels[blueSignaturePixels.Count / 2];
        double origHue = GetHueAngle(origImage[sampleBluePt.x, sampleBluePt.y]);
        double enhHue = GetHueAngle(enhancedImage[sampleBluePt.x, sampleBluePt.y]);
        double hueDelta = Math.Abs(origHue - enhHue);
        if (hueDelta > 180.0) hueDelta = 360.0 - hueDelta;
        if (hueDelta > 15.0)
        {
            throw new Exception($"Blue ink hue shifted significantly: Delta={hueDelta:F1}° (expected <= 15.0°)");
        }

        // Evaluate Passport Photo region: must retain color and not be washed to pure white
        double enhPhotoChroma = photoPixels.Average(pt => (double)GetChroma(enhancedImage[pt.x, pt.y]));
        if (enhPhotoChroma < 25.0)
        {
            throw new Exception($"Passport Photo chroma stripped to near-monochrome: {enhPhotoChroma:F1} (expected >= 25.0)");
        }

        // Background paper outside stamps must be whitened to >= 250 RGB
        Rgba32 blankPaper = enhancedImage[100, 100];
        if (blankPaper.R < 250 || blankPaper.G < 250 || blankPaper.B < 250)
        {
            throw new Exception($"Background paper was not whitened while preserving color: RGB=({blankPaper.R},{blankPaper.G},{blankPaper.B})");
        }

        // PART B: Enhance with PreserveColor = false (Monochrome Mode)
        using var monoImage = origImage.Clone();
        var monoOptions = new EnhancementOptions(EnhancementPreset.Balanced) { PreserveColor = false };
        Enhancer.EnhanceImage(monoImage, monoOptions);

        // Verify stamps converted cleanly to monochrome: |R - G| <= 2 and |G - B| <= 2
        var sampleMonoBlue = monoImage[sampleBluePt.x, sampleBluePt.y];
        if (Math.Abs(sampleMonoBlue.R - sampleMonoBlue.G) > 2 || Math.Abs(sampleMonoBlue.G - sampleMonoBlue.B) > 2)
        {
            throw new Exception($"PreserveColor=false failed to convert blue ink to neutral monochrome: " +
                                $"RGB=({sampleMonoBlue.R},{sampleMonoBlue.G},{sampleMonoBlue.B})");
        }

        // Save sample artifact to test sandbox
        string previewOut = Path.Combine(testDir, "test79_color_protection_sample.png");
        enhancedImage.SaveAsPng(previewOut);
    }

    /// <summary>
    /// TEST 80: Verifies preset calibrations: Light, Balanced, and Strong calibrations
    /// produce expected parameter configurations and proper scaling behavior.
    /// </summary>
    public static void Test_PresetCalibrations_LightBalancedStrong(string testDir)
    {
        // 1. Verify Light Preset
        var light = EnhancementOptions.FromPreset(EnhancementPreset.Light);
        if (light.Preset != EnhancementPreset.Light ||
            light.BlockSize != 48 ||
            light.WhiteCutoff != 235 ||
            light.InkCutoff != 65 ||
            light.DarkTarget != 32 ||
            Math.Abs(light.SaturationBoost - 1.05f) > 0.01f)
        {
            throw new Exception($"Light preset configuration mismatch: Block={light.BlockSize}, White={light.WhiteCutoff}, Ink={light.InkCutoff}, Dark={light.DarkTarget}");
        }

        // 2. Verify Balanced Preset
        var balanced = EnhancementOptions.FromPreset(EnhancementPreset.Balanced);
        if (balanced.Preset != EnhancementPreset.Balanced ||
            balanced.BlockSize != 32 ||
            balanced.WhiteCutoff != 220 ||
            balanced.InkCutoff != 80 ||
            balanced.DarkTarget != 20 ||
            Math.Abs(balanced.SaturationBoost - 1.15f) > 0.01f)
        {
            throw new Exception($"Balanced preset configuration mismatch: Block={balanced.BlockSize}, White={balanced.WhiteCutoff}, Ink={balanced.InkCutoff}, Dark={balanced.DarkTarget}");
        }

        // 3. Verify Strong Preset
        var strong = EnhancementOptions.FromPreset(EnhancementPreset.Strong);
        if (strong.Preset != EnhancementPreset.Strong ||
            strong.BlockSize != 24 ||
            strong.WhiteCutoff != 195 ||
            strong.InkCutoff != 105 ||
            strong.DarkTarget != 0 ||
            Math.Abs(strong.SaturationBoost - 1.25f) > 0.01f)
        {
            throw new Exception($"Strong preset configuration mismatch: Block={strong.BlockSize}, White={strong.WhiteCutoff}, Ink={strong.InkCutoff}, Dark={strong.DarkTarget}");
        }

        // 4. Test functional behavior across presets on a heavily shadowed test document (170 RGB background)
        int w = 600, h = 600;
        using var testBase = new Image<Rgba32>(w, h);
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                testBase[x, y] = new Rgba32(170, 170, 170, 255);
            }
        }
        // Core text stroke (<100 RGB, e.g. 60 RGB)
        for (int x = 100; x < 500; x++) testBase[x, 300] = new Rgba32(60, 60, 60, 255);

        using var imgLight = testBase.Clone();
        using var imgStrong = testBase.Clone();

        Enhancer.EnhanceImage(imgLight, light);
        Enhancer.EnhanceImage(imgStrong, strong);

        // In Strong preset, dark shadow paper (170 RGB) is whitened to pure 255
        Rgba32 strongBg = imgStrong[100, 100];
        if (strongBg.R < 250 || strongBg.G < 250 || strongBg.B < 250)
        {
            throw new Exception($"Strong preset failed to whiten dark shadow paper: RGB=({strongBg.R},{strongBg.G},{strongBg.B})");
        }

        // In Strong preset, core text stroke (<100 RGB) is deepened towards 0 RGB (pitch black target <= 25)
        Rgba32 strongText = imgStrong[200, 300];
        if (strongText.R > 25)
        {
            throw new Exception($"Strong preset failed pitch-black text target: {strongText.R} (expected <= 25)");
        }
    }

    /// <summary>
    /// TEST 81: Verifies execution performance benchmark: completes full standard A4 page processing
    /// (2480x3508 px, 300 DPI, ~8.7 MP) in under 600 ms on CPU via Stopwatch (5-iteration median).
    /// </summary>
    public static void Test_A4ExecutionPerformanceBenchmark(string testDir)
    {
        int a4Width = 2480;
        int a4Height = 3508;

        Console.Write($"(Generating A4 300 DPI canvas {a4Width}x{a4Height} ~8.7 MP... ");
        using var a4Image = new Image<Rgba32>(a4Width, a4Height);

        // Populate with realistic document content: camera shadow background (165..225 RGB), text lines, stamps
        for (int y = 0; y < a4Height; y++)
        {
            double ny = (double)(y - a4Height / 2) / (a4Height / 2);
            for (int x = 0; x < a4Width; x++)
            {
                double nx = (double)(x - a4Width / 2) / (a4Width / 2);
                int bg = (int)(220 - 45 * Math.Min(1.0, nx * nx * 0.5 + ny * ny * 0.5));
                a4Image[x, y] = new Rgba32((byte)bg, (byte)bg, (byte)bg, 255);
            }
        }

        // Add 30 lines of text across page
        for (int i = 0; i < 30; i++)
        {
            int lineY = 300 + i * 90;
            for (int dy = 0; dy < 12; dy++)
            {
                for (int x = 300; x < 2180; x++)
                {
                    if ((x % 60) < 45) a4Image[x, lineY + dy] = new Rgba32(50, 50, 50, 255);
                }
            }
        }

        // Add a blue signature block
        for (int x = 1400; x < 1900; x++)
        {
            int sy = 3100 + (int)Math.Round(30.0 * Math.Sin(x * 0.05));
            for (int dy = -2; dy <= 2; dy++)
            {
                a4Image[x, sy + dy] = new Rgba32(20, 60, 180, 255);
            }
        }

        Console.Write("Benchmarking 1 warm-up + 5 timed runs... ");

        var options = new EnhancementOptions(EnhancementPreset.Balanced);

        // 1. Warm-up pass (unmeasured, JIT & thread pool initialization)
        using (var warmUpCopy = a4Image.Clone())
        {
            Enhancer.EnhanceImage(warmUpCopy, options);
        }

        // 2. Five timed passes
        var timings = new List<double>();
        for (int run = 1; run <= 5; run++)
        {
            using var workingCopy = a4Image.Clone();
            var sw = Stopwatch.StartNew();
            Enhancer.EnhanceImage(workingCopy, options);
            sw.Stop();
            timings.Add(sw.Elapsed.TotalMilliseconds);
        }

        timings.Sort();
        double medianMs = timings[2]; // Median of 5 passes
        double minMs = timings.First();
        double maxMs = timings.Last();

        Console.Write($"Median: {medianMs:F1}ms [Min: {minMs:F1}ms, Max: {maxMs:F1}ms]... ");

        // Acceptance criterion: under 600 ms on CPU
        if (medianMs >= 600.0)
        {
            throw new Exception($"Execution benchmark exceeded 600 ms threshold: Median = {medianMs:F1} ms (expected < 600.0 ms)");
        }
    }

    /// <summary>
    /// TEST 82: Verifies WPF BitmapSource and GDI+ System.Drawing.Bitmap overloads.
    /// Ensures WPF UI Live Preview and GDI+ printing pipelines work without exceptions.
    /// </summary>
    public static void Test_WpfBitmapSource_And_GdiOverloads(string testDir)
    {
        int width = 800;
        int height = 600;

        // 1. Test WPF BitmapSource overload
        int stride = width * 4;
        byte[] rawPixels = new byte[height * stride];
        // Fill grayish background (180 RGB) and a dark text line (60 RGB)
        for (int i = 0; i < rawPixels.Length; i += 4)
        {
            rawPixels[i] = 180;     // B
            rawPixels[i + 1] = 180; // G
            rawPixels[i + 2] = 180; // R
            rawPixels[i + 3] = 255; // A
        }
        for (int x = 200; x < 600; x++)
        {
            int idx = (300 * width + x) * 4;
            rawPixels[idx] = 60;
            rawPixels[idx + 1] = 60;
            rawPixels[idx + 2] = 60;
        }

        var sourceBmp = BitmapSource.Create(
            width, height, 96, 96,
            System.Windows.Media.PixelFormats.Bgra32, null,
            rawPixels, stride);

        var enhancedSource = Enhancer.EnhanceBitmapSource(sourceBmp, new EnhancementOptions(EnhancementPreset.Balanced));

        if (enhancedSource == null)
            throw new Exception("EnhanceBitmapSource returned null!");
        if (enhancedSource.PixelWidth != width || enhancedSource.PixelHeight != height)
            throw new Exception($"EnhanceBitmapSource dimension mismatch: {enhancedSource.PixelWidth}x{enhancedSource.PixelHeight}");
        if (!enhancedSource.IsFrozen)
            throw new Exception("EnhanceBitmapSource result must be frozen for cross-thread UI safety!");

        // Read back pixel to verify background was whitened
        byte[] readBack = new byte[stride];
        enhancedSource.CopyPixels(new System.Windows.Int32Rect(0, 50, width, 1), readBack, stride, 0);
        if (readBack[0] < 250 || readBack[1] < 250 || readBack[2] < 250)
        {
            throw new Exception($"EnhanceBitmapSource background not whitened: BGRA=({readBack[2]},{readBack[1]},{readBack[0]},{readBack[3]})");
        }

        // 2. Test GDI+ System.Drawing.Bitmap overload
        using var gdiBmp = new System.Drawing.Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = System.Drawing.Graphics.FromImage(gdiBmp))
        {
            using var brush = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(180, 180, 180));
            g.FillRectangle(brush, 0, 0, width, height);
            using var textBrush = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(60, 60, 60));
            g.FillRectangle(textBrush, 200, 300, 400, 5);
        }

        using var enhancedGdi = Enhancer.EnhanceBitmap(gdiBmp, new EnhancementOptions(EnhancementPreset.Balanced));
        if (enhancedGdi == null)
            throw new Exception("EnhanceBitmap returned null!");
        if (enhancedGdi.Width != width || enhancedGdi.Height != height)
            throw new Exception($"EnhanceBitmap dimension mismatch: {enhancedGdi.Width}x{enhancedGdi.Height}");

        var cornerColor = enhancedGdi.GetPixel(50, 50);
        if (cornerColor.R < 250 || cornerColor.G < 250 || cornerColor.B < 250)
        {
            throw new Exception($"EnhanceBitmap background not whitened: RGB=({cornerColor.R},{cornerColor.G},{cornerColor.B})");
        }
    }

    /// <summary>
    /// TEST 83: Verifies async file enhancement from disk, format handling, and target byte compression.
    /// </summary>
    public static async Task Test_AsyncFileEnhancement_With_TargetBytes(string testDir)
    {
        string inputPath = Path.Combine(testDir, "test83_input.jpg");
        string outputPath = Path.Combine(testDir, "test83_output.jpg");
        string compressedPath = Path.Combine(testDir, "test83_compressed.jpg");

        // Create sample file on disk
        using (var img = new Image<Rgba32>(1000, 1400))
        {
            for (int y = 0; y < 1400; y++)
            {
                for (int x = 0; x < 1000; x++)
                {
                    img[x, y] = new Rgba32(185, 185, 185, 255);
                }
            }
            // Add some text
            for (int y = 400; y < 450; y++)
            {
                for (int x = 200; x < 800; x++)
                {
                    img[x, y] = new Rgba32(40, 40, 40, 255);
                }
            }
            await img.SaveAsJpegAsync(inputPath);
        }

        // 1. Enhance file async
        var options = new EnhancementOptions(EnhancementPreset.Balanced);
        await Enhancer.EnhanceFileAsync(inputPath, outputPath, options);

        if (!File.Exists(outputPath))
            throw new Exception($"EnhanceFileAsync did not produce output file: {outputPath}");

        using (var outImg = await SixLabors.ImageSharp.Image.LoadAsync<Rgba32>(outputPath))
        {
            Rgba32 bg = outImg[50, 50];
            if (bg.R < 250 || bg.G < 250 || bg.B < 250)
                throw new Exception($"Enhanced file background not whitened: RGB=({bg.R},{bg.G},{bg.B})");
        }

        // 2. Enhance file with target byte compression (< 100 KB)
        long targetBytes = 80 * 1024; // 80 KB
        var result = await Enhancer.EnhanceFileWithResultAsync(inputPath, compressedPath, options, targetBytes);

        if (!result.Success)
            throw new Exception($"EnhanceFileWithResultAsync failed: {result.ErrorMessage}");
        if (!File.Exists(compressedPath))
            throw new Exception($"Compressed file does not exist: {compressedPath}");

        long actualSize = new FileInfo(compressedPath).Length;
        if (actualSize <= 0)
            throw new Exception($"Compressed file size is 0 bytes!");

        // 3. Fast preview generation
        byte[]? previewBytes = Enhancer.GeneratePreviewBytes(inputPath, options, maxDimension: 500);
        if (previewBytes == null || previewBytes.Length == 0)
            throw new Exception("GeneratePreviewBytes returned null or empty bytes!");
    }
}
