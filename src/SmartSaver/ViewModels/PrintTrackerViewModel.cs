using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using Serilog;
using SmartSaver.Models;
using SmartSaver.Services;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;
using Clipboard = System.Windows.Clipboard;

namespace SmartSaver.ViewModels;

public class PrintTrackerViewModel : ViewModelBase
{
    private readonly PrintTrackerService _tracker = PrintTrackerService.Instance;

    // ── Active Customer Cart Properties ──
    private string _customerName = "Walk-in Customer";
    public string CustomerName
    {
        get => _customerName;
        set => SetProperty(ref _customerName, value);
    }

    private string _customerPhone = string.Empty;
    public string CustomerPhone
    {
        get => _customerPhone;
        set => SetProperty(ref _customerPhone, value);
    }

    private string _paymentMode = "Cash";
    public string PaymentMode
    {
        get => _paymentMode;
        set => SetProperty(ref _paymentMode, value);
    }

    private string _billNotes = string.Empty;
    public string BillNotes
    {
        get => _billNotes;
        set => SetProperty(ref _billNotes, value);
    }

    public ObservableCollection<PrintJobRecord> ActiveJobs { get; } = new();

    public double ActiveCartTotalCost => ActiveJobs.Sum(j => j.TotalCost);
    public string ActiveCartTotalCostDisplay => $"₹{ActiveCartTotalCost:F2}";
    public int ActiveCartTotalPages => ActiveJobs.Sum(j => j.TotalImpressions);
    public int ActiveCartTotalSheets => ActiveJobs.Sum(j => j.SheetsUsed);
    public bool HasActiveJobs => ActiveJobs.Count > 0;
    public bool CanFinishBill => HasActiveJobs;

    // ── Last Completed Bill & Receipt ──
    private CustomerBillSession? _lastCompletedBill;
    public CustomerBillSession? LastCompletedBill
    {
        get => _lastCompletedBill;
        set
        {
            if (SetProperty(ref _lastCompletedBill, value))
            {
                OnPropertyChanged(nameof(HasLastCompletedBill));
                OnPropertyChanged(nameof(LastReceiptText));
            }
        }
    }
    public bool HasLastCompletedBill => LastCompletedBill != null;
    public string LastReceiptText => LastCompletedBill != null ? _tracker.GenerateReceiptText(LastCompletedBill) : string.Empty;

    // ── Quick Manual Entry Fields ──
    private int _manualPagesInput = 1;
    public int ManualPagesInput
    {
        get => _manualPagesInput;
        set => SetProperty(ref _manualPagesInput, Math.Max(1, value));
    }

    // ── Daily Analytics ──
    public double TodayRevenue => _tracker.CompletedBillSessions
        .Where(s => s.BilledAt.Date == DateTime.Today)
        .Sum(s => s.TotalAmount);

    public int TodayPages => _tracker.CompletedBillSessions
        .Where(s => s.BilledAt.Date == DateTime.Today)
        .Sum(s => s.TotalPages);

    public int TodaySheets => _tracker.CompletedBillSessions
        .Where(s => s.BilledAt.Date == DateTime.Today)
        .Sum(s => s.TotalSheets);

    public int TodayBwPages => _tracker.AllJobHistory
        .Where(j => j.Timestamp.Date == DateTime.Today && !j.IsColor)
        .Sum(j => j.TotalImpressions);

    public int TodayColorPages => _tracker.AllJobHistory
        .Where(j => j.Timestamp.Date == DateTime.Today && j.IsColor)
        .Sum(j => j.TotalImpressions);

    public int TodayDuplexSheets => _tracker.AllJobHistory
        .Where(j => j.Timestamp.Date == DateTime.Today && j.IsDuplex)
        .Sum(j => j.SheetsUsed);

    // ── Completed Bills & History ──
    public ObservableCollection<CustomerBillSession> FilteredBills { get; } = new();
    public ObservableCollection<PrintJobRecord> FilteredJobs { get; } = new();

