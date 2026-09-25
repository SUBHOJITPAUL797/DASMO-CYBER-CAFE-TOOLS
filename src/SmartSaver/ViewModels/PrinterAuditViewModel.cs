using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using SmartSaver.Models;
using SmartSaver.Services;
using MessageBox = System.Windows.MessageBox;
using Application = System.Windows.Application;

namespace SmartSaver.ViewModels;

public class PrinterAuditViewModel : ViewModelBase
{
    private readonly BrotherPrinterAuditService _auditService = BrotherPrinterAuditService.Instance;

    private string _printerIp = "192.168.1.7";
    public string PrinterIp
    {
        get => _printerIp;
        set => SetProperty(ref _printerIp, value);
    }

    private bool _isQuerying;
    public bool IsQuerying
    {
        get => _isQuerying;
        set => SetProperty(ref _isQuerying, value);
    }

    public PrinterLiveStatus LiveStatus => _auditService.LiveStatus;

    public string DeviceStatusText => LiveStatus.IsOnline
        ? $"🟢 {LiveStatus.DeviceStatus} (Wi-Fi Online @ {LiveStatus.IpAddress})"
        : $"🔌 USB Mode Active (Wi-Fi Standby @ {PrinterIp})";

    public int InkBlack => LiveStatus.InkBlackPercent;
    public int InkCyan => LiveStatus.InkCyanPercent;
    public int InkMagenta => LiveStatus.InkMagentaPercent;
    public int InkYellow => LiveStatus.InkYellowPercent;

    // ── Meter Audit Fields ──
    private int _openingMeter;
    public int OpeningMeter
    {
        get => _openingMeter;
        set
        {
            if (SetProperty(ref _openingMeter, value))
            {
                Recalculate();
            }
        }
    }

    private int _currentHardwareMeter;
    public int CurrentHardwareMeter
    {
        get => _currentHardwareMeter;
        set
        {
            if (SetProperty(ref _currentHardwareMeter, value))
            {
                Recalculate();
            }
        }
    }

    private int _totalHardwareSheets;
    public int TotalHardwareSheets
    {
        get => _totalHardwareSheets;
        private set => SetProperty(ref _totalHardwareSheets, value);
    }

    private int _totalPcSpoolerPages;
    public int TotalPcSpoolerPages
    {
        get => _totalPcSpoolerPages;
        private set => SetProperty(ref _totalPcSpoolerPages, value);
    }

    private int _actualPhysicalXerox;
    public int ActualPhysicalXerox
    {
        get => _actualPhysicalXerox;
        private set => SetProperty(ref _actualPhysicalXerox, value);
    }

    private int _loggedXeroxPages;
    public int LoggedXeroxPages
    {
        get => _loggedXeroxPages;
        private set => SetProperty(ref _loggedXeroxPages, value);
    }

    private int _unrecordedXeroxCopies;
    public int UnrecordedXeroxCopies
    {
        get => _unrecordedXeroxCopies;
        private set
        {
            if (SetProperty(ref _unrecordedXeroxCopies, value))
            {
                OnPropertyChanged(nameof(HasUnrecordedXerox));
            }
        }
    }

    private double _unrecordedXeroxAmount;
    public double UnrecordedXeroxAmount
    {
        get => _unrecordedXeroxAmount;
        private set => SetProperty(ref _unrecordedXeroxAmount, value);
    }

    public bool HasUnrecordedXerox => UnrecordedXeroxCopies > 0;

    private string _selectedMedium = "Cash";
    public string SelectedMedium
    {
        get => _selectedMedium;
        set => SetProperty(ref _selectedMedium, value);
    }

    public double BwXeroxRate => PrintTrackerService.Instance.Settings.PhotocopyBwRate;
    public double ColorXeroxRate => PrintTrackerService.Instance.Settings.PhotocopyColorRate;
    public double DuplexBwRate => PrintTrackerService.Instance.Settings.BwDuplexRate;

    public ICommand RefreshLiveStatusCommand { get; }
    public ICommand SaveOpeningMeterCommand { get; }
    public ICommand SaveClosingMeterCommand { get; }
    public ICommand AutoLogMissingXeroxCommand { get; }
    public ICommand QuickLogXeroxCommand { get; }

