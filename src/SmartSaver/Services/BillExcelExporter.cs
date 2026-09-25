using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using SmartSaver.Models;
using Serilog;

namespace SmartSaver.Services;

/// <summary>
/// Executive-Grade, Professional Financial Excel (.xlsx) Export & Synchronization Engine.
/// Generates pristine, boardroom-ready, 4-tab workbooks with:
/// - Tab 1: Daily Cash Drawer & Financial Journal (Executive KPI Cards, Till & UPI Reconciliation)
/// - Tab 2: Customer Billing Summary (Invoicing, Impressions, Revenue Ledger)
/// - Tab 3: Itemized Print & Xerox Register (Document-by-Document Audit Trail)
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
        string outputPath)
    {
        ExportFullFinanceWorkbook(bills, CashDrawerService.Instance.AllDays, settings, outputPath);
    }

    public static void ExportFullFinanceWorkbook(
        IEnumerable<CustomerBillSession> bills,
        IEnumerable<DailyCashRegister> registers,
        PrintBillingSettings settings,
        string outputPath)
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

        // ── Tab 3: Itemized Print & Xerox Register ─────────────────────────
        var registerPart = workbookPart.AddNewPart<WorksheetPart>();
        var registerData = new SheetData();
        var registerMerge = new MergeCells();
        var registerCols = BuildRegisterSheet(registerData, registerMerge, billList);
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
            Name = "Itemized Register"
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
        IEnumerable<DailyCashRegister> registers)
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
                ExportFullFinanceWorkbook(bills, registers, settings, tempFile);

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
            (10, 10, 18.0)  // J: Debt Status
        );

        uint r = 1;

        // Row 1: Merged Title Banner
        data.Append(CreateRow(r++, CreateMergedBannerCells($"{settings.ShopName.ToUpperInvariant()} — DAILY CASH DRAWER & FINANCIAL JOURNAL", 15, 10), 32.0));
        mergeCells.Append(new MergeCell { Reference = "A1:J1" });

        // Row 2: Merged Subtitle
        data.Append(CreateRow(r++, CreateMergedBannerCells($"Live Cyber Cafe Accounts Ledger  •  Current Date: {DateTime.Now:dd/MM/yyyy hh:mm tt}", 16, 10), 20.0));
        mergeCells.Append(new MergeCell { Reference = "A2:J2" });

        // Row 3: KPI Card Labels
        data.Append(CreateRow(r++, new[]
        {
            CellText("💵 CASH IN DRAWER", 17), CellEmpty(17),
            CellText("📱 ONLINE / BANK / UPI", 19), CellEmpty(19),
            CellText("📈 TODAY NET PROFIT", 21), CellEmpty(21),
            CellText("⭐ TOTAL REVENUE", 23), CellEmpty(23),
            CellText("⏳ UNPAID DUE (KHATA)", 25), CellEmpty(25)
        }, 20.0));
        mergeCells.Append(new MergeCell { Reference = "A3:B3" });
        mergeCells.Append(new MergeCell { Reference = "C3:D3" });
        mergeCells.Append(new MergeCell { Reference = "E3:F3" });
        mergeCells.Append(new MergeCell { Reference = "G3:H3" });
        mergeCells.Append(new MergeCell { Reference = "I3:J3" });

        // Row 4: KPI Card Values
        data.Append(CreateRow(r++, new[]
        {
            CellText($"₹ {todayReg.CurrentCashInDrawer:N2}", 18), CellEmpty(18),
            CellText($"₹ {todayReg.CurrentOnlineBalance:N2}", 20), CellEmpty(20),
            CellText($"₹ {todayReg.TodayNetProfit:N2}", 22), CellEmpty(22),
            CellText($"₹ {todayReg.TodayTotalRevenue:N2}", 24), CellEmpty(24),
            CellText($"₹ {todayReg.TotalCustomerUnpaidDebt:N2}", 26), CellEmpty(26)
        }, 26.0));
        mergeCells.Append(new MergeCell { Reference = "A4:B4" });
        mergeCells.Append(new MergeCell { Reference = "C4:D4" });
        mergeCells.Append(new MergeCell { Reference = "E4:F4" });
        mergeCells.Append(new MergeCell { Reference = "G4:H4" });
        mergeCells.Append(new MergeCell { Reference = "I4:J4" });

        // Row 5: Spacer
        data.Append(CreateRow(r++, Array.Empty<Cell>(), 10.0));

        // Row 6: Table Header
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
            CellText("Debt Status", 1)
        }, 26.0));

        double totalIn = 0;
        double totalOut = 0;

        List<CashTransaction> txList;
        lock (todayReg.Transactions)
        {
            txList = todayReg.Transactions.ToList();
        }

        bool zebra = false;
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

            data.Append(CreateRow(r++, new[]
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
            }, 22.0));

            zebra = !zebra;
        }

        // Totals Row
        data.Append(CreateRow(r++, new[]
        {
            CellText("TOTALS", 12),
            CellText($"{txList.Count} Total Entries", 12),
            CellText($"Revenue: ₹ {todayReg.TodayTotalRevenue:N2}", 12),
            CellText("Net Profit:", 12),
            CellMoney(todayReg.TodayNetProfit, 13),
            CellMoney(todayReg.TodayTotalCommission, 13),
            CellText($"Expenses: ₹ {todayReg.TodayTotalExpenses:N2}", 12),
            CellText($"Due: ₹ {todayReg.TotalCustomerUnpaidDebt:N2}", 12),
            CellText($"Cash: ₹ {todayReg.CurrentCashInDrawer:N2} | Bank: ₹ {todayReg.CurrentOnlineBalance:N2}", 12),
            CellText(todayReg.TotalCustomerUnpaidDebt > 0 ? "UNPAID DUE" : "BALANCED", 12)
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

    // ── Tab 3: Itemized Register ───────────────────────────────────────────
    private static Columns BuildRegisterSheet(
        SheetData data,
        MergeCells mergeCells,
        List<CustomerBillSession> bills)
    {
        var cols = CreateColumns(
            (1, 1, 20.0),  // A: Bill No
            (2, 2, 14.0),  // B: Date
            (3, 3, 22.0),  // C: Customer Name
            (4, 4, 32.0),  // D: Document Name
            (5, 5, 18.0),  // E: Category
            (6, 6, 16.0),  // F: Print Sides
            (7, 7, 14.0),  // G: Color Mode
            (8, 8, 12.0),  // H: Pages
            (9, 9, 12.0),  // I: Copies
            (10, 10, 14.0),// J: Impressions
            (11, 11, 14.0),// K: Sheets Used
            (12, 12, 14.0),// L: Unit Rate (₹)
            (13, 13, 18.0) // M: Total Cost (₹)
        );

        uint r = 1;

        // Row 1: Merged Title
        data.Append(CreateRow(r++, CreateMergedBannerCells("DASMO CYBER CAFE — ITEMIZED PRINT & XEROX AUDIT REGISTER", 15, 13), 32.0));
        mergeCells.Append(new MergeCell { Reference = "A1:M1" });

        // Row 2: Merged Subtitle
        data.Append(CreateRow(r++, CreateMergedBannerCells("Complete audit trail of all individual print jobs, photocopies, scans, and laminations", 16, 13), 20.0));
        mergeCells.Append(new MergeCell { Reference = "A2:M2" });

        // Row 3: Spacer
        data.Append(CreateRow(r++, Array.Empty<Cell>(), 10.0));

        // Row 4: Header
        data.Append(CreateRow(r++, new[]
        {
            CellText("Bill Number", 1),
            CellText("Date", 1),
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
            CellText("Total Cost (₹)", 2)
        }, 26.0));

        int totalImpressions = 0;
        int totalSheets = 0;
        double totalRevenue = 0;

        bool zebra = false;
        foreach (var b in bills)
        {
            foreach (var j in b.Jobs)
            {
                totalImpressions += j.TotalImpressions;
                totalSheets += j.SheetsUsed;
                totalRevenue += j.TotalCost;

                uint textLeft = zebra ? 5u : 4u;
                uint textCenter = zebra ? 7u : 6u;
                uint moneyRight = zebra ? 9u : 8u;
                uint numRight = zebra ? 11u : 10u;

                data.Append(CreateRow(r++, new[]
                {
                    CellText(b.BillNumber, textCenter),
                    CellText(b.BilledAt.ToString("dd/MM/yyyy"), textCenter),
                    CellText(b.CustomerName, textLeft),
                    CellText(j.DocumentName, textLeft),
                    CellText(j.ItemCategory, textCenter),
                    CellText(j.IsDuplex ? "Duplex (2-Sided)" : "Single-Sided", textCenter),
                    CellText(j.IsColor ? "Color" : "B&W", textCenter),
                    CellNumber(j.Pages, numRight),
                    CellNumber(j.Copies, numRight),
                    CellNumber(j.TotalImpressions, numRight),
                    CellNumber(j.SheetsUsed, numRight),
                    CellMoney(j.RatePerUnit, moneyRight),
                    CellMoney(j.TotalCost, moneyRight)
                }, 22.0));

                zebra = !zebra;
            }
        }

        // Totals Row
        data.Append(CreateRow(r++, new[]
        {
            CellText("TOTALS", 12),
            CellEmpty(12), CellEmpty(12), CellEmpty(12), CellEmpty(12), CellEmpty(12), CellEmpty(12),
            CellEmpty(12), CellEmpty(12),
            CellNumber(totalImpressions, 14),
            CellNumber(totalSheets, 14),
            CellEmpty(12),
            CellMoney(totalRevenue, 13)
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

        // Totals Row
        data.Append(CreateRow(r++, new[]
        {
            CellText("TOTALS", 12),
            CellText($"{registers.Count} Days", 12),
            CellEmpty(12),
            CellEmpty(12),
            CellEmpty(12),
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

    private static Cell CellText(string? text, uint style = 4) =>
        new Cell
        {
            DataType = CellValues.InlineString,
            StyleIndex = style,
            InlineString = new InlineString(new Text(text ?? string.Empty))
        };

    private static Cell CellEmpty(uint style = 4) =>
        new Cell
        {
            DataType = CellValues.InlineString,
            StyleIndex = style,
            InlineString = new InlineString(new Text(string.Empty))
        };

    private static Cell CellNumber(int val, uint style = 10) =>
        new Cell
        {
            DataType = CellValues.Number,
            StyleIndex = style,
            CellValue = new CellValue(val.ToString(CultureInfo.InvariantCulture))
        };

    private static Cell CellMoney(double val, uint style = 8) =>
        new Cell
        {
            DataType = CellValues.Number,
            StyleIndex = style,
            CellValue = new CellValue(Math.Round(val, 2).ToString("0.00", CultureInfo.InvariantCulture))
        };

    private static Row CreateRow(uint rowIndex, IEnumerable<Cell> cells, double height = 20.0)
    {
        var row = new Row
        {
            RowIndex = rowIndex,
            Height = height,
            CustomHeight = true
        };
        foreach (var c in cells) row.Append(c);
        return row;
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
