using System.IO;
using Microsoft.Toolkit.Uwp.Notifications;
using Serilog;

namespace SmartSaver.Services;

/// <summary>
/// Sends Windows toast notifications to inform the user about compression outcomes.
/// Uses Microsoft.Toolkit.Uwp.Notifications and respects the
/// <see cref="Models.GeneralSettings.EnableNotifications"/> setting.
/// </summary>
public sealed class NotificationService
{
    private const string AppName = "DASMO CYBER CAFE TOOLS";

    /// <summary>
    /// Notifies the user that a file was successfully compressed.
    /// </summary>
    /// <param name="fileName">Name of the compressed file.</param>
    /// <param name="originalSize">Original file size in bytes.</param>
    /// <param name="newSize">New file size in bytes.</param>
    public void NotifySuccess(string fileName, long originalSize, long newSize)
    {
        if (!IsEnabled()) return;

        try
        {
            long saved = originalSize - newSize;
            double percent = originalSize > 0
                ? Math.Round((double)saved / originalSize * 100, 1)
                : 0;

            new ToastContentBuilder()
                .AddText($"✅ {AppName} — File Compressed")
                .AddText($"{fileName}")
                .AddText($"{FormatSize(originalSize)} → {FormatSize(newSize)} (saved {percent}%)")
                .Show();

            Log.Debug("Success notification sent for {File}", fileName);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to send success notification for {File}", fileName);
        }
    }

    /// <summary>
    /// Notifies the user that a file was skipped during compression.
    /// </summary>
    /// <param name="fileName">Name of the skipped file.</param>
    /// <param name="reason">Human-readable reason the file was skipped.</param>
    public void NotifySkipped(string fileName, string reason)
    {
        if (!IsEnabled()) return;

        try
        {
            new ToastContentBuilder()
                .AddText($"⏭️ {AppName} — File Skipped")
                .AddText($"{fileName}")
                .AddText(reason)
                .Show();

            Log.Debug("Skipped notification sent for {File}: {Reason}", fileName, reason);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to send skipped notification for {File}", fileName);
        }
    }

    /// <summary>
    /// Notifies the user that compression failed for a file.
    /// </summary>
    /// <param name="fileName">Name of the file that failed to compress.</param>
    /// <param name="reason">Human-readable reason for the failure.</param>
    public void NotifyFailure(string fileName, string reason)
    {
        if (!IsEnabled()) return;

        try
        {
            new ToastContentBuilder()
                .AddText($"❌ {AppName} — Compression Failed")
                .AddText($"{fileName}")
                .AddText(reason)
                .Show();

            Log.Debug("Failure notification sent for {File}: {Reason}", fileName, reason);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to send failure notification for {File}", fileName);
        }
    }

    /// <summary>
    /// Sends a Windows toast notification informing the user that a new software update is available.
    /// </summary>
    /// <param name="newVersion">The new version number (e.g. 1.5.7).</param>
    /// <param name="releaseNotes">Optional changelog notes.</param>
    public static void NotifyUpdateAvailable(string newVersion, string? releaseNotes = null)
    {
        try
        {
            var builder = new ToastContentBuilder()
                .AddText($"🚀 {AppName} — Update Available!")
                .AddText($"Version v{newVersion} is now ready to download.")
                .AddAttributionText("DASMO Cloud Update System");

            if (!string.IsNullOrWhiteSpace(releaseNotes))
            {
                string shortNotes = releaseNotes.Length > 120 ? releaseNotes.Substring(0, 117) + "..." : releaseNotes;
                builder.AddText(shortNotes);
            }

            builder.Show();
            Log.Information("Sent update available notification for v{Version}", newVersion);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to send update toast notification for v{Version}", newVersion);
        }
    }

    /// <summary>
    /// Clears all SmartSaver notifications from the Windows notification center.
    /// </summary>
    public static void ClearAll()
    {
        try
        {
            ToastNotificationManagerCompat.History.Clear();
            Log.Debug("All notifications cleared");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to clear notifications");
        }
    }

    private static bool IsEnabled()
    {
        try
        {
            return SettingsManager.Instance.Current.General.EnableNotifications;
        }
        catch
        {
            return true; // Default to enabled if settings aren't available yet
        }
    }

    /// <summary>
    /// Formats a byte count into a human-readable string (e.g., "1.5 MB").
    /// </summary>
    /// <param name="bytes">Size in bytes.</param>
    /// <returns>Formatted size string.</returns>
    public static string FormatSize(long bytes)
    {
        return bytes switch
        {
            < 1024 => $"{bytes} B",
            < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
            < 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
            _ => $"{bytes / (1024.0 * 1024 * 1024):F2} GB"
        };
    }
}