    private string _historySearchText = string.Empty;
    public string HistorySearchText
    {
        get => _historySearchText;
        set
        {
            if (SetProperty(ref _historySearchText, value))
            {
                ApplyHistoryFilter();
            }
        }
    }

    private string _historyDateFilter = "Today";
    public string HistoryDateFilter
    {
        get => _historyDateFilter;
        set
        {
            if (SetProperty(ref _historyDateFilter, value))
            {
                ApplyHistoryFilter();
            }
        }
    }

    // ── Settings & Rates ──
    public double BwSingleSideRate
    {
        get => _tracker.Settings.BwSingleSideRate;
        set { _tracker.UpdateSettings(s => s.BwSingleSideRate = value); OnPropertyChanged(); RefreshCart(); }
    }

    public double BwDuplexRate
    {
        get => _tracker.Settings.BwDuplexRate;
        set { _tracker.UpdateSettings(s => s.BwDuplexRate = value); OnPropertyChanged(); RefreshCart(); }
    }

    public bool BwDuplexPricedPerSheet
    {
        get => _tracker.Settings.BwDuplexPricedPerSheet;
        set { _tracker.UpdateSettings(s => s.BwDuplexPricedPerSheet = value); OnPropertyChanged(); RefreshCart(); }
    }

    public double ColorSingleSideRate
    {
        get => _tracker.Settings.ColorSingleSideRate;
        set { _tracker.UpdateSettings(s => s.ColorSingleSideRate = value); OnPropertyChanged(); RefreshCart(); }
    }

    public double ColorDuplexRate
    {
        get => _tracker.Settings.ColorDuplexRate;
        set { _tracker.UpdateSettings(s => s.ColorDuplexRate = value); OnPropertyChanged(); RefreshCart(); }
    }

    public bool ColorDuplexPricedPerSheet
    {
        get => _tracker.Settings.ColorDuplexPricedPerSheet;
        set { _tracker.UpdateSettings(s => s.ColorDuplexPricedPerSheet = value); OnPropertyChanged(); RefreshCart(); }
    }

    public double PhotocopyBwRate
    {
        get => _tracker.Settings.PhotocopyBwRate;
        set { _tracker.UpdateSettings(s => s.PhotocopyBwRate = value); OnPropertyChanged(); }
    }

    public double PhotocopyColorRate
    {
        get => _tracker.Settings.PhotocopyColorRate;
        set { _tracker.UpdateSettings(s => s.PhotocopyColorRate = value); OnPropertyChanged(); }
    }

    public double PhotoGlossyRate
    {
        get => _tracker.Settings.PhotoGlossyRate;
        set { _tracker.UpdateSettings(s => s.PhotoGlossyRate = value); OnPropertyChanged(); }
    }

    public double LaminationRate
    {
        get => _tracker.Settings.LaminationRate;
        set { _tracker.UpdateSettings(s => s.LaminationRate = value); OnPropertyChanged(); }
    }

    public string ShopName
    {
        get => _tracker.Settings.ShopName;
        set { _tracker.UpdateSettings(s => s.ShopName = value); OnPropertyChanged(); }
    }

    public string ShopPhone
    {
        get => _tracker.Settings.ShopPhone;
        set { _tracker.UpdateSettings(s => s.ShopPhone = value); OnPropertyChanged(); }
    }

    public string ShopAddress
    {
        get => _tracker.Settings.ShopAddress;
        set { _tracker.UpdateSettings(s => s.ShopAddress = value); OnPropertyChanged(); }
    }

    public bool AutoMonitoringEnabled
    {
        get => _tracker.Settings.AutoMonitoringEnabled;
        set
        {
            _tracker.UpdateSettings(s => s.AutoMonitoringEnabled = value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(SpoolerStatusText));
            OnPropertyChanged(nameof(SpoolerStatusIcon));
        }
    }

