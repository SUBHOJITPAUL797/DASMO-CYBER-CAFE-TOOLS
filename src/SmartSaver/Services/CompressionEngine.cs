using System.IO;
using System.Threading.Tasks;
using System.Linq;
using Serilog;
using SmartSaver.Models;
using PdfSharpCore.Pdf;
using PdfSharpCore.Drawing;

namespace SmartSaver.Services;

/// <summary>
/// Central compression orchestrator. Routes files to the appropriate compressor
/// based on extension, manages backup to the <c>_Originals</c> folder, and uses
/// a temp-file-then-replace strategy to prevent data loss.
/// </summary>
public sealed class CompressionEngine
{
    private const long MinimumFloorBytes = 5 * 1024; // 5 KB

    private readonly ImageCompressor _imageCompressor;
    private readonly PdfCompressor _pdfCompressor;
    private readonly OfficeCompressor _officeCompressor;

    /// <summary>
    /// Initializes the <see cref="CompressionEngine"/> with all compressor implementations.
    /// </summary>
    public CompressionEngine(
        ImageCompressor imageCompressor,
        PdfCompressor pdfCompressor,
        OfficeCompressor officeCompressor)
    {
        _imageCompressor = imageCompressor ?? throw new ArgumentNullException(nameof(imageCompressor));
        _pdfCompressor = pdfCompressor ?? throw new ArgumentNullException(nameof(pdfCompressor));
        _officeCompressor = officeCompressor ?? throw new ArgumentNullException(nameof(officeCompressor));
    }

    /// <summary>
    /// Compresses a file based on its extension. Backs up the original if configured,
    /// writes to a temp file first, verifies the result, then replaces the original.
    /// </summary>
    /// <param name="filePath">Absolute path to the file to compress.</param>
    /// <returns>A <see cref="CompressionResult"/> describing the outcome.</returns>
    public async Task<CompressionResult> CompressFileAsync(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        var settings = SettingsManager.Instance.Current;
        long targetBytes = settings.AutoCompress.TargetSizeKB * 1024L;

        return await Task.Run(() => CompressFileInternal(filePath, targetBytes, settings));
    }

    /// <summary>
    /// Compresses a file to a specific target size. Does not read from settings.
    /// </summary>
    /// <param name="filePath">Absolute path to the file to compress.</param>
    /// <param name="targetBytes">Target file size in bytes.</param>
    /// <param name="keepBackup">Whether to back up the original.</param>
    /// <returns>A <see cref="CompressionResult"/> describing the outcome.</returns>
    public async Task<CompressionResult> CompressFileAsync(string filePath, long targetBytes, bool keepBackup = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        var settings = SettingsManager.Instance.Current;
        return await Task.Run(() =>
        {
            var effectiveSettings = settings;
            return CompressFileInternal(filePath, targetBytes, effectiveSettings, keepBackup);
        });
    }

    /// <summary>
    /// Compresses a file to a specific target size with optional format conversion.
    /// </summary>
    public async Task<CompressionResult> CompressFileAsync(
        string filePath, string outputPath, long targetBytes, string outputExtension, bool keepBackup = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        var settings = SettingsManager.Instance.Current;
        return await Task.Run(() =>
            CompressFileToPathInternal(filePath, outputPath, targetBytes, outputExtension, settings, keepBackup));
    }