    public Action? RequestClose { get; set; }

    public PrinterAuditViewModel()
    {
        var todayRec = _auditService.GetTodayMeter();
        _openingMeter = todayRec.OpeningMeter;
        _currentHardwareMeter = todayRec.ClosingMeter > todayRec.OpeningMeter ? todayRec.ClosingMeter : todayRec.OpeningMeter;

        RefreshLiveStatusCommand = new RelayCommand(async _ => await RefreshStatusAsync());
        SaveOpeningMeterCommand = new RelayCommand(_ =>
        {
            _auditService.SaveOpeningMeter(OpeningMeter);
            Recalculate();
            MessageBox.Show($"Opening meter reading ({OpeningMeter}) saved successfully!", "Saved", MessageBoxButton.OK, MessageBoxImage.Information);
        });

        SaveClosingMeterCommand = new RelayCommand(_ =>
        {
            _auditService.SaveClosingMeter(CurrentHardwareMeter);
            Recalculate();
            MessageBox.Show($"Closing meter reading ({CurrentHardwareMeter}) saved successfully!", "Saved", MessageBoxButton.OK, MessageBoxImage.Information);
        });

        AutoLogMissingXeroxCommand = new RelayCommand(_ =>
        {
            if (UnrecordedXeroxCopies <= 0) return;
            _auditService.AutoLogUnrecordedXerox(UnrecordedXeroxCopies, BwXeroxRate, SelectedMedium);
            Recalculate();
            MessageBox.Show($"Logged {UnrecordedXeroxCopies} missing physical Xerox copies (₹{UnrecordedXeroxAmount:F2}) directly into Cash Drawer & Excel!",
                "Audit Reconciled", MessageBoxButton.OK, MessageBoxImage.Information);
        });

        QuickLogXeroxCommand = new RelayCommand(param =>
        {
            if (param is string pStr)
            {
                // Format: copies|duplex|color
                // e.g. "1|false|false"
                var parts = pStr.Split('|');
                int copies = parts.Length > 0 && int.TryParse(parts[0], out int c) ? c : 1;
                bool isDup = parts.Length > 1 && bool.TryParse(parts[1], out bool d) && d;
                bool isCol = parts.Length > 2 && bool.TryParse(parts[2], out bool col) && col;

                _auditService.LogQuickWalkupXerox(copies, isDup, isCol, SelectedMedium);
                Recalculate();
            }
        });

        _auditService.OnAuditUpdated += () =>
        {
            Application.Current?.Dispatcher?.Invoke(() =>
            {
                Recalculate();
                OnPropertyChanged(nameof(LiveStatus));
                OnPropertyChanged(nameof(DeviceStatusText));
                OnPropertyChanged(nameof(InkBlack));
                OnPropertyChanged(nameof(InkCyan));
                OnPropertyChanged(nameof(InkMagenta));
                OnPropertyChanged(nameof(InkYellow));
            });
        };

        Recalculate();
        _ = RefreshStatusAsync();
    }

    public async Task RefreshStatusAsync()
    {
        IsQuerying = true;
        try
        {
            await _auditService.FetchPrinterStatusAsync(PrinterIp);
        }
        catch { }
        finally
        {
            IsQuerying = false;
            OnPropertyChanged(nameof(LiveStatus));
            OnPropertyChanged(nameof(DeviceStatusText));
            OnPropertyChanged(nameof(InkBlack));
            OnPropertyChanged(nameof(InkCyan));
            OnPropertyChanged(nameof(InkMagenta));
            OnPropertyChanged(nameof(InkYellow));
        }
    }

    public void Recalculate()
    {
        var (hw, pc, actualXerox, loggedXerox, unrecorded, unrecordedAmt) =
            _auditService.CalculateReconciliation(CurrentHardwareMeter);

        TotalHardwareSheets = hw;
        TotalPcSpoolerPages = pc;
        ActualPhysicalXerox = actualXerox;
        LoggedXeroxPages = loggedXerox;
        UnrecordedXeroxCopies = unrecorded;
        UnrecordedXeroxAmount = unrecordedAmt;
    }
}
