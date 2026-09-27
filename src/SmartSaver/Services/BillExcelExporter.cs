using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Draw = DocumentFormat.OpenXml.Drawing;
using DrawCharts = DocumentFormat.OpenXml.Drawing.Charts;
using DrawSpreadsheet = DocumentFormat.OpenXml.Drawing.Spreadsheet;
using SmartSaver.Models;
using Serilog;

namespace SmartSaver.Services;

/// <summary>
/// Executive-Grade, Professional Financial Excel (.xlsx) Export & Synchronization Engine.
/// Generates pristine, boardroom-ready, 4-tab workbooks with:
/// - Tab 1: Daily Cash Drawer & Financial Journal (Executive KPI Cards, Till & UPI Reconciliation, Executive Charts)
/// - Tab 2: Customer Billing Summary (Invoicing, Impressions, Revenue Ledger)
/// - Tab 3: Dedicated Print & Xerox History (Complete Audit Trail of Spooler and Physical Walk-up Copies)
/// - Tab 4: Financial KPIs & Ledger (Historical Multi-Day Cash Floats, Profits & Debt)
/// Features custom column widths, executive dark navy title headers, color-coded KPI cards,
/// alternating zebra row striping, accounting-standard double-underline totals, and invariant currency formatting.
/// </summary>
public static class BillExcelExporter
{
    private static readonly object _syncLock = new();

    public static void ExportToExcel(
        IEnumerable<CustomerBillSession> bills,
        PrintBillingSettings settings,
        string outputPath,
        IEnumerable<PrintJobRecord>? jobHistory = null)
    {
        ExportFullFinanceWorkbook(bills, CashDrawerService.Instance.AllDays, settings, outputPath, jobHistory);
    }

    public static void ExportFullFinanceWorkbook(
        IEnumerable<CustomerBillSession> bills,
        IEnumerable<DailyCashRegister> registers,
        PrintBillingSettings settings,
        string outputPath,
        IEnumerable<PrintJobRecord>? jobHistory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        if (File.Exists(outputPath))
        {
            try { File.Delete(outputPath); } catch { }
        }

        using var document = SpreadsheetDocument.Create(outputPath, SpreadsheetDocumentType.Workbook);

        var workbookPart = document.AddWorkbookPart();
        workbookPart.Workbook = new Workbook();

        // Setup styles (fonts, alignments, borders, number formats)
        var stylesPart = workbookPart.AddNewPart<WorkbookStylesPart>();
        stylesPart.Stylesheet = CreateStylesheet();
        stylesPart.Stylesheet.Save();

        var sheets = workbookPart.Workbook.AppendChild(new Sheets());

        var billList = bills.OrderBy(b => b.BilledAt).ToList();
        var regList = registers.OrderByDescending(r => r.Date).ToList();
        var todayReg = regList.FirstOrDefault(r => r.DateKey == DateTime.Today.ToString("yyyy-MM-dd"))
                       ?? regList.FirstOrDefault()
                       ?? CashDrawerService.Instance.Today;

        uint sheetId = 1;

        // ── Tab 1: Daily Cash Drawer & Accounts Journal ───────────────────────
        var cashPart = workbookPart.AddNewPart<WorksheetPart>();
        var cashData = new SheetData();
        var cashMerge = new MergeCells();
        var cashCols = BuildCashDrawerSheet(cashData, cashMerge, todayReg, settings);
        var cashWs = new Worksheet();
        cashWs.AppendChild(cashCols);
        cashWs.AppendChild(cashData);
        if (cashMerge.ChildElements.Count > 0)
        {
            cashMerge.Count = (uint)cashMerge.ChildElements.Count;
            cashWs.AppendChild(cashMerge);
        }

        // Attach Executive OpenXml Charts (Donut % Chart & Clustered Column Chart)
        var drawingsPart = cashPart.AddNewPart<DrawingsPart>();
        AddExecutiveCharts(cashPart, drawingsPart, todayReg);
        cashWs.AppendChild(new DocumentFormat.OpenXml.Spreadsheet.Drawing { Id = cashPart.GetIdOfPart(drawingsPart) });

        cashPart.Worksheet = cashWs;
        cashPart.Worksheet.Save();
        sheets.Append(new Sheet
        {
            Id = workbookPart.GetIdOfPart(cashPart),
            SheetId = sheetId++,
            Name = "Cash Drawer & Accounts"
        });

        // ── Tab 2: Billing Summary ──────────────────────────────────────────
        var summaryPart = workbookPart.AddNewPart<WorksheetPart>();
        var summaryData = new SheetData();
        var summaryMerge = new MergeCells();
        var summaryCols = BuildSummarySheet(summaryData, summaryMerge, billList, settings);
        var summaryWs = new Worksheet();
        summaryWs.AppendChild(summaryCols);
        summaryWs.AppendChild(summaryData);
        if (summaryMerge.ChildElements.Count > 0)
        {
            summaryMerge.Count = (uint)summaryMerge.ChildElements.Count;
            summaryWs.AppendChild(summaryMerge);
        }
        summaryPart.Worksheet = summaryWs;
        summaryPart.Worksheet.Save();
        sheets.Append(new Sheet
        {
            Id = workbookPart.GetIdOfPart(summaryPart),
            SheetId = sheetId++,
            Name = "Billing Summary"
        });

        // ── Tab 3: Dedicated Print & Xerox History ─────────────────────────
        var registerPart = workbookPart.AddNewPart<WorksheetPart>();
        var registerData = new SheetData();
        var registerMerge = new MergeCells();
        var allJobs = (jobHistory ?? PrintTrackerService.Instance.AllJobHistory).ToList();
        var registerCols = BuildRegisterSheet(registerData, registerMerge, billList, allJobs, settings);
        var registerWs = new Worksheet();
        registerWs.AppendChild(registerCols);
        registerWs.AppendChild(registerData);
        if (registerMerge.ChildElements.Count > 0)
        {
            registerMerge.Count = (uint)registerMerge.ChildElements.Count;
            registerWs.AppendChild(registerMerge);
        }
        registerPart.Worksheet = registerWs;
        registerPart.Worksheet.Save();
        sheets.Append(new Sheet
        {
            Id = workbookPart.GetIdOfPart(registerPart),
            SheetId = sheetId++,
            Name = "Print & Xerox History"
        });

        // ── Tab 4: Financial KPIs & Ledger ──────────────────────────────────
        var kpiPart = workbookPart.AddNewPart<WorksheetPart>();
        var kpiData = new SheetData();
        var kpiMerge = new MergeCells();
        var kpiCols = BuildKpiSheet(kpiData, kpiMerge, regList, settings);
        var kpiWs = new Worksheet();
        kpiWs.AppendChild(kpiCols);
        kpiWs.AppendChild(kpiData);
        if (kpiMerge.ChildElements.Count > 0)
        {
            kpiMerge.Count = (uint)kpiMerge.ChildElements.Count;
            kpiWs.AppendChild(kpiMerge);
        }
        kpiPart.Worksheet = kpiWs;
        kpiPart.Worksheet.Save();
        sheets.Append(new Sheet
        {
            Id = workbookPart.GetIdOfPart(kpiPart),
            SheetId = sheetId++,
            Name = "Daily Finance KPIs"
        });

        workbookPart.Workbook.Save();
        Log.Information("Full Finance Excel report exported successfully to {Path}", outputPath);
    }

    public static bool AutoSyncAttachedExcel(
        PrintBillingSettings settings,
        IEnumerable<CustomerBillSession> bills,
        IEnumerable<DailyCashRegister> registers,
        IEnumerable<PrintJobRecord>? jobHistory = null)
    {
        if (!settings.AutoSyncToExcel || string.IsNullOrWhiteSpace(settings.AttachedExcelPath))
            return false;

        lock (_syncLock)
        {
            string targetPath = settings.AttachedExcelPath;
            string? tempFile = null;
            try
            {
                string? dir = Path.GetDirectoryName(targetPath);
                if (string.IsNullOrEmpty(dir))
                    dir = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

                tempFile = Path.Combine(dir, $".sync_{Guid.NewGuid():N}.xlsx");
                ExportFullFinanceWorkbook(bills, registers, settings, tempFile, jobHistory);

                if (File.Exists(tempFile))
                {
                    File.Move(tempFile, targetPath, overwrite: true);
                    Log.Debug("Auto-Synced full financial ledger to attached Excel: {Path}", targetPath);
                    return true;
                }
            }
            catch (IOException ioEx)
            {
                Log.Warning("Attached Excel file is open in Excel or locked: {Msg}", ioEx.Message);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to auto-sync to attached Excel: {Path}", targetPath);
            }
            finally
            {
                if (tempFile != null && File.Exists(tempFile))
                {
                    try { File.Delete(tempFile); } catch { }
                }
            }
            return false;
        }
    }