    /// <summary>
    /// Merges multiple PDF files into one output file, with optional target size compression.
    /// </summary>
    public async Task<CompressionResult> MergePdfsAsync(
        IReadOnlyList<string> sourceFiles, string outputPath, long? targetBytes = null)
    {
        ArgumentNullException.ThrowIfNull(sourceFiles);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        return await Task.Run(() =>
        {
            var validFiles = sourceFiles.Where(File.Exists).ToList();
            if (validFiles.Count == 0)
            {
                return new CompressionResult
                {
                    FilePath = outputPath,
                    Success = false,
                    Message = "No valid PDF files selected."
                };
            }

            long totalOrigBytes = validFiles.Sum(f => new FileInfo(f).Length);
            bool ok = _pdfCompressor.MergePdfs(validFiles, outputPath, targetBytes);

            if (ok && File.Exists(outputPath))
            {
                long newSize = new FileInfo(outputPath).Length;
                string sizeMsg = targetBytes.HasValue && targetBytes.Value > 0
                    ? $"Merged {validFiles.Count} files ({CompressionResult.FormatFileSize(totalOrigBytes)}) → {CompressionResult.FormatFileSize(newSize)} (Target: {CompressionResult.FormatFileSize(targetBytes.Value)})"
                    : $"Merged {validFiles.Count} files ({CompressionResult.FormatFileSize(totalOrigBytes)}) → {CompressionResult.FormatFileSize(newSize)}";

                return new CompressionResult
                {
                    FilePath = outputPath,
                    OriginalSizeBytes = totalOrigBytes,
                    NewSizeBytes = newSize,
                    Success = true,
                    Message = sizeMsg
                };
            }

            return new CompressionResult
            {
                FilePath = outputPath,
                OriginalSizeBytes = totalOrigBytes,
                NewSizeBytes = totalOrigBytes,
                Success = false,
                Message = "Failed to merge PDF files."
            };
        });
    }

    /// <summary>
    /// Extracts specified pages from a PDF to an output file.
    /// </summary>
    public async Task<CompressionResult> ExtractPdfPagesAsync(
        string sourcePath, string outputPath, IEnumerable<int> pageNumbers, long? targetBytes = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        return await Task.Run(() =>
        {
            if (!File.Exists(sourcePath))
            {
                return new CompressionResult
                {
                    FilePath = outputPath,
                    Success = false,
                    Message = "Source PDF not found."
                };
            }

            long origSize = new FileInfo(sourcePath).Length;
            var list = pageNumbers.ToList();
            bool ok = _pdfCompressor.ExtractPdfPages(sourcePath, outputPath, list, targetBytes);

            if (ok && File.Exists(outputPath))
            {
                long newSize = new FileInfo(outputPath).Length;
                string msg = targetBytes.HasValue && targetBytes.Value > 0
                    ? $"Extracted {list.Count} page(s) → {CompressionResult.FormatFileSize(newSize)} (Target: {CompressionResult.FormatFileSize(targetBytes.Value)})"
                    : $"Extracted {list.Count} page(s) → {CompressionResult.FormatFileSize(newSize)}";

                return new CompressionResult
                {
                    FilePath = outputPath,
                    OriginalSizeBytes = origSize,
                    NewSizeBytes = newSize,
                    Success = true,
                    Message = msg
                };
            }

            return new CompressionResult
            {
                FilePath = outputPath,
                OriginalSizeBytes = origSize,
                NewSizeBytes = origSize,
                Success = false,
                Message = "Failed to extract pages."
            };
        });
    }

    /// <summary>
    /// Extracts pages in exact custom order and rotations from one or more source PDF files.
    /// Optionally compresses output to targetBytes if specified.
    /// </summary>
    public async Task<CompressionResult> ExtractPdfPagesOrderedAsync(
        string outputPath, IEnumerable<PdfPageExtractionItem> pageItems, long? targetBytes = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        return await Task.Run(() =>
        {
            var list = pageItems.ToList();
            if (list.Count == 0)
            {
                return new CompressionResult
                {
                    FilePath = outputPath,
                    Success = false,
                    Message = "No pages selected to extract."
                };
            }

            long origSize = 0;
            foreach (var group in list.GroupBy(i => i.SourcePdfPath))
            {
                if (File.Exists(group.Key)) origSize += new FileInfo(group.Key).Length;
            }

            bool ok = _pdfCompressor.ExtractPdfPagesOrdered(outputPath, list, targetBytes);

            if (ok && File.Exists(outputPath))
            {
                long newSize = new FileInfo(outputPath).Length;
                string msg = targetBytes.HasValue && targetBytes.Value > 0
                    ? $"Extracted {list.Count} page(s) in custom order → {CompressionResult.FormatFileSize(newSize)} (Target: {CompressionResult.FormatFileSize(targetBytes.Value)})"
                    : $"Extracted {list.Count} page(s) in custom order → {CompressionResult.FormatFileSize(newSize)}";

                return new CompressionResult
                {
                    FilePath = outputPath,
                    OriginalSizeBytes = origSize,
                    NewSizeBytes = newSize,
                    Success = true,
                    Message = msg
                };
            }

            return new CompressionResult
            {
                FilePath = outputPath,
                OriginalSizeBytes = origSize,
                NewSizeBytes = origSize,
                Success = false,
                Message = "Failed to extract organized pages."
            };
        });
    }

