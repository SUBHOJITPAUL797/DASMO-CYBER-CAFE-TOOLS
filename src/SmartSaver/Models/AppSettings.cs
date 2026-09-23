using System.IO;
namespace SmartSaver.Models;

/// <summary>
/// Root settings object persisted to settings.json
/// </summary>
public class AppSettings
{
    public AutoCompressSettings AutoCompress { get; set; } = new();
    public ImageResizeSettings ImageResize { get; set; } = new();
    public GeneralSettings General { get; set; } = new();
}

/// <summary>
/// Settings for the auto-compress file watcher feature
/// </summary>
public class AutoCompressSettings
{
    public bool Enabled { get; set; } = true;
    /// <summary>
    /// Action mode when new file is detected: "prompt" (Ask user with dialog), "silent" (Auto-compress in background to TargetSizeKB), or "off" (Disabled)
    /// </summary>
    public string ActionOnNewFile { get; set; } = "prompt";
    public List<string> WatchFolders { get; set; } = new()
    {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Desktop"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Pictures")
    };
    public int TargetSizeKB { get; set; } = 200;
    public bool KeepOriginalBackup { get; set; } = true;
    public string BackupFolderName { get; set; } = "_Originals";
    public List<string> SupportedExtensions { get; set; } = new()
    {
        ".pdf", ".jpg", ".jpeg", ".png", ".docx", ".xlsx"
    };
}

/// <summary>
/// Settings for the right-click image resize feature
/// </summary>
public class ImageResizeSettings
{
    /// <summary>
    /// Default mode: "fileSize", "dimensions", or "ask"
    /// </summary>
    public string DefaultMode { get; set; } = "ask";
    public int DefaultTargetSizeKB { get; set; } = 200;
    public int DefaultWidth { get; set; } = 0;
    public int DefaultHeight { get; set; } = 0;
    public bool MaintainAspectRatio { get; set; } = true;
    /// <summary>
    /// Output mode: "newFile" or "replace"
    /// </summary>
    public string OutputMode { get; set; } = "newFile";
    public string OutputSuffix { get; set; } = "_resized";
    public int MinimumJpegQuality { get; set; } = 40;
}

/// <summary>
/// General application settings
/// </summary>
public class GeneralSettings
{
    public bool StartWithWindows { get; set; } = true;
    public bool ShowTrayIcon { get; set; } = true;
    public bool EnableNotifications { get; set; } = true;
    public bool EnableExplorerPdfPreview { get; set; } = true;
    /// <summary>
    /// Log level: "Info", "Debug", or "Off"
    /// </summary>
    public string LogLevel { get; set; } = "Info";
}

/// <summary>
/// Result of a compression operation
/// </summary>
public class CompressionResult
{
    public bool Success { get; set; }
    public string FilePath { get; set; } = string.Empty;
    public long OriginalSizeBytes { get; set; }
    public long NewSizeBytes { get; set; }
    public string? Message { get; set; }
    public bool WasSkipped { get; set; }
    public string? SkipReason { get; set; }

    /// <summary>
    /// Human-readable original size
    /// </summary>
    public string OriginalSizeFormatted => FormatFileSize(OriginalSizeBytes);

    /// <summary>
    /// Human-readable compressed size
    /// </summary>
    public string CompressedSizeFormatted => FormatFileSize(NewSizeBytes);

    /// <summary>
    /// Compression ratio as a percentage saved
    /// </summary>
    public double SavingsPercent => OriginalSizeBytes > 0
        ? Math.Round((1.0 - (double)NewSizeBytes / OriginalSizeBytes) * 100, 1)
        : 0;

    public static string FormatFileSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        return $"{bytes / (1024.0 * 1024.0):F1} MB";
    }
}