    // ── Tab 1: Cash Drawer & Accounts ───────────────────────────────────────
    private static Columns BuildCashDrawerSheet(
        SheetData data,
        MergeCells mergeCells,
        DailyCashRegister todayReg,
        PrintBillingSettings settings)
    {
        var cols = CreateColumns(
            (1, 1, 14.0),   // A: Time
            (2, 2, 28.0),   // B: Category
            (3, 3, 20.0),   // C: Payment Medium
            (4, 4, 15.0),   // D: Direction
            (5, 5, 17.0),   // E: Amount
            (6, 6, 17.0),   // F: Fee / Comm
            (7, 7, 24.0),   // G: Customer
            (8, 8, 17.0),   // H: Mobile No
            (9, 9, 36.0),   // I: Description / Notes
            (10, 10, 18.0), // J: Debt Status
            (11, 11, 4.0),  // K: Spacer
            (12, 19, 13.0), // L-S: Visual Chart Deck Canvas Area
            (20, 20, 4.0),  // T: Spacer
            (21, 21, 22.0), // U: Chart Metric Categories
            (22, 22, 18.0)  // V: Chart Metric Values (₹)
        );

        uint r = 1;

        // Row 1: Merged Title Banner
        data.Append(CreateRow(r++, CreateMergedBannerCells($"{settings.ShopName.ToUpperInvariant()} — DAILY CASH DRAWER & FINANCIAL JOURNAL", 15, 10), 32.0));
        mergeCells.Append(new MergeCell { Reference = "A1:J1" });

        // Row 2: Merged Subtitle + Chart Category Header
        var row2Cells = CreateMergedBannerCells($"Live Cyber Cafe Accounts Ledger  •  Current Date: {DateTime.Now:dd/MM/yyyy hh:mm tt}", 16, 10).ToList();
        row2Cells.Add(CellText("PAYMENT MODE", 1, "U2"));
        row2Cells.Add(CellText("AMOUNT (₹)", 2, "V2"));
        data.Append(CreateRow(r++, row2Cells, 20.0));
        mergeCells.Append(new MergeCell { Reference = "A2:J2" });

        double totalAllTimeDebt = CashDrawerService.Instance.AllDays.Sum(d =>
        {
            lock (d.Transactions)
            {
                return d.Transactions.Where(t => t.Category == CashCategory.CustomerBorrowCredit && !t.IsCleared).Sum(t => t.Amount);
            }
        });

        // Row 3: KPI Card Labels + Chart Data 1 (Cash in Drawer)
        data.Append(CreateRow(r++, new[]
        {
            CellText("💵 CASH IN DRAWER", 17), CellEmpty(17),
            CellText("📱 ONLINE / BANK / UPI", 19), CellEmpty(19),
            CellText("📈 TODAY NET PROFIT", 21), CellEmpty(21),
            CellText("⭐ TOTAL REVENUE", 23), CellEmpty(23),
            CellText("⏳ UNPAID DUE (KHATA)", 25), CellEmpty(25),
            CellText("Cash in Drawer", 4, "U3"),
            CellMoney(todayReg.CurrentCashInDrawer, 8, "V3")
        }, 20.0));
        mergeCells.Append(new MergeCell { Reference = "A3:B3" });
        mergeCells.Append(new MergeCell { Reference = "C3:D3" });
        mergeCells.Append(new MergeCell { Reference = "E3:F3" });
        mergeCells.Append(new MergeCell { Reference = "G3:H3" });
        mergeCells.Append(new MergeCell { Reference = "I3:J3" });

        // Row 4: KPI Card Values + Chart Data 2 (Online UPI / Bank)
        data.Append(CreateRow(r++, new[]
        {
            CellText($"₹ {todayReg.CurrentCashInDrawer:N2}", 18), CellEmpty(18),
            CellText($"₹ {todayReg.CurrentOnlineBalance:N2}", 20), CellEmpty(20),
            CellText($"₹ {todayReg.TodayNetProfit:N2}", 22), CellEmpty(22),
            CellText($"₹ {todayReg.TodayTotalRevenue:N2}", 24), CellEmpty(24),
            CellText($"₹ {totalAllTimeDebt:N2}", 26), CellEmpty(26),
            CellText("Online UPI / Bank", 4, "U4"),
            CellMoney(todayReg.CurrentOnlineBalance, 8, "V4")
        }, 26.0));
        mergeCells.Append(new MergeCell { Reference = "A4:B4" });
        mergeCells.Append(new MergeCell { Reference = "C4:D4" });
        mergeCells.Append(new MergeCell { Reference = "E4:F4" });
        mergeCells.Append(new MergeCell { Reference = "G4:H4" });
        mergeCells.Append(new MergeCell { Reference = "I4:J4" });

        // Row 5: Spacer + Chart 2 Header
        data.Append(CreateRow(r++, new[]
        {
            CellEmpty(), CellEmpty(), CellEmpty(), CellEmpty(),
            CellEmpty(), CellEmpty(), CellEmpty(), CellEmpty(),
            CellEmpty(), CellEmpty(),
            CellText("FINANCIAL METRIC", 1, "U5"),
            CellText("AMOUNT (₹)", 2, "V5")
        }, 10.0));

        // Row 6: Table Header + Chart Data 3 (Total Revenue)
        data.Append(CreateRow(r++, new[]
        {
            CellText("Time", 1),
            CellText("Category", 3),
            CellText("Payment Medium", 1),
            CellText("Direction", 1),
            CellText("Amount (₹)", 2),
            CellText("Fee / Comm (₹)", 2),
            CellText("Customer / Person", 3),
            CellText("Mobile No", 1),
            CellText("Description / Notes", 3),
            CellText("Debt Status", 1),
            CellText("Total Revenue", 4, "U6"),
            CellMoney(todayReg.TodayTotalRevenue, 8, "V6")
        }, 26.0));

        double totalIn = 0;
        double totalOut = 0;

        List<CashTransaction> txList;
        lock (todayReg.Transactions)
        {
            txList = todayReg.Transactions.ToList();
        }

        bool zebra = false;
        int txIndex = 0;
        foreach (var tx in txList)
        {
            if (tx.Direction == TransactionDirection.Income) totalIn += tx.Amount;
            else if (tx.Direction == TransactionDirection.Expense) totalOut += tx.Amount;

            string catName = tx.Category switch
            {
                CashCategory.CustomerUpiCashPayout => "Customer UPI -> Cash Given",
                CashCategory.CustomerCashBankDeposit => "Customer Cash -> Online Paid",
                CashCategory.CustomerBorrowCredit => "Customer Borrow / Credit (Due)",
                CashCategory.CustomerDebtRepaid => "Debt Cleared / Repaid",
                CashCategory.PrintSales => "Print & Xerox Sales",
                CashCategory.XeroxPhotocopy => "Xerox / Photocopy",
                CashCategory.OnlineFormFillup => "Online Form Fillup",
                CashCategory.LaminationPhotos => "Lamination & Photos",
                CashCategory.OwnerInvestment => "Owner Capital Added",
                CashCategory.OwnerWithdrawal => "Owner Withdrawal",
                CashCategory.ShopExpensePaperInk => "Paper / Ink Expense",
                CashCategory.ShopExpenseBillsRent => "Bills / Rent / Electricity",
                _ => "Other Counter Entry"
            };

            string medName = tx.Medium == PaymentMedium.CashInDrawer ? "Cash Drawer" : "Online UPI / Bank";
            string dirName = tx.Direction.ToString();
            string debtStatus = tx.Category == CashCategory.CustomerBorrowCredit
                ? (tx.IsCleared ? "Cleared" : "UNPAID DUE")
                : "—";

            uint statusStyle = tx.Category == CashCategory.CustomerBorrowCredit
                ? (tx.IsCleared ? 27u : 28u)
                : (zebra ? 7u : 6u);

            uint textLeft = zebra ? 5u : 4u;
            uint textCenter = zebra ? 7u : 6u;
            uint moneyRight = zebra ? 9u : 8u;

            var cells = new List<Cell>
            {
                CellText(tx.Timestamp.ToString("hh:mm tt"), textCenter),
                CellText(catName, textLeft),
                CellText(medName, textCenter),
                CellText(dirName, textCenter),
                CellMoney(tx.Amount, moneyRight),
                CellMoney(tx.CommissionFee, moneyRight),
                CellText(tx.CustomerName, textLeft),
                CellText(tx.CustomerPhone, textCenter),
                CellText(tx.Description, textLeft),
                CellText(debtStatus, statusStyle)
            };

            if (txIndex == 0)
            {
                cells.Add(CellText("Total Expenses", 4, "U7"));
                cells.Add(CellMoney(todayReg.TodayTotalExpenses, 8, "V7"));
            }
            else if (txIndex == 1)
            {
                cells.Add(CellText("Net Profit", 4, "U8"));
                cells.Add(CellMoney(todayReg.TodayNetProfit, 8, "V8"));
            }

            data.Append(CreateRow(r++, cells, 22.0));
            zebra = !zebra;
            txIndex++;
        }

        // Ensure rows 7 and 8 exist for Chart Metric reference
        if (txIndex == 0)
        {
            data.Append(CreateRow(r++, new[]
            {
                CellEmpty(), CellEmpty(), CellEmpty(), CellEmpty(),
                CellEmpty(), CellEmpty(), CellEmpty(), CellEmpty(),
                CellEmpty(), CellEmpty(),
                CellText("Total Expenses", 4, "U7"),
                CellMoney(todayReg.TodayTotalExpenses, 8, "V7")
            }, 22.0));

            data.Append(CreateRow(r++, new[]
            {
                CellEmpty(), CellEmpty(), CellEmpty(), CellEmpty(),
                CellEmpty(), CellEmpty(), CellEmpty(), CellEmpty(),
                CellEmpty(), CellEmpty(),
                CellText("Net Profit", 4, "U8"),
                CellMoney(todayReg.TodayNetProfit, 8, "V8")
            }, 22.0));
        }
        else if (txIndex == 1)
        {
            data.Append(CreateRow(r++, new[]
            {
                CellEmpty(), CellEmpty(), CellEmpty(), CellEmpty(),
                CellEmpty(), CellEmpty(), CellEmpty(), CellEmpty(),
                CellEmpty(), CellEmpty(),
                CellText("Net Profit", 4, "U8"),
                CellMoney(todayReg.TodayNetProfit, 8, "V8")
            }, 22.0));
        }

        double totalTurnover = txList.Sum(t => t.Amount);
        double totalComm = txList.Sum(t => t.CommissionFee);

        // Totals Row
        data.Append(CreateRow(r++, new[]
        {
            CellText("TOTALS", 12),
            CellText($"{txList.Count} Total Entries", 12),
            CellText($"Revenue: ₹ {todayReg.TodayTotalRevenue:N2}", 12),
            CellText("Turnover / Comm:", 12),
            CellMoney(totalTurnover, 13),
            CellMoney(totalComm, 13),
            CellText($"Expenses: ₹ {todayReg.TodayTotalExpenses:N2}", 12),
            CellText($"Due: ₹ {totalAllTimeDebt:N2}", 12),
            CellText($"Cash: ₹ {todayReg.CurrentCashInDrawer:N2} | Bank: ₹ {todayReg.CurrentOnlineBalance:N2}", 12),
            CellText(totalAllTimeDebt > 0 ? "UNPAID DUE" : "BALANCED", 12)
        }, 26.0));

        return cols;
    }