    public string SpoolerStatusText => AutoMonitoringEnabled
        ? "🟢 Spooler Active • Auto-Detecting Brother & All Prints"
        : "⏸️ Spooler Paused • Auto-Detection Off";

    public string SpoolerStatusIcon => AutoMonitoringEnabled ? "🟢" : "⏸️";

    public ObservableCollection<string> InstalledPrinters { get; } = new();

    // ── Commands ──
    public ICommand CompleteBillCommand { get; }
    public ICommand ClearCartCommand { get; }
    public ICommand ToggleDuplexCommand { get; }
    public ICommand ToggleColorCommand { get; }
    public ICommand IncrementPagesCommand { get; }
    public ICommand DecrementPagesCommand { get; }
    public ICommand RemoveJobCommand { get; }

    public ICommand AddQuickPhotocopyBwCommand { get; }
    public ICommand AddQuickPhotocopyColorCommand { get; }
    public ICommand AddQuickDuplexBwCommand { get; }
    public ICommand AddQuickDuplexColorCommand { get; }
    public ICommand AddQuickPhotoCommand { get; }
    public ICommand AddQuickLaminationCommand { get; }
    public ICommand AddManualCustomJobCommand { get; }

    public ICommand ShareWhatsAppCommand { get; }
    public ICommand CopyReceiptCommand { get; }
    public ICommand ExportCsvCommand { get; }
    public ICommand ToggleMonitoringCommand { get; }
    public ICommand PollNowCommand { get; }

