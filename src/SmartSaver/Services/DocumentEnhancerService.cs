using System;
using System.Buffers;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using Serilog;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Bmp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace SmartSaver.Services;

/// <summary>
/// High-performance document enhancement engine.
/// Implements spatially adaptive 2D block grid background estimation, bilinear surface interpolation,
/// optical division normalization, chromaticity detection and soft alpha masking,
/// and Hermite cubic S-curve anti-aliased text sharpening.
/// </summary>
public sealed class DocumentEnhancerService : IDocumentEnhancerService
{
    // Precomputed 64 KB Division LUT: DivLut[(bg << 8) | val] = clamp(255 * val / bg)
    // Fits entirely in CPU L2 cache for instant zero-latency optical division.
    private static readonly byte[] DivLut = InitializeDivLut();

    private readonly ImageCompressor _imageCompressor;

    public DocumentEnhancerService() : this(null)
    {
    }

    public DocumentEnhancerService(ImageCompressor? imageCompressor)
    {
        _imageCompressor = imageCompressor ?? new ImageCompressor();
    }

    private static byte[] InitializeDivLut()
    {
        byte[] lut = new byte[256 * 256];
        for (int bg = 0; bg < 256; bg++)
        {
            int effBg = Math.Max(1, bg);
            int bgOffset = bg << 8;
            for (int val = 0; val < 256; val++)
            {
                int norm = (255 * val) / effBg;
                lut[bgOffset | val] = (byte)Math.Clamp(norm, 0, 255);
            }
        }
        return lut;
    }

    /// <summary>
    /// Precomputes a 256-entry monotonic Hermite cubic S-curve LUT for anti-aliased text sharpening and paper whitening.
    /// </summary>
    public static byte[] BuildContrastLut(EnhancementOptions options)
    {
        byte[] lut = new byte[256];
        bool whiten = options.WhitenBackground;
        bool darken = options.DarkenText;

        int tBlack = options.InkCutoff;
        int tWhite = options.WhiteCutoff;
        int kBlack = options.DarkTarget;

        if (!darken && !whiten)
        {
            for (int v = 0; v < 256; v++) lut[v] = (byte)v;
            return lut;
        }

        if (!darken && whiten)
        {
            for (int v = 0; v < 256; v++)
            {
                lut[v] = v >= tWhite ? (byte)255 : (byte)v;
            }
            return lut;
        }

        if (darken && !whiten)
        {
            tWhite = 255;
        }

        if (tBlack >= tWhite)
        {
            tBlack = Math.Max(0, tWhite - 1);
        }

        for (int v = 0; v < 256; v++)
        {
            if (whiten && v >= tWhite)
            {
                lut[v] = 255;
            }
            else if (darken && v <= tBlack)
            {
                if (tBlack <= 0)
                {
                    lut[v] = (byte)Math.Clamp(kBlack, 0, 255);
                }
                else
                {
                    double ratio = (double)v / tBlack;
                    double val = kBlack * Math.Pow(ratio, 1.2);
                    lut[v] = (byte)Math.Clamp((int)Math.Round(val), 0, 255);
                }
            }
            else
            {
                int lowerBound = darken ? tBlack : 0;
                int upperBound = whiten ? tWhite : 255;
                int lowerVal = darken ? kBlack : 0;
                int upperVal = 255;

                if (upperBound <= lowerBound)
                {
                    lut[v] = (byte)upperVal;
                }
                else
                {
                    double t = (double)(v - lowerBound) / (upperBound - lowerBound);
                    t = Math.Clamp(t, 0.0, 1.0);
                    double smoothStep = t * t * (3.0 - 2.0 * t);
                    double val = lowerVal + (upperVal - lowerVal) * smoothStep;
                    lut[v] = (byte)Math.Clamp((int)Math.Round(val), 0, 255);
                }
            }
        }

        return lut;
    }

