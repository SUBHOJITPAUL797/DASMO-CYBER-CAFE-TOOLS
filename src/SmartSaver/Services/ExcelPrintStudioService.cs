using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using PdfSharpCore;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using Serilog;

namespace SmartSaver.Services;

public enum ExcelPrintOrientation
{
    Auto,
    Portrait,
    Landscape
}

public enum ExcelCellAlignment
{
    Left,
    Center
}

public class ExcelColumnItem
{
    public int OriginalIndex { get; set; }
    public string HeaderName { get; set; } = string.Empty;
    public bool IsSelected { get; set; } = true;
    public double CalculatedWeight { get; set; } = 1.0;
}

public class ExcelSheetData
{
    public string SheetName { get; set; } = string.Empty;
    public List<string> Headers { get; set; } = new();
    public List<List<string>> Rows { get; set; } = new();
    public int TotalRows => Rows.Count;
    public int TotalColumns => Headers.Count;
}

public class ExcelWorkbookInfo
{
    public string FilePath { get; set; } = string.Empty;
    public List<string> SheetNames { get; set; } = new();
    public string SelectedSheetName { get; set; } = string.Empty;
    public ExcelSheetData CurrentSheet { get; set; } = new();
}

public class ExcelPrintConfig
{
    public ExcelPrintOrientation Orientation { get; set; } = ExcelPrintOrientation.Auto;
    public double MarginLeftMm { get; set; } = 5.0;
    public double MarginRightMm { get; set; } = 5.0;
    public double MarginTopMm { get; set; } = 6.0;
    public double MarginBottomMm { get; set; } = 6.0;
    public bool RepeatHeaderOnEveryPage { get; set; } = true;
    public int HeaderRowIndex { get; set; } = 1; // 1-based row
    public int StartDataRow { get; set; } = 1; // 1-based data row index (offset from data rows)
    public int EndDataRow { get; set; } = 0; // 0 means all rows
    public List<int> SelectedColumnIndices { get; set; } = new();
    public bool AddSerialNo { get; set; } = true;
    public double FontSize { get; set; } = 8.5; // points
    public bool ZebraStriping { get; set; } = true;
    public bool DrawGridlines { get; set; } = true;
    public string DocumentTitle { get; set; } = string.Empty;
    public string ShopFooter { get; set; } = "DASMO CYBER CAFE TOOLS";
    public bool AutoWrapCellText { get; set; } = true;
    public ExcelCellAlignment CellAlignment { get; set; } = ExcelCellAlignment.Left;
    public int GroupByColumnIndex { get; set; } = -1; // -1 means none
    public string FilterByColumnValue { get; set; } = string.Empty; // empty or "All" means all values
    public bool SortByGroupColumn { get; set; } = true; // organize identical values together
    public bool PageBreakOnGroupChange { get; set; } = false; // starts each group on a new page
    public bool RestartSerialNoPerGroup { get; set; } = false; // restart Sl No at 1 for each group
    public int MaxRowsPerPage { get; set; } = 0; // 0 means auto fit to A4, >0 splits page every N rows
}

public class ExcelPrintResult
{
    public bool Success { get; set; }
    public string PdfPath { get; set; } = string.Empty;
    public int TotalPages { get; set; }
    public int RowsPrinted { get; set; }
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// Professional, industry-grade Excel-to-A4 printing and formatting engine.
/// Reads .xlsx, .xlsm, .csv, .tsv, and HTML-based .xls sheets.
/// Automatically calculates proportional column widths, repeats headers on every page,
/// wraps cell text, fits edge-to-edge on A4, and outputs pristine vector PDFs.
/// </summary>
public static class ExcelPrintStudioService
{
    private const double MmToPoints = 72.0 / 25.4; // 1 mm = ~2.83465 pt

    #region Loading Workbooks and Sheets

    public static ExcelWorkbookInfo LoadWorkbook(string filePath, string? sheetName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (!File.Exists(filePath))
            throw new FileNotFoundException("File not found", filePath);

        string ext = Path.GetExtension(filePath).ToLowerInvariant();

        if (ext == ".csv" || ext == ".tsv" || ext == ".txt")
        {
            return LoadCsvWorkbook(filePath, ext == ".tsv" ? '\t' : ',');
        }

        // Check if .xls is actually an HTML or XML file (common in Indian govt portals)
        if (ext == ".xls")
        {
            if (IsHtmlOrXmlFile(filePath))
            {
                return LoadHtmlTableWorkbook(filePath);
            }
        }

        return LoadOpenXmlWorkbook(filePath, sheetName);
    }

