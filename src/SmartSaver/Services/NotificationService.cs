using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Toolkit.Uwp.Notifications;
using Microsoft.Win32;
using Serilog;

namespace SmartSaver.Services;

/// <summary>
/// Sends Windows toast notifications with official DASMO CYBER CAFE TOOLS branding,
/// custom AUMID registration, and embedded app logo icons.
/// Uses Microsoft.Toolkit.Uwp.Notifications and respects the
/// <see cref="Models.GeneralSettings.EnableNotifications"/> setting.
/// </summary>
public sealed class NotificationService
{
    public const string AppName = "DASMO CYBER CAFE TOOLS";
    public const string AppUserModelId = "DASMO.CYBER.CAFE.TOOLS";

    [DllImport("shell32.dll", SetLastError = true)]
    private static extern void SetCurrentProcessExplicitAppUserModelID([MarshalAs(UnmanagedType.LPWStr)] string AppID);

    private static bool _brandingInitialized = false;
    private static readonly object _brandingLock = new();

    /// <summary>
    /// When true, toast notification popups are suppressed (useful during automated test suites).
    /// </summary>
    public static bool SuppressToastsForTesting { get; set; } = false;

    /// <summary>
    /// Ensures that the current Windows process and registry are stamped with
    /// the official "DASMO CYBER CAFE TOOLS" AUMID, display name, and high-resolution logo.
    /// This prevents Windows from showing generic names like "SmartSaver.Tests" or blank icons.
    /// </summary>
    public static void EnsureNotificationBranding(bool force = false)
    {
        if (_brandingInitialized && !force) return;

        lock (_brandingLock)
        {
            if (_brandingInitialized && !force) return;

            try
            {
                // 1. Explicitly stamp the current process with DASMO CYBER CAFE TOOLS AUMID
                try
                {
                    SetCurrentProcessExplicitAppUserModelID(AppUserModelId);
                }
                catch (Exception ex)
                {
                    Log.Debug(ex, "Failed to set process explicit AppUserModelID");
                }

                // 2. Ensure high-resolution logo PNG exists in AppData
                string logoPath = EnsureAppLogoPng();

                // 3. Register or update the custom AUMID in HKCU\Software\Classes\AppUserModelId
                try
                {
                    using var baseKey = Registry.CurrentUser.CreateSubKey(@"Software\Classes\AppUserModelId\" + AppUserModelId);
                    if (baseKey != null)
                    {
                        baseKey.SetValue("DisplayName", AppName, RegistryValueKind.String);
                        if (!string.IsNullOrEmpty(logoPath) && File.Exists(logoPath))
                        {
                            baseKey.SetValue("IconUri", logoPath, RegistryValueKind.String);
                        }
                        baseKey.SetValue("IconBackgroundColor", "00000000", RegistryValueKind.String);
                    }
                }
                catch (Exception ex)
                {
                    Log.Debug(ex, "Failed to register custom AUMID registry key");
                }

                // 4. Sanitize any legacy SmartSaver or test runner entries so Windows never displays "SmartSaver.Tests"
                try
                {
                    using var parentKey = Registry.CurrentUser.OpenSubKey(@"Software\Classes\AppUserModelId", writable: true);
                    if (parentKey != null)
                    {
                        foreach (string subKeyName in parentKey.GetSubKeyNames())
                        {
                            if (subKeyName.IndexOf("SmartSaver", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                subKeyName.IndexOf("SmartSaver.Tests", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                subKeyName.IndexOf("DASMO", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                try
                                {
                                    using var item = parentKey.OpenSubKey(subKeyName, writable: true);
                                    if (item != null)
                                    {
                                        item.SetValue("DisplayName", AppName, RegistryValueKind.String);
                                        if (!string.IsNullOrEmpty(logoPath) && File.Exists(logoPath))
                                        {
                                            item.SetValue("IconUri", logoPath, RegistryValueKind.String);
                                        }
                                    }
                                }
                                catch { }
                            }
                        }
                    }
                }
                catch { }

                // 5. Update cached Icon.png files in %LOCALAPPDATA%\ToastNotificationManagerCompat\Apps\
                try
                {
                    string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                    string toastAppsDir = Path.Combine(localAppData, "ToastNotificationManagerCompat", "Apps");
                    if (Directory.Exists(toastAppsDir) && !string.IsNullOrEmpty(logoPath) && File.Exists(logoPath))
                    {
                        foreach (string appDir in Directory.GetDirectories(toastAppsDir))
                        {
                            try
                            {
                                string iconFile = Path.Combine(appDir, "Icon.png");
                                File.Copy(logoPath, iconFile, overwrite: true);
                            }
                            catch { }
                        }
                    }
                }
                catch { }

                _brandingInitialized = true;
                Log.Debug("Toast notification branding initialized successfully for {AppName}", AppName);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "EnsureNotificationBranding encountered an error");
            }
        }
    }

    /// <summary>
    /// Ensures a high-resolution 256x256 PNG logo exists at %APPDATA%\DASMO CYBER CAFE TOOLS\app_logo.png.
    /// </summary>
    public static string EnsureAppLogoPng()
    {
        try
        {
            string appDataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "DASMO CYBER CAFE TOOLS");
            Directory.CreateDirectory(appDataDir);

            string logoPath = Path.Combine(appDataDir, "app_logo.png");
            if (File.Exists(logoPath) && new FileInfo(logoPath).Length > 0)
            {
                return logoPath;
            }

            // Check if app_logo.png is in application directory or Resources
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string candidatePng = Path.Combine(baseDir, "Resources", "app_logo.png");
            if (!File.Exists(candidatePng)) candidatePng = Path.Combine(baseDir, "app_logo.png");

            if (File.Exists(candidatePng))
            {
                File.Copy(candidatePng, logoPath, overwrite: true);
                return logoPath;
            }

            // Fallback: extract from tray_icon.ico
            string icoPath = Path.Combine(baseDir, "Resources", "tray_icon.ico");
            if (!File.Exists(icoPath)) icoPath = Path.Combine(baseDir, "tray_icon.ico");

            if (File.Exists(icoPath))
            {
                using var icon = new System.Drawing.Icon(icoPath, 256, 256);
                using var bmp = icon.ToBitmap();
                bmp.Save(logoPath, System.Drawing.Imaging.ImageFormat.Png);
                return logoPath;
            }

            // Fallback: extract associated icon from current executable
            string? exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe) && File.Exists(exe))
            {
                using var icon = System.Drawing.Icon.ExtractAssociatedIcon(exe);
                if (icon != null)
                {
                    using var bmp = icon.ToBitmap();
                    bmp.Save(logoPath, System.Drawing.Imaging.ImageFormat.Png);
                    return logoPath;
                }
            }

            return logoPath;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to extract/ensure app_logo.png");
            return string.Empty;
        }
    }

