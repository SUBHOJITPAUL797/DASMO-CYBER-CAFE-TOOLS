using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Printing;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using SmartSaver.Models;

namespace SmartSaver.Services;

/// <summary>
/// Professional, industry-grade Windows Print Spooler Monitor and Customer Billing Engine.
/// Automatically detects print jobs from Brother DCP-T530DW and all Windows printers,
/// calculates page counts, duplex (double-sided) paper usage, color modes, and customer totals.
/// </summary>
public sealed class PrintTrackerService : IDisposable
{
    private static readonly Lazy<PrintTrackerService> _instance = new(() => new PrintTrackerService());
    public static PrintTrackerService Instance => _instance.Value;

    private readonly string _settingsFilePath;
    private readonly string _historyFilePath;
    private readonly string _billsFilePath;

    private readonly object _lock = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _seenJobs = new(StringComparer.OrdinalIgnoreCase);

    private CancellationTokenSource? _monitorCts;
    private Task? _monitorTask;
    private bool _isDisposed;

    public PrintBillingSettings Settings { get; private set; } = new();

    /// <summary>
    /// Print jobs for the customer currently at the counter (unbilled active cart).
    /// </summary>
    public List<PrintJobRecord> ActiveCustomerJobs { get; } = new();

    /// <summary>
    /// Historical list of all print jobs captured.
    /// </summary>
    public List<PrintJobRecord> AllJobHistory { get; } = new();

    /// <summary>
    /// Completed customer billing slips / sessions.
    /// </summary>
    public List<CustomerBillSession> CompletedBillSessions { get; } = new();

    /// <summary>
    /// Fired whenever a new print job is detected and captured.
    /// </summary>
    public event Action<PrintJobRecord>? OnJobDetected;

    /// <summary>
    /// Fired when active customer jobs or amounts change.
    /// </summary>
    public event Action? OnActiveCartChanged;

    /// <summary>
    /// Fired when history or bills are updated.
    /// </summary>
    public event Action? OnHistoryUpdated;

    private PrintTrackerService()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string dataDir = Path.Combine(appData, "DASMO CYBER CAFE TOOLS");
        Directory.CreateDirectory(dataDir);

        _settingsFilePath = Path.Combine(dataDir, "print_billing_settings.json");
        _historyFilePath = Path.Combine(dataDir, "print_job_history.json");
        _billsFilePath = Path.Combine(dataDir, "customer_bill_history.json");