    private static bool IsHtmlOrXmlFile(string filePath)
    {
        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            byte[] buffer = new byte[512];
            int read = fs.Read(buffer, 0, buffer.Length);
            string text = Encoding.UTF8.GetString(buffer, 0, read).TrimStart();
            return text.StartsWith("<html", StringComparison.OrdinalIgnoreCase) ||
                   text.StartsWith("<!doctype", StringComparison.OrdinalIgnoreCase) ||
                   text.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase) ||
                   text.StartsWith("<table", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static ExcelWorkbookInfo LoadOpenXmlWorkbook(string filePath, string? targetSheetName)
    {
        var info = new ExcelWorkbookInfo { FilePath = filePath };

        using var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var doc = SpreadsheetDocument.Open(fileStream, false);
        var wbPart = doc.WorkbookPart;
        if (wbPart == null)
            throw new InvalidOperationException("Invalid Excel document: Missing WorkbookPart.");

        // Read shared string table
        var sharedStrings = new List<string>();
        var sstPart = wbPart.GetPartsOfType<SharedStringTablePart>().FirstOrDefault();
        if (sstPart != null && sstPart.SharedStringTable != null)
        {
            foreach (var item in sstPart.SharedStringTable.Elements<SharedStringItem>())
            {
                sharedStrings.Add(item.InnerText ?? string.Empty);
            }
        }

        // List all sheets
        var sheets = wbPart.Workbook.Sheets?.Elements<Sheet>().ToList() ?? new List<Sheet>();
        foreach (var s in sheets)
        {
            if (!string.IsNullOrEmpty(s.Name?.Value))
                info.SheetNames.Add(s.Name.Value);
        }

        if (info.SheetNames.Count == 0)
            throw new InvalidOperationException("No worksheets found in this Excel file.");

        // Pick requested sheet or first sheet
        string sheetToLoad = targetSheetName != null && info.SheetNames.Contains(targetSheetName)
            ? targetSheetName
            : info.SheetNames[0];

        info.SelectedSheetName = sheetToLoad;

        var selectedSheet = sheets.FirstOrDefault(s => s.Name?.Value == sheetToLoad) ?? sheets.First();
        var wsPart = (WorksheetPart)wbPart.GetPartById(selectedSheet.Id!);

        var sheetData = wsPart.Worksheet.Elements<SheetData>().FirstOrDefault();
        if (sheetData == null)
        {
            info.CurrentSheet = new ExcelSheetData { SheetName = sheetToLoad };
            return info;
        }

        var allRows = new List<List<string>>();
        int maxCols = 0;

        foreach (var row in sheetData.Elements<Row>())
        {
            var cellValues = new List<string>();
            int currentColIdx = 0;

            foreach (var cell in row.Elements<Cell>())
            {
                int targetCol = GetColumnIndexFromCellReference(cell.CellReference?.Value, currentColIdx);

                // Pad missing empty cells
                while (cellValues.Count < targetCol)
                {
                    cellValues.Add(string.Empty);
                }

                string cellText = ExtractCellValue(cell, sharedStrings);
                cellValues.Add(cellText);
                currentColIdx = targetCol + 1;
            }

            // Trim trailing empty cells from row
            while (cellValues.Count > 0 && string.IsNullOrWhiteSpace(cellValues[^1]))
            {
                cellValues.RemoveAt(cellValues.Count - 1);
            }

            if (cellValues.Count > 0)
            {
                allRows.Add(cellValues);
                if (cellValues.Count > maxCols)
                    maxCols = cellValues.Count;
            }
        }

        // Standardize column counts across all rows
        foreach (var row in allRows)
        {
            while (row.Count < maxCols) row.Add(string.Empty);
        }

        var resultSheet = new ExcelSheetData
        {
            SheetName = sheetToLoad
        };

        if (allRows.Count > 0)
        {
            // Row 0 is header
            resultSheet.Headers = allRows[0].Select((h, i) => string.IsNullOrWhiteSpace(h) ? $"Column {i + 1}" : h.Trim()).ToList();
            if (allRows.Count > 1)
            {
                resultSheet.Rows = allRows.Skip(1).ToList();
            }
        }

        info.CurrentSheet = resultSheet;
        return info;
    }

    private static string ExtractCellValue(Cell cell, List<string> sharedStrings)
    {
        if (cell.DataType != null && cell.DataType.Value == CellValues.SharedString)
        {
            if (int.TryParse(cell.CellValue?.Text, out int sstIndex) && sstIndex >= 0 && sstIndex < sharedStrings.Count)
            {
                return sharedStrings[sstIndex];
            }
            return cell.InnerText ?? string.Empty;
        }

        if (cell.DataType != null && cell.DataType.Value == CellValues.InlineString)
        {
            return cell.InlineString?.InnerText ?? cell.InnerText ?? string.Empty;
        }

        if (cell.DataType != null && cell.DataType.Value == CellValues.Boolean)
        {
            return cell.CellValue?.Text == "1" ? "TRUE" : "FALSE";
        }

        return cell.CellValue?.Text ?? cell.InnerText ?? string.Empty;
    }

    private static int GetColumnIndexFromCellReference(string? cellReference, int fallbackCol)
    {
        if (string.IsNullOrEmpty(cellReference)) return fallbackCol;

        int col = 0;
        for (int i = 0; i < cellReference.Length; i++)
        {
            char c = cellReference[i];
            if (c >= 'A' && c <= 'Z')
            {
                col = col * 26 + (c - 'A' + 1);
            }
            else if (c >= 'a' && c <= 'z')
            {
                col = col * 26 + (c - 'a' + 1);
            }
            else
            {
                break;
            }
        }

        return col > 0 ? col - 1 : fallbackCol;
    }

    private static ExcelWorkbookInfo LoadCsvWorkbook(string filePath, char delimiter)
    {
        var info = new ExcelWorkbookInfo
        {
            FilePath = filePath,
            SheetNames = new List<string> { Path.GetFileNameWithoutExtension(filePath) },
            SelectedSheetName = Path.GetFileNameWithoutExtension(filePath)
        };

        var allRows = new List<List<string>>();
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, Encoding.UTF8, true);

        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var parts = ParseCsvLine(line, delimiter);
            allRows.Add(parts);
        }