    // ── Tab 2: Billing Summary ──────────────────────────────────────────────
    private static Columns BuildSummarySheet(
        SheetData data,
        MergeCells mergeCells,
        List<CustomerBillSession> bills,
        PrintBillingSettings settings)
    {
        var cols = CreateColumns(
            (1, 1, 22.0),  // A: Bill No
            (2, 2, 22.0),  // B: Date & Time
            (3, 3, 24.0),  // C: Customer Name
            (4, 4, 18.0),  // D: Mobile No
            (5, 5, 14.0),  // E: Total Pages
            (6, 6, 14.0),  // F: Total Sheets
            (7, 7, 18.0),  // G: Payment Mode
            (8, 8, 18.0),  // H: Total Amount (₹)
            (9, 9, 34.0)   // I: Notes / Remarks
        );

        uint r = 1;

        // Row 1: Merged Title
        data.Append(CreateRow(r++, CreateMergedBannerCells($"{settings.ShopName.ToUpperInvariant()} — CUSTOMER BILLING & SALES SUMMARY", 15, 9), 32.0));
        mergeCells.Append(new MergeCell { Reference = "A1:I1" });

        // Row 2: Merged Subtitle
        data.Append(CreateRow(r++, CreateMergedBannerCells($"Shop: {settings.ShopAddress}   |   Phone: {settings.ShopPhone}   |   Customer Invoicing Ledger", 16, 9), 20.0));
        mergeCells.Append(new MergeCell { Reference = "A2:I2" });

        // Row 3: Spacer
        data.Append(CreateRow(r++, Array.Empty<Cell>(), 10.0));

        // Row 4: Header
        data.Append(CreateRow(r++, new[]
        {
            CellText("Bill Number", 1),
            CellText("Date & Time", 1),
            CellText("Customer Name", 3),
            CellText("Mobile Number", 1),
            CellText("Total Pages", 2),
            CellText("Total Sheets", 2),
            CellText("Payment Mode", 1),
            CellText("Total Amount (₹)", 2),
            CellText("Notes / Remarks", 3)
        }, 26.0));

        int totalPages = 0;
        int totalSheets = 0;
        double totalRevenue = 0;

        bool zebra = false;
        foreach (var b in bills)
        {
            totalPages += b.TotalPages;
            totalSheets += b.TotalSheets;
            totalRevenue += b.TotalAmount;

            uint textLeft = zebra ? 5u : 4u;
            uint textCenter = zebra ? 7u : 6u;
            uint moneyRight = zebra ? 9u : 8u;
            uint numRight = zebra ? 11u : 10u;

            data.Append(CreateRow(r++, new[]
            {
                CellText(b.BillNumber, textCenter),
                CellText(b.BilledAt.ToString("dd/MM/yyyy hh:mm tt"), textCenter),
                CellText(b.CustomerName, textLeft),
                CellText(b.CustomerPhone, textCenter),
                CellNumber(b.TotalPages, numRight),
                CellNumber(b.TotalSheets, numRight),
                CellText(b.PaymentMode, textCenter),
                CellMoney(b.TotalAmount, moneyRight),
                CellText(b.Notes, textLeft)
            }, 22.0));

            zebra = !zebra;
        }

        // Totals Row
        data.Append(CreateRow(r++, new[]
        {
            CellText("TOTALS", 12),
            CellText($"{bills.Count} Invoices", 12),
            CellEmpty(12),
            CellEmpty(12),
            CellNumber(totalPages, 14),
            CellNumber(totalSheets, 14),
            CellEmpty(12),
            CellMoney(totalRevenue, 13),
            CellEmpty(12)
        }, 26.0));

        return cols;
    }

    // ── Tab 3: Dedicated Print & Xerox History ─────────────────────────
    private static Columns BuildRegisterSheet(
        SheetData data,
        MergeCells mergeCells,
        List<CustomerBillSession> bills,
        List<PrintJobRecord> allJobs,
        PrintBillingSettings settings)
    {
        var cols = CreateColumns(
            (1, 1, 20.0),  // A: Date & Time
            (2, 2, 26.0),  // B: Print Source
            (3, 3, 20.0),  // C: Bill No / Status
            (4, 4, 24.0),  // D: Customer Name
            (5, 5, 34.0),  // E: Document / Job Name
            (6, 6, 20.0),  // F: Category
            (7, 7, 18.0),  // G: Sides
            (8, 8, 14.0),  // H: Color Mode
            (9, 9, 12.0),  // I: Pages
            (10, 10, 12.0),// J: Copies
            (11, 11, 14.0),// K: Impressions
            (12, 12, 14.0),// L: Sheets Used
            (13, 13, 14.0),// M: Unit Rate (₹)
            (14, 14, 18.0),// N: Total Cost (₹)
            (15, 15, 18.0) // O: Audit Status
        );

        uint r = 1;

        // Row 1: Merged Title
        data.Append(CreateRow(r++, CreateMergedBannerCells($"{settings.ShopName.ToUpperInvariant()} — DAILY PRINT & XEROX AUDIT HISTORY", 15, 15), 32.0));
        mergeCells.Append(new MergeCell { Reference = "A1:O1" });

        // Row 2: Merged Subtitle
        data.Append(CreateRow(r++, CreateMergedBannerCells("Complete chronological audit trail of Windows PC prints, physical photocopies, and customer billing", 16, 15), 20.0));
        mergeCells.Append(new MergeCell { Reference = "A2:O2" });

        // Combine all jobs from tracker history and bills
        var combinedJobs = new List<PrintJobRecord>();
        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var billMap = bills.ToDictionary(b => b.SessionId, b => b, StringComparer.OrdinalIgnoreCase);

        foreach (var j in allJobs)
        {
            string key = !string.IsNullOrWhiteSpace(j.Id) ? j.Id : $"{j.Timestamp.Ticks}_{j.DocumentName}_{j.TotalCost}";
            if (seenKeys.Add(key))
            {
                combinedJobs.Add(j);
            }
        }

        foreach (var b in bills)
        {
            foreach (var j in b.Jobs)
            {
                string key = !string.IsNullOrWhiteSpace(j.Id) ? j.Id : $"{j.Timestamp.Ticks}_{j.DocumentName}_{j.TotalCost}";
                if (seenKeys.Add(key))
                {
                    if (string.IsNullOrEmpty(j.CustomerName)) j.CustomerName = b.CustomerName;
                    if (string.IsNullOrEmpty(j.CustomerPhone)) j.CustomerPhone = b.CustomerPhone;
                    j.BillSessionId = b.SessionId;
                    j.IsBilled = true;
                    combinedJobs.Add(j);
                }
            }
        }

        combinedJobs = combinedJobs.OrderBy(j => j.Timestamp).ToList();

        // Calculate KPI Metrics
        int pcPrintCount = combinedJobs.Count(j => !j.IsManualEntry || j.ItemCategory.Contains("Windows", StringComparison.OrdinalIgnoreCase));
        int xeroxCopyCount = combinedJobs.Count(j => j.IsManualEntry && (j.ItemCategory.Contains("Photocopy", StringComparison.OrdinalIgnoreCase) || j.ItemCategory.Contains("Xerox", StringComparison.OrdinalIgnoreCase)));
        int totalImpressions = combinedJobs.Sum(j => j.TotalImpressions);
        int totalSheets = combinedJobs.Sum(j => j.SheetsUsed);
        double totalRevenue = combinedJobs.Sum(j => j.TotalCost);

        // Row 3: KPI Card Labels
        data.Append(CreateRow(r++, new[]
        {
            CellText("💻 WINDOWS PC PRINTS", 19), CellEmpty(19), CellEmpty(19),
            CellText("🖨️ PHYSICAL XEROX", 17), CellEmpty(17), CellEmpty(17),
            CellText("📄 SHEETS CONSUMED", 21), CellEmpty(21), CellEmpty(21),
            CellText("⭐ TOTAL PRINT REVENUE", 23), CellEmpty(23), CellEmpty(23),
            CellText("👥 BILLED SESSIONS", 25), CellEmpty(25), CellEmpty(25)
        }, 20.0));
        mergeCells.Append(new MergeCell { Reference = "A3:C3" });
        mergeCells.Append(new MergeCell { Reference = "D3:F3" });
        mergeCells.Append(new MergeCell { Reference = "G3:I3" });
        mergeCells.Append(new MergeCell { Reference = "J3:L3" });
        mergeCells.Append(new MergeCell { Reference = "M3:O3" });

        // Row 4: KPI Card Values
        data.Append(CreateRow(r++, new[]
        {
            CellText($"{pcPrintCount} Jobs", 20), CellEmpty(20), CellEmpty(20),
            CellText($"{xeroxCopyCount} Copies", 18), CellEmpty(18), CellEmpty(18),
            CellText($"{totalSheets} Sheets", 22), CellEmpty(22), CellEmpty(22),
            CellText($"₹ {totalRevenue:N2}", 24), CellEmpty(24), CellEmpty(24),
            CellText($"{bills.Count} Invoices", 26), CellEmpty(26), CellEmpty(26)
        }, 26.0));
        mergeCells.Append(new MergeCell { Reference = "A4:C4" });
        mergeCells.Append(new MergeCell { Reference = "D4:F4" });
        mergeCells.Append(new MergeCell { Reference = "G4:I4" });
        mergeCells.Append(new MergeCell { Reference = "J4:L4" });
        mergeCells.Append(new MergeCell { Reference = "M4:O4" });

        // Row 5: Spacer
        data.Append(CreateRow(r++, Array.Empty<Cell>(), 10.0));

        // Row 6: Header
        data.Append(CreateRow(r++, new[]
        {
            CellText("Date & Time", 1),
            CellText("Print Source", 1),
            CellText("Bill Number", 1),
            CellText("Customer Name", 3),
            CellText("Document / Job Name", 3),
            CellText("Category", 1),
            CellText("Sides", 1),
            CellText("Color Mode", 1),
            CellText("Pages", 2),
            CellText("Copies", 2),
            CellText("Impressions", 2),
            CellText("Sheets Used", 2),
            CellText("Unit Rate (₹)", 2),
            CellText("Total Cost (₹)", 2),
            CellText("Audit Status", 1)
        }, 26.0));

        bool zebra = false;
        foreach (var j in combinedJobs)
        {
            string billNo = "—";
            string customer = !string.IsNullOrWhiteSpace(j.CustomerName) ? j.CustomerName : "Walk-in Customer";
            if (!string.IsNullOrEmpty(j.BillSessionId) && billMap.TryGetValue(j.BillSessionId, out var bill))
            {
                billNo = bill.BillNumber;
                if (!string.IsNullOrWhiteSpace(bill.CustomerName)) customer = bill.CustomerName;
            }
            else if (j.IsBilled)
            {
                billNo = "Billed";
            }

            uint textLeft = zebra ? 5u : 4u;
            uint textCenter = zebra ? 7u : 6u;
            uint moneyRight = zebra ? 9u : 8u;
            uint numRight = zebra ? 11u : 10u;
            uint statusStyle = j.IsBilled ? 27u : (zebra ? 7u : 6u);

            data.Append(CreateRow(r++, new[]
            {
                CellText(j.Timestamp.ToString("dd/MM/yyyy hh:mm tt"), textCenter),
                CellText(j.PrintSource, textCenter),
                CellText(billNo, textCenter),
                CellText(customer, textLeft),
                CellText(j.DocumentName, textLeft),
                CellText(j.ItemCategory, textCenter),
                CellText(j.IsDuplex ? "Duplex (2-Sided)" : "Single-Sided", textCenter),
                CellText(j.IsColor ? "🌈 Color" : "⚫ B&W", textCenter),
                CellNumber(j.Pages, numRight),
                CellNumber(j.Copies, numRight),
                CellNumber(j.TotalImpressions, numRight),
                CellNumber(j.SheetsUsed, numRight),
                CellMoney(j.RatePerUnit, moneyRight),
                CellMoney(j.TotalCost, moneyRight),
                CellText(j.IsBilled ? "Billed / Settled" : "Active Counter", statusStyle)
            }, 22.0));

            zebra = !zebra;
        }

        // Totals Row
        data.Append(CreateRow(r++, new[]
        {
            CellText("TOTALS", 12),
            CellText($"{combinedJobs.Count} Audit Entries", 12),
            CellText($"{bills.Count} Bills", 12),
            CellEmpty(12), CellEmpty(12), CellEmpty(12), CellEmpty(12), CellEmpty(12),
            CellEmpty(12), CellEmpty(12),
            CellNumber(totalImpressions, 14),
            CellNumber(totalSheets, 14),
            CellEmpty(12),
            CellMoney(totalRevenue, 13),
            CellText("AUDITED", 12)
        }, 26.0));

        return cols;
    }

