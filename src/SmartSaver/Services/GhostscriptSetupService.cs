using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using Serilog;

namespace SmartSaver.Services;

/// <summary>
/// Handles automatic discovery, download, and silent installation of Ghostscript.
///
/// On app startup, call <see cref="EnsureAvailableAsync"/> once.
/// It checks if Ghostscript is present; if not, it downloads and installs it
/// transparently in the background — no user interaction required (except a
/// one-time UAC elevation prompt for the silent installer).
///
/// Once Ghostscript is installed, <see cref="GhostscriptService"/> automatically
/// finds and uses it for all future PDF compressions.
/// </summary>
public static class GhostscriptSetupService
{
    // GitHub Releases API — fetches the latest GS version automatically
    private const string GitHubApiUrl =
        "https://api.github.com/repos/ArtifexSoftware/ghostpdl-downloads/releases/latest";

    // Fallback direct URL if GitHub API is unavailable
    private const string FallbackUrl =
        "https://github.com/ArtifexSoftware/ghostpdl-downloads/releases/download/gs10041/gs10041w64.exe";

    private static volatile bool _isInstalling;

    /// <summary>True while a background installation is running.</summary>
    public static bool IsInstalling => _isInstalling;

    /// <summary>
    /// Ensures Ghostscript is available. If already installed, returns immediately.
    /// If not installed, downloads and installs it silently in the background.
    /// </summary>
    /// <param name="onProgress">Called with progress messages during download/install.</param>
    /// <param name="onComplete">Called when complete — true = success, false = failed.</param>
    public static async Task EnsureAvailableAsync(
        Action<string>? onProgress = null,
        Action<bool>? onComplete = null)
    {
        if (GhostscriptService.IsAvailable)
        {
            Log.Information("Ghostscript ready: {Path}", GhostscriptService.FindExecutable());
            onComplete?.Invoke(true);
            return;
        }

        if (_isInstalling)
        {
            Log.Debug("GS installation already in progress");
            return;
        }

        Log.Information("Ghostscript not found — starting auto-download in background");
        onProgress?.Invoke("📦 Downloading Ghostscript for professional PDF compression...");

        // Fire-and-forget — do NOT await here so app startup is never blocked
        _ = Task.Run(async () =>
        {
            _isInstalling = true;
            try
            {
                bool ok = await DownloadAndInstallAsync(onProgress);
                if (ok)
                {
                    GhostscriptService.InvalidateCache();
                    string? path = GhostscriptService.FindExecutable();
                    if (path != null)
                    {
                        Log.Information("Ghostscript installed: {Path}", path);
                        onProgress?.Invoke("✅ Ghostscript installed — PDF compression is now professional-grade!");
                        onComplete?.Invoke(true);
                    }
                    else
                    {
                        Log.Warning("GS installer ran but executable not found after install");
                        onComplete?.Invoke(false);
                    }
                }
                else
                {
                    Log.Information("GS background download deferred — built-in engine active");
                    onComplete?.Invoke(false);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "GhostscriptSetupService error");
                onComplete?.Invoke(false);
            }
            finally
            {
                _isInstalling = false;
            }
        });

        await Task.CompletedTask; // keeps method signature async without blocking
    }

    // ─── Download + Install ─────────────────────────────────────────────────

    private static async Task<bool> DownloadAndInstallAsync(Action<string>? onProgress)
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "DASMO_GS_" + Guid.NewGuid().ToString("N")[..8]);
        string installerPath = Path.Combine(tempDir, "gs_setup.exe");
        Directory.CreateDirectory(tempDir);

        try
        {
            // Get download URL (latest from GitHub or fallback)
            string downloadUrl = await GetLatestDownloadUrlAsync();
            Log.Information("GS download URL: {Url}", downloadUrl);
            onProgress?.Invoke("📥 Downloading Ghostscript (~50 MB) — this happens only once...");

            // Download the installer
            using var http = CreateHttpClient();
            using var response = await http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            long? totalBytes = response.Content.Headers.ContentLength;
            using var contentStream = await response.Content.ReadAsStreamAsync();
            await using var fileStream = new FileStream(installerPath, FileMode.Create, FileAccess.Write, FileShare.None);

            var buffer = new byte[65536];
            long downloaded = 0;
            int lastPct = -1;
            int read;

            while ((read = await contentStream.ReadAsync(buffer)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, read));
                downloaded += read;

                if (totalBytes.HasValue)
                {
                    int pct = (int)(downloaded * 100 / totalBytes.Value);
                    if (pct >= lastPct + 5)
                    {
                        lastPct = pct;
                        double mb = downloaded / 1048576.0;
                        onProgress?.Invoke($"📥 Downloading Ghostscript... {pct}% ({mb:F1} MB)");
                    }
                }
            }

            await fileStream.FlushAsync();
            fileStream.Close();
            Log.Information("GS installer downloaded: {Size} bytes", downloaded);

            onProgress?.Invoke("⚙️ Installing Ghostscript... (accept the UAC prompt if it appears)");
            return RunInstallerSilently(installerPath);
        }
        catch (HttpRequestException ex)
        {
            Log.Warning(ex, "GS download failed (no internet or server error)");
            onProgress?.Invoke("⚠️ No internet connection — skipping Ghostscript download.");
            return false;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "GS download/install error");
            return false;
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    private static async Task<string> GetLatestDownloadUrlAsync()
    {
        try
        {
            using var http = CreateHttpClient();
            http.Timeout = TimeSpan.FromSeconds(15);

            string json = await http.GetStringAsync(GitHubApiUrl);
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("assets", out var assets))
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    string name = asset.GetProperty("name").GetString() ?? string.Empty;
                    // Match pattern: gs####w64.exe  (64-bit Windows installer)
                    if (name.EndsWith("w64.exe", StringComparison.OrdinalIgnoreCase) &&
                        !name.Contains("arm", StringComparison.OrdinalIgnoreCase))
                    {
                        string? url = asset.GetProperty("browser_download_url").GetString();
                        if (url != null)
                        {
                            Log.Debug("Found GS release asset: {Name} → {Url}", name, url);
                            return url;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "GitHub API unavailable — using fallback GS URL");
        }

        return FallbackUrl;
    }

    private static bool RunInstallerSilently(string installerPath)
    {
        try
        {
            // Ghostscript NSIS installer supports /S /D=Path for silent mode to custom folder.
            // Installing to AppData requires ZERO UAC elevation / admin rights!
            string targetDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "DASMO CYBER COMPRESSOR", "gs");
            Directory.CreateDirectory(targetDir);

            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = installerPath,
                    Arguments = $"/S /D={targetDir}",
                    UseShellExecute = true,
                    Verb = "runas"
                }
            };

            process.Start();
            bool finished = process.WaitForExit(60000);

            if (!finished)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                Log.Warning("GS installer timed out");
                return false;
            }

            int code = process.ExitCode;
            Log.Information("GS installer exit code: {Code}", code);
            return code == 0 || code == 1; // 0=success, 1=NSIS "already installed"
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "GS installation error (fallback active)");
            return false;
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.Add("User-Agent", "DASMO-CyberCafe-SmartSaver/1.0");
        client.Timeout = TimeSpan.FromMinutes(15);
        return client;
    }
}
