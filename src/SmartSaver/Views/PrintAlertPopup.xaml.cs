using System;
using System.Media;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using SmartSaver.Models;
using SmartSaver.Services;
using Application = System.Windows.Application;

namespace SmartSaver.Views;

public partial class PrintAlertPopup : Window
{
    private static PrintAlertPopup? _activePopup;
    private readonly DispatcherTimer _timer;
    private PrintJobRecord? _currentJob;
    private double _remainingSeconds = 6.0;
    private const double TotalSeconds = 6.0;

    public string ColorBadgeText => TxtColorBadge.Text;
    public string TotalCostText => TxtTotalCost.Text;
    public string DuplexBadgeText => TxtDuplexBadge.Text;

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
        _currentJob = job;
        TxtPrinterName.Text = string.IsNullOrWhiteSpace(job.PrinterName) ? "Brother DCP-T530DW" : job.PrinterName;
        TxtDocumentName.Text = string.IsNullOrWhiteSpace(job.DocumentName) ? "Print Document" : job.DocumentName;
        TxtPagesBadge.Text = $"{job.Pages} Page{(job.Pages > 1 ? "s" : "")}";
        TxtDuplexBadge.Text = job.IsDuplex ? $"📑 {job.SheetsUsed} Sheets (Duplex)" : "📄 Single-Sided";

        if (job.IsDuplex)
        {
            TxtDuplexBadge.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x10, 0xB9, 0x81));
            PillDuplex.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x33, 0x10, 0xB9, 0x81));
            PillDuplex.BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x10, 0xB9, 0x81));
            PillDuplex.BorderThickness = new Thickness(1);
        }
        else
        {
            TxtDuplexBadge.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x94, 0xA3, 0xB8));
            PillDuplex.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x22, 0x94, 0xA3, 0xB8));
            PillDuplex.BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x44, 0x94, 0xA3, 0xB8));
            PillDuplex.BorderThickness = new Thickness(1);
        }

        TxtColorBadge.Text = job.IsColor ? "🌈 Color" : "⚫ B&W";
        if (job.IsColor)
        {
            TxtColorBadge.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF5, 0x9E, 0x0B));
            PillColor.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x33, 0xF5, 0x9E, 0x0B));
            PillColor.BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF5, 0x9E, 0x0B));
            PillColor.BorderThickness = new Thickness(1);
        }
        else
        {
            TxtColorBadge.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x00, 0xBC, 0xD4));
            PillColor.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x22, 0x00, 0xBC, 0xD4));
            PillColor.BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x44, 0x00, 0xBC, 0xD4));
            PillColor.BorderThickness = new Thickness(1);
        }

        TxtTotalCost.Text = $"₹{job.TotalCost:F2}";
        _remainingSeconds = TotalSeconds;
        DismissProgress.Value = 100;
    }

    private void PillColor_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_currentJob == null) return;
        bool found = false;
        PrintTrackerService.Instance.UpdateJobInCart(_currentJob.Id, j =>
        {
            j.IsColor = !j.IsColor;
            found = true;
        });

        if (!found)
        {
            _currentJob.IsColor = !_currentJob.IsColor;
            PrintTrackerService.Instance.CalculateCost(_currentJob);
        }

        _remainingSeconds = TotalSeconds;
        UpdateJob(_currentJob);
        try { SystemSounds.Asterisk.Play(); } catch { }
    }

    private void PillDuplex_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_currentJob == null) return;
        bool found = false;
        PrintTrackerService.Instance.UpdateJobInCart(_currentJob.Id, j =>
        {
            j.IsDuplex = !j.IsDuplex;
            found = true;
        });

        if (!found)
        {
            _currentJob.IsDuplex = !_currentJob.IsDuplex;
            PrintTrackerService.Instance.CalculateCost(_currentJob);
        }

        _remainingSeconds = TotalSeconds;
        UpdateJob(_currentJob);
        try { SystemSounds.Asterisk.Play(); } catch { }
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