    // ── Tab 4: Financial KPIs & Ledger ──────────────────────────────────────
    private static Columns BuildKpiSheet(
        SheetData data,
        MergeCells mergeCells,
        List<DailyCashRegister> registers,
        PrintBillingSettings settings)
    {
        var cols = CreateColumns(
            (1, 1, 16.0),  // A: Date
            (2, 2, 18.0),  // B: Opening Cash
            (3, 3, 18.0),  // C: Opening Online
            (4, 4, 22.0),  // D: Closing Cash
            (5, 5, 22.0),  // E: Closing Online
            (6, 6, 18.0),  // F: Day Revenue
            (7, 7, 18.0),  // G: Day Expenses
            (8, 8, 18.0),  // H: Net Profit
            (9, 9, 20.0)   // I: Unpaid Due
        );

        uint r = 1;

        // Row 1: Merged Title
        data.Append(CreateRow(r++, CreateMergedBannerCells($"{settings.ShopName.ToUpperInvariant()} — DAILY FINANCIAL KPIS & ARCHIVE", 15, 9), 32.0));
        mergeCells.Append(new MergeCell { Reference = "A1:I1" });

        // Row 2: Merged Subtitle
        data.Append(CreateRow(r++, CreateMergedBannerCells("Daily cash float tracking, online banking reconciliation, operational expenses, and net profit ledger", 16, 9), 20.0));
        mergeCells.Append(new MergeCell { Reference = "A2:I2" });

        // Row 3: Spacer
        data.Append(CreateRow(r++, Array.Empty<Cell>(), 10.0));

        // Row 4: Header
        data.Append(CreateRow(r++, new[]
        {
            CellText("Date", 1),
            CellText("Opening Cash (₹)", 2),
            CellText("Opening Online (₹)", 2),
            CellText("Closing / Till Cash (₹)", 2),
            CellText("Closing / Bank Online (₹)", 2),
            CellText("Day Revenue (₹)", 2),
            CellText("Day Expenses (₹)", 2),
            CellText("Net Profit (₹)", 2),
            CellText("Unpaid Debt / Due (₹)", 2)
        }, 26.0));

        double sumRevenue = 0;
        double sumExpenses = 0;
        double sumProfit = 0;
        double sumDebt = 0;

        bool zebra = false;
        foreach (var reg in registers)
        {
            sumRevenue += reg.TodayTotalRevenue;
            sumExpenses += reg.TodayTotalExpenses;
            sumProfit += reg.TodayNetProfit;
            sumDebt += reg.TotalCustomerUnpaidDebt;

            uint textCenter = zebra ? 7u : 6u;
            uint moneyRight = zebra ? 9u : 8u;
            uint profitStyle = reg.TodayNetProfit >= 0
                ? (zebra ? 30u : 29u)
                : (zebra ? 32u : 31u);

            uint debtStyle = reg.TotalCustomerUnpaidDebt > 0
                ? (zebra ? 32u : 31u)
                : moneyRight;

            data.Append(CreateRow(r++, new[]
            {
                CellText(reg.Date.ToString("dd/MM/yyyy"), textCenter),
                CellMoney(reg.OpeningCashInDrawer, moneyRight),
                CellMoney(reg.OpeningOnlineBalance, moneyRight),
                CellMoney(reg.CurrentCashInDrawer, moneyRight),
                CellMoney(reg.CurrentOnlineBalance, moneyRight),
                CellMoney(reg.TodayTotalRevenue, moneyRight),
                CellMoney(reg.TodayTotalExpenses, moneyRight),
                CellMoney(reg.TodayNetProfit, profitStyle),
                CellMoney(reg.TotalCustomerUnpaidDebt, debtStyle)
            }, 22.0));

            zebra = !zebra;
        }

        var latestReg = registers.OrderByDescending(r => r.Date).FirstOrDefault() ?? CashDrawerService.Instance.Today;

        // Totals Row
        data.Append(CreateRow(r++, new[]
        {
            CellText("TOTALS", 12),
            CellText($"{registers.Count} Days", 12),
            CellEmpty(12),
            CellMoney(latestReg.CurrentCashInDrawer, 13),
            CellMoney(latestReg.CurrentOnlineBalance, 13),
            CellMoney(sumRevenue, 13),
            CellMoney(sumExpenses, 13),
            CellMoney(sumProfit, 13),
            CellMoney(sumDebt, 13)
        }, 26.0));

        return cols;
    }

    // ── Helper Constructors ────────────────────────────────────────────────
    private static Columns CreateColumns(params (uint min, uint max, double width)[] colDefs)
    {
        var cols = new Columns();
        foreach (var (min, max, width) in colDefs)
        {
            cols.Append(new Column
            {
                Min = min,
                Max = max,
                Width = width,
                CustomWidth = true
            });
        }
        return cols;
    }

    private static IEnumerable<Cell> CreateMergedBannerCells(string text, uint style, int colCount)
    {
        var cells = new List<Cell>
        {
            CellText(text, style)
        };
        for (int i = 1; i < colCount; i++)
        {
            cells.Add(CellEmpty(style));
        }
        return cells;
    }

    private static Cell CellText(string? text, uint style = 4, string? cellRef = null)
    {
        var cell = new Cell
        {
            DataType = CellValues.InlineString,
            StyleIndex = style,
            InlineString = new InlineString(new Text(text ?? string.Empty))
        };
        if (!string.IsNullOrEmpty(cellRef)) cell.CellReference = cellRef;
        return cell;
    }

    private static Cell CellEmpty(uint style = 4, string? cellRef = null)
    {
        var cell = new Cell
        {
            DataType = CellValues.InlineString,
            StyleIndex = style,
            InlineString = new InlineString(new Text(string.Empty))
        };
        if (!string.IsNullOrEmpty(cellRef)) cell.CellReference = cellRef;
        return cell;
    }

    private static Cell CellNumber(int val, uint style = 10, string? cellRef = null)
    {
        var cell = new Cell
        {
            DataType = CellValues.Number,
            StyleIndex = style,
            CellValue = new CellValue(val.ToString(CultureInfo.InvariantCulture))
        };
        if (!string.IsNullOrEmpty(cellRef)) cell.CellReference = cellRef;
        return cell;
    }

    private static Cell CellMoney(double val, uint style = 8, string? cellRef = null)
    {
        var cell = new Cell
        {
            DataType = CellValues.Number,
            StyleIndex = style,
            CellValue = new CellValue(Math.Round(val, 2).ToString("0.00", CultureInfo.InvariantCulture))
        };
        if (!string.IsNullOrEmpty(cellRef)) cell.CellReference = cellRef;
        return cell;
    }

    private static Row CreateRow(uint rowIndex, IEnumerable<Cell> cells, double height = 20.0)
    {
        var row = new Row
        {
            RowIndex = rowIndex,
            Height = height,
            CustomHeight = true
        };
        int colIdx = 1;
        foreach (var c in cells)
        {
            if (string.IsNullOrEmpty(c.CellReference?.Value))
            {
                c.CellReference = $"{GetColLetter(colIdx)}{rowIndex}";
            }
            else
            {
                colIdx = ParseColIdx(c.CellReference.Value);
            }
            row.Append(c);
            colIdx++;
        }
        return row;
    }

    private static string GetColLetter(int colIndex)
    {
        string letter = "";
        while (colIndex > 0)
        {
            int mod = (colIndex - 1) % 26;
            letter = (char)('A' + mod) + letter;
            colIndex = (colIndex - mod) / 26;
        }
        return letter;
    }

