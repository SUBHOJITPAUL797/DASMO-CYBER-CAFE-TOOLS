using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using SmartSaver.Models;

namespace SmartSaver.Services;

/// <summary>
/// Hardware Printer Counter, Ink Level & Physical Walk-up Xerox Audit Service.
/// Supports Brother DCP-T530DW and all network/USB printers.
/// Reconciles physical hardware meter readings against PC spooler logs to detect
/// and account for off-computer walk-up photocopy/Xerox copies.
/// </summary>
public sealed class BrotherPrinterAuditService
{
    private static Lazy<BrotherPrinterAuditService> _instance = new(() => new BrotherPrinterAuditService());
    public static BrotherPrinterAuditService Instance => _instance.Value;

    public static string? CustomDataDirectory { get; set; }

    public static void ResetForTesting(string? testDir = null)
    {
        CustomDataDirectory = testDir;
        _instance = new Lazy<BrotherPrinterAuditService>(() => new BrotherPrinterAuditService(testDir));
    }

    private readonly object _lock = new();
    private readonly HttpClient _http;
    private readonly string _storageFilePath;

    public PrinterLiveStatus LiveStatus { get; private set; } = new();
    public List<DailyPrinterMeterRecord> MeterHistory { get; private set; } = new();

    public event Action? OnAuditUpdated;
    public event Action<string>? OnPrinterIpDiscovered;

    public string CurrentIp
    {
        get
        {
            try
            {
                var configured = PrintTrackerService.Instance?.Settings?.BrotherPrinterIp;
                if (!string.IsNullOrWhiteSpace(configured)) return configured.Trim();
            }
            catch { }
            return "192.168.1.7";
        }
    }

    public string CurrentMac
    {
        get
        {
            try
            {
                var configured = PrintTrackerService.Instance?.Settings?.BrotherPrinterMac;
                if (!string.IsNullOrWhiteSpace(configured)) return configured.Trim();
            }
            catch { }
            return "4C:23:38:3F:8E:ED";
        }
    }

    private BrotherPrinterAuditService(string? customDir = null)
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        _http.DefaultRequestHeaders.Add("User-Agent", "DASMO-CyberCafe-PrinterAudit/1.5");

        string? targetDir = customDir ?? CustomDataDirectory;
        if (targetDir == null)
        {
            try
            {
                var procName = System.Diagnostics.Process.GetCurrentProcess().ProcessName;
                if (procName.Contains("Test", StringComparison.OrdinalIgnoreCase))
                {
                    targetDir = Path.Combine(Path.GetTempPath(), "DasmoTestSandbox_" + procName);
                }
            }
            catch { }
        }

        string dataDir = targetDir ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DASMO CYBER CAFE TOOLS");
        Directory.CreateDirectory(dataDir);
        _storageFilePath = Path.Combine(dataDir, "printer_daily_meters.json");

