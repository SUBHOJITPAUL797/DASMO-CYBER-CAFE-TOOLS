using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Serilog;

namespace SmartSaver.Services;

public class UpdateCheckResult
{
    public bool HasUpdate { get; set; }
    public bool IsMandatory { get; set; }
    public string CurrentVersion { get; set; } = string.Empty;
    public string LatestVersion { get; set; } = string.Empty;
    public string DownloadUrl { get; set; } = string.Empty;
    public string ReleaseNotes { get; set; } = string.Empty;
    public string CustomAdminMessage { get; set; } = string.Empty;
    public string FileName { get; set; } = "DASMO_CYBER_CAFE_TOOLS_Setup.msi";
    public long FileSizeBytes { get; set; }
}

public class UpdateDownloadProgress
{
    public long BytesDownloaded { get; set; }
    public long TotalBytes { get; set; }
    public int Percentage { get; set; }
    public double SpeedBytesPerSecond { get; set; }
    public string FormattedDownloaded => $"{BytesDownloaded / 1048576.0:F1} MB";
    public string FormattedTotal => TotalBytes > 0 ? $"{TotalBytes / 1048576.0:F1} MB" : "Unknown";
    public string FormattedSpeed => SpeedBytesPerSecond > 0 ? $"{SpeedBytesPerSecond / 1048576.0:F1} MB/s" : "Connecting...";
    public string StatusMessage { get; set; } = string.Empty;
}

public class AppUpdateService
{
    private static readonly Lazy<AppUpdateService> _instance = new(() => new AppUpdateService());
    public static AppUpdateService Instance => _instance.Value;

    private readonly HttpClient _http;
    public const string DefaultGithubRepo = "SUBHOJITPAUL797/DASMO-CYBER-CAFE-TOOLS";

    public static string CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.5.0";