    private static int ParseColIdx(string cellRef)
    {
        int col = 0;
        foreach (char ch in cellRef)
        {
            if (char.IsLetter(ch))
            {
                col = col * 26 + (char.ToUpperInvariant(ch) - 'A' + 1);
            }
            else
            {
                break;
            }
        }
        return col > 0 ? col : 1;
    }

    // ── Executive Visual Charts (OpenXml Native) ───────────────────────────
    private static void AddExecutiveCharts(WorksheetPart cashPart, DrawingsPart drawingsPart, DailyCashRegister todayReg)
    {
        var worksheetDrawing = new DrawSpreadsheet.WorksheetDrawing();
        drawingsPart.WorksheetDrawing = worksheetDrawing;

        // Chart 1: Doughnut Chart (Payment Mode %: Cash vs Online UPI)
        var donutPart = drawingsPart.AddNewPart<ChartPart>();
        BuildDoughnutChartPart(donutPart, todayReg);
        AttachChartAnchor(drawingsPart, worksheetDrawing, donutPart, 1u, "Payment Mode Distribution Chart",
            fromCol: 11, fromRow: 1, toCol: 19, toRow: 16);

        // Chart 2: Clustered Column Chart (Financial Overview: Revenue vs Expense vs Profit)
        var columnPart = drawingsPart.AddNewPart<ChartPart>();
        BuildColumnChartPart(columnPart, todayReg);
        AttachChartAnchor(drawingsPart, worksheetDrawing, columnPart, 2u, "Financial Performance Overview Chart",
            fromCol: 11, fromRow: 17, toCol: 19, toRow: 32);

        drawingsPart.WorksheetDrawing.Save();
    }

    private static void AttachChartAnchor(
        DrawingsPart drawingsPart,
        DrawSpreadsheet.WorksheetDrawing worksheetDrawing,
        ChartPart chartPart,
        uint chartId,
        string chartName,
        int fromCol, int fromRow, int toCol, int toRow)
    {
        var anchor = new DrawSpreadsheet.TwoCellAnchor
        {
            EditAs = DrawSpreadsheet.EditAsValues.OneCell,
            FromMarker = new DrawSpreadsheet.FromMarker
            {
                ColumnId = new DrawSpreadsheet.ColumnId(fromCol.ToString(CultureInfo.InvariantCulture)),
                ColumnOffset = new DrawSpreadsheet.ColumnOffset("0"),
                RowId = new DrawSpreadsheet.RowId(fromRow.ToString(CultureInfo.InvariantCulture)),
                RowOffset = new DrawSpreadsheet.RowOffset("0")
            },
            ToMarker = new DrawSpreadsheet.ToMarker
            {
                ColumnId = new DrawSpreadsheet.ColumnId(toCol.ToString(CultureInfo.InvariantCulture)),
                ColumnOffset = new DrawSpreadsheet.ColumnOffset("0"),
                RowId = new DrawSpreadsheet.RowId(toRow.ToString(CultureInfo.InvariantCulture)),
                RowOffset = new DrawSpreadsheet.RowOffset("0")
            }
        };

        var graphicFrame = new DrawSpreadsheet.GraphicFrame { Macro = string.Empty };
        graphicFrame.NonVisualGraphicFrameProperties = new DrawSpreadsheet.NonVisualGraphicFrameProperties(
            new DrawSpreadsheet.NonVisualDrawingProperties { Id = chartId, Name = chartName },
            new DrawSpreadsheet.NonVisualGraphicFrameDrawingProperties()
        );
        graphicFrame.Transform = new DrawSpreadsheet.Transform(
            new Draw.Offset { X = 0, Y = 0 },
            new Draw.Extents { Cx = 0, Cy = 0 }
        );

        var graphic = new Draw.Graphic();
        var graphicData = new Draw.GraphicData { Uri = "http://schemas.openxmlformats.org/drawingml/2006/chart" };
        graphicData.Append(new DrawCharts.ChartReference { Id = drawingsPart.GetIdOfPart(chartPart) });
        graphic.Append(graphicData);
        graphicFrame.Append(graphic);

        anchor.Append(graphicFrame);
        anchor.Append(new DrawSpreadsheet.ClientData());
        worksheetDrawing.Append(anchor);
    }

    private static void BuildDoughnutChartPart(ChartPart chartPart, DailyCashRegister todayReg)
    {
        var chartSpace = new DrawCharts.ChartSpace();
        chartSpace.AddNamespaceDeclaration("c", "http://schemas.openxmlformats.org/drawingml/2006/chart");
        chartSpace.AddNamespaceDeclaration("a", "http://schemas.openxmlformats.org/drawingml/2006/main");
        chartSpace.AddNamespaceDeclaration("r", "http://schemas.openxmlformats.org/officeDocument/2006/relationships");

        chartSpace.AppendChild(new DrawCharts.Date1904 { Val = false });
        chartSpace.AppendChild(new DrawCharts.EditingLanguage { Val = "en-US" });
        chartSpace.AppendChild(new DrawCharts.RoundedCorners { Val = true });

        var chart = new DrawCharts.Chart();
        chart.AppendChild(new DrawCharts.AutoTitleDeleted { Val = false });
        chart.AppendChild(CreateChartTitle("PAYMENT MODE DISTRIBUTION (% CASH VS % UPI)"));

        var plotArea = new DrawCharts.PlotArea();
        plotArea.AppendChild(new DrawCharts.Layout());

        var doughnutChart = new DrawCharts.DoughnutChart();
        doughnutChart.AppendChild(new DrawCharts.VaryColors { Val = true });

        var ser = new DrawCharts.PieChartSeries();
        ser.AppendChild(new DrawCharts.Index { Val = 0u });
        ser.AppendChild(new DrawCharts.Order { Val = 0u });

        var serText = new DrawCharts.SeriesText();
        serText.AppendChild(new DrawCharts.NumericValue("Payment Distribution"));
        ser.AppendChild(serText);

        // Data point colors: Slice 0 = Emerald Green (#10B981), Slice 1 = Sky Blue (#0284C7)
        var dPt0 = new DrawCharts.DataPoint();
        dPt0.AppendChild(new DrawCharts.Index { Val = 0u });
        var spPr0 = new DrawCharts.ChartShapeProperties();
        spPr0.AppendChild(new Draw.SolidFill(new Draw.RgbColorModelHex { Val = "10B981" }));
        dPt0.AppendChild(spPr0);
        ser.AppendChild(dPt0);

        var dPt1 = new DrawCharts.DataPoint();
        dPt1.AppendChild(new DrawCharts.Index { Val = 1u });
        var spPr1 = new DrawCharts.ChartShapeProperties();
        spPr1.AppendChild(new Draw.SolidFill(new Draw.RgbColorModelHex { Val = "0284C7" }));
        dPt1.AppendChild(spPr1);
        ser.AppendChild(dPt1);

        // Category Labels: $U$3:$U$4
        var catAxisData = new DrawCharts.CategoryAxisData();
        var strRef = new DrawCharts.StringReference();
        strRef.AppendChild(new DrawCharts.Formula("'Cash Drawer & Accounts'!$U$3:$U$4"));
        var strCache = new DrawCharts.StringCache();
        strCache.AppendChild(new DrawCharts.PointCount { Val = 2u });
        strCache.AppendChild(new DrawCharts.StringPoint { Index = 0u, NumericValue = new DrawCharts.NumericValue("Cash in Drawer") });
        strCache.AppendChild(new DrawCharts.StringPoint { Index = 1u, NumericValue = new DrawCharts.NumericValue("Online UPI / Bank") });
        strRef.AppendChild(strCache);
        catAxisData.AppendChild(strRef);
        ser.AppendChild(catAxisData);

        // Category Values: $V$3:$V$4
        double cashVal = todayReg.CurrentCashInDrawer;
        double upiVal = todayReg.CurrentOnlineBalance;
        double chartCash = cashVal > 0 ? cashVal : (upiVal > 0 ? 0.0 : 50.0);
        double chartUpi = upiVal > 0 ? upiVal : (cashVal > 0 ? 0.0 : 50.0);

        var values = new DrawCharts.Values();
        var numRef = new DrawCharts.NumberReference();
        numRef.AppendChild(new DrawCharts.Formula("'Cash Drawer & Accounts'!$V$3:$V$4"));
        var numCache = new DrawCharts.NumberingCache();
        numCache.AppendChild(new DrawCharts.FormatCode("\"₹ \"#,##0.00"));
        numCache.AppendChild(new DrawCharts.PointCount { Val = 2u });
        numCache.AppendChild(new DrawCharts.NumericPoint { Index = 0u, NumericValue = new DrawCharts.NumericValue(chartCash.ToString(CultureInfo.InvariantCulture)) });
        numCache.AppendChild(new DrawCharts.NumericPoint { Index = 1u, NumericValue = new DrawCharts.NumericValue(chartUpi.ToString(CultureInfo.InvariantCulture)) });
        numRef.AppendChild(numCache);
        values.AppendChild(numRef);
        ser.AppendChild(values);

        doughnutChart.AppendChild(ser);

        // Data labels with ShowPercent = true
        var dataLabels = new DrawCharts.DataLabels();
        dataLabels.AppendChild(new DrawCharts.ShowLegendKey { Val = false });
        dataLabels.AppendChild(new DrawCharts.ShowValue { Val = false });
        dataLabels.AppendChild(new DrawCharts.ShowCategoryName { Val = true });
        dataLabels.AppendChild(new DrawCharts.ShowSeriesName { Val = false });
        dataLabels.AppendChild(new DrawCharts.ShowPercent { Val = true });
        dataLabels.AppendChild(new DrawCharts.ShowBubbleSize { Val = false });
        doughnutChart.AppendChild(dataLabels);

        doughnutChart.AppendChild(new DrawCharts.FirstSliceAngle { Val = 0 });
        doughnutChart.AppendChild(new DrawCharts.HoleSize { Val = 60 });

        plotArea.AppendChild(doughnutChart);
        chart.AppendChild(plotArea);

        var legend = new DrawCharts.Legend();
        legend.AppendChild(new DrawCharts.LegendPosition { Val = DrawCharts.LegendPositionValues.Bottom });
        legend.AppendChild(new DrawCharts.Overlay { Val = false });
        chart.AppendChild(legend);

        chart.AppendChild(new DrawCharts.PlotVisibleOnly { Val = true });
        chartSpace.AppendChild(chart);
        chartPart.ChartSpace = chartSpace;
        chartPart.ChartSpace.Save();
    }

