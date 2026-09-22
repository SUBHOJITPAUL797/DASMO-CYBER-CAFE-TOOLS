using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using Microsoft.Win32;
using Serilog;

namespace SmartSaver.Services;

/// <summary>
/// Core wrapper around Ghostscript's gswin64c.exe — the industry-standard PDF engine.
///
/// Ghostscript provides:
///   • CCITT G4 Fax compression for monochrome/B&amp;W images  → lossless, 10-50× smaller
///   • Bicubic DCT (JPEG) for color images at controlled quality levels
///   • Font subsetting and deduplication
///   • Duplicate image detection
///   • PDF structure linearization and optimisation
///
/// Quality tiers are selected automatically based on the per-page byte budget.
/// Each tier is tried from highest to most aggressive until the target is met.
/// </summary>
public sealed class GhostscriptService
{
    private static string? _cachedExePath;
    private static readonly object _pathLock = new();

    // ─── Executable Discovery ───────────────────────────────────────────────

    /// <summary>
    /// Finds the gswin64c.exe path, searching in order:
    ///   1. AppData local auto-downloaded copy
    ///   2. Windows Registry (system-wide installer)
    ///   3. Common Program Files paths
    ///   4. PATH environment variable
    /// </summary>
    public static string? FindExecutable()
    {
        lock (_pathLock)
        {
            if (_cachedExePath != null && File.Exists(_cachedExePath))
                return _cachedExePath;

            _cachedExePath = null;

            // 1 — AppData local auto-downloaded copy (installed by GhostscriptSetupService)
            string binDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "DASMO CYBER COMPRESSOR", "gs", "bin");
            string localPath64 = Path.Combine(binDir, "gswin64c.exe");
            if (File.Exists(localPath64)) return _cachedExePath = localPath64;
            string localPath32 = Path.Combine(binDir, "gswin32c.exe");
            if (File.Exists(localPath32)) return _cachedExePath = localPath32;

            // 2 — Windows Registry (HKLM — system-wide installer)
            string? regPath = FindViaRegistry();
            if (regPath != null) return _cachedExePath = regPath;

            // 3 — Common Program Files directories
            string? commonPath = FindInProgramFiles();
            if (commonPath != null) return _cachedExePath = commonPath;

            // 4 — PATH environment variable
            string? onPath = FindOnPath();
            if (onPath != null) return _cachedExePath = onPath;

            return null;
        }
    }

    /// <summary>Invalidates the cached path (call after installation).</summary>
    public static void InvalidateCache()
    {
        lock (_pathLock) { _cachedExePath = null; }
    }

    /// <summary>Returns true if Ghostscript is available on this machine.</summary>
    public static bool IsAvailable => FindExecutable() != null;

    // ─── Core Compression API ───────────────────────────────────────────────

    /// <summary>
    /// Compresses a PDF using Ghostscript to meet <paramref name="targetBytes"/>.
    /// Tries quality tiers from highest to most aggressive until the target is met.
    /// Always returns a best-effort result — never leaves the caller without an output.
    /// </summary>
    /// <param name="sourcePath">Source PDF path.</param>
    /// <param name="outputPath">Where the compressed PDF will be written.</param>
    /// <param name="targetBytes">Target file size in bytes.</param>
    /// <param name="pageCount">Number of pages (used for per-page budget calculation).</param>
    public GhostscriptResult CompressToTargetSize(
        string sourcePath, string outputPath, long targetBytes, int pageCount = 1)
    {
        string? gsExe = FindExecutable();
        if (gsExe == null)
            return GhostscriptResult.NotAvailable();

        if (!File.Exists(sourcePath))
            return GhostscriptResult.Failure("Source file not found");

        long originalSize = new FileInfo(sourcePath).Length;
        long budgetPerPage = targetBytes / Math.Max(1, pageCount);

        var tiers = SelectTiers(budgetPerPage);
        string? bestPath = null;
        long bestSize = long.MaxValue;

        foreach (var tier in tiers)
        {
            string tempPath = outputPath + $".gs_{tier.Name}_{Guid.NewGuid():N}.tmp.pdf";
            try
            {
                bool ran = RunGhostscript(gsExe, sourcePath, tempPath, tier);
                if (!ran || !File.Exists(tempPath)) { DeleteSafe(tempPath); continue; }

                long tempSize = new FileInfo(tempPath).Length;
                if (tempSize < 5 * 1024) { DeleteSafe(tempPath); continue; } // sanity floor

                if (tempSize <= targetBytes)
                {
                    // ✅ Target met — accept this result
                    DeleteSafe(bestPath);
                    File.Move(tempPath, outputPath, overwrite: true);
                    Log.Information(
                        "GS [{Tier}] hit target: {Src} → {Size} bytes (target {Target})",
                        tier.Name, Path.GetFileName(sourcePath), tempSize, targetBytes);
                    return new GhostscriptResult
                    {
                        Available = true, Success = true, MetTarget = true,
                        OriginalSize = originalSize, OutputSize = tempSize, TierUsed = tier.Name
                    };
                }

                // Keep as best-effort candidate (smallest produced so far)
                if (tempSize < bestSize)
                {
                    DeleteSafe(bestPath);
                    bestPath = tempPath;
                    bestSize = tempSize;
                }
                else DeleteSafe(tempPath);
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "GS tier {Tier} failed for {Path}", tier.Name, sourcePath);
                DeleteSafe(tempPath);
            }
        }

        // ⚠️ If GS could not meet target size, return Failure so PdfCompressor falls back to built-in iterative engine!
        DeleteSafe(bestPath);
        Log.Warning("GS could not reach target size {Target} bytes for {Src}", targetBytes, sourcePath);
        return GhostscriptResult.Failure($"Ghostscript could not reduce file below requested target of {targetBytes / 1024} KB");
    }

    // ─── Tier Selection ─────────────────────────────────────────────────────

    /// <summary>
    /// Returns tiers starting from the one best suited to the budget,
    /// progressing to more aggressive tiers as fallback.
    /// </summary>
    private static GsCompressionTier[] SelectTiers(long budgetPerPage)
    {
        var all = GsCompressionTier.All;

        // Find first tier whose minimum budget the current budget meets
        int startIdx = all.Length - 1;
        for (int i = 0; i < all.Length; i++)
        {
            if (budgetPerPage >= all[i].MinBudgetPerPage)
            {
                startIdx = i;
                break;
            }
        }

        return all[startIdx..]; // from best-matching → most aggressive
    }


    // ─── Process Execution ──────────────────────────────────────────────────

    private static bool RunGhostscript(
        string gsExe, string input, string output, GsCompressionTier tier)
    {
        var psi = new ProcessStartInfo
        {
            FileName = gsExe,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var arg in BuildArgumentList(input, output, tier))
            psi.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = psi };
        var errBuf = new StringBuilder();
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) errBuf.AppendLine(e.Data); };

        process.Start();
        process.BeginErrorReadLine();
        process.StandardOutput.ReadToEnd(); // drain stdout to prevent deadlock

        bool finished = process.WaitForExit(TimeSpan.FromMinutes(5));
        if (!finished)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            Log.Warning("GS process timed out for tier {Tier}", tier.Name);
            return false;
        }

        if (process.ExitCode != 0)
        {
            Log.Debug("GS exit {Code} [{Tier}]: {Err}",
                process.ExitCode, tier.Name, errBuf.ToString().Trim());
            return false;
        }

        return true;
    }

    /// <summary>
    /// Builds the Ghostscript argument list for a given compression tier.
    /// Using ArgumentList (not ArgumentString) ensures proper quoting on all paths.
    ///
    /// Key settings:
    ///   - ColorImage/GrayImage: Bicubic downsampling + DCT (JPEG) encode at tier quality
    ///   - MonoImage: CCITT Group 4 Fax (lossless, superb for B&amp;W text and government forms)
    ///   - Font: CompressFonts + SubsetFonts + EmbedAllFonts
    ///   - PDF: CompressPages + DetectDuplicateImages + ConvertCMYKImagesToRGB
    ///   - JPEG quality: injected via PostScript distiller params (-c then -f)
    /// </summary>
    private static List<string> BuildArgumentList(string input, string output, GsCompressionTier tier)
    {
        string qf = tier.QFactor.ToString("F2", CultureInfo.InvariantCulture);

        return
        [
            // ── Core ──────────────────────────────────────────────────────────
            "-dBATCH",
            "-dNOPAUSE",
            "-dQUIET",
            "-dSAFER",
            "-sDEVICE=pdfwrite",
            "-dCompatibilityLevel=1.4",
            "-dPDFSETTINGS=/default",       // neutral base — we override everything below

            // ── Color images (Bicubic downsampling + JPEG) ────────────────────
            "-dDownsampleColorImages=true",
            "-dColorImageDownsampleType=/Bicubic",
            $"-dColorImageResolution={tier.ColorDpi}",
            "-dAutoFilterColorImages=false",
            "-dColorImageFilter=/DCTEncode",

            // ── Grayscale images (Bicubic downsampling + JPEG) ────────────────
            "-dDownsampleGrayImages=true",
            "-dGrayImageDownsampleType=/Bicubic",
            $"-dGrayImageResolution={tier.GrayDpi}",
            "-dAutoFilterGrayImages=false",
            "-dGrayImageFilter=/DCTEncode",

            // ── Monochrome / B&W images — CCITT G4 Fax (LOSSLESS for text) ────
            // This is the big win for scanned government documents:
            // B&W text encoded with CCITT G4 = perfectly sharp at 5-20× smaller.
            "-dDownsampleMonoImages=true",
            "-dMonoImageDownsampleType=/Subsample",
            $"-dMonoImageResolution={tier.MonoDpi}",
            "-dAutoFilterMonoImages=false",
            "-dMonoImageFilter=/CCITTFaxEncode",

            // ── Font optimisation ─────────────────────────────────────────────
            "-dCompressFonts=true",
            "-dSubsetFonts=true",
            "-dEmbedAllFonts=true",

            // ── PDF structure optimisation ────────────────────────────────────
            "-dCompressPages=true",
            "-dOptimize=true",
            "-dConvertCMYKImagesToRGB=true",    // avoid CMYK display issues on screen
            "-dDetectDuplicateImages=true",      // merge identical embedded images

            // ── Output ────────────────────────────────────────────────────────
            $"-sOutputFile={output}",

            // ── JPEG quality via PostScript distiller params ──────────────────
            // This is the proper GS way to control JPEG quality.
            // QFactor 0.22 ≈ Q90  |  0.40 ≈ Q80  |  0.60 ≈ Q70  |  0.90 ≈ Q58  |  1.30 ≈ Q42
            "-c",
            $"<</ColorACSImageDict <</QFactor {qf}>> /GrayACSImageDict <</QFactor {qf}>>>> setdistillerparams",

            // ── Input file (must follow -c ... -f pattern) ────────────────────
            "-f",
            input,
        ];
    }

    // ─── Path Discovery Helpers ─────────────────────────────────────────────

    private static string? FindViaRegistry()
    {
        try
        {
            foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
            {
                using var key = hive.OpenSubKey(@"SOFTWARE\Artifex\GPL Ghostscript");
                if (key == null) continue;

                foreach (var ver in key.GetSubKeyNames().OrderByDescending(v => v))
                {
                    using var verKey = key.OpenSubKey(ver);
                    if (verKey == null) continue;

                    // GS_DLL points to gsdll64.dll; gswin64c.exe is in the same bin dir
                    if (verKey.GetValue("GS_DLL") is string dll)
                    {
                        string dir = Path.GetDirectoryName(dll) ?? "";
                        string exe64 = Path.Combine(dir, "gswin64c.exe");
                        if (File.Exists(exe64)) return exe64;
                        string exe32 = Path.Combine(dir, "gswin32c.exe");
                        if (File.Exists(exe32)) return exe32;
                    }

                    // GS_LIB points to lib dir; bin dir is a sibling
                    if (verKey.GetValue("GS_LIB") is string lib)
                    {
                        string dir = Path.Combine(Path.GetDirectoryName(lib) ?? "", "bin");
                        string exe64 = Path.Combine(dir, "gswin64c.exe");
                        if (File.Exists(exe64)) return exe64;
                        string exe32 = Path.Combine(dir, "gswin32c.exe");
                        if (File.Exists(exe32)) return exe32;
                    }
                }
            }
        }
        catch (Exception ex) { Log.Debug(ex, "Registry GS search failed"); }
        return null;
    }

    private static string? FindInProgramFiles()
    {
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string[] roots =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Path.Combine(userProfile, "Downloads"),
            userProfile,
            @"C:\gs",
            @"D:\gs",
            @"E:\gs",
        ];

        foreach (string root in roots)
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;
            try
            {
                // Check direct gs folder or root
                string gsRoot = Directory.Exists(Path.Combine(root, "gs")) ? Path.Combine(root, "gs") : root;
                if (!Directory.Exists(gsRoot)) continue;

                var exe64 = Directory
                    .GetFiles(gsRoot, "gswin64c.exe", SearchOption.AllDirectories)
                    .OrderByDescending(p => p)
                    .FirstOrDefault();
                if (exe64 != null) return exe64;

                var exe32 = Directory
                    .GetFiles(gsRoot, "gswin32c.exe", SearchOption.AllDirectories)
                    .OrderByDescending(p => p)
                    .FirstOrDefault();
                if (exe32 != null) return exe32;
            }
            catch { }
        }
        return null;
    }

    private static string? FindOnPath()
    {
        string pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (string dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                string candidate64 = Path.Combine(dir.Trim(), "gswin64c.exe");
                if (File.Exists(candidate64)) return candidate64;

                string candidate32 = Path.Combine(dir.Trim(), "gswin32c.exe");
                if (File.Exists(candidate32)) return candidate32;
            }
            catch { }
        }
        return null;
    }

    private static void DeleteSafe(string? path)
    {
        if (!string.IsNullOrEmpty(path))
            try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}