    /// <summary>
    /// Gets the file Uri for the cached application logo PNG.
    /// </summary>
    public static Uri? GetAppLogoUri()
    {
        try
        {
            string path = EnsureAppLogoPng();
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                return new Uri(path);
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Attaches the official application logo and branding to the ToastContentBuilder.
    /// </summary>
    private static void AttachBranding(ToastContentBuilder builder)
    {
        var logoUri = GetAppLogoUri();
        if (logoUri != null)
        {
            try
            {
                builder.AddAppLogoOverride(logoUri, ToastGenericAppLogoCrop.Circle);
            }
            catch
            {
                try
                {
                    builder.AddAppLogoOverride(logoUri, ToastGenericAppLogoCrop.Default);
                }
                catch { }
            }
        }

        // Force stamp branding in registry and cached icon files after ToastContentBuilder has initialized
        EnsureNotificationBranding(force: true);
    }

    /// <summary>
    /// Notifies the user that a file was successfully compressed.
    /// </summary>
    /// <param name="fileName">Name of the compressed file.</param>
    /// <param name="originalSize">Original file size in bytes.</param>
    /// <param name="newSize">New file size in bytes.</param>
    public void NotifySuccess(string fileName, long originalSize, long newSize)
    {
        if (!IsEnabled() || SuppressToastsForTesting) return;

        try
        {
            long saved = originalSize - newSize;
            double percent = originalSize > 0
                ? Math.Round((double)saved / originalSize * 100, 1)
                : 0;

            var builder = new ToastContentBuilder()
                .AddText($"✅ {AppName} — File Compressed")
                .AddText($"{fileName}")
                .AddText($"{FormatSize(originalSize)} → {FormatSize(newSize)} (saved {percent}%)")
                .AddAttributionText(AppName);

            AttachBranding(builder);
            builder.Show();

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
        if (!IsEnabled() || SuppressToastsForTesting) return;

        try
        {
            var builder = new ToastContentBuilder()
                .AddText($"⏭️ {AppName} — File Skipped")
                .AddText($"{fileName}")
                .AddText(reason)
                .AddAttributionText(AppName);

            AttachBranding(builder);
            builder.Show();

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
        if (!IsEnabled() || SuppressToastsForTesting) return;

        try
        {
            var builder = new ToastContentBuilder()
                .AddText($"❌ {AppName} — Compression Failed")
                .AddText($"{fileName}")
                .AddText(reason)
                .AddAttributionText(AppName);

            AttachBranding(builder);
            builder.Show();

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
    /// <param name="newVersion">The new version number (e.g. 1.5.8).</param>
    /// <param name="releaseNotes">Optional changelog notes.</param>
    public static void NotifyUpdateAvailable(string newVersion, string? releaseNotes = null)
    {
        if (SuppressToastsForTesting) return;

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

            AttachBranding(builder);
            builder.Show();
            Log.Information("Sent update available notification for v{Version}", newVersion);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to send update toast notification for v{Version}", newVersion);
        }
    }

    /// <summary>
    /// Sends a Windows toast notification when a print job is automatically detected from the spooler.
    /// </summary>
    public static void NotifyPrintJobCaptured(string docName, int pages, bool isDuplex, bool isColor, double cost)
    {
        if (SuppressToastsForTesting) return;

        try
        {
            string shortDoc = docName.Length > 25 ? docName.Substring(0, 22) + "..." : docName;
            string mode = isDuplex ? "📑 Duplex" : "📄 Single";
            string color = isColor ? "🌈 Color" : "⚫ B&W";

            var builder = new ToastContentBuilder()
                .AddText($"🖨️ Print Detected: {shortDoc}")
                .AddText($"{pages} Page{(pages > 1 ? "s" : "")} ({mode}, {color}) → Total: ₹{cost:F2}")
                .AddAttributionText("DASMO Cyber Cafe Print Counter");

            AttachBranding(builder);
            builder.Show();

            Log.Information("Sent print job captured notification for {Doc}", docName);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to send print toast notification for {Doc}", docName);
        }
    }

    /// <summary>
    /// Clears all DASMO CYBER CAFE TOOLS notifications from the Windows notification center.
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