    private static void BuildColumnChartPart(ChartPart chartPart, DailyCashRegister todayReg)
    {
        var chartSpace = new DrawCharts.ChartSpace();
        chartSpace.AddNamespaceDeclaration("c", "http://schemas.openxmlformats.org/drawingml/2006/chart");
        chartSpace.AddNamespaceDeclaration("a", "http://schemas.openxmlformats.org/drawingml/2006/main");
        chartSpace.AddNamespaceDeclaration("r", "http://schemas.openxmlformats.org/officeDocument/2006/relationships");

        chartSpace.AppendChild(new DrawCharts.Date1904 { Val = false });
        chartSpace.AppendChild(new DrawCharts.EditingLanguage { Val = "en-US" });
        chartSpace.AppendChild(new DrawCharts.RoundedCorners { Val = true });

        var chart = new DrawCharts.Chart();
        chart.AppendChild(new DrawCharts.AutoTitleDeleted { Val = false });
        chart.AppendChild(CreateChartTitle("TODAY'S FINANCIAL OVERVIEW (REVENUE VS EXPENSES VS PROFIT)"));

        var plotArea = new DrawCharts.PlotArea();
        plotArea.AppendChild(new DrawCharts.Layout());

        var barChart = new DrawCharts.BarChart();
        barChart.AppendChild(new DrawCharts.BarDirection { Val = DrawCharts.BarDirectionValues.Column });
        barChart.AppendChild(new DrawCharts.BarGrouping { Val = DrawCharts.BarGroupingValues.Clustered });
        barChart.AppendChild(new DrawCharts.VaryColors { Val = true });

        var bSer = new DrawCharts.BarChartSeries();
        bSer.AppendChild(new DrawCharts.Index { Val = 0u });
        bSer.AppendChild(new DrawCharts.Order { Val = 0u });

        var bSerText = new DrawCharts.SeriesText();
        bSerText.AppendChild(new DrawCharts.NumericValue("Amount (₹)"));
        bSer.AppendChild(bSerText);

        // Bar custom colors: 0=Revenue (#10B981 Emerald), 1=Expense (#E11D48 Rose), 2=Net Profit (#6366F1 Indigo)
        var bPt0 = new DrawCharts.DataPoint();
        bPt0.AppendChild(new DrawCharts.Index { Val = 0u });
        var bSp0 = new DrawCharts.ChartShapeProperties();
        bSp0.AppendChild(new Draw.SolidFill(new Draw.RgbColorModelHex { Val = "10B981" }));
        bPt0.AppendChild(bSp0);
        bSer.AppendChild(bPt0);

        var bPt1 = new DrawCharts.DataPoint();
        bPt1.AppendChild(new DrawCharts.Index { Val = 1u });
        var bSp1 = new DrawCharts.ChartShapeProperties();
        bSp1.AppendChild(new Draw.SolidFill(new Draw.RgbColorModelHex { Val = "E11D48" }));
        bPt1.AppendChild(bSp1);
        bSer.AppendChild(bPt1);

        var bPt2 = new DrawCharts.DataPoint();
        bPt2.AppendChild(new DrawCharts.Index { Val = 2u });
        var bSp2 = new DrawCharts.ChartShapeProperties();
        bSp2.AppendChild(new Draw.SolidFill(new Draw.RgbColorModelHex { Val = "6366F1" }));
        bPt2.AppendChild(bSp2);
        bSer.AppendChild(bPt2);

        // Category Axis Data: $U$6:$U$8
        var bCat = new DrawCharts.CategoryAxisData();
        var bStrRef = new DrawCharts.StringReference();
        bStrRef.AppendChild(new DrawCharts.Formula("'Cash Drawer & Accounts'!$U$6:$U$8"));
        var bStrCache = new DrawCharts.StringCache();
        bStrCache.AppendChild(new DrawCharts.PointCount { Val = 3u });
        bStrCache.AppendChild(new DrawCharts.StringPoint { Index = 0u, NumericValue = new DrawCharts.NumericValue("Total Revenue") });
        bStrCache.AppendChild(new DrawCharts.StringPoint { Index = 1u, NumericValue = new DrawCharts.NumericValue("Total Expenses") });
        bStrCache.AppendChild(new DrawCharts.StringPoint { Index = 2u, NumericValue = new DrawCharts.NumericValue("Net Profit") });
        bStrRef.AppendChild(bStrCache);
        bCat.AppendChild(bStrRef);
        bSer.AppendChild(bCat);

        // Values: $V$6:$V$8
        var bVal = new DrawCharts.Values();
        var bNumRef = new DrawCharts.NumberReference();
        bNumRef.AppendChild(new DrawCharts.Formula("'Cash Drawer & Accounts'!$V$6:$V$8"));
        var bNumCache = new DrawCharts.NumberingCache();
        bNumCache.AppendChild(new DrawCharts.FormatCode("\"₹ \"#,##0.00"));
        bNumCache.AppendChild(new DrawCharts.PointCount { Val = 3u });
        bNumCache.AppendChild(new DrawCharts.NumericPoint { Index = 0u, NumericValue = new DrawCharts.NumericValue(todayReg.TodayTotalRevenue.ToString(CultureInfo.InvariantCulture)) });
        bNumCache.AppendChild(new DrawCharts.NumericPoint { Index = 1u, NumericValue = new DrawCharts.NumericValue(todayReg.TodayTotalExpenses.ToString(CultureInfo.InvariantCulture)) });
        bNumCache.AppendChild(new DrawCharts.NumericPoint { Index = 2u, NumericValue = new DrawCharts.NumericValue(todayReg.TodayNetProfit.ToString(CultureInfo.InvariantCulture)) });
        bNumRef.AppendChild(bNumCache);
        bVal.AppendChild(bNumRef);
        bSer.AppendChild(bVal);

        barChart.AppendChild(bSer);

        // Data labels: show values above columns
        var bLabels = new DrawCharts.DataLabels();
        bLabels.AppendChild(new DrawCharts.ShowLegendKey { Val = false });
        bLabels.AppendChild(new DrawCharts.ShowValue { Val = true });
        bLabels.AppendChild(new DrawCharts.ShowCategoryName { Val = false });
        bLabels.AppendChild(new DrawCharts.ShowSeriesName { Val = false });
        bLabels.AppendChild(new DrawCharts.ShowPercent { Val = false });
        bLabels.AppendChild(new DrawCharts.ShowBubbleSize { Val = false });
        barChart.AppendChild(bLabels);

        uint catAxisId = 40000000u;
        uint valAxisId = 40000001u;
        barChart.AppendChild(new DrawCharts.AxisId { Val = catAxisId });
        barChart.AppendChild(new DrawCharts.AxisId { Val = valAxisId });
        plotArea.AppendChild(barChart);

        // Category Axis
        var categoryAxis = new DrawCharts.CategoryAxis();
        categoryAxis.AppendChild(new DrawCharts.AxisId { Val = catAxisId });
        categoryAxis.AppendChild(new DrawCharts.Scaling { Orientation = new DrawCharts.Orientation { Val = DrawCharts.OrientationValues.MinMax } });
        categoryAxis.AppendChild(new DrawCharts.Delete { Val = false });
        categoryAxis.AppendChild(new DrawCharts.AxisPosition { Val = DrawCharts.AxisPositionValues.Bottom });
        categoryAxis.AppendChild(new DrawCharts.CrossingAxis { Val = valAxisId });
        categoryAxis.AppendChild(new DrawCharts.Crosses { Val = DrawCharts.CrossesValues.AutoZero });
        categoryAxis.AppendChild(new DrawCharts.AutoLabeled { Val = true });
        categoryAxis.AppendChild(new DrawCharts.LabelAlignment { Val = DrawCharts.LabelAlignmentValues.Center });
        categoryAxis.AppendChild(new DrawCharts.LabelOffset { Val = 100 });
        plotArea.AppendChild(categoryAxis);

        // Value Axis
        var valueAxis = new DrawCharts.ValueAxis();
        valueAxis.AppendChild(new DrawCharts.AxisId { Val = valAxisId });
        valueAxis.AppendChild(new DrawCharts.Scaling { Orientation = new DrawCharts.Orientation { Val = DrawCharts.OrientationValues.MinMax } });
        valueAxis.AppendChild(new DrawCharts.Delete { Val = false });
        valueAxis.AppendChild(new DrawCharts.AxisPosition { Val = DrawCharts.AxisPositionValues.Left });
        valueAxis.AppendChild(new DrawCharts.MajorGridlines());
        valueAxis.AppendChild(new DrawCharts.NumberingFormat { FormatCode = "\"₹ \"#,##0", SourceLinked = false });
        valueAxis.AppendChild(new DrawCharts.CrossingAxis { Val = catAxisId });
        valueAxis.AppendChild(new DrawCharts.Crosses { Val = DrawCharts.CrossesValues.AutoZero });
        valueAxis.AppendChild(new DrawCharts.CrossBetween { Val = DrawCharts.CrossBetweenValues.Between });
        plotArea.AppendChild(valueAxis);

        chart.AppendChild(plotArea);

        var legend = new DrawCharts.Legend();
        legend.AppendChild(new DrawCharts.LegendPosition { Val = DrawCharts.LegendPositionValues.Bottom });
        legend.AppendChild(new DrawCharts.Overlay { Val = false });
        chart.AppendChild(legend);

        chart.AppendChild(new DrawCharts.PlotVisibleOnly { Val = true });
        chartSpace.AppendChild(chart);
        chartPart.ChartSpace = chartSpace;
        chartPart.ChartSpace.Save();
    }