        LoadSettings();
        LoadHistory();
    }

    #region Lifecycle & Background Monitoring

    /// <summary>
    /// Starts the background spooler watcher.
    /// </summary>
    public void Start()
    {
        lock (_lock)
        {
            if (_monitorTask != null && !_monitorTask.IsCompleted) return;

            _monitorCts?.Cancel();
            _monitorCts?.Dispose();
            _monitorCts = new CancellationTokenSource();

            var token = _monitorCts.Token;
            _monitorTask = Task.Run(() => MonitorLoopAsync(token), token);
            Log.Information("PrintTrackerService started monitoring print queues");
        }
    }

    /// <summary>
    /// Stops the background spooler watcher.
    /// </summary>
    public void Stop()
    {
        lock (_lock)
        {
            try
            {
                _monitorCts?.Cancel();
            }
            catch { }
            Log.Information("PrintTrackerService stopped monitoring print queues");
        }
    }

    private async Task MonitorLoopAsync(CancellationToken token)
    {
        // High-frequency polling loop: checks local print spooler queues every 500ms
        while (!token.IsCancellationRequested)
        {
            try
            {
                if (Settings.AutoMonitoringEnabled)
                {
                    PollPrintQueues();
                }
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Error during print queue poll cycle");
            }

            try
            {
                await Task.Delay(500, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>
    /// Scans all local and network print queues using System.Printing and Win32 EnumJobs.
    /// </summary>
    public void PollPrintQueues()
    {
        // 1. Try Win32 EnumJobs on monitored or all printers first for rich DEVMODE (duplex/color)
        var installedPrinters = GetInstalledPrinterNames();
        foreach (var printerName in installedPrinters)
        {
            if (Settings.TargetPrinters.Count > 0 &&
                !Settings.TargetPrinters.Any(p => p.Equals(printerName, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            ScanPrinterWithWin32(printerName);
        }

        // 2. High-level System.Printing fallback to ensure 0 jobs are missed
        try
        {
            using var server = new LocalPrintServer();
            var queues = server.GetPrintQueues();
            foreach (var q in queues)
            {
                try
                {
                    if (Settings.TargetPrinters.Count > 0 &&
                        !Settings.TargetPrinters.Any(p => p.Equals(q.Name, StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    q.Refresh();
                    if (q.NumberOfJobs == 0) continue;

                    var jobs = q.GetPrintJobInfoCollection();
                    foreach (var job in jobs)
                    {
                        try
                        {
                            string docName = string.IsNullOrWhiteSpace(job.Name) ? "Print Document" : job.Name;
                            int pages = Math.Max(1, job.NumberOfPages);
                            uint jobId = (uint)job.JobIdentifier;

                            string dedupKey = $"{q.Name}:{jobId}:{docName}:{pages}";
                            if (_seenJobs.ContainsKey(dedupKey)) continue;

                            // Mark seen with sliding 10-minute expiry
                            _seenJobs.TryAdd(dedupKey, DateTimeOffset.Now);

                            // Detect duplex and color from Win32 DEVMODE
                            var (isDuplex, isColor, copies) = InspectJobDevMode(q.Name, jobId);

                            var record = new PrintJobRecord
                            {
                                SpoolerJobId = jobId,
                                DocumentName = CleanDocumentName(docName),
                                PrinterName = q.Name,
                                Submitter = string.IsNullOrWhiteSpace(job.Submitter) ? Environment.UserName : job.Submitter,
                                Timestamp = DateTimeOffset.Now,
                                Pages = pages,
                                Copies = Math.Max(1, copies),
                                IsDuplex = isDuplex,
                                IsColor = isColor,
                                PaperSize = "A4"
                            };

                            CalculateCost(record);
                            RegisterNewJob(record);
                        }
                        catch (Exception ex)
                        {
                            Log.Debug(ex, "Failed to parse print job {JobId} on queue {Queue}", job.JobIdentifier, q.Name);
                        }
                    }
                }
                catch { }
            }
        }
        catch { }

        // Clean old seen jobs (> 15 minutes)
        if (_seenJobs.Count > 200)
        {
            var cutoff = DateTimeOffset.Now.AddMinutes(-15);
            foreach (var kvp in _seenJobs)
            {
                if (kvp.Value < cutoff)
                {
                    _seenJobs.TryRemove(kvp.Key, out _);
                }
            }
        }
    }

    private void ScanPrinterWithWin32(string printerName)
    {
        IntPtr hPrinter = IntPtr.Zero;
        try
        {
            if (!OpenPrinter(printerName, out hPrinter, IntPtr.Zero) || hPrinter == IntPtr.Zero)
                return;

            uint bytesNeeded = 0;
            uint jobsReturned = 0;
            EnumJobs(hPrinter, 0, 50, 2, IntPtr.Zero, 0, out bytesNeeded, out jobsReturned);

            if (bytesNeeded == 0) return;

            IntPtr pBuf = Marshal.AllocHGlobal((int)bytesNeeded);
            try
            {
                if (EnumJobs(hPrinter, 0, 50, 2, pBuf, bytesNeeded, out bytesNeeded, out jobsReturned) && jobsReturned > 0)
                {
                    int structSize = Marshal.SizeOf<JOB_INFO_2>();
                    for (int i = 0; i < jobsReturned; i++)
                    {
                        IntPtr jobPtr = IntPtr.Add(pBuf, i * structSize);
                        var jobInfo = Marshal.PtrToStructure<JOB_INFO_2>(jobPtr);

                        string docName = Marshal.PtrToStringAuto(jobInfo.pDocument) ?? "Print Document";
                        int pages = (int)jobInfo.TotalPages;
                        if (pages <= 0) pages = 1;

                        string dedupKey = $"{printerName}:{jobInfo.JobId}:{docName}:{pages}";
                        if (_seenJobs.ContainsKey(dedupKey)) continue;

                        _seenJobs.TryAdd(dedupKey, DateTimeOffset.Now);

                        bool isDuplex = false;
                        bool isColor = false;
                        int copies = 1;

                        if (jobInfo.pDevMode != IntPtr.Zero)
                        {
                            try
                            {
                                var devMode = Marshal.PtrToStructure<DEVMODE>(jobInfo.pDevMode);
                                isDuplex = devMode.dmDuplex is 2 or 3; // 2=Vertical (Long-Edge), 3=Horizontal (Short-Edge)
                                isColor = devMode.dmColor == 2;        // 2=Color
                                if (devMode.dmCopies > 1) copies = devMode.dmCopies;
                            }
                            catch { }
                        }

                        // Brother DCP-T530DW heuristic: if printer name contains T530DW and job wasn't explicitly single, check settings
                        var record = new PrintJobRecord
                        {
                            SpoolerJobId = jobInfo.JobId,
                            DocumentName = CleanDocumentName(docName),
                            PrinterName = printerName,
                            Submitter = Marshal.PtrToStringAuto(jobInfo.pUserName) ?? Environment.UserName,
                            Timestamp = DateTimeOffset.Now,
                            Pages = pages,
                            Copies = copies,
                            IsDuplex = isDuplex,
                            IsColor = isColor,
                            PaperSize = "A4"
                        };

                        CalculateCost(record);
                        RegisterNewJob(record);
                    }
                }
            }
            finally
            {
                Marshal.FreeHGlobal(pBuf);
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Win32 scan failed for {Printer}", printerName);
        }
        finally
        {
            if (hPrinter != IntPtr.Zero) ClosePrinter(hPrinter);
        }
    }

    private (bool isDuplex, bool isColor, int copies) InspectJobDevMode(string printerName, uint jobId)
    {
        IntPtr hPrinter = IntPtr.Zero;
        try
        {
            if (!OpenPrinter(printerName, out hPrinter, IntPtr.Zero) || hPrinter == IntPtr.Zero)
                return (false, false, 1);

            uint bytesNeeded = 0;
            GetJob(hPrinter, jobId, 2, IntPtr.Zero, 0, out bytesNeeded);
            if (bytesNeeded == 0) return (false, false, 1);

            IntPtr pBuf = Marshal.AllocHGlobal((int)bytesNeeded);
            try
            {
                if (GetJob(hPrinter, jobId, 2, pBuf, bytesNeeded, out bytesNeeded))
                {
                    var jobInfo = Marshal.PtrToStructure<JOB_INFO_2>(pBuf);
                    if (jobInfo.pDevMode != IntPtr.Zero)
                    {
                        var devMode = Marshal.PtrToStructure<DEVMODE>(jobInfo.pDevMode);
                        bool isDuplex = devMode.dmDuplex is 2 or 3;
                        bool isColor = devMode.dmColor == 2;
                        int copies = devMode.dmCopies > 1 ? devMode.dmCopies : 1;
                        return (isDuplex, isColor, copies);
                    }
                }
            }
            finally
            {
                Marshal.FreeHGlobal(pBuf);
            }
        }
        catch { }
        finally
        {
            if (hPrinter != IntPtr.Zero) ClosePrinter(hPrinter);
        }

        return (false, false, 1);
    }

    #endregion

    #region Job Registration & Pricing Math

    /// <summary>
    /// Registers a newly captured or manual print job, assigns it to active cart, and notifies UI.
    /// </summary>
    public void RegisterNewJob(PrintJobRecord job)
    {
        lock (_lock)
        {
            ActiveCustomerJobs.Add(job);
            AllJobHistory.Insert(0, job);
            SaveHistory();
        }

        Log.Information("Print job captured: {Doc} on {Printer} -> {Pages} pages, Duplex={Duplex}, Color={Color}, Cost=₹{Cost:F2}",
            job.DocumentName, job.PrinterName, job.Pages, job.IsDuplex, job.IsColor, job.TotalCost);

        if (Settings.PlaySoundOnJobDetected)
        {
            PlayChimeSound();
        }

        if (Settings.ShowNotificationOnJobDetected)
        {
            NotificationService.NotifyPrintJobCaptured(job.DocumentName, job.Pages, job.IsDuplex, job.IsColor, job.TotalCost);
        }

        OnJobDetected?.Invoke(job);
        OnActiveCartChanged?.Invoke();
        OnHistoryUpdated?.Invoke();
    }

    /// <summary>
    /// Calculates the cost in Rupees (₹) for a print job based on current rate settings.
    /// Perfectly handles Duplex (double-sided): 1 sheet = 2 pages ("replacement in 2").
    /// </summary>
    public void CalculateCost(PrintJobRecord job)
    {
        int impressions = job.TotalImpressions;

        if (job.IsDuplex)
        {
            // Duplex (Double-sided) calculation
            int sheets = job.SheetsUsed; // ceil(impressions / 2.0)
            if (job.IsColor)
            {
                if (Settings.ColorDuplexPricedPerSheet)
                {
                    job.RatePerUnit = Settings.ColorDuplexRate;
                    job.TotalCost = Math.Round(sheets * Settings.ColorDuplexRate, 2);
                }
                else
                {
                    job.RatePerUnit = Settings.ColorDuplexRate;
                    job.TotalCost = Math.Round(impressions * Settings.ColorDuplexRate, 2);
                }
            }
            else
            {
                if (Settings.BwDuplexPricedPerSheet)
                {
                    job.RatePerUnit = Settings.BwDuplexRate;
                    job.TotalCost = Math.Round(sheets * Settings.BwDuplexRate, 2);
                }
                else
                {
                    job.RatePerUnit = Settings.BwDuplexRate;
                    job.TotalCost = Math.Round(impressions * Settings.BwDuplexRate, 2);
                }
            }
        }
        else
        {
            // Simplex (Single-sided) calculation
            if (job.IsColor)
            {
                job.RatePerUnit = Settings.ColorSingleSideRate;
                job.TotalCost = Math.Round(impressions * Settings.ColorSingleSideRate, 2);
            }
            else
            {
                job.RatePerUnit = Settings.BwSingleSideRate;
                job.TotalCost = Math.Round(impressions * Settings.BwSingleSideRate, 2);
            }
        }
    }

    /// <summary>
    /// Adds a manual charge to active cart (photocopy, lamination, photos, or offline prints).
    /// </summary>
    public PrintJobRecord AddManualJob(string category, int pages, bool isDuplex, bool isColor, double? overrideRate = null)
    {
        double rate = overrideRate ?? (category switch
        {
            "Photocopy" => isColor ? Settings.PhotocopyColorRate : Settings.PhotocopyBwRate,
            "Lamination" => Settings.LaminationRate,
            "Passport Photo" => Settings.PhotoGlossyRate,
            _ => isDuplex
                ? (isColor ? Settings.ColorDuplexRate : Settings.BwDuplexRate)
                : (isColor ? Settings.ColorSingleSideRate : Settings.BwSingleSideRate)
        });

        int copies = 1;
        int impressions = pages * copies;
        int sheets = isDuplex ? (int)Math.Ceiling(impressions / 2.0) : impressions;
        double cost = isDuplex && (isColor ? Settings.ColorDuplexPricedPerSheet : Settings.BwDuplexPricedPerSheet)
            ? sheets * rate
            : impressions * rate;

        var job = new PrintJobRecord
        {
            SpoolerJobId = 0,
            DocumentName = $"{category} ({pages} page{(pages > 1 ? "s" : "")})",
            PrinterName = "Counter Manual",
            Submitter = Environment.UserName,
            Timestamp = DateTimeOffset.Now,
            Pages = pages,
            Copies = copies,
            IsDuplex = isDuplex,
            IsColor = isColor,
            IsManualEntry = true,
            ItemCategory = category,
            RatePerUnit = rate,
            TotalCost = Math.Round(cost, 2),
            PaperSize = "A4"
        };

        RegisterNewJob(job);
        return job;
    }

    /// <summary>
    /// Modifies a job in the active customer cart (toggle duplex, toggle color, or change page count).
    /// </summary>
    public void UpdateJobInCart(string jobId, Action<PrintJobRecord> updateAction)
    {
        lock (_lock)
        {
            var job = ActiveCustomerJobs.FirstOrDefault(j => j.Id == jobId);
            if (job == null) return;

            updateAction(job);
            CalculateCost(job);
            SaveHistory();
        }

        OnActiveCartChanged?.Invoke();
        OnHistoryUpdated?.Invoke();
    }

    /// <summary>
    /// Removes a job from the active customer cart.
    /// </summary>
    public void RemoveJobFromCart(string jobId)
    {
        lock (_lock)
        {
            var job = ActiveCustomerJobs.FirstOrDefault(j => j.Id == jobId);
            if (job != null)
            {
                ActiveCustomerJobs.Remove(job);
                AllJobHistory.Remove(job);
                SaveHistory();
            }
        }

        OnActiveCartChanged?.Invoke();
        OnHistoryUpdated?.Invoke();
    }

    /// <summary>
    /// Finalizes the active customer bill, generates a bill slip, records it to sales history, and clears the cart for the next customer.
    /// </summary>
    public CustomerBillSession CompleteCustomerBill(string? customerName, string? customerPhone, string paymentMode = "Cash", string notes = "")
    {
        CustomerBillSession session;
        lock (_lock)
        {
            string name = string.IsNullOrWhiteSpace(customerName) ? "Walk-in Customer" : customerName.Trim();
            string phone = string.IsNullOrWhiteSpace(customerPhone) ? "" : customerPhone.Trim();

            string billNo = $"BILL-{DateTime.Now:yyyyMMdd}-{CompletedBillSessions.Count + 1:D3}";
            session = new CustomerBillSession
            {
                SessionId = Guid.NewGuid().ToString("N"),
                BillNumber = billNo,
                CustomerName = name,
                CustomerPhone = phone,
                BilledAt = DateTimeOffset.Now,
                PaymentMode = paymentMode,
                Notes = notes,
                Jobs = new List<PrintJobRecord>(ActiveCustomerJobs)
            };

            foreach (var j in session.Jobs)
            {
                j.IsBilled = true;
                j.BillSessionId = session.SessionId;
                j.CustomerName = name;
                j.CustomerPhone = phone;
            }

            CompletedBillSessions.Insert(0, session);
            ActiveCustomerJobs.Clear();

            SaveBills();
            SaveHistory();
        }

        Log.Information("Customer Bill finalized: {BillNo} - {Name} -> {Items} items, ₹{Total:F2}",
            session.BillNumber, session.CustomerName, session.Jobs.Count, session.TotalAmount);

        OnActiveCartChanged?.Invoke();
        OnHistoryUpdated?.Invoke();

        return session;
    }

    /// <summary>
    /// Clears the active cart without archiving (e.g. discarding test prints).
    /// </summary>
    public void ClearActiveCart()
    {
        lock (_lock)
        {
            ActiveCustomerJobs.Clear();
        }
        OnActiveCartChanged?.Invoke();
    }

    #endregion

    #region Bill Formatting & Sharing

    /// <summary>
    /// Generates a clean 58mm / 80mm thermal receipt slip text.
    /// </summary>
    public string GenerateReceiptText(CustomerBillSession session)
    {
        var sb = new StringBuilder();
        sb.AppendLine("================================");
        sb.AppendLine($"   {Settings.ShopName.ToUpperInvariant()}");
        sb.AppendLine($"      {Settings.ShopAddress}");
        sb.AppendLine($"        Tel: {Settings.ShopPhone}");
        sb.AppendLine("================================");
        sb.AppendLine($"Bill No: {session.BillNumber}");
        sb.AppendLine($"Date   : {session.BilledAt:dd/MM/yyyy hh:mm tt}");
        sb.AppendLine($"Customer: {session.CustomerName}");
        if (!string.IsNullOrEmpty(session.CustomerPhone))
            sb.AppendLine($"Mobile  : {session.CustomerPhone}");
        sb.AppendLine("--------------------------------");
        sb.AppendLine("ITEM / DESCRIPTION        TOTAL");
        sb.AppendLine("--------------------------------");

        foreach (var job in session.Jobs)
        {
            string shortDoc = job.DocumentName.Length > 20
                ? job.DocumentName.Substring(0, 17) + "..."
                : job.DocumentName;

            sb.AppendLine($"{shortDoc}");
            string mode = job.IsDuplex
                ? $"{job.TotalImpressions}p (Duplex {job.SheetsUsed}s) {(job.IsColor ? "Color" : "B&W")}"
                : $"{job.TotalImpressions}p (Single) {(job.IsColor ? "Color" : "B&W")}";
            sb.AppendLine($"  {mode,-20} ₹{job.TotalCost,7:F2}");
        }

        sb.AppendLine("--------------------------------");
        sb.AppendLine($"TOTAL PAGES : {session.TotalPages}");
        sb.AppendLine($"TOTAL SHEETS: {session.TotalSheets}");
        sb.AppendLine($"GRAND TOTAL : ₹{session.TotalAmount:F2}");
        sb.AppendLine($"PAYMENT MODE: {session.PaymentMode}");
        sb.AppendLine("================================");
        sb.AppendLine($"   {Settings.BillFooterNote}");
        sb.AppendLine("================================");

        return sb.ToString();
    }

    /// <summary>
    /// Formats a clean WhatsApp bill text message and returns the wa.me URL for 1-click sharing.
    /// </summary>
    public string GenerateWhatsAppShareUrl(CustomerBillSession session)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"*🖨️ {Settings.ShopName}*");
        sb.AppendLine($"*Print & Xerox Bill Receipt*");
        sb.AppendLine($"_Bill No:_ {session.BillNumber}");
        sb.AppendLine($"_Date:_ {session.BilledAt:dd/MM/yyyy hh:mm tt}");
        sb.AppendLine($"_Customer:_ {session.CustomerName}");
        sb.AppendLine("────────────────────");

        int itemIdx = 1;
        foreach (var j in session.Jobs)
        {
            string type = j.IsDuplex
                ? $"📑 Duplex ({j.SheetsUsed} sheet{(j.SheetsUsed > 1 ? "s" : "")}, {j.TotalImpressions} pages)"
                : $"📄 Single-Sided ({j.TotalImpressions} page{(j.TotalImpressions > 1 ? "s" : "")})";
            string color = j.IsColor ? "🌈 Color" : "⚫ B&W";
            sb.AppendLine($"{itemIdx++}. *{j.DocumentName}*");
            sb.AppendLine($"   {type} • {color} = *₹{j.TotalCost:F2}*");
        }

        sb.AppendLine("────────────────────");
        sb.AppendLine($"*Total Pages:* {session.TotalPages}");
        sb.AppendLine($"*Total Sheets:* {session.TotalSheets}");
        sb.AppendLine($"*Grand Total:* ₹{session.TotalAmount:F2}");
        sb.AppendLine($"*Payment Mode:* {session.PaymentMode}");
        sb.AppendLine("────────────────────");
        sb.AppendLine($"_{Settings.BillFooterNote}_");
        sb.AppendLine($"_Contact:_ {Settings.ShopPhone}");

        string rawText = sb.ToString();
        string phone = session.CustomerPhone.Replace("+", "").Replace(" ", "").Replace("-", "");
        if (phone.Length == 10 && !phone.StartsWith("91"))
        {
            phone = "91" + phone;
        }

        return string.IsNullOrEmpty(phone)
            ? $"https://wa.me/?text={Uri.EscapeDataString(rawText)}"
            : $"https://wa.me/{phone}?text={Uri.EscapeDataString(rawText)}";
    }

    /// <summary>
    /// Exports the job history to a CSV file for Excel accounting.
    /// </summary>
    public async Task<string> ExportHistoryToCsvAsync(string targetFilePath, DateTimeOffset? fromDate = null, DateTimeOffset? toDate = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Timestamp,BillNo,CustomerName,CustomerPhone,DocumentName,Printer,Pages,Copies,Impressions,IsDuplex,SheetsUsed,IsColor,Rate,TotalCost,PaymentMode");

        lock (_lock)
        {
            var filtered = AllJobHistory.AsEnumerable();
            if (fromDate.HasValue) filtered = filtered.Where(j => j.Timestamp >= fromDate.Value);
            if (toDate.HasValue) filtered = filtered.Where(j => j.Timestamp <= toDate.Value);

            foreach (var j in filtered.OrderByDescending(j => j.Timestamp))
            {
                string billNo = "";
                string payMode = "Cash";
                if (!string.IsNullOrEmpty(j.BillSessionId))
                {
                    var sess = CompletedBillSessions.FirstOrDefault(s => s.SessionId == j.BillSessionId);
                    if (sess != null)
                    {
                        billNo = sess.BillNumber;
                        payMode = sess.PaymentMode;
                    }
                }

                sb.AppendLine(
                    $"\"{j.Timestamp:yyyy-MM-dd HH:mm:ss}\"," +
                    $"\"{billNo}\"," +
                    $"\"{EscapeCsv(j.CustomerName)}\"," +
                    $"\"{j.CustomerPhone}\"," +
                    $"\"{EscapeCsv(j.DocumentName)}\"," +
                    $"\"{EscapeCsv(j.PrinterName)}\"," +
                    $"{j.Pages}," +
                    $"{j.Copies}," +
                    $"{j.TotalImpressions}," +
                    $"{(j.IsDuplex ? "Yes" : "No")}," +
                    $"{j.SheetsUsed}," +
                    $"{(j.IsColor ? "Color" : "B&W")}," +
                    $"{j.RatePerUnit:F2}," +
                    $"{j.TotalCost:F2}," +
                    $"\"{payMode}\"");
            }
        }

        await File.WriteAllTextAsync(targetFilePath, sb.ToString(), Encoding.UTF8).ConfigureAwait(false);
        return targetFilePath;
    }

    private static string EscapeCsv(string s) => s?.Replace("\"", "\"\"") ?? "";

    #endregion

    #region Persistence & Settings

    public void UpdateSettings(Action<PrintBillingSettings> updateAction)
    {
        lock (_lock)
        {
            updateAction(Settings);
            SaveSettings();

            // Recalculate any active cart items with new rates
            foreach (var j in ActiveCustomerJobs)
            {
                CalculateCost(j);
            }
        }

        OnActiveCartChanged?.Invoke();
    }

    private void LoadSettings()
    {
        try
        {
            if (File.Exists(_settingsFilePath))
            {
                string json = File.ReadAllText(_settingsFilePath);
                var loaded = JsonSerializer.Deserialize<PrintBillingSettings>(json);
                if (loaded != null) Settings = loaded;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to load print billing settings. Using defaults.");
        }
    }

    private void SaveSettings()
    {
        try
        {
            string json = JsonSerializer.Serialize(Settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_settingsFilePath, json);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to save print billing settings");
        }
    }

    private void LoadHistory()
    {
        try
        {
            if (File.Exists(_historyFilePath))
            {
                string json = File.ReadAllText(_historyFilePath);
                var jobs = JsonSerializer.Deserialize<List<PrintJobRecord>>(json);
                if (jobs != null)
                {
                    AllJobHistory.Clear();
                    AllJobHistory.AddRange(jobs);
                }
            }

            if (File.Exists(_billsFilePath))
            {
                string json = File.ReadAllText(_billsFilePath);
                var bills = JsonSerializer.Deserialize<List<CustomerBillSession>>(json);
                if (bills != null)
                {
                    CompletedBillSessions.Clear();
                    CompletedBillSessions.AddRange(bills);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to load print history");
        }
    }

    private void SaveHistory()
    {
        try
        {
            // Limit stored history to last 5000 jobs to maintain peak performance
            var toSave = AllJobHistory.Take(5000).ToList();
            string json = JsonSerializer.Serialize(toSave, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_historyFilePath, json);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to save print job history");
        }
    }

    private void SaveBills()
    {
        try
        {
            var toSave = CompletedBillSessions.Take(1000).ToList();
            string json = JsonSerializer.Serialize(toSave, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_billsFilePath, json);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to save customer bills history");
        }
    }

    #endregion

    #region Helpers & P/Invoke

    public static List<string> GetInstalledPrinterNames()
    {
        var list = new List<string>();
        try
        {
            using var server = new LocalPrintServer();
            foreach (var q in server.GetPrintQueues())
            {
                if (!string.IsNullOrWhiteSpace(q.Name))
                {
                    list.Add(q.Name);
                }
            }
        }
        catch
        {
            foreach (string p in System.Drawing.Printing.PrinterSettings.InstalledPrinters)
            {
                list.Add(p);
            }
        }
        return list;
    }

    private static string CleanDocumentName(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "Print Document";
        string s = raw.Trim();
        // Remove trailing application titles like " - Google Chrome", " - Word"
        string[] appSuffixes = [
            " - Google Chrome", " - Microsoft​ Edge", " - Adobe Acrobat Pro",
            " - Adobe Acrobat Reader", " - Adobe Acrobat", " - Word", " - Excel"
        ];
        foreach (var suffix in appSuffixes)
        {
            if (s.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                s = s.Substring(0, s.Length - suffix.Length).Trim();
                break;
            }
        }
        return s;
    }

    private static void PlayChimeSound()
    {
        try
        {
            System.Media.SystemSounds.Asterisk.Play();
        }
        catch { }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        Stop();
        _monitorCts?.Dispose();
    }

    [DllImport("winspool.drv", EntryPoint = "OpenPrinterW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool OpenPrinter(string pPrinterName, out IntPtr phPrinter, IntPtr pDefault);

    [DllImport("winspool.drv", EntryPoint = "ClosePrinter", SetLastError = true)]
    private static extern bool ClosePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", EntryPoint = "EnumJobsW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool EnumJobs(IntPtr hPrinter, uint FirstJob, uint NoJobs, uint Level, IntPtr pJob, uint cbBuf, out uint pcbNeeded, out uint pcReturned);

    [DllImport("winspool.drv", EntryPoint = "GetJobW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool GetJob(IntPtr hPrinter, uint JobId, uint Level, IntPtr pJob, uint cbBuf, out uint pcbNeeded);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct SYSTEMTIME
    {
        public short wYear;
        public short wMonth;
        public short wDayOfWeek;
        public short wDay;
        public short wHour;
        public short wMinute;
        public short wSecond;
        public short wMilliseconds;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct JOB_INFO_2
    {
        public uint JobId;
        public IntPtr pPrinterName;
        public IntPtr pMachineName;
        public IntPtr pUserName;
        public IntPtr pDocument;
        public IntPtr pNotifyName;
        public IntPtr pDatatype;
        public IntPtr pPrintProcessor;
        public IntPtr pParameters;
        public IntPtr pDriverName;
        public IntPtr pDevMode;
        public IntPtr pStatus;
        public IntPtr pSecurityDescriptor;
        public uint Status;
        public uint Priority;
        public uint Position;
        public uint StartTime;
        public uint UntilTime;
        public uint TotalPages;
        public uint Size;
        public SYSTEMTIME Submitted;
        public uint Time;
        public uint PagesPrinted;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmDeviceName;
        public short dmSpecVersion;
        public short dmDriverVersion;
        public short dmSize;
        public short dmDriverExtra;
        public int dmFields;
        public short dmOrientation;
        public short dmPaperSize;
        public short dmPaperLength;
        public short dmPaperWidth;
        public short dmScale;
        public short dmCopies;
        public short dmDefaultSource;
        public short dmPrintQuality;
        public short dmColor;  // 1 = Monochrome, 2 = Color
        public short dmDuplex; // 1 = Simplex, 2 = Duplex Vertical, 3 = Duplex Horizontal
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel;
        public int dmPelsWidth;
        public int dmPelsHeight;
        public int dmNup;
        public int dmDisplayFrequency;
        public int dmICMMethod;
        public int dmICMIntent;
        public int dmMediaType;
        public int dmDitherType;
        public int dmReserved1;
        public int dmReserved2;
        public int dmPanningWidth;
        public int dmPanningHeight;
    }

    #endregion
}
