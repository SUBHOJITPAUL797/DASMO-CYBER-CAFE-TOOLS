using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace SmartSaver.Models;

/// <summary>
/// Mode of duplex (double-sided) printing.
/// </summary>
public enum PrintDuplexMode
{
    Simplex = 1,          // Single-sided (1 page per sheet)
    DuplexVertical = 2,    // Double-sided Long Edge (Flip on long edge)
    DuplexHorizontal = 3  // Double-sided Short Edge (Flip on short edge)
}

/// <summary>
/// Represents a single print job captured from the Windows Print Spooler or manually entered.
/// </summary>
public class PrintJobRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public uint SpoolerJobId { get; set; }
    public string DocumentName { get; set; } = "Untitled Document";
    public string PrinterName { get; set; } = string.Empty;
    public string Submitter { get; set; } = Environment.UserName;
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.Now;

    public int Pages { get; set; } = 1;
    public int Copies { get; set; } = 1;

    /// <summary>
    /// Total impressions / sides printed = Pages * Copies.
    /// </summary>
    public int TotalImpressions => Math.Max(1, Pages * Copies);

    /// <summary>
    /// True if printed double-sided (duplex).
    /// </summary>
    public bool IsDuplex { get; set; } = false;

    /// <summary>
    /// Physical sheets of paper used.
    /// If Duplex: ceil(TotalImpressions / 2.0). If Single-Sided: TotalImpressions.
    /// </summary>
    public int SheetsUsed => IsDuplex ? (int)Math.Ceiling(TotalImpressions / 2.0) : TotalImpressions;

    /// <summary>
    /// True if color print, false if black and white (monochrome).
    /// </summary>
    public bool IsColor { get; set; } = false;

    /// <summary>
    /// Paper size string (e.g. A4, Letter, Legal, 4x6).
    /// </summary>
    public string PaperSize { get; set; } = "A4";

    /// <summary>
    /// True if this is a manual counter entry (e.g. physical photocopy/Xerox, lamination) rather than spooler job.
    /// </summary>
    public bool IsManualEntry { get; set; } = false;

    /// <summary>
    /// Category / Item type for manual entries (e.g., "Printout", "Photocopy", "Lamination", "Passport Photo").
    /// </summary>
    public string ItemCategory { get; set; } = "Printout";

    /// <summary>
    /// Applied rate per unit (Rupees).
    /// </summary>
    public double RatePerUnit { get; set; } = 2.0;

    /// <summary>
    /// Total calculated cost in Rupees (₹).
    /// </summary>
    public double TotalCost { get; set; } = 2.0;

    /// <summary>
    /// Customer name or identifier associated with this job.
    /// </summary>
    public string CustomerName { get; set; } = string.Empty;

    /// <summary>
    /// Optional customer mobile number for WhatsApp receipt.
    /// </summary>
    public string CustomerPhone { get; set; } = string.Empty;

    /// <summary>
    /// ID of the billing session/slip this job belongs to.
    /// </summary>
    public string? BillSessionId { get; set; }

    /// <summary>
    /// Whether this print job has been billed and closed.
    /// </summary>
    public bool IsBilled { get; set; } = false;

    /// <summary>
    /// Friendly human-readable summary badge.
    /// </summary>
    [JsonIgnore]
    public string SummaryText =>
        $"{(IsColor ? "🌈 Color" : "⚫ B&W")} • {(IsDuplex ? $"📑 Duplex ({SheetsUsed} sheet{(SheetsUsed > 1 ? "s" : "")})" : $"📄 Single-Sided ({Pages} page{(Pages > 1 ? "s" : "")})")}" +
        (Copies > 1 ? $" × {Copies} copies" : "") +
        $" = ₹{TotalCost:F2}";
}

