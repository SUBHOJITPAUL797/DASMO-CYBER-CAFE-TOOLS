using System;
using System.IO;
using PdfSharpCore.Pdf;
using PdfSharpCore.Drawing;
using SmartSaver.Models;
using Serilog;

namespace SmartSaver.Services;

/// <summary>
/// Generates a professional, branded PDF bill receipt for customer billing sessions.
/// Uses PdfSharpCore for high-fidelity rendering suitable for WhatsApp sharing and printing.
/// </summary>
public static class BillPdfGenerator
{
    /// <summary>
    /// Generates a PDF bill and saves it to the specified path.
    /// Returns the output path on success, null on failure.
    /// </summary>
    public static string? GenerateBillPdf(CustomerBillSession session, PrintBillingSettings settings, string outputPath)
    {
        try
        {
            var document = new PdfDocument();
            document.Info.Title = $"Bill {session.BillNumber} — {settings.ShopName}";
            document.Info.Author = settings.ShopName;
            document.Info.Subject = "Print & Xerox Bill Receipt";
            document.Info.Creator = "DASMO CYBER CAFE TOOLS";

            var page = document.AddPage();
            page.Size = PdfSharpCore.PageSize.A4;
            using var gfx = XGraphics.FromPdfPage(page);

            double pageWidth = page.Width.Point;
            double pageHeight = page.Height.Point;
            double margin = 36; // 0.5 inch
            double contentWidth = pageWidth - (2 * margin);
            double y = margin;

            // ── Palette ────────────────────────────────────────────────
            var cPrimaryDark = XColor.FromArgb(24, 28, 44);     // Deep midnight
            var cAccentGreen = XColor.FromArgb(16, 185, 129);   // Modern Emerald #10B981
            var cAccentTeal  = XColor.FromArgb(0, 188, 212);    // Cyan #00BCD4
            var cTextDark    = XColor.FromArgb(30, 35, 50);
            var cTextMuted   = XColor.FromArgb(100, 110, 130);
            var cBorderLight = XColor.FromArgb(226, 232, 240);  // Soft slate
            var cRowAlt      = XColor.FromArgb(248, 250, 252);
            var cWhite       = XColors.White;

            // ── Typography ─────────────────────────────────────────────
            var fShopTitle   = new XFont("Arial", 16, XFontStyle.Bold);
            var fShopSub     = new XFont("Arial", 9, XFontStyle.Regular);
            var fH1          = new XFont("Arial", 12, XFontStyle.Bold);
            var fLabel       = new XFont("Arial", 8.5, XFontStyle.Regular);
            var fValueBold   = new XFont("Arial", 9.5, XFontStyle.Bold);
            var fTableHdr    = new XFont("Arial", 9, XFontStyle.Bold);
            var fTableRow    = new XFont("Arial", 9, XFontStyle.Regular);
            var fTableRowB   = new XFont("Arial", 9, XFontStyle.Bold);
            var fSmallItalic = new XFont("Arial", 8, XFontStyle.Italic);
            var fGrandTotal  = new XFont("Arial", 16, XFontStyle.Bold);

            // ── Top Header Banner ──────────────────────────────────────
            double headerHeight = 76;
            gfx.DrawRectangle(new XSolidBrush(cPrimaryDark), margin, y, contentWidth, headerHeight);

            // Left decorative accent bar
            gfx.DrawRectangle(new XSolidBrush(cAccentGreen), margin, y, 6, headerHeight);

            // Shop Name & Contact Info
            gfx.DrawString(settings.ShopName.ToUpperInvariant(), fShopTitle, new XSolidBrush(cWhite),
                margin + 18, y + 26);
            gfx.DrawString(settings.ShopAddress, fShopSub, new XSolidBrush(XColor.FromArgb(200, 210, 225)),
                margin + 18, y + 44);
            gfx.DrawString($"📞 Mobile / WhatsApp: {settings.ShopPhone}", fShopSub, new XSolidBrush(cAccentTeal),
                margin + 18, y + 60);

            // Right Header Badge: "INVOICE / RECEIPT"
            gfx.DrawString("TAX INVOICE / RECEIPT", fH1, new XSolidBrush(cAccentGreen),
                new XRect(margin, y + 22, contentWidth - 16, 20), XStringFormats.TopRight);
            gfx.DrawString("Original for Customer", fSmallItalic, new XSolidBrush(XColor.FromArgb(170, 185, 205)),
                new XRect(margin, y + 42, contentWidth - 16, 15), XStringFormats.TopRight);

            y += headerHeight + 12;

            // ── Bill Info & Customer Details Card ───────────────────────
            double infoCardHeight = 62;
            gfx.DrawRoundedRectangle(new XPen(cBorderLight, 1), new XSolidBrush(cRowAlt),
                margin, y, contentWidth, infoCardHeight, 6, 6);

            double col1 = margin + 14;
            double col2 = margin + (contentWidth * 0.52);

            // Column 1: Bill details
            gfx.DrawString("INVOICE NO:", fLabel, new XSolidBrush(cTextMuted), col1, y + 16);
            gfx.DrawString(session.BillNumber, fValueBold, new XSolidBrush(cPrimaryDark), col1 + 75, y + 16);

            gfx.DrawString("DATE & TIME:", fLabel, new XSolidBrush(cTextMuted), col1, y + 36);
            gfx.DrawString(session.BilledAt.ToString("dd/MM/yyyy  hh:mm tt"), fTableRow, new XSolidBrush(cTextDark), col1 + 75, y + 36);

            gfx.DrawString("PAYMENT:", fLabel, new XSolidBrush(cTextMuted), col1, y + 52);
            gfx.DrawString(session.PaymentMode.ToUpperInvariant(), fValueBold, new XSolidBrush(cAccentGreen), col1 + 75, y + 52);

            // Column 2: Customer details
            gfx.DrawString("CUSTOMER:", fLabel, new XSolidBrush(cTextMuted), col2, y + 16);
            gfx.DrawString(string.IsNullOrWhiteSpace(session.CustomerName) ? "Walk-in Customer" : session.CustomerName,
                fValueBold, new XSolidBrush(cPrimaryDark), col2 + 75, y + 16);

            gfx.DrawString("MOBILE NO:", fLabel, new XSolidBrush(cTextMuted), col2, y + 36);
            string phoneStr = string.IsNullOrWhiteSpace(session.CustomerPhone) ? "Not Provided" : session.CustomerPhone;
            gfx.DrawString(phoneStr, fTableRow, new XSolidBrush(cTextDark), col2 + 75, y + 36);

            if (!string.IsNullOrWhiteSpace(session.Notes))
            {
                gfx.DrawString("NOTES:", fLabel, new XSolidBrush(cTextMuted), col2, y + 52);
                string noteTrunc = session.Notes.Length > 28 ? session.Notes.Substring(0, 25) + "..." : session.Notes;
                gfx.DrawString(noteTrunc, fSmallItalic, new XSolidBrush(cTextMuted), col2 + 75, y + 52);
            }

            y += infoCardHeight + 14;

            // ── Itemized Table Header ──────────────────────────────────
            double thH = 24;
            gfx.DrawRectangle(new XSolidBrush(cPrimaryDark), margin, y, contentWidth, thH);

            double xSl     = margin + 8;
            double xItem   = margin + 34;
            double xType   = margin + (contentWidth * 0.52);
            double xQty    = margin + (contentWidth * 0.68);
            double xRate   = margin + (contentWidth * 0.81);
            double xTotal  = margin + contentWidth - 10;

            gfx.DrawString("#", fTableHdr, new XSolidBrush(cWhite), xSl, y + 16);
            gfx.DrawString("ITEM / SERVICE DESCRIPTION", fTableHdr, new XSolidBrush(cWhite), xItem, y + 16);
            gfx.DrawString("PRINT MODE", fTableHdr, new XSolidBrush(cWhite), xType, y + 16);
            gfx.DrawString("PAGES/SHEETS", fTableHdr, new XSolidBrush(cWhite), xQty, y + 16);
            gfx.DrawString("RATE", fTableHdr, new XSolidBrush(cWhite), xRate, y + 16);
            gfx.DrawString("TOTAL (Rs)", fTableHdr, new XSolidBrush(cWhite),
                new XRect(margin, y, contentWidth - 10, thH), XStringFormats.CenterRight);

            y += thH;

            // ── Itemized Table Rows ────────────────────────────────────
            int itemNo = 1;
            bool altRow = false;

            foreach (var job in session.Jobs)
            {
                double rowH = 28;
                if (altRow)
                {
                    gfx.DrawRectangle(new XSolidBrush(cRowAlt), margin, y, contentWidth, rowH);
                }
                gfx.DrawLine(new XPen(cBorderLight, 0.6), margin, y + rowH, margin + contentWidth, y + rowH);
                altRow = !altRow;

                // Item number
                gfx.DrawString(itemNo.ToString(), fTableRow, new XSolidBrush(cTextMuted), xSl, y + 18);

                // Document / Item Name
                string docDisplay = job.DocumentName;
                if (docDisplay.Length > 36) docDisplay = docDisplay.Substring(0, 33) + "...";
                gfx.DrawString(docDisplay, fTableRowB, new XSolidBrush(cTextDark), xItem, y + 13);

                // Category sub-text
                string subText = job.IsManualEntry ? job.ItemCategory : $"{job.PaperSize} Spooler Job";
                gfx.DrawString(subText, fSmallItalic, new XSolidBrush(cTextMuted), xItem, y + 23);

                // Mode
                string modeText = (job.IsDuplex ? "Duplex" : "Single") + " • " + (job.IsColor ? "Color" : "B&W");
                var modeColor = job.IsColor ? XColor.FromArgb(2, 132, 199) : cTextDark;
                gfx.DrawString(modeText, fTableRow, new XSolidBrush(modeColor), xType, y + 18);

                // Pages / Sheets
                string qtyText = job.IsDuplex
                    ? $"{job.TotalImpressions}p ({job.SheetsUsed}s)"
                    : $"{job.TotalImpressions}p";
                if (job.Copies > 1) qtyText += $" ×{job.Copies}";
                gfx.DrawString(qtyText, fTableRow, new XSolidBrush(cTextDark), xQty, y + 18);

                // Rate
                gfx.DrawString($"₹{job.RatePerUnit:G}", fTableRow, new XSolidBrush(cTextDark), xRate, y + 18);

                // Total
                gfx.DrawString($"₹{job.TotalCost:F2}", fTableRowB, new XSolidBrush(cPrimaryDark),
                    new XRect(margin, y, contentWidth - 10, rowH), XStringFormats.CenterRight);

                y += rowH;
                itemNo++;
            }

            // ── Totals Box ─────────────────────────────────────────────
            y += 8;
            double totalsH = 74;
            gfx.DrawRoundedRectangle(new XPen(cAccentGreen, 1.2), new XSolidBrush(XColor.FromArgb(240, 253, 244)),
                margin, y, contentWidth, totalsH, 6, 6);

            // Left side: summary metrics
            double mCol = margin + 16;
            gfx.DrawString("SUMMARY BREAKDOWN:", fLabel, new XSolidBrush(cTextMuted), mCol, y + 18);
            gfx.DrawString($"• Total Print Impressions: {session.TotalPages} pages", fTableRow, new XSolidBrush(cTextDark), mCol, y + 34);
            gfx.DrawString($"• Physical Paper Consumed: {session.TotalSheets} sheets", fTableRow, new XSolidBrush(cTextDark), mCol, y + 48);
            gfx.DrawString($"• Total Line Items Billed: {session.Jobs.Count} items", fTableRow, new XSolidBrush(cTextDark), mCol, y + 62);

            // Right side: Grand Total
            gfx.DrawString("AMOUNT PAYABLE", fH1, new XSolidBrush(cPrimaryDark),
                new XRect(margin, y + 16, contentWidth - 16, 20), XStringFormats.TopRight);
            gfx.DrawString($"₹ {session.TotalAmount:F2}", fGrandTotal, new XSolidBrush(cAccentGreen),
                new XRect(margin, y + 36, contentWidth - 16, 30), XStringFormats.TopRight);

            y += totalsH + 16;

            // ── Footer Note & Terms ────────────────────────────────────
            gfx.DrawRectangle(new XSolidBrush(cRowAlt), margin, y, contentWidth, 42);
            gfx.DrawRectangle(new XPen(cBorderLight, 0.8), margin, y, contentWidth, 42);

            gfx.DrawString($"\"{settings.BillFooterNote}\"", fSmallItalic, new XSolidBrush(cTextDark),
                new XRect(margin, y + 8, contentWidth, 14), XStringFormats.TopCenter);
            gfx.DrawString($"Thank you for your business! For queries or next orders, contact {settings.ShopPhone}.",
                fLabel, new XSolidBrush(cTextMuted),
                new XRect(margin, y + 24, contentWidth, 14), XStringFormats.TopCenter);

            y += 50;
            gfx.DrawString("DASMO CYBER CAFE TOOLS • AUTOMATIC PRINT TRACKER STUDIO", fSmallItalic,
                new XSolidBrush(XColor.FromArgb(160, 170, 185)),
                new XRect(margin, pageHeight - 24, contentWidth, 14), XStringFormats.TopCenter);

            // ── Save Document ──────────────────────────────────────────
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            document.Save(outputPath);
            Log.Information("Bill PDF successfully generated at {Path}", outputPath);
            return outputPath;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to generate bill PDF for {BillNo}", session.BillNumber);
            return null;
        }
    }

    /// <summary>
    /// Generates and saves a bill PDF to a persistent Documents/CyberCafe/Bills folder,
    /// ideal for archival or instant WhatsApp document attachment.
    /// </summary>
    public static string? SavePermanentBillPdf(CustomerBillSession session, PrintBillingSettings settings)
    {
        string docsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "DASMO CYBER CAFE", "Bills", DateTime.Now.ToString("yyyy-MM"));
        Directory.CreateDirectory(docsDir);

        string safeCustName = string.Join("_", (session.CustomerName ?? "Customer").Split(Path.GetInvalidFileNameChars()));
        string fileName = $"{session.BillNumber}_{safeCustName}.pdf";
        string targetPath = Path.Combine(docsDir, fileName);

        return GenerateBillPdf(session, settings, targetPath);
    }
}
