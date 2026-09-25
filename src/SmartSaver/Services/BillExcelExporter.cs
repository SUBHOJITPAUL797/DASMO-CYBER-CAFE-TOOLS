using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using SmartSaver.Models;
using Serilog;

namespace SmartSaver.Services;

/// <summary>
/// Professional, finance-grade Excel (.xlsx) export engine for Cyber Cafe accounts.
/// Generates a multi-tab workbook with structured summaries, itemized registers, and totals.
/// Built with DocumentFormat.OpenXml for standalone performance without requiring Microsoft Office.
/// </summary>
public static class BillExcelExporter
{
    public static void ExportToExcel(
        IEnumerable<CustomerBillSession> bills,
        PrintBillingSettings settings,
        string outputPath)
    {
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

        // ── Tab 1: Billing Summary ──────────────────────────────────────────
        var summaryPart = workbookPart.AddNewPart<WorksheetPart>();
        var summaryData = new SheetData();
        BuildSummarySheet(summaryData, billList, settings);
        summaryPart.Worksheet = new Worksheet(summaryData);
        summaryPart.Worksheet.Save();

        sheets.Append(new Sheet
        {
            Id = workbookPart.GetIdOfPart(summaryPart),
            SheetId = 1,
            Name = "Billing Summary"
        });

        // ── Tab 2: Itemized Print & Xerox Register ─────────────────────────
        var registerPart = workbookPart.AddNewPart<WorksheetPart>();
        var registerData = new SheetData();
        BuildRegisterSheet(registerData, billList);
        registerPart.Worksheet = new Worksheet(registerData);
        registerPart.Worksheet.Save();

        sheets.Append(new Sheet
        {
            Id = workbookPart.GetIdOfPart(registerPart),
            SheetId = 2,
            Name = "Itemized Register"
        });

        workbookPart.Workbook.Save();
        Log.Information("Finance Excel report exported successfully to {Path}", outputPath);
    }

    private static void BuildSummarySheet(SheetData data, List<CustomerBillSession> bills, PrintBillingSettings settings)
    {
        uint r = 1;

        // Title Row
        data.Append(CreateRow(r++, new[]
        {
            CellText($"{settings.ShopName.ToUpperInvariant()} — CUSTOMER BILLING & SALES SUMMARY", style: 1),
            CellEmpty(), CellEmpty(), CellEmpty(), CellEmpty(), CellEmpty(), CellEmpty(), CellEmpty(), CellEmpty(), CellEmpty(), CellEmpty()
        }));

        data.Append(CreateRow(r++, new[]
        {
            CellText($"Report Generated: {DateTime.Now:dd/MM/yyyy hh:mm tt}  |  Address: {settings.ShopAddress}  |  Phone: {settings.ShopPhone}", style: 0)
        }));

        data.Append(CreateRow(r++, Array.Empty<Cell>())); // Empty spacer

        // Header Row
        data.Append(CreateRow(r++, new[]
        {
            CellText("Bill No", style: 1),
            CellText("Date & Time", style: 1),
            CellText("Customer Name", style: 1),
            CellText("Mobile", style: 1),
            CellText("Payment Mode", style: 1),
            CellText("Total Impressions", style: 1),
            CellText("Total Sheets", style: 1),
            CellText("B&W Pages", style: 1),
            CellText("Color Pages", style: 1),
            CellText("Duplex Sheets", style: 1),
            CellText("Grand Total (₹)", style: 1),
            CellText("Notes / Remarks", style: 1)
        }));

        double grandTotal = 0;
        int grandPages = 0;
        int grandSheets = 0;
        int grandBw = 0;
        int grandColor = 0;
        int grandDuplex = 0;

        foreach (var b in bills)
        {
            int bw = b.Jobs.Where(j => !j.IsColor).Sum(j => j.TotalImpressions);
            int color = b.Jobs.Where(j => j.IsColor).Sum(j => j.TotalImpressions);
            int duplex = b.Jobs.Where(j => j.IsDuplex).Sum(j => j.SheetsUsed);

            grandTotal += b.TotalAmount;
            grandPages += b.TotalPages;
            grandSheets += b.TotalSheets;
            grandBw += bw;
            grandColor += color;
            grandDuplex += duplex;

            data.Append(CreateRow(r++, new[]
            {
                CellText(b.BillNumber, style: 0),
                CellText(b.BilledAt.ToString("dd/MM/yyyy hh:mm tt"), style: 0),
                CellText(b.CustomerName, style: 0),
                CellText(b.CustomerPhone, style: 0),
                CellText(b.PaymentMode, style: 0),
                CellNumber(b.TotalPages),
                CellNumber(b.TotalSheets),
                CellNumber(bw),
                CellNumber(color),
                CellNumber(duplex),
                CellMoney(b.TotalAmount),
                CellText(b.Notes, style: 0)
            }));
        }

        // Totals Row
        data.Append(CreateRow(r++, new[]
        {
            CellText("TOTAL", style: 1),
            CellText($"{bills.Count} Bills", style: 1),
            CellEmpty(), CellEmpty(), CellEmpty(),
            CellNumber(grandPages, style: 1),
            CellNumber(grandSheets, style: 1),
            CellNumber(grandBw, style: 1),
            CellNumber(grandColor, style: 1),
            CellNumber(grandDuplex, style: 1),
            CellMoney(grandTotal, style: 2), // Bold money
            CellEmpty()
        }));
    }

