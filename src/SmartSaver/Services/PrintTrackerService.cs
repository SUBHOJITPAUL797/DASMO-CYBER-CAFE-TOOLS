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
    private static Lazy<PrintTrackerService> _instance = new(() => new PrintTrackerService());
    public static PrintTrackerService Instance => _instance.Value;

    public static string? CustomDataDirectory { get; set; }

    public static void ResetForTesting(string? testDir = null)
    {
        CustomDataDirectory = testDir;
        _instance = new Lazy<PrintTrackerService>(() => new PrintTrackerService(testDir));
    }

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

    private PrintTrackerService(string? customDir = null)
    {
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

        _settingsFilePath = Path.Combine(dataDir, "print_billing_settings.json");
        _historyFilePath = Path.Combine(dataDir, "print_job_history.json");
        _billsFilePath = Path.Combine(dataDir, "customer_bill_history.json");

        LoadSettings();
        LoadHistory();
        PurgeTestArtifacts();
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
    /// Scans all local print queues using Win32 EnumJobs — the sole authoritative path.
    /// The old System.Printing fallback has been removed: it was registering the same job
    /// a second time because dedup keyed on (pages+docName) which change mid-spool.
    /// </summary>
    public void PollPrintQueues()
    {
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

        // Prune old seen-job entries (>20 minutes) to prevent memory growth in long sessions
        var cutoff = DateTimeOffset.Now.AddMinutes(-20);
        if (_seenJobs.Count > 100)
        {
            foreach (var kvp in _seenJobs)
            {
                if (kvp.Value < cutoff)
                    _seenJobs.TryRemove(kvp.Key, out _);
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
            EnumJobs(hPrinter, 0, 100, 2, IntPtr.Zero, 0, out bytesNeeded, out jobsReturned);

            if (bytesNeeded == 0) return;

            IntPtr pBuf = Marshal.AllocHGlobal((int)bytesNeeded);
            try
            {
                if (!EnumJobs(hPrinter, 0, 100, 2, pBuf, bytesNeeded, out bytesNeeded, out jobsReturned) || jobsReturned == 0)
                    return;

                int structSize = Marshal.SizeOf<JOB_INFO_2>();
                for (int i = 0; i < jobsReturned; i++)
                {
                    IntPtr jobPtr = IntPtr.Add(pBuf, i * structSize);
                    var jobInfo = Marshal.PtrToStructure<JOB_INFO_2>(jobPtr);

                    // ── GATE 1: Only capture fully-spooled jobs ──────────────────────────────
                    // JOB_STATUS_SPOOLING = 0x0004, JOB_STATUS_DELETING = 0x0004 overlap check:
                    // We want at least TotalPages > 0 meaning the spooler has counted the pages.
                    // Jobs with TotalPages==0 are still streaming in — skip them; we'll catch them
                    // on the next 500ms poll cycle once TotalPages is finalized.
                    int pages = (int)jobInfo.TotalPages;
                    if (pages <= 0)
                    {
                        Log.Debug("Skipping partially-spooled job {JobId} on {Printer} (TotalPages=0)", jobInfo.JobId, printerName);
                        continue;
                    }

                    // ── GATE 2: Deduplicate by JobId only ───────────────────────────────────
                    // Do NOT include pages or docName in the key — they can change between polls
                    // as the spooler writes more data, causing the same physical print job to be
                    // registered as two different entries with different page counts / costs.
                    string dedupKey = $"{printerName}:{jobInfo.JobId}";
                    if (_seenJobs.ContainsKey(dedupKey)) continue;
                    _seenJobs.TryAdd(dedupKey, DateTimeOffset.Now);

                    // ── GATE 3: Parse DEVMODE — authoritative duplex + color flags ──────────
                    bool isDuplex = false;
                    bool isColor  = false;
                    int  copies   = 1;

                    string docName = Marshal.PtrToStringAuto(jobInfo.pDocument) ?? "Print Document";

                    if (jobInfo.pDevMode != IntPtr.Zero)
                    {
                        try
                        {
                            var devMode = Marshal.PtrToStructure<DEVMODE>(jobInfo.pDevMode);

                            // dmDuplex: 1=Simplex, 2=Duplex Long-Edge (portrait), 3=Duplex Short-Edge (landscape)
                            isDuplex = devMode.dmDuplex is 2 or 3;

                            // ── COLOR DETECTION (industry-grade) ────────────────────────────
                            // dmColor=2 means the printer driver *supports* color, NOT that this
                            // job is color. Brother DCP-T530DW always sends dmColor=2 even for
                            // plain black text documents printed from Word, Chrome, or Adobe.
                            //
                            // The CORRECT flag is dmICMIntent:
                            //   0 = Not specified / driver default (treat as B&W for billing)
                            //   1 = Saturate        ← Color (ICC color management)
                            //   2 = RelativeColorimetric ← Color
                            //   3 = Perceptual      ← Color
                            //   4 = AbsoluteColorimetric ← Color
                            //
                            // Additionally, if dmColor==1 the driver explicitly forces monochrome.
                            // This overrides any ICM intent.
                            if (devMode.dmColor == 1)
                            {
                                // Driver explicitly forced monochrome — definitely B&W
                                isColor = false;
                            }
                            else if (devMode.dmICMIntent >= 1 && devMode.dmICMIntent <= 4)
                            {
                                // ICM is active → color job
                                isColor = true;
                            }
                            else
                            {
                                // dmColor==2 (color-capable hardware) but no ICM intent set.
                                // This is the "false color" Brother scenario.
                                // Default to B&W — the user printed a B&W document.
                                isColor = false;
                            }

                            if (devMode.dmCopies > 1) copies = devMode.dmCopies;
                        }
                        catch (Exception ex)
                        {
                            Log.Debug(ex, "DEVMODE parse failed for job {JobId} on {Printer}", jobInfo.JobId, printerName);
                        }
                    }

                    Log.Information(
                        "Win32 print job captured: [{Printer}] JobId={JobId} Doc='{Doc}' Pages={Pages} Duplex={Duplex} Color={Color} Copies={Copies}",
                        printerName, jobInfo.JobId, CleanDocumentName(docName), pages, isDuplex, isColor, copies);

                    var record = new PrintJobRecord
                    {
                        SpoolerJobId = jobInfo.JobId,
                        DocumentName = CleanDocumentName(docName),
                        PrinterName  = printerName,
                        Submitter    = Marshal.PtrToStringAuto(jobInfo.pUserName) ?? Environment.UserName,
                        Timestamp    = DateTimeOffset.Now,
                        Pages        = pages,
                        Copies       = copies,
                        IsDuplex     = isDuplex,
                        IsColor      = isColor,
                        PaperSize    = "A4"
                    };

                    CalculateCost(record);
                    RegisterNewJob(record);
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
                        // Use the same industry-grade color detection as ScanPrinterWithWin32
                        bool isColor;
                        if (devMode.dmColor == 1)
                            isColor = false; // driver explicitly forced monochrome
                        else if (devMode.dmICMIntent >= 1 && devMode.dmICMIntent <= 4)
                            isColor = true;  // ICC color management active → color job
                        else
                            isColor = false; // hardware color-capable but no ICM → B&W
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
            Views.PrintAlertPopup.ShowAlert(job);
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

            string todayPrefix = $"BILL-{DateTime.Now:yyyyMMdd}-";
            int maxSeq = 0;
            foreach (var b in CompletedBillSessions)
            {
                if (b.BillNumber != null && b.BillNumber.StartsWith(todayPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    string seqStr = b.BillNumber.Substring(todayPrefix.Length);
                    if (int.TryParse(seqStr, out int s) && s > maxSeq)
                    {
                        maxSeq = s;
                    }
                }
            }
            string billNo = $"{todayPrefix}{maxSeq + 1:D3}";
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

        // ── Auto-Integrate into Cash Drawer & Daily Finance Register ──
        try
        {
            var isDue = session.PaymentMode.Contains("Due", StringComparison.OrdinalIgnoreCase) ||
                        session.PaymentMode.Contains("Credit", StringComparison.OrdinalIgnoreCase) ||
                        session.PaymentMode.Contains("Borrow", StringComparison.OrdinalIgnoreCase) ||
                        session.PaymentMode.Contains("Account", StringComparison.OrdinalIgnoreCase);

            var isUpi = session.PaymentMode.Contains("UPI", StringComparison.OrdinalIgnoreCase) ||
                        session.PaymentMode.Contains("QR", StringComparison.OrdinalIgnoreCase) ||
                        session.PaymentMode.Contains("Online", StringComparison.OrdinalIgnoreCase) ||
                        session.PaymentMode.Contains("Card", StringComparison.OrdinalIgnoreCase);

            if (isDue)
            {
                CashDrawerService.Instance.RecordCustomerBorrow(
                    session.TotalAmount,
                    session.CustomerName,
                    session.CustomerPhone,
                    $"Bill #{session.BillNumber} ({session.TotalPages}p / {session.TotalSheets}s) - Due / Khata",
                    linkedBillNumber: session.BillNumber
                );
            }
            else
            {
                var medium = isUpi ? PaymentMedium.OnlineUPI : PaymentMedium.CashInDrawer;

                CashDrawerService.Instance.AddTransaction(
                    TransactionDirection.Income,
                    medium,
                    CashCategory.PrintSales,
                    session.TotalAmount,
                    $"Bill #{session.BillNumber} ({session.TotalPages}p / {session.TotalSheets}s) - {session.CustomerName}",
                    session.CustomerName,
                    session.CustomerPhone,
                    commission: 0,
                    linkedBillNumber: session.BillNumber
                );
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to auto-record bill #{BillNo} into Cash Drawer", session.BillNumber);
        }

        Log.Information("Customer Bill finalized: {BillNo} - {Name} -> {Items} items, ₹{Total:F2}",
            session.BillNumber, session.CustomerName, session.Jobs.Count, session.TotalAmount);

        OnActiveCartChanged?.Invoke();
        OnHistoryUpdated?.Invoke();
        TriggerExcelAutoSync();

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

    /// <summary>
    /// Permanently deletes a completed customer bill and its associated print jobs from history.
    /// Also synchronizes with Cash Drawer & Accounts across all days to remove the corresponding financial transaction.
    /// </summary>
    public bool DeleteBill(string sessionId)
    {
        string? deletedBillNo = null;
        lock (_lock)
        {
            var bill = CompletedBillSessions.FirstOrDefault(b => b.SessionId == sessionId);
            if (bill == null) return false;

            deletedBillNo = bill.BillNumber;
            CompletedBillSessions.Remove(bill);
            AllJobHistory.RemoveAll(j => j.BillSessionId == sessionId);

            SaveBills();
            SaveHistory();
        }

        // Also remove from Cash Drawer transactions across all registers if found
        if (!string.IsNullOrEmpty(deletedBillNo))
        {
            try
            {
                var matchingTxs = CashDrawerService.Instance.AllDays
                    .SelectMany(r => r.Transactions)
                    .Where(t => (!string.IsNullOrEmpty(t.LinkedBillNumber) && t.LinkedBillNumber == deletedBillNo) ||
                                 t.Description.Contains(deletedBillNo))
                    .ToList();

                foreach (var tx in matchingTxs)
                {
                    CashDrawerService.Instance.DeleteTransaction(tx.Id);
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to auto-remove matching transaction for bill {BillNo} from Cash Drawer", deletedBillNo);
            }
        }

        Log.Information("Customer bill {SessionId} permanently deleted", sessionId);
        OnHistoryUpdated?.Invoke();
        TriggerExcelAutoSync();
        return true;
    }

    /// <summary>
    /// Automatically synchronizes all sales and cash drawer records to the user's attached Excel spreadsheet.
    /// Runs asynchronously in the background so the UI is never blocked.
    /// </summary>
    public void TriggerExcelAutoSync()
    {
        if (Settings.AutoSyncToExcel && !string.IsNullOrWhiteSpace(Settings.AttachedExcelPath))
        {
            Task.Run(() =>
            {
                try
                {
                    var bills = CompletedBillSessions.ToList();
                    var registers = CashDrawerService.Instance.AllDays.ToList();
                    BillExcelExporter.AutoSyncAttachedExcel(Settings, bills, registers);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Background auto-sync to Excel failed");
                }
            });
        }
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
                if (!string.IsNullOrWhiteSpace(json))
                {
                    List<CustomerBillSession>? bills = null;
                    if (json.TrimStart().StartsWith("["))
                    {
                        bills = JsonSerializer.Deserialize<List<CustomerBillSession>>(json);
                    }
                    else if (json.TrimStart().StartsWith("{"))
                    {
                        var single = JsonSerializer.Deserialize<CustomerBillSession>(json);
                        if (single != null) bills = new List<CustomerBillSession> { single };
                    }

                    if (bills != null)
                    {
                        CompletedBillSessions.Clear();
                        CompletedBillSessions.AddRange(bills);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to load print history");
        }
    }

    private void PurgeTestArtifacts()
    {
        lock (_lock)
        {
            try
            {
                var testCustomerNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "Rajesh Sharma", "Pooja Sharma", "Rahul Das", "Rahul Sen", "Amit Kumar",
                    "Cust A", "Cust B", "Cust C", "Arun Roy", "Delete Test Cust",
                    "Rush Customer", "Walk-in Xerox", "Subhojit Paul", "Bikram"
                };

                int billsBefore = CompletedBillSessions.Count;
                CompletedBillSessions.RemoveAll(b =>
                    testCustomerNames.Contains(b.CustomerName) ||
                    b.Notes.Contains("College project prints", StringComparison.OrdinalIgnoreCase) ||
                    b.Notes.Contains("College form", StringComparison.OrdinalIgnoreCase) ||
                    b.Notes.Contains("Urgent admit card prints", StringComparison.OrdinalIgnoreCase));

                int jobsBefore = AllJobHistory.Count;
                AllJobHistory.RemoveAll(j =>
                    testCustomerNames.Contains(j.CustomerName) ||
                    j.DocumentName.Contains("Bill Seq Test", StringComparison.OrdinalIgnoreCase) ||
                    j.DocumentName.Contains("College Project Xerox Due", StringComparison.OrdinalIgnoreCase) ||
                    j.DocumentName.Contains("Online Form Print", StringComparison.OrdinalIgnoreCase) ||
                    j.DocumentName.Contains("Quick B&W Xerox × 5", StringComparison.OrdinalIgnoreCase) ||
                    j.DocumentName.Contains("Physical Xerox Meter Audit Reconciliation", StringComparison.OrdinalIgnoreCase) ||
                    j.DocumentName.Contains("Exam_Admit_Card", StringComparison.OrdinalIgnoreCase) ||
                    j.DocumentName.Contains("Photo_ID_Card", StringComparison.OrdinalIgnoreCase) ||
                    j.DocumentName.Contains("Test Print For Delete", StringComparison.OrdinalIgnoreCase));

                if (CompletedBillSessions.Count != billsBefore)
                {
                    SaveBills();
                    Log.Information("Purged test bills from completed bill history");
                }

                if (AllJobHistory.Count != jobsBefore)
                {
                    SaveHistory();
                    Log.Information("Purged test jobs from print job history");
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to purge test artifacts from print tracker");
            }
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
