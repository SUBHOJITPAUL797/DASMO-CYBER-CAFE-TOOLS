using System.IO;
using Serilog;

namespace SmartSaver.Helpers;

/// <summary>
/// Utility methods for safe file operations — lock checking, temp files, backup management
/// </summary>
public static class FileHelper
{
    /// <summary>
    /// Minimum compressed file size (5 KB) — below this we consider the output corrupt
    /// </summary>
    public const long MinimumFileSizeBytes = 5 * 1024;

    /// <summary>
    /// Attempts to check if a file is locked by another process.
    /// Retries up to maxRetries times with retryDelayMs between each attempt.
    /// </summary>
    public static async Task<bool> WaitForFileUnlocked(string filePath, int maxRetries = 5, int retryDelayMs = 1000)
    {
        for (int attempt = 0; attempt < maxRetries; attempt++)
        {
            if (IsFileUnlocked(filePath))
                return true;

            Log.Debug("File {FilePath} is locked, attempt {Attempt}/{MaxRetries}", filePath, attempt + 1, maxRetries);
            await Task.Delay(retryDelayMs);
        }

        Log.Warning("File {FilePath} remained locked after {MaxRetries} retries", filePath, maxRetries);
        return false;
    }

    /// <summary>
    /// Checks if a file can be opened for read/write (not locked)
    /// </summary>
    public static bool IsFileUnlocked(string filePath)
    {
        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Creates a temp file path in the same directory as the source file
    /// </summary>
    public static string GetTempFilePath(string originalPath)
    {
        string dir = Path.GetDirectoryName(originalPath) ?? Path.GetTempPath();
        string name = Path.GetFileNameWithoutExtension(originalPath);
        string ext = Path.GetExtension(originalPath);
        return Path.Combine(dir, $"{name}_smartsaver_tmp_{Guid.NewGuid():N}{ext}");
    }

    /// <summary>
    /// Safely replaces the original file with the compressed version using temp file approach.
    /// 1. Verify compressed file exists and is valid
    /// 2. Optionally backup original
    /// 3. Delete original
    /// 4. Move compressed to original path
    /// </summary>
    public static bool SafeReplaceFile(string originalPath, string compressedTempPath, string? backupDir = null)
    {
        try
        {
            // Verify the compressed file is valid
            var compressedInfo = new FileInfo(compressedTempPath);
            if (!compressedInfo.Exists || compressedInfo.Length < MinimumFileSizeBytes)
            {
                Log.Error("Compressed file is invalid or too small: {Size} bytes at {Path}",
                    compressedInfo.Length, compressedTempPath);
                TryDeleteFile(compressedTempPath);
                return false;
            }

            // Backup original if requested
            if (!string.IsNullOrEmpty(backupDir))
            {
                BackupFile(originalPath, backupDir);
            }

            // Replace: delete original, rename temp to original
            string originalDir = Path.GetDirectoryName(originalPath)!;
            string originalName = Path.GetFileName(originalPath);

            File.Delete(originalPath);
            File.Move(compressedTempPath, Path.Combine(originalDir, originalName));

            Log.Information("Successfully replaced {FilePath} with compressed version", originalPath);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to replace file {OriginalPath} with {CompressedPath}",
                originalPath, compressedTempPath);
            TryDeleteFile(compressedTempPath);
            return false;
        }
    }

    /// <summary>
    /// Saves compressed file alongside the original with a suffix
    /// </summary>
    public static string SaveAsNewFile(string originalPath, string compressedTempPath, string suffix)
    {
        string dir = Path.GetDirectoryName(originalPath) ?? ".";
        string name = Path.GetFileNameWithoutExtension(originalPath);
        string ext = Path.GetExtension(originalPath);
        string newPath = Path.Combine(dir, $"{name}{suffix}{ext}");

        // Handle name collision
        int counter = 1;
        while (File.Exists(newPath))
        {
            newPath = Path.Combine(dir, $"{name}{suffix}_{counter}{ext}");
            counter++;
        }

        File.Move(compressedTempPath, newPath);
        Log.Information("Saved compressed file as {NewPath}", newPath);
        return newPath;
    }

    /// <summary>
    /// Backs up a file to the specified backup directory
    /// </summary>
    public static void BackupFile(string filePath, string backupDirName)
    {
        try
        {
            string sourceDir = Path.GetDirectoryName(filePath) ?? ".";
            string backupDir = Path.Combine(sourceDir, backupDirName);
            Directory.CreateDirectory(backupDir);

            string fileName = Path.GetFileName(filePath);
            string backupPath = Path.Combine(backupDir, fileName);

            // Handle name collision in backup dir
            int counter = 1;
            while (File.Exists(backupPath))
            {
                string name = Path.GetFileNameWithoutExtension(filePath);
                string ext = Path.GetExtension(filePath);
                backupPath = Path.Combine(backupDir, $"{name}_{counter}{ext}");
                counter++;
            }

            File.Copy(filePath, backupPath, overwrite: false);
            Log.Debug("Backed up {FileName} to {BackupPath}", fileName, backupPath);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to backup file {FilePath}", filePath);
        }
    }

    /// <summary>
    /// Attempts to delete a file, logging any failures without throwing
    /// </summary>
    public static void TryDeleteFile(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
                File.Delete(filePath);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to delete temp file {FilePath}", filePath);
        }
    }

    /// <summary>
    /// Gets the file extension in lowercase, including the dot
    /// </summary>
    public static string GetNormalizedExtension(string filePath)
    {
        return Path.GetExtension(filePath).ToLowerInvariant();
    }

    /// <summary>
    /// Checks if an extension is a supported image type
    /// </summary>
    public static bool IsImageExtension(string extension)
    {
        return extension is ".jpg" or ".jpeg" or ".png" or ".bmp" or ".webp" or ".tiff" or ".tif";
    }

    /// <summary>
    /// Checks if an extension is a supported file type for compression
    /// </summary>
    public static bool IsSupportedExtension(string extension, IEnumerable<string> supportedExtensions)
    {
        return supportedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }
}
