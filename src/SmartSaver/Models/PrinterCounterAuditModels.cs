using System;

namespace SmartSaver.Models;

/// <summary>
/// Real-time live status and hardware information for the Brother DCP-T530DW printer.
/// </summary>
public class PrinterLiveStatus
{
    public bool IsOnline { get; set; } = false;
    public string ConnectionType { get; set; } = "Wi-Fi & USB"; // "Wi-Fi (192.168.1.7)", "USB Cable (USB001)", "Wi-Fi & USB Dual"
    public string DeviceStatus { get; set; } = "Checking...";   // "Sleep", "Ready", "Printing", "Copying", "Scanning", "Offline"
    public string ModelName { get; set; } = "Brother DCP-T530DW";
    public string IpAddress { get; set; } = "192.168.1.7";
    public string MacAddress { get; set; } = "4C:23:38:3F:8E:ED";

    // Ink Levels (0 to 100%) - Defaults to 100% full unless live-queried or calibrated by user
    public int InkBlackPercent { get; set; } = 100;
    public int InkCyanPercent { get; set; } = 100;
    public int InkMagentaPercent { get; set; } = 100;
    public int InkYellowPercent { get; set; } = 100;
    public bool IsCalibratedByVisualCheck { get; set; } = false;

    public DateTimeOffset LastChecked { get; set; } = DateTimeOffset.Now;
}

/// <summary>
/// Daily hardware meter record for reconciling physical paper sheets passed vs PC spooler prints.
/// </summary>
public class DailyPrinterMeterRecord
{
    public string Date { get; set; } = DateTime.Now.ToString("yyyy-MM-dd");
    public int OpeningMeter { get; set; }
    public int ClosingMeter { get; set; }
    public bool IsClosingSaved { get; set; } = false;
    public DateTimeOffset OpeningRecordedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset ClosingRecordedAt { get; set; } = DateTimeOffset.Now;
    public string Notes { get; set; } = string.Empty;
}