    private static DrawCharts.Title CreateChartTitle(string titleText)
    {
        var title = new DrawCharts.Title();
        var chartText = new DrawCharts.ChartText();
        var richText = new DrawCharts.RichText();

        richText.AppendChild(new Draw.BodyProperties());
        richText.AppendChild(new Draw.ListStyle());

        var para = new Draw.Paragraph();
        var paraProps = new Draw.ParagraphProperties();
        paraProps.AppendChild(new Draw.DefaultRunProperties());
        para.AppendChild(paraProps);

        var run = new Draw.Run();
        var runProps = new Draw.RunProperties
        {
            FontSize = 1100,
            Bold = true
        };
        runProps.AppendChild(new Draw.SolidFill(new Draw.RgbColorModelHex { Val = "0F172A" }));
        run.AppendChild(runProps);
        run.AppendChild(new Draw.Text(titleText));
        para.AppendChild(run);

        richText.AppendChild(para);
        chartText.AppendChild(richText);
        title.AppendChild(chartText);
        title.AppendChild(new DrawCharts.Overlay { Val = false });

        return title;
    }

    private static Border CreateBoxBorder(string hexRgb, BorderStyleValues style)
    {
        var clr1 = new DocumentFormat.OpenXml.Spreadsheet.Color { Rgb = "FF" + hexRgb.TrimStart('#') };
        var clr2 = new DocumentFormat.OpenXml.Spreadsheet.Color { Rgb = "FF" + hexRgb.TrimStart('#') };
        var clr3 = new DocumentFormat.OpenXml.Spreadsheet.Color { Rgb = "FF" + hexRgb.TrimStart('#') };
        var clr4 = new DocumentFormat.OpenXml.Spreadsheet.Color { Rgb = "FF" + hexRgb.TrimStart('#') };

        return new Border
        {
            LeftBorder = new LeftBorder { Style = style, Color = clr1 },
            RightBorder = new RightBorder { Style = style, Color = clr2 },
            TopBorder = new TopBorder { Style = style, Color = clr3 },
            BottomBorder = new BottomBorder { Style = style, Color = clr4 },
            DiagonalBorder = new DiagonalBorder()
        };
    }

    private static Border CreateAccountingBorder(string hexRgb)
    {
        var clrTop = new DocumentFormat.OpenXml.Spreadsheet.Color { Rgb = "FF" + hexRgb.TrimStart('#') };
        var clrBottom = new DocumentFormat.OpenXml.Spreadsheet.Color { Rgb = "FF" + hexRgb.TrimStart('#') };

        return new Border
        {
            LeftBorder = new LeftBorder { Style = BorderStyleValues.None },
            RightBorder = new RightBorder { Style = BorderStyleValues.None },
            TopBorder = new TopBorder { Style = BorderStyleValues.Thin, Color = clrTop },
            BottomBorder = new BottomBorder { Style = BorderStyleValues.Double, Color = clrBottom },
            DiagonalBorder = new DiagonalBorder()
        };
    }

