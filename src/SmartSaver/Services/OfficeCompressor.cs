using System.IO;
using System.IO.Compression;
using Serilog;

namespace SmartSaver.Services;

/// <summary>
/// Compresses DOCX and XLSX files by treating them as ZIP archives,
/// locating embedded images, re-compressing them via <see cref="ImageCompressor"/>,
/// and rebuilding the archive.
/// </summary>
public sealed class OfficeCompressor
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".bmp", ".tiff", ".tif", ".gif"
    };

    private readonly ImageCompressor _imageCompressor;

    /// <summary>
    /// Initializes a new <see cref="OfficeCompressor"/> with the specified image compressor.
    /// </summary>
    /// <param name="imageCompressor">The image compressor used to re-encode embedded images.</param>
    public OfficeCompressor(ImageCompressor imageCompressor)
    {
        _imageCompressor = imageCompressor ?? throw new ArgumentNullException(nameof(imageCompressor));
    }

    /// <summary>
    /// Compresses an Office document (DOCX or XLSX) to meet a target file size
    /// by re-compressing embedded images.
    /// </summary>
    /// <param name="sourcePath">Absolute path to the source document.</param>
    /// <param name="outputPath">Absolute path for the compressed output.</param>
    /// <param name="targetBytes">Target file size in bytes.</param>
    /// <returns><c>true</c> if the document was compressed to at or below the target size.</returns>
    public bool CompressToTargetSize(string sourcePath, string outputPath, long targetBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        try
        {
            // Work on a copy so the original stays intact
            File.Copy(sourcePath, outputPath, overwrite: true);

            int imagesCompressed = 0;
            long totalSaved = 0;

            using (var archive = ZipFile.Open(outputPath, ZipArchiveMode.Update))
            {
                var imageEntries = archive.Entries
                    .Where(e => IsImageEntry(e.FullName))
                    .ToList();

                if (imageEntries.Count == 0)
                {
                    Log.Information("No embedded images found in {Path}", sourcePath);
                    return new FileInfo(outputPath).Length <= targetBytes;
                }

                // Calculate per-image target: distribute remaining budget across images
                long nonImageSize = new FileInfo(outputPath).Length - imageEntries.Sum(e => e.Length);
                long imageTargetBudget = Math.Max(1024, targetBytes - nonImageSize);
                long perImageTarget = Math.Max(1024, imageTargetBudget / imageEntries.Count);

                foreach (var entry in imageEntries)
                {
                    try
                    {
                        byte[] originalBytes;
                        using (var entryStream = entry.Open())
                        using (var ms = new MemoryStream())
                        {
                            entryStream.CopyTo(ms);
                            originalBytes = ms.ToArray();
                        }

                        if (originalBytes.Length <= perImageTarget)
                        {
                            Log.Debug("Skipping image {Name} — already under target ({Size} bytes)",
                                entry.FullName, originalBytes.Length);
                            continue;
                        }

                        var compressedBytes = _imageCompressor.CompressImageBytes(
                            originalBytes, perImageTarget);

                        if (compressedBytes.Length < originalBytes.Length)
                        {
                            // Delete old entry and create a new one with compressed data
                            string entryName = entry.FullName;
                            entry.Delete();

                            var newEntry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
                            using (var newStream = newEntry.Open())
                            {
                                newStream.Write(compressedBytes, 0, compressedBytes.Length);
                            }

                            long saved = originalBytes.Length - compressedBytes.Length;
                            totalSaved += saved;
                            imagesCompressed++;

                            Log.Debug("Compressed {Name}: {OrigSize} → {NewSize} bytes (saved {Saved})",
                                entryName, originalBytes.Length, compressedBytes.Length, saved);
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "Failed to compress embedded image {Name}", entry.FullName);
                    }
                }
            }

            var finalInfo = new FileInfo(outputPath);
            Log.Information(
                "Office document compressed: {Images} images, {Saved} bytes saved. Final size: {Size} bytes",
                imagesCompressed, totalSaved, finalInfo.Length);

            return finalInfo.Length <= targetBytes;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to compress Office document {SourcePath}", sourcePath);

            // Clean up failed output
            try { if (File.Exists(outputPath)) File.Delete(outputPath); }
            catch { /* best effort cleanup */ }

            return false;
        }
    }

    private static bool IsImageEntry(string entryPath)
    {
        var extension = Path.GetExtension(entryPath);
        if (string.IsNullOrEmpty(extension)) return false;

        // Office documents store images under word/media/ or xl/media/
        bool isMediaPath = entryPath.Contains("/media/", StringComparison.OrdinalIgnoreCase) ||
                           entryPath.Contains("\\media\\", StringComparison.OrdinalIgnoreCase);

        return isMediaPath && ImageExtensions.Contains(extension);
    }
}