    private AppUpdateService()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.Add("User-Agent", "DASMO-CyberCafe-Updater/1.5");
    }

    /// <summary>
    /// Checks for updates via Firebase Policy and GitHub Releases API.
    /// Priority:
    /// 1. Firebase system_config/licensing (Live instant override by Super Admin)
    /// 2. GitHub Releases API (/releases/latest)
    /// </summary>
    public async Task<UpdateCheckResult> CheckForUpdateAsync(string? repoOverride = null)
    {
        var result = new UpdateCheckResult
        {
            CurrentVersion = CurrentVersion,
            LatestVersion = CurrentVersion
        };

        try
        {
            // 1. Fetch Cloud Policy from Firestore
            var policy = await FirebaseCloudAuthService.Instance.GetVersionPolicyAsync().ConfigureAwait(false);
            result.CustomAdminMessage = policy.CustomUpdateMessage;

            string repo = !string.IsNullOrWhiteSpace(repoOverride) ? repoOverride.Trim() :
                          (!string.IsNullOrWhiteSpace(policy.GithubRepo) ? policy.GithubRepo.Trim() : DefaultGithubRepo);

            // 2. Check GitHub Release if repo is defined
            string gitHubLatest = string.Empty;
            string gitHubDownloadUrl = string.Empty;
            string gitHubNotes = string.Empty;
            long gitHubFileSize = 0;

            try
            {
                string apiUrl = $"https://api.github.com/repos/{repo}/releases/latest";
                using var req = new HttpRequestMessage(HttpMethod.Get, apiUrl);
                req.Headers.Add("Accept", "application/vnd.github.v3+json");
                using var resp = await _http.SendAsync(req).ConfigureAwait(false);

                if (resp.IsSuccessStatusCode)
                {
                    string json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;

                    if (root.TryGetProperty("tag_name", out var tagProp))
                    {
                        gitHubLatest = tagProp.GetString()?.Trim().TrimStart('v', 'V') ?? string.Empty;
                    }

                    if (root.TryGetProperty("body", out var bodyProp))
                    {
                        gitHubNotes = bodyProp.GetString() ?? string.Empty;
                    }

                    if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var asset in assets.EnumerateArray())
                        {
                            string name = asset.GetProperty("name").GetString() ?? "";
                            if (name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase) ||
                                name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                            {
                                gitHubDownloadUrl = asset.GetProperty("browser_download_url").GetString() ?? "";
                                if (asset.TryGetProperty("size", out var sizeProp))
                                {
                                    gitHubFileSize = sizeProp.GetInt64();
                                }
                                result.FileName = name;
                                break;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "GitHub Releases API unavailable; using Firestore cloud policy fallback.");
            }

            // Determine latest version and download URL: Pick the highest version between GitHub and Firestore
            string effectiveLatest = CurrentVersion;
            string effectiveDownloadUrl = string.Empty;
            string effectiveNotes = string.Empty;

            bool gitHubIsNewer = !string.IsNullOrWhiteSpace(gitHubLatest) &&
                (string.IsNullOrWhiteSpace(policy.LatestVersion) || FirebaseCloudAuthService.IsVersionOutdated(policy.LatestVersion, gitHubLatest));

            if (gitHubIsNewer)
            {
                effectiveLatest = gitHubLatest;
                effectiveDownloadUrl = !string.IsNullOrWhiteSpace(gitHubDownloadUrl) ? gitHubDownloadUrl : policy.UpdateDownloadUrl;
                effectiveNotes = !string.IsNullOrWhiteSpace(gitHubNotes) ? gitHubNotes : policy.UpdateChangelog;
            }
            else if (!string.IsNullOrWhiteSpace(policy.LatestVersion))
            {
                effectiveLatest = policy.LatestVersion;
                effectiveDownloadUrl = !string.IsNullOrWhiteSpace(policy.UpdateDownloadUrl) ? policy.UpdateDownloadUrl : gitHubDownloadUrl;
                effectiveNotes = !string.IsNullOrWhiteSpace(policy.UpdateChangelog) ? policy.UpdateChangelog : gitHubNotes;
            }
            else if (!string.IsNullOrWhiteSpace(gitHubLatest))
            {
                effectiveLatest = gitHubLatest;
                effectiveDownloadUrl = gitHubDownloadUrl;
                effectiveNotes = gitHubNotes;
            }

            if (string.IsNullOrWhiteSpace(effectiveLatest)) effectiveLatest = CurrentVersion;

            result.LatestVersion = effectiveLatest;
            result.DownloadUrl = effectiveDownloadUrl;
            result.ReleaseNotes = effectiveNotes;
            result.FileSizeBytes = gitHubFileSize;

            // Check if newer version is available
            bool hasNewer = FirebaseCloudAuthService.IsVersionOutdated(CurrentVersion, effectiveLatest);
            result.HasUpdate = hasNewer;

            // Check if current version is strictly mandatory to update
            bool isOutdated = FirebaseCloudAuthService.IsVersionOutdated(CurrentVersion, policy.MinRequiredVersion);
            bool isBlocked = FirebaseCloudAuthService.IsVersionBlocked(CurrentVersion, policy.BlockedVersions);
            result.IsMandatory = (policy.ForceUpdate && hasNewer) || isOutdated || isBlocked;

            return result;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error while checking for application updates");
            return result;
        }
    }

    /// <summary>
    /// Downloads the update installer with real-time progress reporting.
    /// </summary>
    public async Task<string?> DownloadUpdateAsync(
        string downloadUrl,
        Action<UpdateDownloadProgress>? onProgress = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(downloadUrl))
        {
            throw new ArgumentException("Download URL cannot be empty.", nameof(downloadUrl));
        }

        string tempFolder = Path.Combine(Path.GetTempPath(), "DASMO_CYBER_CAFE_UPDATES");
        Directory.CreateDirectory(tempFolder);

        string ext = Path.GetExtension(new Uri(downloadUrl).AbsolutePath);
        if (string.IsNullOrEmpty(ext)) ext = ".msi";
        string targetFile = Path.Combine(tempFolder, $"DASMO_Update_{DateTime.Now:yyyyMMddHHmmss}{ext}");

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
            client.DefaultRequestHeaders.Add("User-Agent", "DASMO-CyberCafe-Updater/1.5");

            using var response = await client.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            long totalBytes = response.Content.Headers.ContentLength ?? -1;
            using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var destination = new FileStream(targetFile, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

            var buffer = new byte[81920];
            long totalRead = 0;
            int bytesRead;
            var sw = Stopwatch.StartNew();
            long lastBytes = 0;
            var lastTime = sw.Elapsed;

            while ((bytesRead = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, bytesRead), ct).ConfigureAwait(false);
                totalRead += bytesRead;

                var elapsed = sw.Elapsed;
                double speed = 0;
                if ((elapsed - lastTime).TotalSeconds >= 0.25)
                {
                    speed = (totalRead - lastBytes) / (elapsed - lastTime).TotalSeconds;
                    lastBytes = totalRead;
                    lastTime = elapsed;

                    int pct = totalBytes > 0 ? (int)((totalRead * 100) / totalBytes) : 0;
                    onProgress?.Invoke(new UpdateDownloadProgress
                    {
                        BytesDownloaded = totalRead,
                        TotalBytes = totalBytes,
                        Percentage = pct,
                        SpeedBytesPerSecond = speed,
                        StatusMessage = $"Downloading update... {pct}%"
                    });
                }
            }

            await destination.FlushAsync(ct).ConfigureAwait(false);
            destination.Close();

            onProgress?.Invoke(new UpdateDownloadProgress
            {
                BytesDownloaded = totalRead,
                TotalBytes = totalRead,
                Percentage = 100,
                SpeedBytesPerSecond = 0,
                StatusMessage = "Download Complete! Ready to install."
            });

            return targetFile;
        }
        catch (OperationCanceledException)
        {
            try { if (File.Exists(targetFile)) File.Delete(targetFile); } catch { }
            Log.Information("Update download was canceled by user.");
            return null;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to download update from {Url}", downloadUrl);
            try { if (File.Exists(targetFile)) File.Delete(targetFile); } catch { }
            throw;
        }
    }

    /// <summary>
    /// Launches the downloaded installer (MSI or EXE) and cleanly exits the running application.
    /// </summary>
    public void LaunchInstallerAndExit(string installerPath)
    {
        if (!File.Exists(installerPath))
        {
            throw new FileNotFoundException("Installer file not found.", installerPath);
        }

        try
        {
            var psi = new ProcessStartInfo();
            if (installerPath.EndsWith(".msi", StringComparison.OrdinalIgnoreCase))
            {
                psi.FileName = "msiexec.exe";
                psi.Arguments = $"/i \"{installerPath}\"";
            }
            else
            {
                psi.FileName = installerPath;
            }

            psi.UseShellExecute = true;
            Process.Start(psi);

            Log.Information("Update installer launched. Exiting DASMO CYBER CAFE TOOLS...");

            // Cleanly shutdown WPF application
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                System.Windows.Application.Current.Shutdown(0);
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to launch update installer: {Path}", installerPath);
            System.Windows.MessageBox.Show($"Could not launch installer: {ex.Message}\n\nPlease run the file manually:\n{installerPath}",
                "Installer Launch Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
