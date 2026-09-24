using System;
using System.Media;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using SmartSaver.Models;
using Application = System.Windows.Application;

namespace SmartSaver.Views;

public partial class PrintAlertPopup : Window
{
    private static PrintAlertPopup? _activePopup;
    private readonly DispatcherTimer _timer;
    private double _remainingSeconds = 6.0;
    private const double TotalSeconds = 6.0;

    public PrintAlertPopup(PrintJobRecord job)
    {
        InitializeComponent();
        UpdateJob(job);

        // Position neatly at bottom-right corner of screen work area
        var wa = SystemParameters.WorkArea;
        Left = Math.Max(0, wa.Right - Width - 16);
        Top = Math.Max(0, wa.Bottom - Height - 16);

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _timer.Tick += Timer_Tick;
        _timer.Start();

        PlayNotificationSound();
    }

    public static void ShowAlert(PrintJobRecord job)
    {
        try
        {
            var app = Application.Current;
            if (app == null) return;

            app.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_activePopup != null && _activePopup.IsLoaded)
                {
                    _activePopup.UpdateJob(job);
                    _activePopup._remainingSeconds = TotalSeconds;
                    _activePopup.PlayNotificationSound();
                    return;
                }

                _activePopup = new PrintAlertPopup(job);
                _activePopup.Show();
            }));
        }
        catch { }
    }

    public void UpdateJob(PrintJobRecord job)
    {
        TxtPrinterName.Text = string.IsNullOrWhiteSpace(job.PrinterName) ? "Brother DCP-T530DW" : job.PrinterName;
        TxtDocumentName.Text = string.IsNullOrWhiteSpace(job.DocumentName) ? "Print Document" : job.DocumentName;
        TxtPagesBadge.Text = $"{job.Pages} Page{(job.Pages > 1 ? "s" : "")}";
        TxtDuplexBadge.Text = job.IsDuplex ? $"📑 {job.SheetsUsed} Sheets (Duplex)" : "📄 Single-Sided";
        TxtColorBadge.Text = job.IsColor ? "🌈 Color" : "⚫ B&W";
        TxtTotalCost.Text = $"₹{job.TotalCost:F2}";
        _remainingSeconds = TotalSeconds;
        DismissProgress.Value = 100;
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        _remainingSeconds -= 0.05;
        if (_remainingSeconds <= 0)
        {
            _timer.Stop();
            Close();
            _activePopup = null;
        }
        else
        {
            DismissProgress.Value = (_remainingSeconds / TotalSeconds) * 100.0;
        }
    }

    private void PlayNotificationSound()
    {
        try { SystemSounds.Asterisk.Play(); } catch { }
    }

    private void Window_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _timer.Stop();
    }

    private void Window_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _timer.Start();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        _timer.Stop();
        Close();
        _activePopup = null;
    }

    private void OpenStudio_Click(object sender, RoutedEventArgs e)
    {
        _timer.Stop();
        Close();
        _activePopup = null;
        PrintTrackerStudioWindow.ShowStudio();
    }
}