    private static void BuildRegisterSheet(SheetData data, List<CustomerBillSession> bills)
    {
        uint r = 1;

        // Header Row
        data.Append(CreateRow(r++, new[]
        {
            CellText("Bill No", style: 1),
            CellText("Date", style: 1),
            CellText("Customer Name", style: 1),
            CellText("Document / Job Name", style: 1),
            CellText("Category", style: 1),
            CellText("Sides (Duplex/Single)", style: 1),
            CellText("Color Mode", style: 1),
            CellText("Pages", style: 1),
            CellText("Copies", style: 1),
            CellText("Total Impressions", style: 1),
            CellText("Sheets Used", style: 1),
            CellText("Rate / Unit (₹)", style: 1),
            CellText("Line Total (₹)", style: 1)
        }));

        double totalRevenue = 0;
        int totalImpressions = 0;
        int totalSheets = 0;

        foreach (var b in bills)
        {
            foreach (var j in b.Jobs)
            {
                totalRevenue += j.TotalCost;
                totalImpressions += j.TotalImpressions;
                totalSheets += j.SheetsUsed;

                data.Append(CreateRow(r++, new[]
                {
                    CellText(b.BillNumber, style: 0),
                    CellText(b.BilledAt.ToString("dd/MM/yyyy"), style: 0),
                    CellText(b.CustomerName, style: 0),
                    CellText(j.DocumentName, style: 0),
                    CellText(j.ItemCategory, style: 0),
                    CellText(j.IsDuplex ? "Duplex (2-Sided)" : "Single-Sided", style: 0),
                    CellText(j.IsColor ? "Color" : "B&W", style: 0),
                    CellNumber(j.Pages),
                    CellNumber(j.Copies),
                    CellNumber(j.TotalImpressions),
                    CellNumber(j.SheetsUsed),
                    CellMoney(j.RatePerUnit),
                    CellMoney(j.TotalCost)
                }));
            }
        }

        // Totals Row
        data.Append(CreateRow(r++, new[]
        {
            CellText("TOTAL", style: 1),
            CellEmpty(), CellEmpty(), CellEmpty(), CellEmpty(), CellEmpty(), CellEmpty(),
            CellEmpty(), CellEmpty(),
            CellNumber(totalImpressions, style: 1),
            CellNumber(totalSheets, style: 1),
            CellEmpty(),
            CellMoney(totalRevenue, style: 2)
        }));
    }

    // ── Helper Constructors ────────────────────────────────────────────────
    private static Cell CellText(string? text, uint style = 0) =>
        new Cell
        {
            DataType = CellValues.InlineString,
            StyleIndex = style,
            InlineString = new InlineString(new Text(text ?? string.Empty))
        };

    private static Cell CellEmpty() =>
        new Cell
        {
            DataType = CellValues.InlineString,
            StyleIndex = 0,
            InlineString = new InlineString(new Text(string.Empty))
        };

    private static Cell CellNumber(int val, uint style = 0) =>
        new Cell
        {
            DataType = CellValues.Number,
            StyleIndex = style,
            CellValue = new CellValue(val)
        };

    private static Cell CellMoney(double val, uint style = 0) =>
        new Cell
        {
            DataType = CellValues.Number,
            StyleIndex = style,
            CellValue = new CellValue(Math.Round(val, 2))
        };

    private static Row CreateRow(uint rowIndex, IEnumerable<Cell> cells)
    {
        var row = new Row { RowIndex = rowIndex };
        foreach (var c in cells) row.Append(c);
        return row;
    }

    private static Stylesheet CreateStylesheet()
    {
        // 0 = Normal, 1 = Bold Header, 2 = Bold Green/Money
        return new Stylesheet(
            new Fonts(
                new DocumentFormat.OpenXml.Spreadsheet.Font(new FontSize { Val = 10 }, new FontName { Val = "Calibri" }),                      // 0: Regular
                new DocumentFormat.OpenXml.Spreadsheet.Font(new Bold(), new FontSize { Val = 10.5 }, new FontName { Val = "Calibri" }),        // 1: Bold Header
                new DocumentFormat.OpenXml.Spreadsheet.Font(new Bold(), new FontSize { Val = 11 }, new DocumentFormat.OpenXml.Spreadsheet.Color { Rgb = "10B981" }, new FontName { Val = "Calibri" }) // 2: Bold Accent
            ),
            new Fills(
                new Fill(new PatternFill { PatternType = PatternValues.None }),
                new Fill(new PatternFill { PatternType = PatternValues.Gray125 })
            ),
            new Borders(new Border()),
            new CellStyleFormats(new CellFormat()),
            new CellFormats(
                new CellFormat { FontId = 0, FillId = 0, BorderId = 0 }, // 0: Default
                new CellFormat { FontId = 1, FillId = 0, BorderId = 0, ApplyFont = true }, // 1: Bold
                new CellFormat { FontId = 2, FillId = 0, BorderId = 0, ApplyFont = true }  // 2: Bold Accent
            )
        );
    }
}