    private static Stylesheet CreateStylesheet()
    {
        // ── Numbering Formats ──────────────────────────────────────────────
        var numberingFormats = new NumberingFormats(
            new NumberingFormat { NumberFormatId = 164u, FormatCode = "\"₹ \"#,##0.00" },
            new NumberingFormat { NumberFormatId = 165u, FormatCode = "#,##0" },
            new NumberingFormat { NumberFormatId = 166u, FormatCode = "\"₹ \"#,##0.00;[Red]-\"₹ \"#,##0.00;\"₹ 0.00\"" }
        );

        // ── Fonts ──────────────────────────────────────────────────────────
        var fonts = new Fonts(
            new DocumentFormat.OpenXml.Spreadsheet.Font(new FontSize { Val = 10 }, new FontName { Val = "Calibri" }, new DocumentFormat.OpenXml.Spreadsheet.Color { Rgb = "FF0F172A" }),
            new DocumentFormat.OpenXml.Spreadsheet.Font(new Bold(), new FontSize { Val = 10.5 }, new FontName { Val = "Calibri" }, new DocumentFormat.OpenXml.Spreadsheet.Color { Rgb = "FFFFFFFF" }),
            new DocumentFormat.OpenXml.Spreadsheet.Font(new Bold(), new FontSize { Val = 14 }, new FontName { Val = "Calibri" }, new DocumentFormat.OpenXml.Spreadsheet.Color { Rgb = "FFFFFFFF" }),
            new DocumentFormat.OpenXml.Spreadsheet.Font(new Italic(), new FontSize { Val = 9.5 }, new FontName { Val = "Calibri" }, new DocumentFormat.OpenXml.Spreadsheet.Color { Rgb = "FFCBD5E1" }),
            new DocumentFormat.OpenXml.Spreadsheet.Font(new Bold(), new FontSize { Val = 11 }, new FontName { Val = "Calibri" }, new DocumentFormat.OpenXml.Spreadsheet.Color { Rgb = "FF0F172A" }),
            new DocumentFormat.OpenXml.Spreadsheet.Font(new Bold(), new FontSize { Val = 10.5 }, new FontName { Val = "Calibri" }, new DocumentFormat.OpenXml.Spreadsheet.Color { Rgb = "FF15803D" }),
            new DocumentFormat.OpenXml.Spreadsheet.Font(new Bold(), new FontSize { Val = 10.5 }, new FontName { Val = "Calibri" }, new DocumentFormat.OpenXml.Spreadsheet.Color { Rgb = "FFB91C1C" }),
            new DocumentFormat.OpenXml.Spreadsheet.Font(new Bold(), new FontSize { Val = 8.5 }, new FontName { Val = "Calibri" }, new DocumentFormat.OpenXml.Spreadsheet.Color { Rgb = "FF475569" }),
            new DocumentFormat.OpenXml.Spreadsheet.Font(new Bold(), new FontSize { Val = 13 }, new FontName { Val = "Calibri" }, new DocumentFormat.OpenXml.Spreadsheet.Color { Rgb = "FF0F172A" })
        );

        // ── Fills ──────────────────────────────────────────────────────────
        var fills = new Fills(
            new Fill(new PatternFill { PatternType = PatternValues.None }),
            new Fill(new PatternFill { PatternType = PatternValues.Gray125 }),
            new Fill(new PatternFill(new ForegroundColor { Rgb = "FF0F172A" }) { PatternType = PatternValues.Solid }),
            new Fill(new PatternFill(new ForegroundColor { Rgb = "FF1E293B" }) { PatternType = PatternValues.Solid }),
            new Fill(new PatternFill(new ForegroundColor { Rgb = "FF1E293B" }) { PatternType = PatternValues.Solid }),
            new Fill(new PatternFill(new ForegroundColor { Rgb = "FFF8FAFC" }) { PatternType = PatternValues.Solid }),
            new Fill(new PatternFill(new ForegroundColor { Rgb = "FFE2E8F0" }) { PatternType = PatternValues.Solid }),
            new Fill(new PatternFill(new ForegroundColor { Rgb = "FFDCFCE7" }) { PatternType = PatternValues.Solid }),
            new Fill(new PatternFill(new ForegroundColor { Rgb = "FFE0F2FE" }) { PatternType = PatternValues.Solid }),
            new Fill(new PatternFill(new ForegroundColor { Rgb = "FFFEF3C7" }) { PatternType = PatternValues.Solid }),
            new Fill(new PatternFill(new ForegroundColor { Rgb = "FFF3E8FF" }) { PatternType = PatternValues.Solid }),
            new Fill(new PatternFill(new ForegroundColor { Rgb = "FFFFE4E6" }) { PatternType = PatternValues.Solid }),
            new Fill(new PatternFill(new ForegroundColor { Rgb = "FFFEE2E2" }) { PatternType = PatternValues.Solid }),
            new Fill(new PatternFill(new ForegroundColor { Rgb = "FFDCFCE7" }) { PatternType = PatternValues.Solid })
        );

        // ── Borders ────────────────────────────────────────────────────────
        var borders = new Borders(
            new Border(), // 0: None
            CreateBoxBorder("CBD5E1", BorderStyleValues.Thin), // 1: Thin
            CreateAccountingBorder("475569"), // 2: Accounting Totals
            CreateBoxBorder("94A3B8", BorderStyleValues.Thin)  // 3: KPI Border
        );

        var cellStyleFormats = new CellStyleFormats(new CellFormat());

        // ── Cell Formats ───────────────────────────────────────────────────
        var cellFormats = new CellFormats(
            // 0: Default
            new CellFormat { FontId = 0, FillId = 0, BorderId = 0 },

            // 1: Table Header Center
            new CellFormat { FontId = 1, FillId = 4, BorderId = 1, ApplyFont = true, ApplyFill = true, ApplyBorder = true, ApplyAlignment = true,
                Alignment = new Alignment { Horizontal = HorizontalAlignmentValues.Center, Vertical = VerticalAlignmentValues.Center } },

            // 2: Table Header Right
            new CellFormat { FontId = 1, FillId = 4, BorderId = 1, ApplyFont = true, ApplyFill = true, ApplyBorder = true, ApplyAlignment = true,
                Alignment = new Alignment { Horizontal = HorizontalAlignmentValues.Right, Vertical = VerticalAlignmentValues.Center } },

            // 3: Table Header Left
            new CellFormat { FontId = 1, FillId = 4, BorderId = 1, ApplyFont = true, ApplyFill = true, ApplyBorder = true, ApplyAlignment = true,
                Alignment = new Alignment { Horizontal = HorizontalAlignmentValues.Left, Vertical = VerticalAlignmentValues.Center } },

            // 4: Data Text Left White
            new CellFormat { FontId = 0, FillId = 0, BorderId = 1, ApplyFont = true, ApplyBorder = true, ApplyAlignment = true,
                Alignment = new Alignment { Horizontal = HorizontalAlignmentValues.Left, Vertical = VerticalAlignmentValues.Center } },

            // 5: Data Text Left Zebra
            new CellFormat { FontId = 0, FillId = 5, BorderId = 1, ApplyFont = true, ApplyFill = true, ApplyBorder = true, ApplyAlignment = true,
                Alignment = new Alignment { Horizontal = HorizontalAlignmentValues.Left, Vertical = VerticalAlignmentValues.Center } },

            // 6: Data Text Center White
            new CellFormat { FontId = 0, FillId = 0, BorderId = 1, ApplyFont = true, ApplyBorder = true, ApplyAlignment = true,
                Alignment = new Alignment { Horizontal = HorizontalAlignmentValues.Center, Vertical = VerticalAlignmentValues.Center } },

            // 7: Data Text Center Zebra
            new CellFormat { FontId = 0, FillId = 5, BorderId = 1, ApplyFont = true, ApplyFill = true, ApplyBorder = true, ApplyAlignment = true,
                Alignment = new Alignment { Horizontal = HorizontalAlignmentValues.Center, Vertical = VerticalAlignmentValues.Center } },

            // 8: Data Currency Right White
            new CellFormat { FontId = 0, FillId = 0, BorderId = 1, NumberFormatId = 164u, ApplyFont = true, ApplyBorder = true, ApplyNumberFormat = true, ApplyAlignment = true,
                Alignment = new Alignment { Horizontal = HorizontalAlignmentValues.Right, Vertical = VerticalAlignmentValues.Center } },

            // 9: Data Currency Right Zebra
            new CellFormat { FontId = 0, FillId = 5, BorderId = 1, NumberFormatId = 164u, ApplyFont = true, ApplyFill = true, ApplyBorder = true, ApplyNumberFormat = true, ApplyAlignment = true,
                Alignment = new Alignment { Horizontal = HorizontalAlignmentValues.Right, Vertical = VerticalAlignmentValues.Center } },

            // 10: Data Number Right White
            new CellFormat { FontId = 0, FillId = 0, BorderId = 1, NumberFormatId = 165u, ApplyFont = true, ApplyBorder = true, ApplyNumberFormat = true, ApplyAlignment = true,
                Alignment = new Alignment { Horizontal = HorizontalAlignmentValues.Right, Vertical = VerticalAlignmentValues.Center } },

            // 11: Data Number Right Zebra
            new CellFormat { FontId = 0, FillId = 5, BorderId = 1, NumberFormatId = 165u, ApplyFont = true, ApplyFill = true, ApplyBorder = true, ApplyNumberFormat = true, ApplyAlignment = true,
                Alignment = new Alignment { Horizontal = HorizontalAlignmentValues.Right, Vertical = VerticalAlignmentValues.Center } },

            // 12: Totals Label Left
            new CellFormat { FontId = 4, FillId = 6, BorderId = 2, ApplyFont = true, ApplyFill = true, ApplyBorder = true, ApplyAlignment = true,
                Alignment = new Alignment { Horizontal = HorizontalAlignmentValues.Left, Vertical = VerticalAlignmentValues.Center } },

            // 13: Totals Currency Right
            new CellFormat { FontId = 4, FillId = 6, BorderId = 2, NumberFormatId = 164u, ApplyFont = true, ApplyFill = true, ApplyBorder = true, ApplyNumberFormat = true, ApplyAlignment = true,
                Alignment = new Alignment { Horizontal = HorizontalAlignmentValues.Right, Vertical = VerticalAlignmentValues.Center } },

            // 14: Totals Number Right
            new CellFormat { FontId = 4, FillId = 6, BorderId = 2, NumberFormatId = 165u, ApplyFont = true, ApplyFill = true, ApplyBorder = true, ApplyNumberFormat = true, ApplyAlignment = true,
                Alignment = new Alignment { Horizontal = HorizontalAlignmentValues.Right, Vertical = VerticalAlignmentValues.Center } },

            // 15: Title Banner
            new CellFormat { FontId = 2, FillId = 2, BorderId = 0, ApplyFont = true, ApplyFill = true, ApplyAlignment = true,
                Alignment = new Alignment { Horizontal = HorizontalAlignmentValues.Center, Vertical = VerticalAlignmentValues.Center } },

            // 16: Subtitle Banner
            new CellFormat { FontId = 3, FillId = 3, BorderId = 0, ApplyFont = true, ApplyFill = true, ApplyAlignment = true,
                Alignment = new Alignment { Horizontal = HorizontalAlignmentValues.Center, Vertical = VerticalAlignmentValues.Center } },

            // 17: KPI Card Title Green
            new CellFormat { FontId = 7, FillId = 7, BorderId = 3, ApplyFont = true, ApplyFill = true, ApplyBorder = true, ApplyAlignment = true,
                Alignment = new Alignment { Horizontal = HorizontalAlignmentValues.Center, Vertical = VerticalAlignmentValues.Center } },

            // 18: KPI Card Value Green
            new CellFormat { FontId = 8, FillId = 7, BorderId = 3, ApplyFont = true, ApplyFill = true, ApplyBorder = true, ApplyAlignment = true,
                Alignment = new Alignment { Horizontal = HorizontalAlignmentValues.Center, Vertical = VerticalAlignmentValues.Center } },

            // 19: KPI Card Title Blue
            new CellFormat { FontId = 7, FillId = 8, BorderId = 3, ApplyFont = true, ApplyFill = true, ApplyBorder = true, ApplyAlignment = true,
                Alignment = new Alignment { Horizontal = HorizontalAlignmentValues.Center, Vertical = VerticalAlignmentValues.Center } },

            // 20: KPI Card Value Blue
            new CellFormat { FontId = 8, FillId = 8, BorderId = 3, ApplyFont = true, ApplyFill = true, ApplyBorder = true, ApplyAlignment = true,
                Alignment = new Alignment { Horizontal = HorizontalAlignmentValues.Center, Vertical = VerticalAlignmentValues.Center } },

            // 21: KPI Card Title Amber
            new CellFormat { FontId = 7, FillId = 9, BorderId = 3, ApplyFont = true, ApplyFill = true, ApplyBorder = true, ApplyAlignment = true,
                Alignment = new Alignment { Horizontal = HorizontalAlignmentValues.Center, Vertical = VerticalAlignmentValues.Center } },

            // 22: KPI Card Value Amber
            new CellFormat { FontId = 8, FillId = 9, BorderId = 3, ApplyFont = true, ApplyFill = true, ApplyBorder = true, ApplyAlignment = true,
                Alignment = new Alignment { Horizontal = HorizontalAlignmentValues.Center, Vertical = VerticalAlignmentValues.Center } },

            // 23: KPI Card Title Violet
            new CellFormat { FontId = 7, FillId = 10, BorderId = 3, ApplyFont = true, ApplyFill = true, ApplyBorder = true, ApplyAlignment = true,
                Alignment = new Alignment { Horizontal = HorizontalAlignmentValues.Center, Vertical = VerticalAlignmentValues.Center } },

            // 24: KPI Card Value Violet
            new CellFormat { FontId = 8, FillId = 10, BorderId = 3, ApplyFont = true, ApplyFill = true, ApplyBorder = true, ApplyAlignment = true,
                Alignment = new Alignment { Horizontal = HorizontalAlignmentValues.Center, Vertical = VerticalAlignmentValues.Center } },

            // 25: KPI Card Title Rose
            new CellFormat { FontId = 7, FillId = 11, BorderId = 3, ApplyFont = true, ApplyFill = true, ApplyBorder = true, ApplyAlignment = true,
                Alignment = new Alignment { Horizontal = HorizontalAlignmentValues.Center, Vertical = VerticalAlignmentValues.Center } },

            // 26: KPI Card Value Rose
            new CellFormat { FontId = 8, FillId = 11, BorderId = 3, ApplyFont = true, ApplyFill = true, ApplyBorder = true, ApplyAlignment = true,
                Alignment = new Alignment { Horizontal = HorizontalAlignmentValues.Center, Vertical = VerticalAlignmentValues.Center } },

            // 27: Status Cleared Badge
            new CellFormat { FontId = 5, FillId = 13, BorderId = 1, ApplyFont = true, ApplyFill = true, ApplyBorder = true, ApplyAlignment = true,
                Alignment = new Alignment { Horizontal = HorizontalAlignmentValues.Center, Vertical = VerticalAlignmentValues.Center } },

            // 28: Status Unpaid Badge
            new CellFormat { FontId = 6, FillId = 12, BorderId = 1, ApplyFont = true, ApplyFill = true, ApplyBorder = true, ApplyAlignment = true,
                Alignment = new Alignment { Horizontal = HorizontalAlignmentValues.Center, Vertical = VerticalAlignmentValues.Center } },

            // 29: Positive Profit Currency White
            new CellFormat { FontId = 5, FillId = 0, BorderId = 1, NumberFormatId = 164u, ApplyFont = true, ApplyBorder = true, ApplyNumberFormat = true, ApplyAlignment = true,
                Alignment = new Alignment { Horizontal = HorizontalAlignmentValues.Right, Vertical = VerticalAlignmentValues.Center } },

            // 30: Positive Profit Currency Zebra
            new CellFormat { FontId = 5, FillId = 5, BorderId = 1, NumberFormatId = 164u, ApplyFont = true, ApplyFill = true, ApplyBorder = true, ApplyNumberFormat = true, ApplyAlignment = true,
                Alignment = new Alignment { Horizontal = HorizontalAlignmentValues.Right, Vertical = VerticalAlignmentValues.Center } },

            // 31: Negative Loss Currency White
            new CellFormat { FontId = 6, FillId = 0, BorderId = 1, NumberFormatId = 164u, ApplyFont = true, ApplyBorder = true, ApplyNumberFormat = true, ApplyAlignment = true,
                Alignment = new Alignment { Horizontal = HorizontalAlignmentValues.Right, Vertical = VerticalAlignmentValues.Center } },

            // 32: Negative Loss Currency Zebra
            new CellFormat { FontId = 6, FillId = 5, BorderId = 1, NumberFormatId = 164u, ApplyFont = true, ApplyFill = true, ApplyBorder = true, ApplyNumberFormat = true, ApplyAlignment = true,
                Alignment = new Alignment { Horizontal = HorizontalAlignmentValues.Right, Vertical = VerticalAlignmentValues.Center } }
        );

        return new Stylesheet(numberingFormats, fonts, fills, borders, cellStyleFormats, cellFormats);
    }
}