    /// <inheritdoc />
    public void EnhanceImage(Image<Rgba32> image, EnhancementOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(image);
        options ??= new EnhancementOptions();

        int width = image.Width;
        int height = image.Height;
        if (width <= 0 || height <= 0) return;

        int stride = width * 4;

        if (image.DangerousTryGetSinglePixelMemory(out Memory<Rgba32> memory))
        {
            unsafe
            {
                using var pin = memory.Pin();
                byte* pPixels = (byte*)pin.Pointer;
                EnhanceRasterBuffer(pPixels, width, height, stride, isBgra: false, options);
            }
        }
        else
        {
            byte[] pixels = ArrayPool<byte>.Shared.Rent(height * stride);
            try
            {
                image.CopyPixelDataTo(pixels.AsSpan(0, height * stride));
                unsafe
                {
                    fixed (byte* pPixels = pixels)
                    {
                        EnhanceRasterBuffer(pPixels, width, height, stride, isBgra: false, options);
                    }
                }

                image.ProcessPixelRows(accessor =>
                {
                    for (int y = 0; y < height; y++)
                    {
                        var rowSpan = MemoryMarshal.Cast<Rgba32, byte>(accessor.GetRowSpan(y));
                        pixels.AsSpan(y * stride, stride).CopyTo(rowSpan);
                    }
                });
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(pixels);
            }
        }
    }

    /// <inheritdoc />
    public void EnhanceImageInPlace(Image<Rgba32> image, EnhancementOptions? options = null)
    {
        EnhanceImage(image, options);
    }

    /// <inheritdoc />
    public BitmapSource EnhanceBitmapSource(BitmapSource source, EnhancementOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        options ??= new EnhancementOptions();

        var formatted = new FormatConvertedBitmap(source, System.Windows.Media.PixelFormats.Bgra32, null, 0);
        int width = formatted.PixelWidth;
        int height = formatted.PixelHeight;
        if (width <= 0 || height <= 0) return source;

        int stride = width * 4;
        byte[] pixels = new byte[height * stride];
        formatted.CopyPixels(pixels, stride, 0);

        unsafe
        {
            fixed (byte* pPixels = pixels)
            {
                EnhanceRasterBuffer(pPixels, width, height, stride, isBgra: true, options);
            }
        }

        var result = BitmapSource.Create(
            width,
            height,
            source.DpiX > 0 ? source.DpiX : 96.0,
            source.DpiY > 0 ? source.DpiY : 96.0,
            System.Windows.Media.PixelFormats.Bgra32,
            null,
            pixels,
            stride);
        result.Freeze();
        return result;
    }

    /// <inheritdoc />
    public System.Drawing.Bitmap EnhanceBitmap(System.Drawing.Bitmap bitmap, EnhancementOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        options ??= new EnhancementOptions();

        int width = bitmap.Width;
        int height = bitmap.Height;
        if (width <= 0 || height <= 0) return (System.Drawing.Bitmap)bitmap.Clone();

        var target = new System.Drawing.Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        target.SetResolution(bitmap.HorizontalResolution, bitmap.VerticalResolution);

        using (var g = System.Drawing.Graphics.FromImage(target))
        {
            g.DrawImage(bitmap, new System.Drawing.Rectangle(0, 0, width, height), 0, 0, width, height, System.Drawing.GraphicsUnit.Pixel);
        }

        var rect = new System.Drawing.Rectangle(0, 0, width, height);
        var bmpData = target.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadWrite, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            unsafe
            {
                byte* scan0 = (byte*)bmpData.Scan0;
                EnhanceRasterBuffer(scan0, width, height, bmpData.Stride, isBgra: true, options);
            }
        }
        finally
        {
            target.UnlockBits(bmpData);
        }