        LoadData();
    }

    private void LoadData()
    {
        lock (_lock)
        {
            if (File.Exists(_storageFilePath))
            {
                try
                {
                    string json = File.ReadAllText(_storageFilePath);
                    var list = JsonSerializer.Deserialize<List<DailyPrinterMeterRecord>>(json);
                    if (list != null)
                    {
                        MeterHistory = list;
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Failed to load printer meter records");
                }
            }

            try
            {
                var s = PrintTrackerService.Instance?.Settings;
                if (s != null && s.PreferVisualInkLevels)
                {
                    LiveStatus.InkBlackPercent = s.CalibratedInkBlack;
                    LiveStatus.InkCyanPercent = s.CalibratedInkCyan;
                    LiveStatus.InkMagentaPercent = s.CalibratedInkMagenta;
                    LiveStatus.InkYellowPercent = s.CalibratedInkYellow;
                    LiveStatus.IsCalibratedByVisualCheck = true;
                }
            }
            catch { }
        }
    }

    private void SaveData()
    {
        lock (_lock)
        {
            try
            {
                string json = JsonSerializer.Serialize(MeterHistory, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_storageFilePath, json);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to save printer meter records");
            }
        }
    }

    public DailyPrinterMeterRecord GetTodayMeter()
    {
        string todayStr = DateTime.Now.ToString("yyyy-MM-dd");
        lock (_lock)
        {
            var existing = MeterHistory.FirstOrDefault(m => m.Date == todayStr);
            if (existing != null) return existing;

            // Carry forward yesterday's closing meter as today's opening meter if available
            int previousClosing = MeterHistory
                .OrderByDescending(m => m.Date)
                .Select(m => m.ClosingMeter > 0 ? m.ClosingMeter : m.OpeningMeter)
                .FirstOrDefault();

            var newDay = new DailyPrinterMeterRecord
            {
                Date = todayStr,
                OpeningMeter = previousClosing,
                ClosingMeter = previousClosing,
                OpeningRecordedAt = DateTimeOffset.Now
            };
            MeterHistory.Insert(0, newDay);
            SaveData();
            return newDay;
        }
    }

    public void SaveOpeningMeter(int meter)
    {
        lock (_lock)
        {
            var today = GetTodayMeter();
            today.OpeningMeter = Math.Max(0, meter);
            today.OpeningRecordedAt = DateTimeOffset.Now;
            if (today.ClosingMeter < today.OpeningMeter)
            {
                today.ClosingMeter = today.OpeningMeter;
            }
            SaveData();
        }
        OnAuditUpdated?.Invoke();
    }

    public void SaveClosingMeter(int meter)
    {
        lock (_lock)
        {
            var today = GetTodayMeter();
            today.ClosingMeter = Math.Max(today.OpeningMeter, meter);
            today.IsClosingSaved = true;
            today.ClosingRecordedAt = DateTimeOffset.Now;
            SaveData();
        }
        OnAuditUpdated?.Invoke();
    }

    /// <summary>
    /// Computes the exact reconciliation between hardware printer meter and software records.
    /// Formula:
    /// Hardware Sheets = Closing - Opening
    /// Actual Xerox = Hardware Sheets - PC Spooler Prints
    /// Unrecorded Xerox = Actual Xerox - Logged Xerox in App
    /// </summary>
    public (int totalHardware, int totalPcSpooler, int actualXerox, int loggedXerox, int unrecordedXerox, double unrecordedAmt)
        CalculateReconciliation(int currentOrClosingMeter, IEnumerable<PrintJobRecord>? jobsOverride = null)
    {
        var todayRec = GetTodayMeter();
        int opening = todayRec.OpeningMeter;
        int currentMeter = Math.Max(opening, currentOrClosingMeter);

        int totalHardware = Math.Max(0, currentMeter - opening);

        DateTime today = DateTime.Today;
        var sourceJobs = jobsOverride ?? PrintTrackerService.Instance.AllJobHistory;

        // 1. Spooler PC Prints (Total impressions processed by PC)
        int totalPcSpooler = sourceJobs
            .Where(j => !j.IsManualEntry && j.Timestamp.LocalDateTime.Date == today)
            .Sum(j => j.TotalImpressions);

        // 2. Physical Xerox derived from machine
        int actualXerox = Math.Max(0, totalHardware - totalPcSpooler);

        // 3. Logged Xerox in Billing Cart & Jobs History
        int loggedXerox = sourceJobs
            .Where(j => j.IsManualEntry &&
                       (j.DocumentName.Contains("Photocopy", StringComparison.OrdinalIgnoreCase) ||
                        j.DocumentName.Contains("Xerox", StringComparison.OrdinalIgnoreCase) ||
                        j.ItemCategory.Contains("Photocopy", StringComparison.OrdinalIgnoreCase)) &&
                       j.Timestamp.LocalDateTime.Date == today)
            .Sum(j => j.TotalImpressions);

        // 4. Missing / Unrecorded Xerox
        int unrecordedXerox = Math.Max(0, actualXerox - loggedXerox);

        double rate = PrintTrackerService.Instance.Settings.PhotocopyBwRate;
        double unrecordedAmt = Math.Round(unrecordedXerox * rate, 2);

        return (totalHardware, totalPcSpooler, actualXerox, loggedXerox, unrecordedXerox, unrecordedAmt);
    }

    /// <summary>
    /// Auto-adds detected missing physical Xerox copies to the Daily Cash Register & Excel.
    /// </summary>
    public void AutoLogUnrecordedXerox(int count, double rate, string medium)
    {
        if (count <= 0) return;

        double totalAmount = Math.Round(count * rate, 2);
        var paymentMedium = medium.Equals("UPI", StringComparison.OrdinalIgnoreCase)
            ? PaymentMedium.OnlineUPI
            : PaymentMedium.CashInDrawer;

        // 1. Record income to Cash Drawer
        CashDrawerService.Instance.AddTransaction(
            TransactionDirection.Income,
            paymentMedium,
            CashCategory.XeroxPhotocopy,
            totalAmount,
            $"Physical Xerox Meter Audit Reconciliation ({count} copies @ ₹{rate:0.##})",
            "Walk-in Xerox",
            "");

        // 2. Add an entry to PrintTracker history so audit math balances
        var job = new PrintJobRecord
        {
            DocumentName = $"Physical Xerox (Audit Reconciled × {count})",
            PrinterName = "Brother DCP-T530DW",
            Submitter = Environment.UserName,
            Timestamp = DateTimeOffset.Now,
            Pages = count,
            Copies = 1,
            IsDuplex = false,
            IsColor = false,
            IsManualEntry = true,
            ItemCategory = "Photocopy",
            RatePerUnit = rate,
            TotalCost = totalAmount,
            IsBilled = true
        };

        PrintTrackerService.Instance.AllJobHistory.Insert(0, job);

        // 3. Trigger background Excel Auto-Sync
        PrintTrackerService.Instance.TriggerExcelAutoSync();

        OnAuditUpdated?.Invoke();
        Log.Information("Reconciled {Count} physical Xerox copies (₹{Amt}) into Cash Drawer & Excel", count, totalAmount);
    }

    /// <summary>
    /// 1-Click Rush-Hour counter: logs walk-up Xerox directly into Cash Drawer, job history, and Excel in 1 millisecond.
    /// </summary>
    public void LogQuickWalkupXerox(int copies, bool isDuplex, bool isColor, string medium, string customerName = "Walk-in Customer")
    {
        var settings = PrintTrackerService.Instance.Settings;
        double rate = isColor
            ? (isDuplex ? settings.ColorDuplexRate : settings.ColorSingleSideRate)
            : (isDuplex ? settings.BwDuplexRate : settings.PhotocopyBwRate);

        int sheets = isDuplex ? (int)Math.Ceiling(copies / 2.0) : copies;
        double totalCost = Math.Round(sheets * rate, 2);

        var paymentMedium = medium.Equals("UPI", StringComparison.OrdinalIgnoreCase)
            ? PaymentMedium.OnlineUPI
            : PaymentMedium.CashInDrawer;

        string desc = $"Quick {(isColor ? "Color" : "B&W")} Xerox × {copies} {(isDuplex ? "(Duplex)" : "(Single)")}";

        // 1. Add to Cash Drawer
        CashDrawerService.Instance.AddTransaction(
            TransactionDirection.Income,
            paymentMedium,
            CashCategory.XeroxPhotocopy,
            totalCost,
            desc,
            customerName,
            "");

        // 2. Add to PrintTracker History
        var job = new PrintJobRecord
        {
            DocumentName = desc,
            PrinterName = "Brother DCP-T530DW",
            Submitter = Environment.UserName,
            Timestamp = DateTimeOffset.Now,
            Pages = copies,
            Copies = 1,
            IsDuplex = isDuplex,
            IsColor = isColor,
            IsManualEntry = true,
            ItemCategory = "Photocopy",
            RatePerUnit = rate,
            TotalCost = totalCost,
            IsBilled = true
        };

        PrintTrackerService.Instance.AllJobHistory.Insert(0, job);

        // 3. Trigger Excel sync
        PrintTrackerService.Instance.TriggerExcelAutoSync();

        OnAuditUpdated?.Invoke();
    }

    /// <summary>
    /// Queries the Brother DCP-T530DW embedded web server over Wi-Fi/Network in real-time.
    /// Extracts operational state (Ready, Sleep, Printing, Copying) and exact ink levels.
    /// If unreachable, automatically triggers smart multi-tier discovery to locate the new IP.
    /// </summary>
    public async Task<PrinterLiveStatus> FetchPrinterStatusAsync(string? ipOverride = null)
    {
        string ip = !string.IsNullOrWhiteSpace(ipOverride) ? ipOverride.Trim() : CurrentIp;
        var status = new PrinterLiveStatus
        {
            IpAddress = ip,
            ConnectionType = "Wi-Fi & USB Dual"
        };

        bool querySuccess = false;
        try
        {
            querySuccess = await QueryPrinterHttpAsync(ip, status, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Debug("Printer Wi-Fi query failed for {Ip}: {Msg}", ip, ex.Message);
        }

        // If direct query failed and auto-discovery is enabled, attempt smart multi-tier discovery
        if (!querySuccess)
        {
            bool autoDiscover = true;
            try
            {
                autoDiscover = PrintTrackerService.Instance?.Settings?.AutoDiscoverPrinterIp ?? true;
            }
            catch { }

            if (autoDiscover)
            {
                Log.Information("Brother printer at {Ip} is unreachable. Initiating smart auto-discovery...", ip);
                string? discoveredIp = await AutoDiscoverPrinterIpAsync(ip).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(discoveredIp) && !string.Equals(discoveredIp, ip, StringComparison.OrdinalIgnoreCase))
                {
                    Log.Information("Auto-discovery found printer at {NewIp}. Querying status...", discoveredIp);
                    ip = discoveredIp;
                    status.IpAddress = discoveredIp;
                    try
                    {
                        querySuccess = await QueryPrinterHttpAsync(ip, status, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch { }
                }
            }
        }

        if (!querySuccess)
        {
            status.IsOnline = false;
            status.DeviceStatus = "USB Connected (Wi-Fi Standby)";
        }

        status.LastChecked = DateTimeOffset.Now;
        LiveStatus = status;
        OnAuditUpdated?.Invoke();
        return status;
    }

    private async Task<bool> QueryPrinterHttpAsync(string ip, PrinterLiveStatus status, CancellationToken ct)
    {
        bool hadSuccess = false;

        // 1. Query Monitor Page for operational state (Sleep, Ready, Copying, Printing)
        try
        {
            string monitorUrl = $"http://{ip}/home/monitor.html";
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2.0));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, cts.Token);
            var resp = await _http.GetAsync(monitorUrl, linked.Token).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode)
            {
                status.IsOnline = true;
                hadSuccess = true;
                string html = await resp.Content.ReadAsStringAsync(linked.Token).ConfigureAwait(false);
                var match = Regex.Match(html, @"<span class=""moni [^""]*"">([^<]+)</span>", RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    status.DeviceStatus = match.Groups[1].Value.Trim();
                }
                else
                {
                    status.DeviceStatus = "Ready";
                }
            }
        }
        catch { }

        // 2. Query Status Page for ink levels
        try
        {
            string statusUrl = $"http://{ip}/home/status.html";
            using var cts2 = new CancellationTokenSource(TimeSpan.FromSeconds(2.0));
            using var linked2 = CancellationTokenSource.CreateLinkedTokenSource(ct, cts2.Token);
            var resp2 = await _http.GetAsync(statusUrl, linked2.Token).ConfigureAwait(false);
            if (resp2.IsSuccessStatusCode)
            {
                status.IsOnline = true;
                hadSuccess = true;
                string html2 = await resp2.Content.ReadAsStringAsync(linked2.Token).ConfigureAwait(false);

                var mMatch = Regex.Match(html2, @"magenta\.gif""[^>]*height=""(\d+)""", RegexOptions.IgnoreCase);
                var cMatch = Regex.Match(html2, @"cyan\.gif""[^>]*height=""(\d+)""", RegexOptions.IgnoreCase);
                var yMatch = Regex.Match(html2, @"yellow\.gif""[^>]*height=""(\d+)""", RegexOptions.IgnoreCase);
                var kMatch = Regex.Match(html2, @"black\.gif""[^>]*height=""(\d+)""", RegexOptions.IgnoreCase);

                int k = 100, c = 100, m = 100, y = 100;
                bool parsedAny = false;

                if (kMatch.Success && int.TryParse(kMatch.Groups[1].Value, out int kHeight))
                {
                    k = Math.Clamp((int)Math.Round(kHeight / 44.0 * 100), 0, 100);
                    parsedAny = true;
                }

                if (cMatch.Success && int.TryParse(cMatch.Groups[1].Value, out int cHeight))
                {
                    c = Math.Clamp((int)Math.Round(cHeight / 44.0 * 100), 0, 100);
                    parsedAny = true;
                }

                if (mMatch.Success && int.TryParse(mMatch.Groups[1].Value, out int mHeight))
                {
                    m = Math.Clamp((int)Math.Round(mHeight / 44.0 * 100), 0, 100);
                    parsedAny = true;
                }

                if (yMatch.Success && int.TryParse(yMatch.Groups[1].Value, out int yHeight))
                {
                    y = Math.Clamp((int)Math.Round(yHeight / 44.0 * 100), 0, 100);
                    parsedAny = true;
                }

                var settings = PrintTrackerService.Instance?.Settings;
                if (settings != null && settings.PreferVisualInkLevels)
                {
                    status.InkBlackPercent = settings.CalibratedInkBlack;
                    status.InkCyanPercent = settings.CalibratedInkCyan;
                    status.InkMagentaPercent = settings.CalibratedInkMagenta;
                    status.InkYellowPercent = settings.CalibratedInkYellow;
                    status.IsCalibratedByVisualCheck = true;
                }
                else if (parsedAny)
                {
                    status.InkBlackPercent = k;
                    status.InkCyanPercent = c;
                    status.InkMagentaPercent = m;
                    status.InkYellowPercent = y;
                    status.IsCalibratedByVisualCheck = false;
                }
                else if (settings != null)
                {
                    status.InkBlackPercent = settings.CalibratedInkBlack;
                    status.InkCyanPercent = settings.CalibratedInkCyan;
                    status.InkMagentaPercent = settings.CalibratedInkMagenta;
                    status.InkYellowPercent = settings.CalibratedInkYellow;
                    status.IsCalibratedByVisualCheck = true;
                }
            }
        }
        catch { }

        return hadSuccess;
    }

    /// <summary>
    /// Calibrates the physical visual ink tank levels and saves them to settings.
    /// Used when refilling ink bottles or when head-drop chip counters differ from the physical tank window.
    /// </summary>
    public void CalibrateInkLevels(int black, int cyan, int magenta, int yellow, bool preferVisual = true)
    {
        PrintTrackerService.Instance.UpdateSettings(s =>
        {
            s.CalibratedInkBlack = Math.Clamp(black, 0, 100);
            s.CalibratedInkCyan = Math.Clamp(cyan, 0, 100);
            s.CalibratedInkMagenta = Math.Clamp(magenta, 0, 100);
            s.CalibratedInkYellow = Math.Clamp(yellow, 0, 100);
            s.PreferVisualInkLevels = preferVisual;
        });

        LiveStatus.InkBlackPercent = black;
        LiveStatus.InkCyanPercent = cyan;
        LiveStatus.InkMagentaPercent = magenta;
        LiveStatus.InkYellowPercent = yellow;
        LiveStatus.IsCalibratedByVisualCheck = preferVisual;

        OnAuditUpdated?.Invoke();
        Log.Information("Calibrated visual ink tank levels: BK={Bk}%, C={C}%, M={M}%, Y={Y}% (PreferVisual={Pref})",
            black, cyan, magenta, yellow, preferVisual);
    }

    /// <summary>
    /// Fast probe to test whether a given IP address hosts the Brother DCP-T530DW printer web server.
    /// Checks port 80 /home/status.html or /home/monitor.html.
    /// </summary>
    public async Task<bool> IsBrotherPrinterAliveAsync(string ip, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(ip)) return false;
        try
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            string url = $"http://{ip.Trim()}/home/status.html";
            using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, linkedCts.Token).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode)
            {
                string body = await resp.Content.ReadAsStringAsync(linkedCts.Token).ConfigureAwait(false);
                if (body.Contains("DCP-T530DW", StringComparison.OrdinalIgnoreCase) ||
                    body.Contains("Brother", StringComparison.OrdinalIgnoreCase) ||
                    body.Contains("tonerremain", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch { }
        return false;
    }

    /// <summary>
    /// Multi-tier smart discovery engine to locate the Brother DCP-T530DW printer when DHCP reassigns its IP.
    /// Tier 1: Probe stored / fallback IP.
    /// Tier 2: Query NetBIOS / DNS / mDNS hostnames (e.g. brw4c23383f8eed).
    /// Tier 3: Query Windows ARP table matching hardware MAC (4c-23-38-3f-8e-ed) & Brother OUIs.
    /// Tier 4: High-speed parallel local subnet sweep (/24) with 30 concurrent probes.
    /// </summary>
    public async Task<string?> AutoDiscoverPrinterIpAsync(string? fallbackIp = null, CancellationToken ct = default)
    {
        Log.Information("Starting Brother printer smart network discovery...");

        // ── TIER 1: Check Last Known / Configured IP ──
        string targetIp = !string.IsNullOrWhiteSpace(fallbackIp) ? fallbackIp.Trim() : CurrentIp;
        if (!string.IsNullOrWhiteSpace(targetIp))
        {
            if (await IsBrotherPrinterAliveAsync(targetIp, ct).ConfigureAwait(false))
            {
                Log.Information("Tier 1: Printer responded at last known IP {Ip}", targetIp);
                return targetIp;
            }
        }

        // ── TIER 2: Brother Hostname DNS / NetBIOS Resolution ──
        var candidateHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string cleanMac = CurrentMac.Replace(":", "").Replace("-", "").Trim().ToLowerInvariant();
        if (!string.IsNullOrEmpty(cleanMac))
        {
            candidateHosts.Add($"brw{cleanMac}");
            candidateHosts.Add($"brw{cleanMac}.local");
            candidateHosts.Add($"brw{cleanMac}.lan");
            candidateHosts.Add($"brn{cleanMac}");
        }
        candidateHosts.Add("brw4c23383f8eed");
        candidateHosts.Add("brw4c23383f8eed.local");
        candidateHosts.Add("brother");
        candidateHosts.Add("brother-printer");

        foreach (var host in candidateHosts)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                var addrs = await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
                foreach (var addr in addrs)
                {
                    if (addr.AddressFamily == AddressFamily.InterNetwork)
                    {
                        string ip = addr.ToString();
                        if (await IsBrotherPrinterAliveAsync(ip, ct).ConfigureAwait(false))
                        {
                            Log.Information("Tier 2: Discovered Brother printer via hostname '{Host}' -> {Ip}", host, ip);
                            PersistDiscoveredIp(ip);
                            return ip;
                        }
                    }
                }
            }
            catch { }
        }

        // ── TIER 3: Local Windows ARP Cache Matching ──
        try
        {
            var arpEntries = GetCandidateIpsFromArpTable();
            foreach (var candidateIp in arpEntries)
            {
                if (ct.IsCancellationRequested) break;
                if (string.Equals(candidateIp, targetIp, StringComparison.OrdinalIgnoreCase)) continue;

                if (await IsBrotherPrinterAliveAsync(candidateIp, ct).ConfigureAwait(false))
                {
                    Log.Information("Tier 3: Discovered Brother printer via ARP cache MAC match -> {Ip}", candidateIp);
                    PersistDiscoveredIp(candidateIp);
                    return candidateIp;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "ARP table inspection had error");
        }

        // ── TIER 4: High-Speed Parallel Local Subnet Sweep ──
        try
        {
            string? sweptIp = await SweepLocalSubnetsForBrotherAsync(ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(sweptIp))
            {
                Log.Information("Tier 4: Discovered Brother printer via parallel subnet sweep -> {Ip}", sweptIp);
                PersistDiscoveredIp(sweptIp);
                return sweptIp;
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Subnet sweep had error");
        }

        Log.Warning("Brother printer was not detected on local network");
        return null;
    }

    /// <summary>
    /// Reads the Windows ARP table and returns IPs matching the Brother MAC or known Brother OUIs.
    /// </summary>
    public List<string> GetCandidateIpsFromArpTable()
    {
        var candidates = new List<string>();
        string targetMac = CurrentMac.Replace(":", "").Replace("-", "").ToUpperInvariant();

        // Brother manufacturer OUI prefixes
        string[] brotherOuis = { "4C2338", "008077", "30055C", "B499BA", "DCEFCA", "E0D55E" };

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "arp.exe",
                Arguments = "-a",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            if (proc != null)
            {
                string output = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(1000);

                var matches = Regex.Matches(output, @"(\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3})\s+([0-9a-fA-F]{2}[:-][0-9a-fA-F]{2}[:-][0-9a-fA-F]{2}[:-][0-9a-fA-F]{2}[:-][0-9a-fA-F]{2}[:-][0-9a-fA-F]{2})");
                foreach (Match m in matches)
                {
                    string ip = m.Groups[1].Value;
                    string mac = m.Groups[2].Value.Replace("-", "").Replace(":", "").ToUpperInvariant();

                    if (!string.IsNullOrEmpty(targetMac) && mac.Equals(targetMac, StringComparison.OrdinalIgnoreCase))
                    {
                        // Priority 1: Exact MAC match!
                        candidates.Insert(0, ip);
                    }
                    else if (brotherOuis.Any(oui => mac.StartsWith(oui, StringComparison.OrdinalIgnoreCase)))
                    {
                        // Priority 2: Brother OUI match
                        candidates.Add(ip);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Failed to read arp table");
        }

        return candidates.Distinct().ToList();
    }

    /// <summary>
    /// High-speed parallel /24 subnet sweep across active local network adapters.
    /// Probes port 80 on all 254 IPs using 30 concurrent workers with fast cancellation.
    /// </summary>
    private async Task<string?> SweepLocalSubnetsForBrotherAsync(CancellationToken ct)
    {
        var localIps = GetLocalIPv4Addresses();
        var subnets = localIps
            .Where(ip => ip.Contains('.'))
            .Select(ip => ip.Substring(0, ip.LastIndexOf('.') + 1))
            .Distinct()
            .ToList();

        if (subnets.Count == 0) subnets.Add("192.168.1.");

        foreach (var subnetBase in subnets)
        {
            if (ct.IsCancellationRequested) break;

            using var sweepCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            string? foundIp = null;
            var sem = new SemaphoreSlim(30, 30);

            // Prioritize common printer IPs (.7, .2 to .50) before higher ranges
            var hostIndices = Enumerable.Range(1, 254)
                .OrderBy(h => h == 7 ? 0 : (h < 50 ? 1 : 2))
                .ToList();

            var tasks = hostIndices.Select(async host =>
            {
                if (sweepCts.IsCancellationRequested) return;

                await sem.WaitAsync(sweepCts.Token).ConfigureAwait(false);
                try
                {
                    if (sweepCts.IsCancellationRequested) return;

                    string testIp = $"{subnetBase}{host}";
                    if (await IsBrotherPrinterAliveAsync(testIp, sweepCts.Token).ConfigureAwait(false))
                    {
                        foundIp = testIp;
                        sweepCts.Cancel(); // Cancel remaining sweep tasks immediately!
                    }
                }
                catch { }
                finally
                {
                    sem.Release();
                }
            });

            try
            {
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }

            if (!string.IsNullOrWhiteSpace(foundIp))
            {
                return foundIp;
            }
        }

        return null;
    }

    private static List<string> GetLocalIPv4Addresses()
    {
        var list = new List<string>();
        try
        {
            var interfaces = NetworkInterface.GetAllNetworkInterfaces();
            foreach (var iface in interfaces)
            {
                if (iface.OperationalStatus != OperationalStatus.Up ||
                    iface.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;

                var ipProps = iface.GetIPProperties();
                foreach (var addr in ipProps.UnicastAddresses)
                {
                    if (addr.Address.AddressFamily == AddressFamily.InterNetwork)
                    {
                        list.Add(addr.Address.ToString());
                    }
                }
            }
        }
        catch { }
        return list;
    }

    private void PersistDiscoveredIp(string ip)
    {
        if (string.IsNullOrWhiteSpace(ip)) return;
        ip = ip.Trim();

        try
        {
            PrintTrackerService.Instance.UpdateSettings(s =>
            {
                s.BrotherPrinterIp = ip;
            });
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Unable to save discovered IP to settings");
        }

        LiveStatus.IpAddress = ip;
        OnPrinterIpDiscovered?.Invoke(ip);
        OnAuditUpdated?.Invoke();
    }
}