    /// <summary>
    /// Splits every page of a PDF into individual single-page PDF files.
    /// </summary>
    public async Task<List<CompressionResult>> SplitPdfAllPagesAsync(
        string sourcePath, string outputDirectory, long? targetBytes = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);

        return await Task.Run(() =>
        {
            var results = new List<CompressionResult>();
            if (!File.Exists(sourcePath)) return results;

            int totalPages = _pdfCompressor.GetPageCount(sourcePath);
            if (totalPages == 0) return results;

            Directory.CreateDirectory(outputDirectory);
            string baseName = Path.GetFileNameWithoutExtension(sourcePath);

            for (int p = 1; p <= totalPages; p++)
            {
                string outPath = Path.Combine(outputDirectory, $"{baseName}_page_{p}.pdf");
                bool ok = _pdfCompressor.ExtractPdfPages(sourcePath, outPath, new[] { p }, targetBytes);
                if (ok && File.Exists(outPath))
                {
                    results.Add(new CompressionResult
                    {
                        FilePath = outPath,
                        NewSizeBytes = new FileInfo(outPath).Length,
                        Success = true,
                        Message = $"Page {p}/{totalPages} extracted"
                    });
                }
            }

            return results;
        });
    }

    /// <summary>
    /// Converts a list of image files into a single A4 PDF.
    /// </summary>
    public async Task<CompressionResult> ImagesToPdfAsync(
        IReadOnlyList<string> imagePaths, string outputPath, bool fitA4 = true, long? targetBytes = null)
    {
        ArgumentNullException.ThrowIfNull(imagePaths);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        return await Task.Run(() =>
        {
            var validImages = imagePaths.Where(File.Exists).ToList();
            if (validImages.Count == 0)
            {
                return new CompressionResult
                {
                    FilePath = outputPath,
                    Success = false,
                    Message = "No valid input image files found."
                };
            }

            long totalOrigBytes = validImages.Sum(f => new FileInfo(f).Length);
            bool ok = _pdfCompressor.ImagesToPdf(validImages, outputPath, fitA4, targetBytes);

            if (ok && File.Exists(outputPath))
            {
                long newSize = new FileInfo(outputPath).Length;
                string sizeMsg = targetBytes.HasValue && targetBytes.Value > 0
                    ? $"Converted {validImages.Count} images to PDF: {CompressionResult.FormatFileSize(newSize)} (Target: {CompressionResult.FormatFileSize(targetBytes.Value)})"
                    : $"Converted {validImages.Count} images to PDF: {CompressionResult.FormatFileSize(newSize)}";

                return new CompressionResult
                {
                    FilePath = outputPath,
                    OriginalSizeBytes = totalOrigBytes,
                    NewSizeBytes = newSize,
                    Success = true,
                    Message = sizeMsg
                };
            }

            return new CompressionResult
            {
                FilePath = outputPath,
                OriginalSizeBytes = totalOrigBytes,
                NewSizeBytes = totalOrigBytes,
                Success = false,
                Message = "Failed to convert images to PDF."
            };
        });
    }

    /// <summary>
    /// Converts pages of a PDF to high-resolution images.
    /// </summary>
    public async Task<List<string>> PdfToImagesAsync(
        string pdfPath, string outputDirectory, string format = ".jpg", int dpi = 300)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pdfPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);

        return await Task.Run(() => _pdfCompressor.PdfToImages(pdfPath, outputDirectory, format, dpi));
    }

    private CompressionResult CompressFileInternal(
        string filePath, long targetBytes, AppSettings settings, bool? overrideKeepBackup = null)
    {
        if (!File.Exists(filePath))
        {
            Log.Warning("File not found: {Path}", filePath);
            return new CompressionResult
            {
                FilePath = filePath,
                Success = false,
                Message = "File not found"
            };
        }

        var originalInfo = new FileInfo(filePath);
        long originalSize = originalInfo.Length;
        string extension = originalInfo.Extension.ToLowerInvariant();

        // ── UPSCALE PATH: target is LARGER than original ─────────────────────
        // e.g. portal requires minimum 200 KB but file is only 50 KB
        if (originalSize < targetBytes)
        {
            return HandleUpscaleInternal(filePath, filePath, originalSize, targetBytes, extension, settings,
                overrideKeepBackup, replacing: true);
        }

        if (!IsSupportedExtension(extension, settings))
        {
            Log.Debug("Unsupported extension {Ext} for {Path}", extension, filePath);
            return new CompressionResult
            {
                FilePath = filePath,
                OriginalSizeBytes = originalSize,
                NewSizeBytes = originalSize,
                Success = false,
                Message = $"Unsupported file type: {extension}"
            };
        }

        // Compress to a temporary file first
        string tempPath = Path.Combine(
            Path.GetDirectoryName(filePath)!,
            $".smartsaver_tmp_{Guid.NewGuid():N}{extension}");

        try
        {
            bool compressed = extension switch
            {
                ".jpg" or ".jpeg" or ".png" =>
                    _imageCompressor.CompressToTargetSize(filePath, tempPath, targetBytes,
                        settings.ImageResize.MinimumJpegQuality),

                ".pdf" =>
                    _pdfCompressor.CompressToTargetSize(filePath, tempPath, targetBytes),

                ".docx" or ".xlsx" =>
                    _officeCompressor.CompressToTargetSize(filePath, tempPath, targetBytes),

                _ => false
            };

            // Verify temp file integrity
            if (!File.Exists(tempPath))
            {
                Log.Warning("Compression produced no output file for {Path}", filePath);
                return FailResult(filePath, originalSize, "Compressor produced no output");
            }

            var tempInfo = new FileInfo(tempPath);
            if (tempInfo.Length == 0)
            {
                Log.Warning("Compression produced zero-byte file for {Path}", filePath);
                CleanupTemp(tempPath);
                return FailResult(filePath, originalSize, "Compressor produced zero-byte file");
            }

            if (tempInfo.Length < MinimumFloorBytes)
            {
                Log.Warning("Compressed file below {Floor} byte floor for {Path}", MinimumFloorBytes, filePath);
                CleanupTemp(tempPath);
                return FailResult(filePath, originalSize, $"Result below minimum {MinimumFloorBytes / 1024} KB floor");
            }

            // No improvement — don't replace
            if (tempInfo.Length >= originalSize)
            {
                Log.Information("Compression did not reduce size for {Path}", filePath);
                CleanupTemp(tempPath);
                return new CompressionResult
                {
                    FilePath = filePath,
                    OriginalSizeBytes = originalSize,
                    NewSizeBytes = originalSize,
                    Success = true,
                    Message = "File could not be compressed further"
                };
            }

            // Backup original if configured
            bool keepBackup = overrideKeepBackup ?? settings.AutoCompress.KeepOriginalBackup;
            if (keepBackup)
            {
                BackupOriginal(filePath, settings.AutoCompress.BackupFolderName);
            }

            // Replace original with compressed version
            File.Move(tempPath, filePath, overwrite: true);

            long newSize = new FileInfo(filePath).Length;
            Log.Information("Compressed {Path}: {OrigSize} → {NewSize} bytes ({Pct:F1}% saved)",
                filePath, originalSize, newSize,
                (1.0 - (double)newSize / originalSize) * 100);

            return new CompressionResult
            {
                FilePath = filePath,
                OriginalSizeBytes = originalSize,
                NewSizeBytes = newSize,
                Success = true
            };
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Compression failed for {Path}", filePath);
            CleanupTemp(tempPath);
            return FailResult(filePath, originalSize, ex.Message);
        }
    }

    private static bool IsSupportedExtension(string extension, AppSettings settings) =>
        settings.AutoCompress.SupportedExtensions
            .Any(e => e.Equals(extension, StringComparison.OrdinalIgnoreCase));

    private static void BackupOriginal(string filePath, string backupFolderName)
    {
        try
        {
            string directory = Path.GetDirectoryName(filePath)!;
            string backupDir = Path.Combine(directory, backupFolderName);
            Directory.CreateDirectory(backupDir);

            string fileName = Path.GetFileName(filePath);
            string backupPath = Path.Combine(backupDir, fileName);

            // Avoid overwriting existing backups — append timestamp
            if (File.Exists(backupPath))
            {
                string nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
                string ext = Path.GetExtension(fileName);
                string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                backupPath = Path.Combine(backupDir, $"{nameWithoutExt}_{timestamp}{ext}");
            }

            File.Copy(filePath, backupPath, overwrite: false);
            Log.Debug("Backed up original to {BackupPath}", backupPath);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to back up {Path}", filePath);
        }
    }

    private static void CleanupTemp(string tempPath)
    {
        try
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Failed to clean up temp file {Path}", tempPath);
        }
    }

    private static CompressionResult FailResult(string filePath, long originalSize, string message) =>
        new()
        {
            FilePath = filePath,
            OriginalSizeBytes = originalSize,
            NewSizeBytes = originalSize,
            Success = false,
            Message = message
        };

    private CompressionResult CompressFileToPathInternal(
        string sourcePath, string outputPath, long targetBytes, string outputExtension,
        AppSettings settings, bool keepBackup)
    {
        if (!File.Exists(sourcePath))
        {
            return new CompressionResult { FilePath = sourcePath, Success = false, Message = "Source file not found" };
        }

        var originalInfo = new FileInfo(sourcePath);
        long originalSize = originalInfo.Length;
        string sourceExt = originalInfo.Extension.ToLowerInvariant();
        string outExt = outputExtension.ToLowerInvariant();
        bool replacing = string.Equals(sourcePath, outputPath, StringComparison.OrdinalIgnoreCase);

        // Use a temp path to avoid overwriting source prematurely
        string tempPath = Path.Combine(
            Path.GetDirectoryName(outputPath)!,
            $".smartsaver_tmp_{Guid.NewGuid():N}{outExt}");

        try
        {
            // ── UPSCALE PATH: when target is LARGER than the original ─────────────
            // e.g. portal needs minimum 200 KB but this file is only 50 KB
            if (originalSize < targetBytes && (sourceExt == outExt) &&
                (IsImageExt(sourceExt) || sourceExt == ".pdf"))
            {
                // Route to upscale, then let the normal temp→output move logic proceed below
                // We call UpscaleToTargetSize directly here, writing to tempPath
                bool upscaleOk = sourceExt == ".pdf"
                    ? _pdfCompressor.UpscaleToTargetSize(sourcePath, tempPath, targetBytes)
                    : _imageCompressor.UpscaleToTargetSize(sourcePath, tempPath, targetBytes);

                if (!upscaleOk || !File.Exists(tempPath) || new FileInfo(tempPath).Length == 0)
                {
                    CleanupTemp(tempPath);
                    return FailResult(sourcePath, originalSize, "Upscale not possible for this file type.");
                }

                long upscaledSize = new FileInfo(tempPath).Length;
                if (replacing && keepBackup)
                    BackupOriginal(sourcePath, settings.AutoCompress.BackupFolderName);

                Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                FileWatcherService.IgnoreOutputFile(outputPath);
                if (replacing) FileWatcherService.IgnoreOutputFile(sourcePath);
                File.Move(tempPath, outputPath, overwrite: true);

                bool metMin = upscaledSize >= targetBytes;
                string upMsg = metMin
                    ? $"📈 Upscaled from {CompressionResult.FormatFileSize(originalSize)} to {CompressionResult.FormatFileSize(upscaledSize)} to meet minimum size."
                    : $"📈 Best effort upscale: {CompressionResult.FormatFileSize(originalSize)} → {CompressionResult.FormatFileSize(upscaledSize)} (Target was {CompressionResult.FormatFileSize(targetBytes)}).";                

                return new CompressionResult
                {
                    FilePath = outputPath,
                    OriginalSizeBytes = originalSize,
                    NewSizeBytes = upscaledSize,
                    Success = true,
                    Message = upMsg
                };
            }

            bool compressed = (sourceExt, outExt) switch
            {
                // Image → Image (same or different format)
                var (s, o) when IsImageExt(s) && IsImageExt(o) =>
                    _imageCompressor.CompressToTargetSize(sourcePath, tempPath, targetBytes,
                        settings.ImageResize.MinimumJpegQuality, outExt),

                // Image → PDF conversion
                var (s, o) when IsImageExt(s) && o == ".pdf" =>
                    ConvertImageToPdf(sourcePath, tempPath, targetBytes),

                // PDF → PDF
                (".pdf", ".pdf") => _pdfCompressor.CompressToTargetSize(sourcePath, tempPath, targetBytes),

                // Office → Office
                var (s, o) when IsOfficeExt(s) && s == o =>
                    _officeCompressor.CompressToTargetSize(sourcePath, tempPath, targetBytes),

                _ => false
            };

            if (!File.Exists(tempPath))
            {
                if (!replacing && File.Exists(sourcePath))
                {
                    File.Copy(sourcePath, tempPath, overwrite: true);
                    Log.Warning("Compressor produced no output for {Path} — copied original as best-effort fallback", sourcePath);
                }
                else
                {
                    return FailResult(sourcePath, originalSize, "Compressor produced no output");
                }
            }

            var tempInfo = new FileInfo(tempPath);
            if (tempInfo.Length == 0)
            {
                CleanupTemp(tempPath);
                return FailResult(sourcePath, originalSize, "Compressor produced zero-byte output");
            }

            // If output is no improvement over original — handle gracefully
            if (tempInfo.Length >= originalSize && !compressed)
            {
                CleanupTemp(tempPath);

                string sizeHint = $"File could not be compressed further ({CompressionResult.FormatFileSize(originalSize)}). It may already be optimized.";

                return new CompressionResult
                {
                    FilePath = sourcePath,
                    OriginalSizeBytes = originalSize,
                    NewSizeBytes = originalSize,
                    Success = false,
                    Message = sizeHint
                };
            }

            // Backup original if replacing and keepBackup=true
            if (replacing && keepBackup)
                BackupOriginal(sourcePath, settings.AutoCompress.BackupFolderName);

            // Move temp → output
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            FileWatcherService.IgnoreOutputFile(outputPath);
            if (replacing) FileWatcherService.IgnoreOutputFile(sourcePath);
            File.Move(tempPath, outputPath, overwrite: true);

            long newSize = new FileInfo(outputPath).Length;
            Log.Information("Compress {Src} -> {Out}: {Orig} -> {New} bytes (Target: {Target})",
                sourcePath, outputPath, originalSize, newSize, targetBytes);

            bool metTarget = newSize <= targetBytes;
            string? msg = null;
            if (!metTarget)
            {
                msg = newSize < originalSize
                    ? $"⚠️ Best effort: Reduced from {CompressionResult.FormatFileSize(originalSize)} to {CompressionResult.FormatFileSize(newSize)} (Target was {CompressionResult.FormatFileSize(targetBytes)})."
                    : $"⚠️ Could not compress below original size. File may already be highly optimized.";
            }

            return new CompressionResult
            {
                FilePath = outputPath,
                OriginalSizeBytes = originalSize,
                NewSizeBytes = newSize,
                Success = true,
                Message = msg
            };
        }
        catch (Exception ex)
        {
            Log.Error(ex, "CompressFileToPathInternal failed for {Path}", sourcePath);
            CleanupTemp(tempPath);
            return FailResult(sourcePath, originalSize, ex.Message);
        }
    }

    private CompressionResult HandleUpscaleInternal(
        string sourcePath, string outputPath, long originalSize, long targetBytes,
        string extension, AppSettings settings, bool? overrideKeepBackup, bool replacing)
    {
        string tempPath = Path.Combine(
            Path.GetDirectoryName(sourcePath)!,
            $".smartsaver_up_{Guid.NewGuid():N}{extension}");

        try
        {
            bool ok = extension switch
            {
                ".jpg" or ".jpeg" or ".png" or ".webp" or ".bmp" or ".tiff" or ".tif" =>
                    _imageCompressor.UpscaleToTargetSize(sourcePath, tempPath, targetBytes),
                ".pdf" =>
                    _pdfCompressor.UpscaleToTargetSize(sourcePath, tempPath, targetBytes),
                _ => false
            };

            if (!ok || !File.Exists(tempPath) || new FileInfo(tempPath).Length == 0)
            {
                CleanupTemp(tempPath);
                return FailResult(sourcePath, originalSize, "Upscale is not supported for this file type.");
            }

            long newSize = new FileInfo(tempPath).Length;

            bool keepBackup = overrideKeepBackup ?? settings.AutoCompress.KeepOriginalBackup;
            if (replacing && keepBackup)
                BackupOriginal(sourcePath, settings.AutoCompress.BackupFolderName);

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            File.Move(tempPath, outputPath, overwrite: true);

            Log.Information("Upscaled {Path}: {Old} → {New} bytes (Target: {Target})",
                sourcePath, originalSize, newSize, targetBytes);

            bool metTarget = newSize >= targetBytes;
            string msg = metTarget
                ? $"📈 Upscaled from {CompressionResult.FormatFileSize(originalSize)} to {CompressionResult.FormatFileSize(newSize)} to meet minimum size."
                : $"📈 Best effort upscale: {CompressionResult.FormatFileSize(originalSize)} → {CompressionResult.FormatFileSize(newSize)} (Target was {CompressionResult.FormatFileSize(targetBytes)}).";                

            return new CompressionResult
            {
                FilePath = outputPath,
                OriginalSizeBytes = originalSize,
                NewSizeBytes = newSize,
                Success = true,
                Message = msg
            };
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Upscale failed for {Path}", sourcePath);
            CleanupTemp(tempPath);
            return FailResult(sourcePath, originalSize, $"Upscale failed: {ex.Message}");
        }
    }

    private static bool IsImageExt(string ext) =>
        ext is ".jpg" or ".jpeg" or ".png" or ".bmp" or ".webp" or ".tiff" or ".tif";

    private static bool IsOfficeExt(string ext) =>
        ext is ".docx" or ".xlsx";

    private bool ConvertImageToPdf(string imagePath, string pdfTempPath, long targetBytes)
    {
        string rawPdfTemp = Path.Combine(
            Path.GetDirectoryName(pdfTempPath)!,
            $".smartsaver_raw_pdf_{Guid.NewGuid():N}.pdf");

        try
        {
            using (var document = new PdfDocument())
            {
                var page = document.AddPage();
                using (var image = XImage.FromFile(imagePath))
                {
                    page.Width = image.PointWidth;
                    page.Height = image.PointHeight;
                    using (var gfx = XGraphics.FromPdfPage(page))
                    {
                        gfx.DrawImage(image, 0, 0, image.PointWidth, image.PointHeight);
                    }
                }
                document.Save(rawPdfTemp);
            }

            bool compressed = _pdfCompressor.CompressToTargetSize(rawPdfTemp, pdfTempPath, targetBytes);

            // Clean up intermediate raw PDF
            try { if (File.Exists(rawPdfTemp)) File.Delete(rawPdfTemp); } catch { }

            return compressed;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to convert image {ImagePath} to PDF", imagePath);
            try { if (File.Exists(rawPdfTemp)) File.Delete(rawPdfTemp); } catch { }
            return false;
        }
    }
}
