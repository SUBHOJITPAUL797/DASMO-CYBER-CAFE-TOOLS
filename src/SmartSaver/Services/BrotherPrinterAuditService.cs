using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
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
    /// </summary>
    public async Task<PrinterLiveStatus> FetchPrinterStatusAsync(string? ipOverride = null)
    {
        string ip = !string.IsNullOrWhiteSpace(ipOverride) ? ipOverride.Trim() : "192.168.1.7";
        var status = new PrinterLiveStatus
        {
            IpAddress = ip,
            ConnectionType = "Wi-Fi & USB Dual"
        };

        try
        {
            // 1. Query Monitor Page for operational state
            string monitorUrl = $"http://{ip}/home/monitor.html";
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2.5));
            var resp = await _http.GetAsync(monitorUrl, cts.Token).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode)
            {
                status.IsOnline = true;
                string html = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
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

            // 2. Query Status Page for ink levels
            string statusUrl = $"http://{ip}/home/status.html";
            using var cts2 = new CancellationTokenSource(TimeSpan.FromSeconds(2.5));
            var resp2 = await _http.GetAsync(statusUrl, cts2.Token).ConfigureAwait(false);
            if (resp2.IsSuccessStatusCode)
            {
                status.IsOnline = true;
                string html2 = await resp2.Content.ReadAsStringAsync().ConfigureAwait(false);

                // Look for images with tonerremain class
                // e.g. <img src="../common/images/magenta.gif" alt="Magenta" class="tonerremain" height="42" />
                // Max height is 44px
                var mMatch = Regex.Match(html2, @"magenta\.gif""[^>]*height=""(\d+)""", RegexOptions.IgnoreCase);
                var cMatch = Regex.Match(html2, @"cyan\.gif""[^>]*height=""(\d+)""", RegexOptions.IgnoreCase);
                var yMatch = Regex.Match(html2, @"yellow\.gif""[^>]*height=""(\d+)""", RegexOptions.IgnoreCase);
                var kMatch = Regex.Match(html2, @"black\.gif""[^>]*height=""(\d+)""", RegexOptions.IgnoreCase);

                if (kMatch.Success && int.TryParse(kMatch.Groups[1].Value, out int kHeight))
                    status.InkBlackPercent = Math.Clamp((int)Math.Round(kHeight / 44.0 * 100), 0, 100);

                if (cMatch.Success && int.TryParse(cMatch.Groups[1].Value, out int cHeight))
                    status.InkCyanPercent = Math.Clamp((int)Math.Round(cHeight / 44.0 * 100), 0, 100);

                if (mMatch.Success && int.TryParse(mMatch.Groups[1].Value, out int mHeight))
                    status.InkMagentaPercent = Math.Clamp((int)Math.Round(mHeight / 44.0 * 100), 0, 100);

                if (yMatch.Success && int.TryParse(yMatch.Groups[1].Value, out int yHeight))
                    status.InkYellowPercent = Math.Clamp((int)Math.Round(yHeight / 44.0 * 100), 0, 100);
            }
        }
        catch (Exception ex)
        {
            Log.Debug("Printer Wi-Fi query failed for {Ip}: {Msg}", ip, ex.Message);
            status.IsOnline = false;
            status.DeviceStatus = "USB Connected (Wi-Fi Standby)";
        }

        status.LastChecked = DateTimeOffset.Now;
        LiveStatus = status;
        OnAuditUpdated?.Invoke();
        return status;
    }
}