    public PrintTrackerViewModel()
    {
        // Populate installed printers
        foreach (var p in PrintTrackerService.GetInstalledPrinterNames())
        {
            InstalledPrinters.Add(p);
        }

        // Subscribe to service events
        _tracker.OnJobDetected += job =>
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                RefreshCart();
                RefreshAnalytics();
            });
        };

        _tracker.OnActiveCartChanged += () =>
        {
            Application.Current?.Dispatcher.Invoke(RefreshCart);
        };

        _tracker.OnHistoryUpdated += () =>
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                RefreshAnalytics();
                ApplyHistoryFilter();
            });
        };

        // Wire Commands
        CompleteBillCommand = new RelayCommand(_ => FinishBill(), _ => CanFinishBill);
        ClearCartCommand = new RelayCommand(_ => ClearCart(), _ => HasActiveJobs);

        ToggleDuplexCommand = new RelayCommand(p =>
        {
            if (p is PrintJobRecord job)
            {
                _tracker.UpdateJobInCart(job.Id, j => j.IsDuplex = !j.IsDuplex);
            }
        });

        ToggleColorCommand = new RelayCommand(p =>
        {
            if (p is PrintJobRecord job)
            {
                _tracker.UpdateJobInCart(job.Id, j => j.IsColor = !j.IsColor);
            }
        });

        IncrementPagesCommand = new RelayCommand(p =>
        {
            if (p is PrintJobRecord job)
            {
                _tracker.UpdateJobInCart(job.Id, j => j.Pages++);
            }
        });

        DecrementPagesCommand = new RelayCommand(p =>
        {
            if (p is PrintJobRecord job && job.Pages > 1)
            {
                _tracker.UpdateJobInCart(job.Id, j => j.Pages--);
            }
        });

        RemoveJobCommand = new RelayCommand(p =>
        {
            if (p is PrintJobRecord job)
            {
                _tracker.RemoveJobFromCart(job.Id);
            }
        });

        // Quick Counter buttons
        AddQuickPhotocopyBwCommand = new RelayCommand(param =>
        {
            int count = param != null && int.TryParse(param.ToString(), out int c) ? c : ManualPagesInput;
            _tracker.AddManualJob("Photocopy", count, isDuplex: false, isColor: false);
        });

        AddQuickPhotocopyColorCommand = new RelayCommand(param =>
        {
            int count = param != null && int.TryParse(param.ToString(), out int c) ? c : ManualPagesInput;
            _tracker.AddManualJob("Color Photocopy", count, isDuplex: false, isColor: true);
        });

        AddQuickDuplexBwCommand = new RelayCommand(param =>
        {
            int count = param != null && int.TryParse(param.ToString(), out int c) ? c : ManualPagesInput;
            _tracker.AddManualJob("Duplex B&W Print", count, isDuplex: true, isColor: false);
        });

        AddQuickDuplexColorCommand = new RelayCommand(param =>
        {
            int count = param != null && int.TryParse(param.ToString(), out int c) ? c : ManualPagesInput;
            _tracker.AddManualJob("Duplex Color Print", count, isDuplex: true, isColor: true);
        });

        AddQuickPhotoCommand = new RelayCommand(_ =>
        {
            _tracker.AddManualJob("Passport Photo Set", 1, isDuplex: false, isColor: true, overrideRate: PhotoGlossyRate);
        });

        AddQuickLaminationCommand = new RelayCommand(_ =>
        {
            _tracker.AddManualJob("Lamination", 1, isDuplex: false, isColor: false, overrideRate: LaminationRate);
        });

        AddManualCustomJobCommand = new RelayCommand(_ =>
        {
            _tracker.AddManualJob("Printout", ManualPagesInput, isDuplex: false, isColor: false);
        });

        ShareWhatsAppCommand = new RelayCommand(p =>
        {
            var session = (p as CustomerBillSession) ?? LastCompletedBill;
            if (session == null && HasActiveJobs)
            {
                // If not finished yet, create temporary slip preview
                session = new CustomerBillSession
                {
                    CustomerName = CustomerName,
                    CustomerPhone = CustomerPhone,
                    PaymentMode = PaymentMode,
                    Jobs = new List<PrintJobRecord>(ActiveJobs)
                };
            }

            if (session != null)
            {
                string url = _tracker.GenerateWhatsAppShareUrl(session);
                try
                {
                    Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Failed to launch WhatsApp URL");
                }
            }
        });

        CopyReceiptCommand = new RelayCommand(p =>
        {
            var session = (p as CustomerBillSession) ?? LastCompletedBill;
            if (session != null)
            {
                string receipt = _tracker.GenerateReceiptText(session);
                try
                {
                    Clipboard.SetText(receipt);
                    MessageBox.Show("✅ Bill Receipt copied to clipboard!\n\nYou can paste it in WhatsApp or print on a thermal printer.",
                        "Bill Copied", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch { }
            }
        });

        ExportCsvCommand = new RelayCommand(async _ => await ExportCsvAsync());

        ToggleMonitoringCommand = new RelayCommand(_ =>
        {
            AutoMonitoringEnabled = !AutoMonitoringEnabled;
        });

        PollNowCommand = new RelayCommand(_ =>
        {
            _tracker.PollPrintQueues();
        });

        // Initialize state
        RefreshCart();
        RefreshAnalytics();
        ApplyHistoryFilter();
    }

    private void FinishBill()
    {
        if (!HasActiveJobs) return;

        var bill = _tracker.CompleteCustomerBill(CustomerName, CustomerPhone, PaymentMode, BillNotes);
        LastCompletedBill = bill;

        // Reset inputs ready for next customer
        CustomerName = "Walk-in Customer";
        CustomerPhone = string.Empty;
        PaymentMode = "Cash";
        BillNotes = string.Empty;
        ManualPagesInput = 1;

        RefreshCart();
        RefreshAnalytics();
        ApplyHistoryFilter();
    }

    private void ClearCart()
    {
        var ask = MessageBox.Show(
            "Are you sure you want to clear the current customer's unbilled print jobs?",
            "Clear Current Bill", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (ask == MessageBoxResult.Yes)
        {
            _tracker.ClearActiveCart();
            RefreshCart();
        }
    }

    private void RefreshCart()
    {
        ActiveJobs.Clear();
        foreach (var job in _tracker.ActiveCustomerJobs)
        {
            ActiveJobs.Add(job);
        }

        OnPropertyChanged(nameof(ActiveCartTotalCost));
        OnPropertyChanged(nameof(ActiveCartTotalCostDisplay));
        OnPropertyChanged(nameof(ActiveCartTotalPages));
        OnPropertyChanged(nameof(ActiveCartTotalSheets));
        OnPropertyChanged(nameof(HasActiveJobs));
        OnPropertyChanged(nameof(CanFinishBill));
        ((RelayCommand)CompleteBillCommand).OnCanExecuteChanged();
        ((RelayCommand)ClearCartCommand).OnCanExecuteChanged();
    }

    private void RefreshAnalytics()
    {
        OnPropertyChanged(nameof(TodayRevenue));
        OnPropertyChanged(nameof(TodayPages));
        OnPropertyChanged(nameof(TodaySheets));
        OnPropertyChanged(nameof(TodayBwPages));
        OnPropertyChanged(nameof(TodayColorPages));
        OnPropertyChanged(nameof(TodayDuplexSheets));
    }

    private void ApplyHistoryFilter()
    {
        FilteredBills.Clear();
        FilteredJobs.Clear();

        DateTime today = DateTime.Today;
        var bills = _tracker.CompletedBillSessions.AsEnumerable();

        if (HistoryDateFilter == "Today")
        {
            bills = bills.Where(b => b.BilledAt.Date == today);
        }
        else if (HistoryDateFilter == "Yesterday")
        {
            bills = bills.Where(b => b.BilledAt.Date == today.AddDays(-1));
        }
        else if (HistoryDateFilter == "Last 7 Days")
        {
            bills = bills.Where(b => b.BilledAt.Date >= today.AddDays(-7));
        }

        if (!string.IsNullOrWhiteSpace(HistorySearchText))
        {
            string search = HistorySearchText.Trim().ToLowerInvariant();
            bills = bills.Where(b =>
                (b.CustomerName?.ToLowerInvariant().Contains(search) ?? false) ||
                (b.CustomerPhone?.Contains(search) ?? false) ||
                (b.BillNumber?.ToLowerInvariant().Contains(search) ?? false) ||
                b.Jobs.Any(j => j.DocumentName.ToLowerInvariant().Contains(search)));
        }

        foreach (var b in bills.OrderByDescending(b => b.BilledAt))
        {
            FilteredBills.Add(b);
        }

        var jobs = _tracker.AllJobHistory.AsEnumerable();
        if (HistoryDateFilter == "Today") jobs = jobs.Where(j => j.Timestamp.Date == today);
        else if (HistoryDateFilter == "Yesterday") jobs = jobs.Where(j => j.Timestamp.Date == today.AddDays(-1));
        else if (HistoryDateFilter == "Last 7 Days") jobs = jobs.Where(j => j.Timestamp.Date >= today.AddDays(-7));

        if (!string.IsNullOrWhiteSpace(HistorySearchText))
        {
            string search = HistorySearchText.Trim().ToLowerInvariant();
            jobs = jobs.Where(j =>
                j.DocumentName.ToLowerInvariant().Contains(search) ||
                (j.CustomerName?.ToLowerInvariant().Contains(search) ?? false) ||
                j.PrinterName.ToLowerInvariant().Contains(search));
        }

        foreach (var j in jobs.Take(300))
        {
            FilteredJobs.Add(j);
        }
    }

    private async Task ExportCsvAsync()
    {
        try
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "CSV Spreadsheet (*.csv)|*.csv",
                FileName = $"Print_Sales_{DateTime.Now:yyyyMMdd}.csv"
            };

            if (dialog.ShowDialog() == true)
            {
                await _tracker.ExportHistoryToCsvAsync(dialog.FileName);
                MessageBox.Show($"✅ Print history exported successfully to:\n\n{dialog.FileName}",
                    "Export Successful", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to export print sales CSV");
            MessageBox.Show($"Failed to export: {ex.Message}", "Export Failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