/// <summary>
/// Represents a finalized customer billing session containing one or more print jobs.
/// </summary>
public class CustomerBillSession
{
    public string SessionId { get; set; } = Guid.NewGuid().ToString("N");
    public string BillNumber { get; set; } = string.Empty;
    public string CustomerName { get; set; } = "Walk-in Customer";
    public string CustomerPhone { get; set; } = string.Empty;
    public DateTimeOffset BilledAt { get; set; } = DateTimeOffset.Now;

    public List<PrintJobRecord> Jobs { get; set; } = new();

    public int TotalPages
    {
        get => Jobs.Sum(j => j.TotalImpressions);
        set { }
    }

    public int TotalSheets
    {
        get => Jobs.Sum(j => j.SheetsUsed);
        set { }
    }

    public double TotalAmount
    {
        get => Jobs.Sum(j => j.TotalCost);
        set { }
    }

    public string PaymentMode { get; set; } = "Cash"; // Cash, UPI, Card, Due
    public string Notes { get; set; } = string.Empty;
}

/// <summary>
/// Configuration settings for automatic print detection, pricing rates, and receipts.
/// </summary>
public class PrintBillingSettings
{
    public bool AutoMonitoringEnabled { get; set; } = true;

    /// <summary>
    /// Specific printers to monitor (empty means monitor all local &amp; network printers).
    /// </summary>
    public List<string> TargetPrinters { get; set; } = new();

    // ── Rates (in Rupees ₹) ──
    public double BwSingleSideRate { get; set; } = 2.0;       // ₹2.00 per single-sided page
    public double BwDuplexRate { get; set; } = 3.0;           // ₹3.00 per duplex sheet (or ₹1.50/side)
    public bool BwDuplexPricedPerSheet { get; set; } = true;   // true = price per sheet (₹3/sheet); false = price per side (₹1.50/side)

    public double ColorSingleSideRate { get; set; } = 10.0;    // ₹10.00 per single-sided color page
    public double ColorDuplexRate { get; set; } = 15.0;        // ₹15.00 per duplex color sheet
    public bool ColorDuplexPricedPerSheet { get; set; } = true;

    public double PhotoGlossyRate { get; set; } = 20.0;       // ₹20.00 per 4x6 / A4 glossy photo
    public double PhotocopyBwRate { get; set; } = 2.0;        // ₹2.00 per B&W photocopy
    public double PhotocopyColorRate { get; set; } = 10.0;    // ₹10.00 per Color photocopy
    public double LaminationRate { get; set; } = 20.0;        // ₹20.00 per lamination pouch

    // ── Sound & Notifications ──
    public bool PlaySoundOnJobDetected { get; set; } = true;
    public bool ShowNotificationOnJobDetected { get; set; } = true;

    // ── Cyber Cafe / Shop Details for Bill Slip ──
    public string ShopName { get; set; } = "DASMO CYBER CAFE";
    public string ShopPhone { get; set; } = "+91 8927408840";
    public string ShopAddress { get; set; } = "Cyber Cafe & Digital Service Point";
    public string BillFooterNote { get; set; } = "Thank you for visiting! Please visit again.";

    // ── Linked Excel Spreadsheet Integration ──
    public string AttachedExcelPath { get; set; } = string.Empty;
    public bool AutoSyncToExcel { get; set; } = true;

    // ── Brother Printer Network & Auto-Discovery Settings ──
    public string BrotherPrinterIp { get; set; } = "192.168.1.7";
    public string BrotherPrinterMac { get; set; } = "4C:23:38:3F:8E:ED";
    public string BrotherPrinterHostname { get; set; } = "brw4c23383f8eed";
    public bool AutoDiscoverPrinterIp { get; set; } = true;

    // ── Physical Visual Ink Tank Calibration & Overrides ──
    public int CalibratedInkBlack { get; set; } = 100;
    public int CalibratedInkCyan { get; set; } = 100;
    public int CalibratedInkMagenta { get; set; } = 100;
    public int CalibratedInkYellow { get; set; } = 100;
    public bool PreferVisualInkLevels { get; set; } = false;
}