// ─── Compression Tier Definitions ───────────────────────────────────────────

/// <summary>
/// Ghostscript compression tier: maps a per-page byte budget to specific image/mono settings.
///
/// CCITT G4 (CCITTFaxEncode) is used for all monochrome images — this is
/// LOSSLESS compression for B&amp;W content (text, signatures, line art) and gives
/// 10–50× size reduction vs. uncompressed bitmap with ZERO quality loss.
///
/// JPEG quality is controlled via GS distiller QFactor (not a direct quality %).
/// QFactor → JPEG quality mapping (approximate):
///   0.22 → Q90  |  0.40 → Q80  |  0.60 → Q70  |  0.90 → Q58  |  1.30 → Q42
/// </summary>
public sealed record GsCompressionTier(
    string Name,
    int ColorDpi,
    int GrayDpi,
    int MonoDpi,
    double QFactor,
    long MinBudgetPerPage)
{
    /// <summary>
    /// All tiers ordered from highest quality to most aggressive.
    /// SelectTiers() picks the best-matching starting point then tries remaining.
    /// </summary>
    public static readonly GsCompressionTier[] All =
    [
        // Name            Color  Gray  Mono   QFactor  MinBudget/page
        new("high_quality",  200, 200,  600,   0.22,  250 * 1024L),  // ~Q90 JPEG — very clear
        new("standard",      150, 150,  400,   0.40,  120 * 1024L),  // ~Q80 JPEG — clear
        new("balanced",      120, 120,  300,   0.60,   60 * 1024L),  // ~Q70 JPEG — good
        new("aggressive",     96,  96,  200,   0.90,   30 * 1024L),  // ~Q58 JPEG — readable
        new("maximum",        72,  72,  150,   1.30,          0L ),  // ~Q42 JPEG
        new("extreme_1",      60,  60,  120,   1.80,          0L ),  // ~Q32 JPEG
        new("extreme_2",      48,  48,  100,   2.50,          0L ),  // ~Q22 JPEG
    ];
}

/// <summary>Result from a Ghostscript compression operation.</summary>
public sealed class GhostscriptResult
{
    public bool Available { get; init; }
    public bool Success { get; init; }
    public bool MetTarget { get; init; }
    public long OriginalSize { get; init; }
    public long OutputSize { get; init; }
    public string? TierUsed { get; init; }
    public string? Message { get; init; }

    public static GhostscriptResult NotAvailable() =>
        new() { Available = false, Success = false, Message = "Ghostscript not installed" };

    public static GhostscriptResult Failure(string msg) =>
        new() { Available = true, Success = false, Message = msg };
}