        return target;
    }

    /// <inheritdoc />
    public async Task EnhanceFileAsync(string inputPath, string outputPath, EnhancementOptions? options = null)
    {
        var res = await EnhanceFileWithResultAsync(inputPath, outputPath, options);
        if (!res.Success)
        {
            throw new InvalidOperationException(res.ErrorMessage ?? "Document enhancement failed.");
        }
    }

    /// <summary>
    /// Overload supporting target compression size and cancellation token.
    /// </summary>
    public Task<EnhancementResult> EnhanceFileAsync(
        string inputPath,
        string outputPath,
        EnhancementOptions? options,
        long? targetBytes,
        CancellationToken cancellationToken = default)
    {
        return EnhanceFileWithResultAsync(inputPath, outputPath, options, targetBytes, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<EnhancementResult> EnhanceFileWithResultAsync(
        string inputPath,
        string outputPath,
        EnhancementOptions? options = null,
        long? targetBytes = null,
        CancellationToken cancellationToken = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = new EnhancementResult();

        try
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
            ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

            if (!File.Exists(inputPath))
            {
                result.Success = false;
                result.ErrorMessage = $"Source file does not exist: {inputPath}";
                return result;
            }

            using var image = await SixLabors.ImageSharp.Image.LoadAsync<Rgba32>(inputPath, cancellationToken);
            image.Mutate(ctx => ctx.AutoOrient());
            result.Width = image.Width;
            result.Height = image.Height;

            EnhanceImage(image, options);

            string? outDir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(outDir))
            {
                Directory.CreateDirectory(outDir);
            }

            string ext = Path.GetExtension(outputPath).ToLowerInvariant();
            if (ext == ".png")
            {
                await image.SaveAsPngAsync(outputPath, cancellationToken);
            }
            else if (ext == ".bmp")
            {
                await image.SaveAsBmpAsync(outputPath, cancellationToken);
            }
            else if (ext == ".webp")
            {
                await image.SaveAsWebpAsync(outputPath, cancellationToken);
            }
            else
            {
                var jpegEncoder = new JpegEncoder { Quality = 92 };
                await image.SaveAsJpegAsync(outputPath, jpegEncoder, cancellationToken);
            }

            // Optional target byte compression
            if (targetBytes.HasValue && targetBytes.Value > 0 && File.Exists(outputPath))
            {
                long currentSize = new FileInfo(outputPath).Length;
                if (currentSize > targetBytes.Value)
                {
                    string tempCompressed = Path.Combine(Path.GetTempPath(), $"dasmo_enh_comp_{Guid.NewGuid():N}{ext}");
                    try
                    {
                        if (_imageCompressor.CompressToTargetSize(outputPath, tempCompressed, targetBytes.Value))
                        {
                            File.Copy(tempCompressed, outputPath, overwrite: true);
                        }
                    }
                    finally
                    {
                        if (File.Exists(tempCompressed)) File.Delete(tempCompressed);
                    }
                }
            }

            result.OutputBytes = await File.ReadAllBytesAsync(outputPath, cancellationToken);
            sw.Stop();
            result.Success = true;
            result.OutputPath = outputPath;
            result.ElapsedMilliseconds = sw.ElapsedMilliseconds;
            return result;
        }
        catch (Exception ex)
        {
            sw.Stop();
            Log.Error(ex, "Document enhancement failed for {InputPath}", inputPath);
            result.Success = false;
            result.ErrorMessage = ex.Message;
            result.ElapsedMilliseconds = sw.ElapsedMilliseconds;
            return result;
        }
    }

    /// <inheritdoc />
    public byte[]? GeneratePreviewBytes(string sourcePath, EnhancementOptions? options = null, int maxDimension = 1000)
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

            EnhanceImage(image, options);

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

    // =========================================================================
    // Core Low-Level Raster Pipelines (ImageSharp & Unmanaged BGRA)
    // =========================================================================

    private static unsafe void EnhanceRasterBuffer(
        byte* pPixels,
        int width,
        int height,
        int stride,
        bool isBgra,
        EnhancementOptions options)
    {
        bool whiten = options.WhitenBackground;
        bool darken = options.DarkenText;
        if (!whiten && !darken)
        {
            // Original (No Filter) preset: completely untouched raw pixels!
            return;
        }

        int sb = Math.Max(8, options.BlockSize);
        int gridW = Math.Max(1, (width + sb - 1) / sb);
        int gridH = Math.Max(1, (height + sb - 1) / sb);

        byte[] contrastLut = BuildContrastLut(options);
        bool preserveColor = options.PreserveColor;
        bool protectFaces = options.ProtectFacesAndPhotos;

        float chromaLow = options.ColorThresholdLow;
        float chromaHigh = Math.Max(chromaLow + 0.1f, options.ColorThresholdHigh);
        float chromaRangeInv = 1.0f / (chromaHigh - chromaLow);
        float satBoost = options.SaturationBoost;

        // Pass 1: Spatially adaptive 2D background grid estimation & Face/Photo Block Detection
        bool[,] rawPhotoBlocks = new bool[gridH, gridW];
        bool hasAnyFace = false;

        byte[,] grid = ComputeBackgroundGridWithPhotoDetection(
            pPixels, width, height, stride, isBgra, sb, gridW, gridH, protectFaces, rawPhotoBlocks, ref hasAnyFace);

        // Dilate photo blocks to encompass hair, ears, clothing, and ID photo borders
        bool[,] dilatedPhotoBlocks = new bool[gridH, gridW];
        if (protectFaces && hasAnyFace)
        {
            int dilateRadius = Math.Min(gridW, gridH) <= 10 ? 1 : 2;
            for (int gy = 0; gy < gridH; gy++)
            {
                for (int gx = 0; gx < gridW; gx++)
                {
                    if (rawPhotoBlocks[gy, gx])
                    {
                        for (int dy = -dilateRadius; dy <= dilateRadius; dy++)
                        {
                            int ny = gy + dy;
                            if (ny < 0 || ny >= gridH) continue;
                            for (int dx = -dilateRadius; dx <= dilateRadius; dx++)
                            {
                                int nx = gx + dx;
                                if (nx < 0 || nx >= gridW) continue;
                                dilatedPhotoBlocks[ny, nx] = true;
                            }
                        }
                    }
                }
            }
        }

        // Pass 2: Row-wise Parallel.For bilinear interpolation and pixel enhancement
        Parallel.For(0, height, y =>
        {
            Span<byte> bgRow = width <= 4096 ? stackalloc byte[width] : new byte[width];
            FillBgRow(bgRow, y, width, sb, gridW, gridH, grid);

            byte* rowPtr = pPixels + y * stride;
            if (isBgra)
            {
                ProcessBgraRow(
                    rowPtr,
                    width,
                    y,
                    sb,
                    bgRow,
                    DivLut,
                    contrastLut,
                    whiten,
                    darken,
                    preserveColor,
                    chromaLow,
                    chromaHigh,
                    chromaRangeInv,
                    satBoost,
                    dilatedPhotoBlocks,
                    protectFaces && hasAnyFace);
            }
            else
            {
                ProcessRgbaRow(
                    rowPtr,
                    width,
                    y,
                    sb,
                    bgRow,
                    DivLut,
                    contrastLut,
                    whiten,
                    darken,
                    preserveColor,
                    chromaLow,
                    chromaHigh,
                    chromaRangeInv,
                    satBoost,
                    dilatedPhotoBlocks,
                    protectFaces && hasAnyFace);
            }
        });
    }

    /// <summary>
    /// Intelligently checks if an RGB pixel represents human skin tone across diverse ethnicities.
    /// Uses Kovac / Peer digital forensics chrominance and luminance bounds.
    /// </summary>
    public static bool IsSkinTone(byte r, byte g, byte b)
    {
        if (r < 60 || g < 35 || b < 20) return false;
        if (r <= g) return false;
        if ((r - g) < 8) return false;

        int y = (77 * r + 150 * g + 29 * b) >> 8;
        int cb = 128 + ((-43 * r - 85 * g + 128 * b) >> 8);
        int cr = 128 + ((128 * r - 107 * g - 21 * b) >> 8);

        return cb >= 77 && cb <= 127 && cr >= 133 && cr <= 175 && y >= 35 && y <= 240;
    }

    private static unsafe byte[,] ComputeBackgroundGridWithPhotoDetection(
        byte* pPixels,
        int width,
        int height,
        int stride,
        bool isBgra,
        int sb,
        int gridW,
        int gridH,
        bool protectFaces,
        bool[,] rawPhotoBlocks,
        ref bool hasAnyFace)
    {
        byte[,] rawGrid = new byte[gridH, gridW];
        Span<byte> samples = stackalloc byte[64];
        bool detectedFace = false;

        for (int gy = 0; gy < gridH; gy++)
        {
            int y0 = gy * sb;
            int blockH = Math.Min(sb, height - y0);
            int stepY = Math.Max(1, blockH / 8);

            for (int gx = 0; gx < gridW; gx++)
            {
                int x0 = gx * sb;
                int blockW = Math.Min(sb, width - x0);
                int stepX = Math.Max(1, blockW / 8);

                int sampleCount = 0;
                int skinCount = 0;

                for (int sy = 0; sy < 8; sy++)
                {
                    int py = Math.Min(y0 + sy * stepY, y0 + blockH - 1);
                    byte* row = pPixels + py * stride;

                    for (int sx = 0; sx < 8; sx++)
                    {
                        int px = Math.Min(x0 + sx * stepX, x0 + blockW - 1);
                        byte b = isBgra ? row[px * 4 + 0] : row[px * 4 + 2];
                        byte g = row[px * 4 + 1];
                        byte r = isBgra ? row[px * 4 + 2] : row[px * 4 + 0];

                        byte lum = (byte)((77 * r + 150 * g + 29 * b) >> 8);
                        samples[sampleCount++] = lum;

                        if (protectFaces && IsSkinTone(r, g, b))
                        {
                            skinCount++;
                        }
                    }
                }

                if (protectFaces && skinCount >= 2)
                {
                    rawPhotoBlocks[gy, gx] = true;
                    detectedFace = true;
                }

                // 92nd-percentile luminance representing local paper background
                samples.Slice(0, sampleCount).Sort();
                int p92Idx = Math.Min(sampleCount - 1, (int)(sampleCount * 0.92f));
                rawGrid[gy, gx] = samples[p92Idx];
            }
        }

        hasAnyFace = detectedFace;

        // Calculate global paper baseline across grid to protect passport photos & dark seals
        int totalNodes = gridW * gridH;
        byte[] allNodes = new byte[totalNodes];
        int nIdx = 0;
        for (int gy = 0; gy < gridH; gy++)
        {
            for (int gx = 0; gx < gridW; gx++)
            {
                allNodes[nIdx++] = rawGrid[gy, gx];
            }
        }
        Array.Sort(allNodes);
        byte docPaperBaseline = allNodes[Math.Min(totalNodes - 1, (int)(totalNodes * 0.85f))];
        byte minAllowedBg = Math.Max((byte)40, (byte)(docPaperBaseline * 0.60f));

        // 3x3 local neighborhood median filter and background floor clamping
        byte[,] filteredGrid = new byte[gridH, gridW];
        Span<byte> window = stackalloc byte[9];

        for (int gy = 0; gy < gridH; gy++)
        {
            for (int gx = 0; gx < gridW; gx++)
            {
                int winCount = 0;
                for (int dy = -1; dy <= 1; dy++)
                {
                    int ny = gy + dy;
                    if (ny < 0 || ny >= gridH) continue;
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = gx + dx;
                        if (nx < 0 || nx >= gridW) continue;
                        window[winCount++] = rawGrid[ny, nx];
                    }
                }

                window.Slice(0, winCount).Sort();
                byte medianVal = window[winCount / 2];
                byte combined = Math.Max(rawGrid[gy, gx], medianVal);
                filteredGrid[gy, gx] = Math.Max(combined, minAllowedBg);
            }
        }

        return filteredGrid;
    }

    private static byte[,] ComputeBackgroundGrid(
        int width,
        int height,
        int sb,
        int gridW,
        int gridH,
        Func<int, int, byte> getLuminance)
    {
        byte[,] rawGrid = new byte[gridH, gridW];
        Span<byte> samples = stackalloc byte[64];

        for (int gy = 0; gy < gridH; gy++)
        {
            int y0 = gy * sb;
            int blockH = Math.Min(sb, height - y0);
            int stepY = Math.Max(1, blockH / 8);

            for (int gx = 0; gx < gridW; gx++)
            {
                int x0 = gx * sb;
                int blockW = Math.Min(sb, width - x0);
                int stepX = Math.Max(1, blockW / 8);

                int sampleCount = 0;
                for (int sy = 0; sy < 8; sy++)
                {
                    int py = Math.Min(y0 + sy * stepY, y0 + blockH - 1);
                    for (int sx = 0; sx < 8; sx++)
                    {
                        int px = Math.Min(x0 + sx * stepX, x0 + blockW - 1);
                        samples[sampleCount++] = getLuminance(px, py);
                    }
                }

                // 92nd-percentile luminance representing local paper background
                samples.Slice(0, sampleCount).Sort();
                int p92Idx = Math.Min(sampleCount - 1, (int)(sampleCount * 0.92f));
                rawGrid[gy, gx] = samples[p92Idx];
            }
        }

        // Calculate global paper baseline across grid to protect passport photos & dark seals
        int totalNodes = gridW * gridH;
        byte[] allNodes = new byte[totalNodes];
        int nIdx = 0;
        for (int gy = 0; gy < gridH; gy++)
        {
            for (int gx = 0; gx < gridW; gx++)
            {
                allNodes[nIdx++] = rawGrid[gy, gx];
            }
        }
        Array.Sort(allNodes);
        byte docPaperBaseline = allNodes[Math.Min(totalNodes - 1, (int)(totalNodes * 0.85f))];
        byte minAllowedBg = Math.Max((byte)40, (byte)(docPaperBaseline * 0.60f));

        // 3x3 local neighborhood median filter and background floor clamping
        byte[,] filteredGrid = new byte[gridH, gridW];
        Span<byte> window = stackalloc byte[9];

        for (int gy = 0; gy < gridH; gy++)
        {
            for (int gx = 0; gx < gridW; gx++)
            {
                int winCount = 0;
                for (int dy = -1; dy <= 1; dy++)
                {
                    int ny = gy + dy;
                    if (ny < 0 || ny >= gridH) continue;
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = gx + dx;
                        if (nx < 0 || nx >= gridW) continue;
                        window[winCount++] = rawGrid[ny, nx];
                    }
                }

                window.Slice(0, winCount).Sort();
                byte medianVal = window[winCount / 2];
                byte combined = Math.Max(rawGrid[gy, gx], medianVal);
                filteredGrid[gy, gx] = Math.Max(combined, minAllowedBg);
            }
        }

        return filteredGrid;
    }

    private static void FillBgRow(
        Span<byte> bgRow,
        int y,
        int width,
        int sb,
        int gridW,
        int gridH,
        byte[,] grid)
    {
        int gy0 = y / sb;
        int v = y % sb;
        int gy1 = Math.Min(gy0 + 1, gridH - 1);

        Span<int> nodeY = gridW <= 512 ? stackalloc int[gridW] : new int[gridW];
        int sbMinusV = sb - v;
        for (int gx = 0; gx < gridW; gx++)
        {
            nodeY[gx] = sbMinusV * grid[gy0, gx] + v * grid[gy1, gx];
        }

        if (sb == 32)
        {
            for (int gx = 0; gx < gridW; gx++)
            {
                int n0 = nodeY[gx];
                int n1 = nodeY[Math.Min(gx + 1, gridW - 1)];
                int xStart = gx << 5;
                int xEnd = Math.Min(xStart + 32, width);
                for (int x = xStart; x < xEnd; x++)
                {
                    int u = x - xStart;
                    int b = ((32 - u) * n0 + u * n1) >> 10;
                    bgRow[x] = (byte)Math.Clamp(b, 1, 255);
                }
            }
        }
        else
        {
            int denom = sb * sb;
            for (int gx = 0; gx < gridW; gx++)
            {
                int n0 = nodeY[gx];
                int n1 = nodeY[Math.Min(gx + 1, gridW - 1)];
                int xStart = gx * sb;
                int xEnd = Math.Min(xStart + sb, width);
                for (int x = xStart; x < xEnd; x++)
                {
                    int u = x - xStart;
                    int b = ((sb - u) * n0 + u * n1) / denom;
                    bgRow[x] = (byte)Math.Clamp(b, 1, 255);
                }
            }
        }
    }

    private static unsafe void ProcessRgbaRow(
        byte* rowPtr,
        int width,
        int y,
        int sb,
        ReadOnlySpan<byte> bgRow,
        byte[] divLut,
        byte[] contrastLut,
        bool whiten,
        bool darken,
        bool preserveColor,
        float chromaLow,
        float chromaHigh,
        float chromaRangeInv,
        float satBoost,
        bool[,] dilatedPhotoBlocks,
        bool hasFaceProtection)
    {
        int gy = y / sb;
        bool hasPhotoBlocksInRow = hasFaceProtection && dilatedPhotoBlocks != null && gy < dilatedPhotoBlocks.GetLength(0);

        for (int x = 0; x < width; x++)
        {
            byte* px = rowPtr + (x << 2);
            byte r = px[0];
            byte g = px[1];
            byte b = px[2];

            int maxC = Math.Max(r, Math.Max(g, b));
            int minC = Math.Min(r, Math.Min(g, b));
            int deltaChroma = maxC - minC;

            // Face / ID Photo Protection Check: Keep 100% natural and untouched
            if (hasFaceProtection)
            {
                if (IsSkinTone(r, g, b))
                {
                    // Direct skin tone: preserve 100% untouched
                    continue;
                }

                int gx = x / sb;
                if (hasPhotoBlocksInRow && dilatedPhotoBlocks != null && gx < dilatedPhotoBlocks.GetLength(1) && dilatedPhotoBlocks[gy, gx])
                {
                    // Inside dilated photo region:
                    // Protect hair, facial features, and photo subject.
                    // Only whiten if it is genuinely the neutral paper background.
                    int lum = (77 * r + 150 * g + 29 * b) >> 8;
                    int bgEst = bgRow[x];
                    bool isNeutralPaper = deltaChroma <= 6 && lum >= Math.Max(150, bgEst - 15);
                    if (!isNeutralPaper)
                    {
                        // True photo content (hair, eyes, clothing, or tinted backdrop): keep 100% untouched
                        continue;
                    }
                }
            }

            byte rNorm, gNorm, bNorm;
            if (whiten)
            {
                int bg = bgRow[x];
                int bgShift = bg << 8;
                rNorm = divLut[bgShift | r];
                gNorm = divLut[bgShift | g];
                bNorm = divLut[bgShift | b];
            }
            else
            {
                rNorm = r;
                gNorm = g;
                bNorm = b;
            }

            int yNorm = (77 * rNorm + 150 * gNorm + 29 * bNorm) >> 8;
            byte monoVal = (darken || whiten) ? contrastLut[yNorm] : (byte)yNorm;

            if (!preserveColor || deltaChroma <= chromaLow)
            {
                px[0] = monoVal;
                px[1] = monoVal;
                px[2] = monoVal;
            }
            else if (deltaChroma >= chromaHigh)
            {
                int rDiff = rNorm - yNorm;
                int gDiff = gNorm - yNorm;
                int bDiff = bNorm - yNorm;

                px[0] = (byte)Math.Clamp((int)(yNorm + rDiff * satBoost + 0.5f), 0, 255);
                px[1] = (byte)Math.Clamp((int)(yNorm + gDiff * satBoost + 0.5f), 0, 255);
                px[2] = (byte)Math.Clamp((int)(yNorm + bDiff * satBoost + 0.5f), 0, 255);
            }
            else
            {
                float alpha = (deltaChroma - chromaLow) * chromaRangeInv;
                float invAlpha = 1.0f - alpha;

                int rDiff = rNorm - yNorm;
                int gDiff = gNorm - yNorm;
                int bDiff = bNorm - yNorm;

                int rBoost = Math.Clamp((int)(yNorm + rDiff * satBoost + 0.5f), 0, 255);
                int gBoost = Math.Clamp((int)(yNorm + gDiff * satBoost + 0.5f), 0, 255);
                int bBoost = Math.Clamp((int)(yNorm + bDiff * satBoost + 0.5f), 0, 255);

                px[0] = (byte)Math.Clamp((int)(invAlpha * monoVal + alpha * rBoost + 0.5f), 0, 255);
                px[1] = (byte)Math.Clamp((int)(invAlpha * monoVal + alpha * gBoost + 0.5f), 0, 255);
                px[2] = (byte)Math.Clamp((int)(invAlpha * monoVal + alpha * bBoost + 0.5f), 0, 255);
            }
        }
    }

    private static unsafe void ProcessBgraRow(
        byte* rowPtr,
        int width,
        int y,
        int sb,
        ReadOnlySpan<byte> bgRow,
        byte[] divLut,
        byte[] contrastLut,
        bool whiten,
        bool darken,
        bool preserveColor,
        float chromaLow,
        float chromaHigh,
        float chromaRangeInv,
        float satBoost,
        bool[,] dilatedPhotoBlocks,
        bool hasFaceProtection)
    {
        int gy = y / sb;
        bool hasPhotoBlocksInRow = hasFaceProtection && dilatedPhotoBlocks != null && gy < dilatedPhotoBlocks.GetLength(0);

        for (int x = 0; x < width; x++)
        {
            byte* px = rowPtr + (x << 2);
            byte b = px[0];
            byte g = px[1];
            byte r = px[2];

            int maxC = Math.Max(r, Math.Max(g, b));
            int minC = Math.Min(r, Math.Min(g, b));
            int deltaChroma = maxC - minC;

            // Face / ID Photo Protection Check: Keep 100% natural and untouched
            if (hasFaceProtection)
            {
                if (IsSkinTone(r, g, b))
                {
                    // Direct skin tone: preserve 100% untouched
                    continue;
                }

                int gx = x / sb;
                if (hasPhotoBlocksInRow && dilatedPhotoBlocks != null && gx < dilatedPhotoBlocks.GetLength(1) && dilatedPhotoBlocks[gy, gx])
                {
                    // Inside dilated photo region:
                    // Protect hair, facial features, and photo subject.
                    // Only whiten if it is genuinely the neutral paper background.
                    int lum = (77 * r + 150 * g + 29 * b) >> 8;
                    int bgEst = bgRow[x];
                    bool isNeutralPaper = deltaChroma <= 6 && lum >= Math.Max(150, bgEst - 15);
                    if (!isNeutralPaper)
                    {
                        // True photo content (hair, eyes, clothing, or tinted backdrop): keep 100% untouched
                        continue;
                    }
                }
            }

            byte rNorm, gNorm, bNorm;
            if (whiten)
            {
                int bg = bgRow[x];
                int bgShift = bg << 8;
                rNorm = divLut[bgShift | r];
                gNorm = divLut[bgShift | g];
                bNorm = divLut[bgShift | b];
            }
            else
            {
                rNorm = r;
                gNorm = g;
                bNorm = b;
            }

            int yNorm = (77 * rNorm + 150 * gNorm + 29 * bNorm) >> 8;
            byte monoVal = (darken || whiten) ? contrastLut[yNorm] : (byte)yNorm;

            if (!preserveColor || deltaChroma <= chromaLow)
            {
                px[0] = monoVal;
                px[1] = monoVal;
                px[2] = monoVal;
            }
            else if (deltaChroma >= chromaHigh)
            {
                int rDiff = rNorm - yNorm;
                int gDiff = gNorm - yNorm;
                int bDiff = bNorm - yNorm;

                px[0] = (byte)Math.Clamp((int)(yNorm + bDiff * satBoost + 0.5f), 0, 255);
                px[1] = (byte)Math.Clamp((int)(yNorm + gDiff * satBoost + 0.5f), 0, 255);
                px[2] = (byte)Math.Clamp((int)(yNorm + rDiff * satBoost + 0.5f), 0, 255);
            }
            else
            {
                float alpha = (deltaChroma - chromaLow) * chromaRangeInv;
                float invAlpha = 1.0f - alpha;

                int rDiff = rNorm - yNorm;
                int gDiff = gNorm - yNorm;
                int bDiff = bNorm - yNorm;

                int rBoost = Math.Clamp((int)(yNorm + rDiff * satBoost + 0.5f), 0, 255);
                int gBoost = Math.Clamp((int)(yNorm + gDiff * satBoost + 0.5f), 0, 255);
                int bBoost = Math.Clamp((int)(yNorm + bDiff * satBoost + 0.5f), 0, 255);

                px[0] = (byte)Math.Clamp((int)(invAlpha * monoVal + alpha * bBoost + 0.5f), 0, 255);
                px[1] = (byte)Math.Clamp((int)(invAlpha * monoVal + alpha * gBoost + 0.5f), 0, 255);
                px[2] = (byte)Math.Clamp((int)(invAlpha * monoVal + alpha * rBoost + 0.5f), 0, 255);
            }
        }
    }
}