        int maxCols = allRows.Count > 0 ? allRows.Max(r => r.Count) : 0;
        foreach (var r in allRows)
        {
            while (r.Count < maxCols) r.Add(string.Empty);
        }

        var sheetData = new ExcelSheetData
        {
            SheetName = info.SelectedSheetName
        };

        if (allRows.Count > 0)
        {
            sheetData.Headers = allRows[0].Select((h, i) => string.IsNullOrWhiteSpace(h) ? $"Column {i + 1}" : h.Trim()).ToList();
            if (allRows.Count > 1)
            {
                sheetData.Rows = allRows.Skip(1).ToList();
            }
        }

        info.CurrentSheet = sheetData;
        return info;
    }

    private static List<string> ParseCsvLine(string line, char delimiter)
    {
        var result = new List<string>();
        var sb = new StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];

            if (c == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    sb.Append('"');
                    i++; // skip escaped quote
                }
                else
                {
                    inQuotes = !inQuotes;
                }
            }
            else if (c == delimiter && !inQuotes)
            {
                result.Add(sb.ToString().Trim());
                sb.Clear();
            }
            else
            {
                sb.Append(c);
            }
        }

        result.Add(sb.ToString().Trim());
        return result;
    }

    private static ExcelWorkbookInfo LoadHtmlTableWorkbook(string filePath)
    {
        var info = new ExcelWorkbookInfo
        {
            FilePath = filePath,
            SheetNames = new List<string> { Path.GetFileNameWithoutExtension(filePath) },
            SelectedSheetName = Path.GetFileNameWithoutExtension(filePath)
        };

        using var htmlStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var htmlReader = new StreamReader(htmlStream, Encoding.UTF8, true);
        string html = htmlReader.ReadToEnd();
        var allRows = new List<List<string>>();

        // Extract <tr>...</tr> rows
        var rowMatches = Regex.Matches(html, @"<tr[^>]*>(.*?)</tr>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        foreach (Match rm in rowMatches)
        {
            string rowContent = rm.Groups[1].Value;
            var cellMatches = Regex.Matches(rowContent, @"<(?:td|th)[^>]*>(.*?)</(?:td|th)>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            var rowVals = new List<string>();

            foreach (Match cm in cellMatches)
            {
                string rawCell = cm.Groups[1].Value;
                // Strip tags & decode html entities
                string plainText = Regex.Replace(rawCell, @"<[^>]+>", " ");
                plainText = System.Net.WebUtility.HtmlDecode(plainText).Trim();
                rowVals.Add(plainText);
            }

            if (rowVals.Count > 0 && rowVals.Any(v => !string.IsNullOrWhiteSpace(v)))
            {
                allRows.Add(rowVals);
            }
        }

        int maxCols = allRows.Count > 0 ? allRows.Max(r => r.Count) : 0;
        foreach (var r in allRows)
        {
            while (r.Count < maxCols) r.Add(string.Empty);
        }

        var sheetData = new ExcelSheetData
        {
            SheetName = info.SelectedSheetName
        };

        if (allRows.Count > 0)
        {
            sheetData.Headers = allRows[0].Select((h, i) => string.IsNullOrWhiteSpace(h) ? $"Column {i + 1}" : h.Trim()).ToList();
            if (allRows.Count > 1)
            {
                sheetData.Rows = allRows.Skip(1).ToList();
            }
        }

        info.CurrentSheet = sheetData;
        return info;
    }

    #endregion

    #region Layout & Column Widths Calculation

    /// <summary>
    /// Calculates smart proportional widths for selected columns so that the total width
    /// EXACTLY equals the printable page width with zero horizontal cutoff.
    /// </summary>
    public static List<double> CalculateProportionalColumnWidths(
        ExcelSheetData sheet,
        List<int> selectedColumnIndices,
        double printableWidth,
        bool includeSerialNo,
        double serialNoWidth = 26.0)
    {
        var widths = new List<double>();
        if (selectedColumnIndices == null || selectedColumnIndices.Count == 0)
            return widths;

        double availableWidth = printableWidth;
        if (includeSerialNo)
        {
            availableWidth = Math.Max(100.0, availableWidth - serialNoWidth);
        }

        // Calculate character density weights for each column
        var weights = new List<double>();
        int sampleRows = Math.Min(sheet.Rows.Count, 300); // sample up to 300 rows for lightning speed

        foreach (int colIdx in selectedColumnIndices)
        {
            string header = colIdx < sheet.Headers.Count ? sheet.Headers[colIdx] : $"Col {colIdx + 1}";
            double maxHeaderLen = Math.Max(4.0, (double)header.Length);

            var lengths = new List<int>();
            for (int r = 0; r < sampleRows; r++)
            {
                var row = sheet.Rows[r];
                if (colIdx < row.Count)
                {
                    lengths.Add(row[colIdx]?.Length ?? 0);
                }
            }

            double cellWeight = 6.0;
            if (lengths.Count > 0)
            {
                lengths.Sort();
                // 90th percentile length
                int p90Idx = (int)(lengths.Count * 0.90);
                cellWeight = Math.Max(6.0, (double)lengths[p90Idx]);
            }

            // Weight combines header and typical cell content
            double weight = Math.Max(maxHeaderLen * 1.1, cellWeight);

            // Special heuristic: If column name or content looks like an ID, date, or phone, cap weight
            if (header.Contains("ID", StringComparison.OrdinalIgnoreCase) ||
                header.Contains("Date", StringComparison.OrdinalIgnoreCase) ||
                header.Contains("Mobile", StringComparison.OrdinalIgnoreCase) ||
                header.Contains("Phone", StringComparison.OrdinalIgnoreCase))
            {
                weight = Math.Min(weight, 16.0);
            }

            // If column name indicates Name or Address or Father, give higher minimum
            if (header.Contains("Name", StringComparison.OrdinalIgnoreCase) ||
                header.Contains("Father", StringComparison.OrdinalIgnoreCase) ||
                header.Contains("Address", StringComparison.OrdinalIgnoreCase) ||
                header.Contains("Village", StringComparison.OrdinalIgnoreCase))
            {
                weight = Math.Max(weight, 14.0);
            }

            weights.Add(weight);
        }

        double totalWeight = weights.Sum();
        if (totalWeight <= 0) totalWeight = 1.0;

        // Distribute availableWidth proportionally
        double minColWidth = 32.0; // minimum 32 pt (~11.3 mm)
        double allocatedWidth = 0.0;

        for (int i = 0; i < weights.Count; i++)
        {
            double w = (weights[i] / totalWeight) * availableWidth;
            w = Math.Max(minColWidth, w);
            widths.Add(w);
            allocatedWidth += w;
        }

        // Normalize so sum of widths equals EXACTLY availableWidth
        double scale = availableWidth / allocatedWidth;
        for (int i = 0; i < widths.Count; i++)
        {
            widths[i] = Math.Round(widths[i] * scale, 2);
        }

        // Adjust rounding residue onto the largest column
        double diff = availableWidth - widths.Sum();
        if (Math.Abs(diff) > 0.01 && widths.Count > 0)
        {
            int maxIdx = 0;
            double maxW = widths[0];
            for (int i = 1; i < widths.Count; i++)
            {
                if (widths[i] > maxW)
                {
                    maxW = widths[i];
                    maxIdx = i;
                }
            }
            widths[maxIdx] += diff;
        }

        return widths;
    }

    #endregion

    #region PDF Generation

    /// <summary>
    /// Generates a vector A4 PDF with repeated headers on every page, smart column widths,
    /// configurable 4-side margins, zebra striping, and cell text wrapping.
    /// </summary>
    public static ExcelPrintResult GeneratePrintPdf(ExcelSheetData sheet, ExcelPrintConfig config, string outputPath)
    {
        try
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

            var document = new PdfDocument();
            document.Info.Title = string.IsNullOrWhiteSpace(config.DocumentTitle) ? sheet.SheetName : config.DocumentTitle;
            document.Info.Author = config.ShopFooter;
            document.Info.Subject = "A4 Smart Table Print";
            document.Info.Creator = "DASMO CYBER CAFE TOOLS";

            // Determine Page Orientation
            bool isLandscape;
            int numCols = config.SelectedColumnIndices.Count + (config.AddSerialNo ? 1 : 0);

            if (config.Orientation == ExcelPrintOrientation.Landscape)
            {
                isLandscape = true;
            }
            else if (config.Orientation == ExcelPrintOrientation.Portrait)
            {
                isLandscape = false;
            }
            else
            {
                // Auto: If more than 5 columns or total headers length > 50, landscape is far better
                int totalHeaderChars = config.SelectedColumnIndices.Sum(i => i < sheet.Headers.Count ? sheet.Headers[i].Length : 8);
                isLandscape = numCols > 5 || totalHeaderChars > 50;
            }

            // A4 dimensions in PDF points
            double pageWidth = isLandscape ? 841.89 : 595.28;
            double pageHeight = isLandscape ? 595.28 : 841.89;

            // Convert mm margins to PDF points
            double marginLeft = Math.Max(2.0, config.MarginLeftMm) * MmToPoints;
            double marginRight = Math.Max(2.0, config.MarginRightMm) * MmToPoints;
            double marginTop = Math.Max(2.0, config.MarginTopMm) * MmToPoints;
            double marginBottom = Math.Max(2.0, config.MarginBottomMm) * MmToPoints;

            double printableWidth = pageWidth - marginLeft - marginRight;
            double printableHeight = pageHeight - marginTop - marginBottom;

            if (printableWidth < 100 || printableHeight < 100)
            {
                return new ExcelPrintResult
                {
                    Success = false,
                    ErrorMessage = "Margins are too large for A4 paper. Please reduce margins."
                };
            }

            // Typography & Fonts
            string fontName = "Arial";
            var fontTitle = new XFont(fontName, 11, XFontStyle.Bold);
            var fontHeader = new XFont(fontName, config.FontSize, XFontStyle.Bold);
            var fontCell = new XFont(fontName, config.FontSize, XFontStyle.Regular);
            var fontCellBold = new XFont(fontName, config.FontSize, XFontStyle.Bold);
            var fontFooter = new XFont(fontName, 7.5, XFontStyle.Regular);

            // Palette
            var cHeaderBg = XColor.FromArgb(30, 41, 59); // Slate Dark #1E293B
            var cHeaderText = XColors.White;
            var cZebraEven = XColors.White;
            var cZebraOdd = XColor.FromArgb(248, 250, 252); // #F8FAFC
            var cBorder = XColor.FromArgb(203, 213, 225); // Hairline Slate #CBD5E1
            var cTextDark = XColor.FromArgb(15, 23, 42); // Slate 900
            var cTextMuted = XColor.FromArgb(100, 116, 139);

            var penBorder = new XPen(cBorder, 0.5);
            var brushZebraEven = new XSolidBrush(cZebraEven);
            var brushZebraOdd = new XSolidBrush(cZebraOdd);
            var brushHeader = new XSolidBrush(cHeaderBg);
            var brushText = new XSolidBrush(cTextDark);
            var brushHeaderText = new XSolidBrush(cHeaderText);

            // Filter columns
            var selectedCols = config.SelectedColumnIndices.Where(i => i >= 0 && i < sheet.Headers.Count).ToList();
            if (selectedCols.Count == 0)
            {
                // Fallback to all columns
                selectedCols = Enumerable.Range(0, sheet.Headers.Count).ToList();
            }

            // Calculate column widths
            double serialColWidth = config.AddSerialNo ? Math.Max(24.0, config.FontSize * 3.2) : 0.0;
            var colWidths = CalculateProportionalColumnWidths(sheet, selectedCols, printableWidth, config.AddSerialNo, serialColWidth);

            // Slice rows based on StartDataRow and EndDataRow
            int startIdx = Math.Max(0, config.StartDataRow - 1);
            int endIdx = config.EndDataRow > 0 ? Math.Min(sheet.Rows.Count, config.EndDataRow) : sheet.Rows.Count;
            var dataRows = sheet.Rows.Skip(startIdx).Take(Math.Max(0, endIdx - startIdx)).ToList();

            // Apply Column Grouping / Filtering
            if (config.GroupByColumnIndex >= 0 && config.GroupByColumnIndex < sheet.Headers.Count)
            {
                int groupCol = config.GroupByColumnIndex;
                string filterVal = config.FilterByColumnValue?.Trim() ?? string.Empty;

                if (!string.IsNullOrEmpty(filterVal) &&
                    !filterVal.Equals("All", StringComparison.OrdinalIgnoreCase) &&
                    !filterVal.Equals("(All)", StringComparison.OrdinalIgnoreCase) &&
                    !filterVal.StartsWith("All (", StringComparison.OrdinalIgnoreCase))
                {
                    dataRows = dataRows.Where(r =>
                        groupCol < r.Count &&
                        string.Equals(r[groupCol]?.Trim(), filterVal, StringComparison.OrdinalIgnoreCase)
                    ).ToList();
                }

                if (config.SortByGroupColumn)
                {
                    // Stable sort keeps identical group rows in their original relative order while clustering groups together
                    dataRows = dataRows.OrderBy(r =>
                        groupCol < r.Count ? (r[groupCol]?.Trim() ?? string.Empty) : string.Empty,
                        StringComparer.CurrentCultureIgnoreCase
                    ).ToList();
                }
            }

            // Calculate heights
            bool hasTitle = !string.IsNullOrWhiteSpace(config.DocumentTitle);
            double titleHeight = hasTitle ? 22.0 : 0.0;
            double footerHeight = 16.0;

            // Header & cell line heights
            double headerLineHeight = config.FontSize * 1.25;
            double cellLineHeight = config.FontSize * 1.25;
            double minRowHeight = Math.Max(13.5, cellLineHeight + 4.5);

            // Measure headers to allow clean multi-line wrapping if needed
            int maxHeaderLines = 1;
            using (var tempDoc = new PdfDocument())
            {
                var tempPage = tempDoc.AddPage();
                using var tempGfx = XGraphics.FromPdfPage(tempPage);

                for (int i = 0; i < selectedCols.Count; i++)
                {
                    int colIdx = selectedCols[i];
                    double w = colWidths[i];
                    string headerText = colIdx < sheet.Headers.Count ? sheet.Headers[colIdx] : $"Col {colIdx + 1}";
                    var hLines = WrapText(headerText, fontHeader, tempGfx, Math.Max(10.0, w - 6.0));
                    if (hLines.Count > maxHeaderLines)
                    {
                        maxHeaderLines = Math.Min(3, hLines.Count);
                    }
                }
            }

            double headerRowHeight = Math.Max(18.0, (maxHeaderLines * headerLineHeight) + 8.0);

            // Pagination engine
            var pages = new List<List<MeasuredRow>>();
            var currentPageRows = new List<MeasuredRow>();
            double currentYAvailable = printableHeight - (hasTitle ? titleHeight : 0) - headerRowHeight - footerHeight;

            int rowNumber = startIdx + 1;
            int groupSerialNo = 1;
            string? lastGroupVal = null;

            // Temporary measurement graphics
            using (var tempDoc = new PdfDocument())
            {
                var tempPage = tempDoc.AddPage();
                using var tempGfx = XGraphics.FromPdfPage(tempPage);

                foreach (var rowData in dataRows)
                {
                    string currentGroupVal = string.Empty;
                    if (config.GroupByColumnIndex >= 0 && config.GroupByColumnIndex < rowData.Count)
                    {
                        currentGroupVal = rowData[config.GroupByColumnIndex]?.Trim() ?? string.Empty;
                    }

                    bool isGroupChanged = config.GroupByColumnIndex >= 0 &&
                                          lastGroupVal != null &&
                                          !string.Equals(currentGroupVal, lastGroupVal, StringComparison.OrdinalIgnoreCase);

                    bool rowLimitReached = config.MaxRowsPerPage > 0 && currentPageRows.Count >= config.MaxRowsPerPage;

                    // If page break on group change is active, end the page immediately when group value changes!
                    if (isGroupChanged && config.PageBreakOnGroupChange && currentPageRows.Count > 0)
                    {
                        pages.Add(currentPageRows);
                        currentPageRows = new List<MeasuredRow>();
                        currentYAvailable = printableHeight - (hasTitle ? titleHeight : 0) - headerRowHeight - footerHeight;

                        if (config.RestartSerialNoPerGroup)
                        {
                            groupSerialNo = 1;
                        }
                    }
                    else if (rowLimitReached && currentPageRows.Count > 0)
                    {
                        // Fixed rows per page limit reached!
                        pages.Add(currentPageRows);
                        currentPageRows = new List<MeasuredRow>();
                        currentYAvailable = printableHeight - (hasTitle ? titleHeight : 0) - headerRowHeight - footerHeight;

                        if (isGroupChanged && config.RestartSerialNoPerGroup)
                        {
                            groupSerialNo = 1;
                        }
                    }
                    else if (isGroupChanged && config.RestartSerialNoPerGroup)
                    {
                        groupSerialNo = 1;
                    }

                    lastGroupVal = currentGroupVal;

                    int displayRowNumber = config.RestartSerialNoPerGroup ? groupSerialNo : rowNumber;
                    var measured = MeasureDataRow(rowData, selectedCols, colWidths, fontCell, tempGfx, minRowHeight, cellLineHeight, displayRowNumber);
                    rowNumber++;
                    groupSerialNo++;

                    if (currentPageRows.Count > 0 && (currentYAvailable - measured.RowHeight < 0))
                    {
                        // Page full, start next page
                        pages.Add(currentPageRows);
                        currentPageRows = new List<MeasuredRow>();
                        currentYAvailable = printableHeight - (hasTitle ? titleHeight : 0) - headerRowHeight - footerHeight;
                    }

                    currentPageRows.Add(measured);
                    currentYAvailable -= measured.RowHeight;
                }

                if (currentPageRows.Count > 0)
                {
                    pages.Add(currentPageRows);
                }
            }

            if (pages.Count == 0)
            {
                // Empty sheet: at least create 1 page with header
                pages.Add(new List<MeasuredRow>());
            }

            int totalPages = pages.Count;
            bool isCenter = config.CellAlignment == ExcelCellAlignment.Center;

            // Render all pages
            for (int pageIdx = 0; pageIdx < totalPages; pageIdx++)
            {
                var page = document.AddPage();
                page.Size = PageSize.A4;
                page.Orientation = isLandscape ? PageOrientation.Landscape : PageOrientation.Portrait;

                using var gfx = XGraphics.FromPdfPage(page);
                double y = marginTop;

                // 1. Draw Document Title Header (on every page, or page 1)
                if (hasTitle)
                {
                    double subtitleWidth = 160.0;
                    double titleWidth = Math.Max(40.0, printableWidth - subtitleWidth);
                    var titleRect = new XRect(marginLeft, y, titleWidth, titleHeight - 4);
                    var subRect = new XRect(marginLeft + titleWidth, y, subtitleWidth, titleHeight - 4);

                    gfx.DrawString(config.DocumentTitle, fontTitle, new XSolidBrush(cHeaderBg), titleRect, XStringFormats.CenterLeft);

                    // Sheet subtitle / timestamp on right
                    string subtitle = $"{sheet.SheetName} • {DateTime.Now:dd/MM/yyyy}";
                    gfx.DrawString(subtitle, fontFooter, new XSolidBrush(cTextMuted), subRect, XStringFormats.CenterRight);

                    y += titleHeight;
                }

                // 2. Draw Table Header Row (Repeated on EVERY single page!)
                if (config.RepeatHeaderOnEveryPage || pageIdx == 0)
                {
                    DrawTableHeader(gfx, marginLeft, y, printableWidth, headerRowHeight, config.AddSerialNo, serialColWidth, selectedCols, sheet.Headers, colWidths, fontHeader, brushHeader, brushHeaderText, penBorder, config.DrawGridlines, headerLineHeight, isCenter);
                    y += headerRowHeight;
                }

                // 3. Draw Data Rows for this page
                var pageRows = pages[pageIdx];
                for (int r = 0; r < pageRows.Count; r++)
                {
                    var row = pageRows[r];
                    bool isZebra = config.ZebraStriping && (r % 2 == 1);
                    var bgBrush = isZebra ? brushZebraOdd : brushZebraEven;

                    DrawDataRow(gfx, marginLeft, y, printableWidth, row, config.AddSerialNo, serialColWidth, colWidths, fontCell, brushText, bgBrush, penBorder, config.DrawGridlines, cellLineHeight, isCenter);
                    y += row.RowHeight;
                }

                // 4. Draw Footer (Page Number and Shop Stamp)
                double footerY = pageHeight - marginBottom - footerHeight + 4;
                gfx.DrawLine(penBorder, marginLeft, footerY - 2, marginLeft + printableWidth, footerY - 2);

                string footerLeft = string.IsNullOrWhiteSpace(config.ShopFooter)
                    ? "DASMO CYBER CAFE TOOLS"
                    : config.ShopFooter;
                gfx.DrawString(footerLeft, fontFooter, new XSolidBrush(cTextMuted), marginLeft, footerY + 8);

                string footerRight = $"Page {pageIdx + 1} of {totalPages}";
                var footerRect = new XRect(marginLeft, footerY, printableWidth, footerHeight);
                gfx.DrawString(footerRight, fontFooter, new XSolidBrush(cTextMuted), footerRect, XStringFormats.TopRight);
            }

            document.Save(outputPath);
            Log.Information("ExcelPrintStudio: Generated {Pages} pages PDF at {Path}", totalPages, outputPath);

            return new ExcelPrintResult
            {
                Success = true,
                PdfPath = outputPath,
                TotalPages = totalPages,
                RowsPrinted = dataRows.Count
            };
        }
        catch (Exception ex)
        {
            Log.Error(ex, "ExcelPrintStudio: Failed to generate PDF");
            return new ExcelPrintResult
            {
                Success = false,
                ErrorMessage = ex.Message
            };
        }
    }

    private static void DrawTableHeader(
        XGraphics gfx,
        double xStart,
        double y,
        double totalWidth,
        double rowHeight,
        bool addSerialNo,
        double serialWidth,
        List<int> selectedCols,
        List<string> headers,
        List<double> colWidths,
        XFont font,
        XBrush bgBrush,
        XBrush textBrush,
        XPen borderPen,
        bool drawGridlines,
        double lineHeight,
        bool isCenter)
    {
        // Fill header background across entire width
        gfx.DrawRectangle(bgBrush, xStart, y, totalWidth, rowHeight);

        double curX = xStart;

        // Serial No. Header
        if (addSerialNo)
        {
            var cellRect = new XRect(curX, y, serialWidth, rowHeight);
            if (drawGridlines)
            {
                gfx.DrawRectangle(borderPen, cellRect);
            }
            gfx.DrawString("Sl No", font, textBrush, cellRect, XStringFormats.Center);
            curX += serialWidth;
        }

        // Data Column Headers
        for (int i = 0; i < selectedCols.Count; i++)
        {
            int colIdx = selectedCols[i];
            double w = colWidths[i];
            string headerText = colIdx < headers.Count ? headers[colIdx] : $"Col {colIdx + 1}";

            var cellRect = new XRect(curX, y, w, rowHeight);
            if (drawGridlines)
            {
                gfx.DrawRectangle(borderPen, cellRect);
            }

            // Cell text with 3pt padding and multi-line wrapping
            var lines = WrapText(headerText, font, gfx, Math.Max(10.0, w - 6.0));
            double totalTextHeight = lines.Count * lineHeight;
            double startY = y + Math.Max(2.0, (rowHeight - totalTextHeight) / 2.0) + (font.Size * 0.82);
            double textY = startY;

            foreach (var line in lines)
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    double lineX;
                    if (isCenter)
                    {
                        double lineWidth = gfx.MeasureString(line, font).Width;
                        lineX = Math.Max(curX + 2.0, curX + (w - lineWidth) / 2.0);
                    }
                    else
                    {
                        lineX = curX + 3.0;
                    }
                    gfx.DrawString(line, font, textBrush, lineX, textY);
                }
                textY += lineHeight;
            }

            curX += w;
        }
    }

    private static void DrawDataRow(
        XGraphics gfx,
        double xStart,
        double y,
        double totalWidth,
        MeasuredRow row,
        bool addSerialNo,
        double serialWidth,
        List<double> colWidths,
        XFont font,
        XBrush textBrush,
        XBrush bgBrush,
        XPen borderPen,
        bool drawGridlines,
        double lineHeight,
        bool isCenter)
    {
        // Background
        gfx.DrawRectangle(bgBrush, xStart, y, totalWidth, row.RowHeight);

        double curX = xStart;

        // Serial No. Cell
        if (addSerialNo)
        {
            var cellRect = new XRect(curX, y, serialWidth, row.RowHeight);
            if (drawGridlines)
            {
                gfx.DrawRectangle(borderPen, cellRect);
            }
            gfx.DrawString(row.RowNumber.ToString(), font, textBrush, cellRect, XStringFormats.Center);
            curX += serialWidth;
        }

        // Data Cells
        for (int i = 0; i < row.CellLines.Count; i++)
        {
            double w = colWidths[i];
            var cellRect = new XRect(curX, y, w, row.RowHeight);
            if (drawGridlines)
            {
                gfx.DrawRectangle(borderPen, cellRect);
            }

            var lines = row.CellLines[i];
            if (lines.Count > 0)
            {
                double totalTextHeight = lines.Count * lineHeight;
                double startY = y + Math.Max(2.0, (row.RowHeight - totalTextHeight) / 2.0) + (font.Size * 0.82);
                double textY = startY;

                foreach (var line in lines)
                {
                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        double lineX;
                        if (isCenter)
                        {
                            double lineWidth = gfx.MeasureString(line, font).Width;
                            lineX = Math.Max(curX + 2.0, curX + (w - lineWidth) / 2.0);
                        }
                        else
                        {
                            lineX = curX + 3.0;
                        }
                        gfx.DrawString(line, font, textBrush, lineX, textY);
                    }
                    textY += lineHeight;
                }
            }

            curX += w;
        }
    }

    private class MeasuredRow
    {
        public int RowNumber { get; set; }
        public double RowHeight { get; set; }
        public List<List<string>> CellLines { get; set; } = new();
    }

    private static MeasuredRow MeasureDataRow(
        List<string> rowData,
        List<int> selectedCols,
        List<double> colWidths,
        XFont font,
        XGraphics gfx,
        double minRowHeight,
        double lineHeight,
        int rowNumber)
    {
        var measured = new MeasuredRow
        {
            RowNumber = rowNumber
        };

        int maxLines = 1;

        for (int i = 0; i < selectedCols.Count; i++)
        {
            int colIdx = selectedCols[i];
            double w = colWidths[i];
            string text = colIdx < rowData.Count ? rowData[colIdx] ?? string.Empty : string.Empty;

            double maxTextWidth = Math.Max(10.0, w - 6.0); // 3pt padding on each side
            var lines = WrapText(text, font, gfx, maxTextWidth);
            measured.CellLines.Add(lines);

            if (lines.Count > maxLines)
                maxLines = lines.Count;
        }

        // Calculate final row height based on number of wrapped lines
        double contentHeight = (maxLines * lineHeight) + 6.0;
        measured.RowHeight = Math.Max(minRowHeight, contentHeight);

        return measured;
    }

    private static List<string> WrapText(string text, XFont font, XGraphics gfx, double maxWidth)
    {
        var lines = new List<string>();
        if (string.IsNullOrWhiteSpace(text))
        {
            lines.Add(string.Empty);
            return lines;
        }

        // Split by existing hard line breaks first (Alt+Enter in Excel)
        var paragraphs = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        foreach (var paragraph in paragraphs)
        {
            string p = paragraph.Trim();
            if (string.IsNullOrEmpty(p))
            {
                lines.Add(string.Empty);
                continue;
            }

            // Check if entire paragraph fits in one line
            var totalSize = gfx.MeasureString(p, font);
            if (totalSize.Width <= maxWidth)
            {
                lines.Add(p);
                continue;
            }

            var words = p.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var currentLine = new StringBuilder();

            foreach (var word in words)
            {
                string testLine = currentLine.Length == 0 ? word : currentLine + " " + word;
                var size = gfx.MeasureString(testLine, font);

                if (size.Width <= maxWidth)
                {
                    currentLine.Append(currentLine.Length == 0 ? word : " " + word);
                }
                else
                {
                    if (currentLine.Length > 0)
                    {
                        lines.Add(currentLine.ToString());
                        currentLine.Clear();
                    }

                    // If a single word is wider than maxWidth, break word into characters with hyphenation
                    var wordSize = gfx.MeasureString(word, font);
                    if (wordSize.Width > maxWidth)
                    {
                        var wordSb = new StringBuilder();
                        for (int c = 0; c < word.Length; c++)
                        {
                            wordSb.Append(word[c]);
                            if (gfx.MeasureString(wordSb.ToString() + "-", font).Width > maxWidth)
                            {
                                if (wordSb.Length > 1)
                                {
                                    wordSb.Length--;
                                    lines.Add(wordSb.ToString() + "-");
                                    wordSb.Clear();
                                    wordSb.Append(word[c]);
                                }
                                else
                                {
                                    lines.Add(wordSb.ToString());
                                    wordSb.Clear();
                                }
                            }
                        }
                        if (wordSb.Length > 0)
                        {
                            currentLine.Append(wordSb.ToString());
                        }
                    }
                    else
                    {
                        currentLine.Append(word);
                    }
                }
            }

            if (currentLine.Length > 0)
            {
                lines.Add(currentLine.ToString());
            }
        }

        if (lines.Count == 0)
        {
            lines.Add(string.Empty);
        }

        return lines;
    }

    #endregion
}
