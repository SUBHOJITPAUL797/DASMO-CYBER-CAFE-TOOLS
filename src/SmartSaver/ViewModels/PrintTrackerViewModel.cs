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
    private readonly BrotherPrinterAuditService _auditService = BrotherPrinterAuditService.Instance;
    private readonly CashDrawerService _cashDrawer = CashDrawerService.Instance;

    // ── Workspace Navigation Tabs ──
    // 0 = Active Customer Billing Cart
    // 1 = Hardware Meter & Walk-up Xerox Audit
    // 2 = Completed Bills & Daily Sales History
    // 3 = Cash Drawer & Daily Finance Overview
    private int _selectedWorkspaceTab = 0;
    public int SelectedWorkspaceTab
    {
        get => _selectedWorkspaceTab;
        set
        {
            if (SetProperty(ref _selectedWorkspaceTab, value))
            {
                OnPropertyChanged(nameof(IsTabBilling));
                OnPropertyChanged(nameof(IsTabMeterAudit));
                OnPropertyChanged(nameof(IsTabSalesHistory));
                OnPropertyChanged(nameof(IsTabCashDrawer));
                if (value == 1) RecalculateMeterAudit();
                if (value == 3) RefreshDrawerStats();
            }
        }
    }

    public bool IsTabBilling => SelectedWorkspaceTab == 0;
    public bool IsTabMeterAudit => SelectedWorkspaceTab == 1;
    public bool IsTabSalesHistory => SelectedWorkspaceTab == 2;
    public bool IsTabCashDrawer => SelectedWorkspaceTab == 3;

    // ── Brother DCP-T530DW Live Status & Smart Discovery ──
    public PrinterLiveStatus LiveStatus => _auditService.LiveStatus;

    private string _printerIp = "192.168.1.7";
    public string PrinterIp
    {
        get => _printerIp;
        set => SetProperty(ref _printerIp, value);
    }

    private bool _isAutoDetectingIp;
    public bool IsAutoDetectingIp
    {
        get => _isAutoDetectingIp;
        set => SetProperty(ref _isAutoDetectingIp, value);
    }

    private string _discoveryNote = string.Empty;
    public string DiscoveryNote
    {
        get => _discoveryNote;
        set => SetProperty(ref _discoveryNote, value);
    }

    public string DeviceStatusText => LiveStatus.IsOnline
        ? $"🟢 {LiveStatus.DeviceStatus} (Wi-Fi Online @ {LiveStatus.IpAddress})"
        : $"🔌 USB Mode Active (Wi-Fi Standby @ {PrinterIp})";

    public int InkBlack
    {
        get => LiveStatus.InkBlackPercent;
        set { /* Defensive setter to prevent WPF TwoWay binding exceptions */ }
    }
    public int InkCyan
    {
        get => LiveStatus.InkCyanPercent;
        set { /* Defensive setter to prevent WPF TwoWay binding exceptions */ }
    }
    public int InkMagenta
    {
        get => LiveStatus.InkMagentaPercent;
        set { /* Defensive setter to prevent WPF TwoWay binding exceptions */ }
    }
    public int InkYellow
    {
        get => LiveStatus.InkYellowPercent;
        set { /* Defensive setter to prevent WPF TwoWay binding exceptions */ }
    }
    public bool IsInkVisual => LiveStatus.IsCalibratedByVisualCheck;

    // ── Physical Hardware Meter Audit Properties ──
    private int _openingMeter;
    public int OpeningMeter
    {
        get => _openingMeter;
        set
        {
            if (SetProperty(ref _openingMeter, value))
            {
                RecalculateMeterAudit();
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
                RecalculateMeterAudit();
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

    private string _auditSelectedMedium = "Cash";
    public string AuditSelectedMedium
    {
        get => _auditSelectedMedium;
        set => SetProperty(ref _auditSelectedMedium, value);
    }
    public string SelectedMedium
    {
        get => AuditSelectedMedium;
        set => AuditSelectedMedium = value;
    }

    // ── Visual Tank Calibration Modal State ──
    private bool _isInkCalibrateModalOpen;
    public bool IsInkCalibrateModalOpen
    {
        get => _isInkCalibrateModalOpen;
        set => SetProperty(ref _isInkCalibrateModalOpen, value);
    }

    private int _calibBlack = 100;
    public int CalibBlack { get => _calibBlack; set => SetProperty(ref _calibBlack, Math.Clamp(value, 0, 100)); }

    private int _calibCyan = 100;
    public int CalibCyan { get => _calibCyan; set => SetProperty(ref _calibCyan, Math.Clamp(value, 0, 100)); }

    private int _calibMagenta = 100;
    public int CalibMagenta { get => _calibMagenta; set => SetProperty(ref _calibMagenta, Math.Clamp(value, 0, 100)); }

    private int _calibYellow = 100;
    public int CalibYellow { get => _calibYellow; set => SetProperty(ref _calibYellow, Math.Clamp(value, 0, 100)); }

    private bool _preferVisualTanks;
    public bool PreferVisualTanks { get => _preferVisualTanks; set => SetProperty(ref _preferVisualTanks, value); }

    // ── Cash Drawer Summary for Tab 3 ──
    public double DrawerOpeningTill => _cashDrawer.Today.OpeningCashInDrawer;
    public double DrawerCashBalance => _cashDrawer.Today.CurrentCashInDrawer;
    public double DrawerUpiTotal => _cashDrawer.Today.CurrentOnlineBalance;
    public double DrawerTotalIncome => _cashDrawer.Today.TodayTotalRevenue;
    public double DrawerTotalExpense => _cashDrawer.Today.TodayTotalExpenses;
    public double DrawerCustomerDue => _cashDrawer.Today.TotalCustomerUnpaidDebt;
    public double DrawerTodayNet => _cashDrawer.Today.TodayNetProfit;
    public ObservableCollection<CashTransaction> DrawerRecentTransactions { get; } = new();

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

    // ── Live Detected Job Banner ──
    private PrintJobRecord? _lastDetectedJob;
    public PrintJobRecord? LastDetectedJob
    {
        get => _lastDetectedJob;
        set
        {
            if (SetProperty(ref _lastDetectedJob, value))
            {
                OnPropertyChanged(nameof(HasRecentDetectedJob));
                OnPropertyChanged(nameof(RecentDetectedJobText));
            }
        }
    }

    public bool HasRecentDetectedJob => _lastDetectedJob != null;

    public string RecentDetectedJobText => _lastDetectedJob == null
        ? string.Empty
        : $"⚡ NEW PRINT CAPTURED: {_lastDetectedJob.Pages} Pages • {(_lastDetectedJob.IsDuplex ? $"{_lastDetectedJob.SheetsUsed} Sheets (Duplex)" : "Single-Sided")} ({(_lastDetectedJob.IsColor ? "Color" : "B&W")}) = ₹{_lastDetectedJob.TotalCost:F2} added to active cart!";

    public ICommand DismissDetectedJobBannerCommand { get; }
    public ICommand ApplyStandardRatesCommand { get; }
    public ICommand ApplyEconomyRatesCommand { get; }

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
    public ICommand SaveBillPdfCommand { get; }
    public ICommand DeleteBillCommand { get; }
    public ICommand ExportCsvCommand { get; }
    public ICommand ExportExcelCommand { get; }
    public ICommand OpenCashDrawerCommand { get; }
    public ICommand OpenPrinterAuditCommand { get; }
    public ICommand ToggleMonitoringCommand { get; }
    public ICommand PollNowCommand { get; }

    // ── Linked Excel Spreadsheet Auto-Sync ──
    public string AttachedExcelPath
    {
        get => _tracker.Settings.AttachedExcelPath;
        set
        {
            _tracker.UpdateSettings(s => s.AttachedExcelPath = value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(AttachedExcelName));
            OnPropertyChanged(nameof(HasAttachedExcel));
        }
    }

    public string AttachedExcelName => string.IsNullOrWhiteSpace(AttachedExcelPath)
        ? "No Excel Linked"
        : Path.GetFileName(AttachedExcelPath);

    public bool HasAttachedExcel => !string.IsNullOrWhiteSpace(AttachedExcelPath);

    public bool AutoSyncToExcel
    {
        get => _tracker.Settings.AutoSyncToExcel;
        set
        {
            _tracker.UpdateSettings(s => s.AutoSyncToExcel = value);
            OnPropertyChanged();
        }
    }

    public ICommand LinkExcelFileCommand { get; }
    public ICommand SyncExcelNowCommand { get; }
    public ICommand OpenAttachedExcelCommand { get; }

    // ── Integrated Hardware Meter & Xerox Audit Commands ──
    public ICommand SwitchTabCommand { get; }
    public ICommand SaveOpeningMeterCommand { get; }
    public ICommand SaveClosingMeterCommand { get; }
    public ICommand AutoLogMissingXeroxCommand { get; }
    public ICommand QuickLogWalkupXeroxCommand { get; }
    public ICommand QuickLogXeroxCommand => QuickLogWalkupXeroxCommand;
    public ICommand AutoDetectIpCommand { get; }
    public ICommand AutoReadHardwareMeterCommand { get; }
    public ICommand RefreshLiveStatusCommand { get; }
    public ICommand OpenInkCalibrateModalCommand { get; }
    public ICommand CloseInkCalibrateModalCommand { get; }
    public ICommand SaveInkCalibrationCommand { get; }
    public ICommand RefillAllTanks100Command { get; }
    public ICommand RefreshDrawerStatsCommand { get; }

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
            RunOnUI(() =>
            {
                LastDetectedJob = job;
                RefreshCart();
                RefreshAnalytics();
            });
        };

        _tracker.OnActiveCartChanged += () =>
        {
            RunOnUI(RefreshCart);
        };

        _tracker.OnHistoryUpdated += () =>
        {
            RunOnUI(() =>
            {
                RefreshAnalytics();
                ApplyHistoryFilter();
            });
        };

        // Wire Commands
        DismissDetectedJobBannerCommand = new RelayCommand(_ => LastDetectedJob = null);

        ApplyStandardRatesCommand = new RelayCommand(_ =>
        {
            BwSingleSideRate = 2.0;
            BwDuplexRate = 3.0;
            ColorSingleSideRate = 10.0;
            ColorDuplexRate = 15.0;
            PhotoGlossyRate = 20.0;
            PhotocopyBwRate = 2.0;
            PhotocopyColorRate = 10.0;
            LaminationRate = 20.0;
        });

        ApplyEconomyRatesCommand = new RelayCommand(_ =>
        {
            BwSingleSideRate = 1.5;
            BwDuplexRate = 2.5;
            ColorSingleSideRate = 8.0;
            ColorDuplexRate = 12.0;
            PhotoGlossyRate = 15.0;
            PhotocopyBwRate = 1.5;
            PhotocopyColorRate = 8.0;
            LaminationRate = 15.0;
        });

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

        SaveBillPdfCommand = new RelayCommand(p =>
        {
            var session = (p as CustomerBillSession) ?? LastCompletedBill;
            if (session == null && HasActiveJobs)
            {
                session = new CustomerBillSession
                {
                    BillNumber = $"DRAFT-{DateTime.Now:HHmmss}",
                    CustomerName = CustomerName,
                    CustomerPhone = CustomerPhone,
                    PaymentMode = PaymentMode,
                    Jobs = new List<PrintJobRecord>(ActiveJobs)
                };
            }

            if (session == null)
            {
                MessageBox.Show("No bill selected to generate PDF.", "Notice", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            try
            {
                var dialog = new Microsoft.Win32.SaveFileDialog
                {
                    Filter = "PDF Document (*.pdf)|*.pdf",
                    FileName = $"Bill_{session.BillNumber.Replace("-", "_")}.pdf",
                    InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
                };

                if (dialog.ShowDialog() == true)
                {
                    string? path = BillPdfGenerator.GenerateBillPdf(session, _tracker.Settings, dialog.FileName);
                    if (!string.IsNullOrEmpty(path) && File.Exists(path))
                    {
                        try
                        {
                            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                        }
                        catch { }

                        MessageBox.Show($"✅ PDF Bill created successfully!\n\nFile saved to:\n{path}\n\nYou can send this PDF on WhatsApp or print it.",
                            "PDF Bill Generated", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    else
                    {
                        MessageBox.Show("Could not generate PDF file.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to generate bill PDF");
                MessageBox.Show($"Error generating PDF: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        });

        DeleteBillCommand = new RelayCommand(p =>
        {
            if (p is not CustomerBillSession session) return;

            var confirm = MessageBox.Show(
                $"Are you sure you want to delete Bill #{session.BillNumber} ({session.CustomerName} — ₹{session.TotalAmount:F2})?\n\nThis will remove it from sales history and update your totals.",
                "Delete Bill", MessageBoxButton.YesNo, MessageBoxImage.Warning);

            if (confirm == MessageBoxResult.Yes)
            {
                if (_tracker.DeleteBill(session.SessionId))
                {
                    if (LastCompletedBill?.SessionId == session.SessionId)
                    {
                        LastCompletedBill = null;
                    }
                    RefreshAnalytics();
                    ApplyHistoryFilter();
                }
            }
        });

        ExportCsvCommand = new RelayCommand(async _ => await ExportCsvAsync());
        ExportExcelCommand = new RelayCommand(async _ => await ExportExcelAsync());
        SwitchTabCommand = new RelayCommand(p =>
        {
            if (p is int idx) SelectedWorkspaceTab = idx;
            else if (p is string s && int.TryParse(s, out int sIdx)) SelectedWorkspaceTab = sIdx;
        });

        OpenCashDrawerCommand = new RelayCommand(_ => Views.CashDrawerWindow.ShowCashDrawer());
        OpenPrinterAuditCommand = new RelayCommand(_ =>
        {
            SelectedWorkspaceTab = 1;
        });

        SaveOpeningMeterCommand = new RelayCommand(_ =>
        {
            _auditService.SaveOpeningMeter(OpeningMeter);
            RecalculateMeterAudit();
            ShowMessage($"Morning Opening meter ({OpeningMeter}) saved successfully!", "Opening Meter Saved", MessageBoxButton.OK, MessageBoxImage.Information);
        });

        SaveClosingMeterCommand = new RelayCommand(_ =>
        {
            _auditService.SaveClosingMeter(CurrentHardwareMeter);
            RecalculateMeterAudit();
            ShowMessage($"Evening Closing meter ({CurrentHardwareMeter}) saved successfully!", "Closing Meter Saved", MessageBoxButton.OK, MessageBoxImage.Information);
        });

        AutoLogMissingXeroxCommand = new RelayCommand(_ =>
        {
            if (UnrecordedXeroxCopies <= 0) return;
            _auditService.AutoLogUnrecordedXerox(UnrecordedXeroxCopies, PhotocopyBwRate, AuditSelectedMedium);
            RecalculateMeterAudit();
            RefreshAnalytics();
            ApplyHistoryFilter();
            ShowMessage($"✅ Reconciled & logged {UnrecordedXeroxCopies} missing physical Xerox copies (₹{UnrecordedXeroxAmount:F2}) into Cash Drawer & Excel!",
                "Audit Reconciled", MessageBoxButton.OK, MessageBoxImage.Information);
        });

        QuickLogWalkupXeroxCommand = new RelayCommand(param =>
        {
            if (param is string pStr)
            {
                var parts = pStr.Split('|');
                int copies = parts.Length > 0 && int.TryParse(parts[0], out int c) ? c : 1;
                bool isDup = parts.Length > 1 && bool.TryParse(parts[1], out bool d) && d;
                bool isCol = parts.Length > 2 && bool.TryParse(parts[2], out bool col) && col;

                _auditService.LogQuickWalkupXerox(copies, isDup, isCol, AuditSelectedMedium);
                RecalculateMeterAudit();
                RefreshAnalytics();
                ApplyHistoryFilter();
            }
        });

        AutoDetectIpCommand = new RelayCommand(async _ => await AutoDetectIpAsync());
        AutoReadHardwareMeterCommand = new RelayCommand(async _ => await AutoReadMeterAsync(showToast: true));
        RefreshLiveStatusCommand = new RelayCommand(async _ => await RefreshStatusAsync());

        OpenInkCalibrateModalCommand = new RelayCommand(_ =>
        {
            var s = _tracker.Settings;
            CalibBlack = s.CalibratedInkBlack;
            CalibCyan = s.CalibratedInkCyan;
            CalibMagenta = s.CalibratedInkMagenta;
            CalibYellow = s.CalibratedInkYellow;
            PreferVisualTanks = s.PreferVisualInkLevels;
            IsInkCalibrateModalOpen = true;
        });

        CloseInkCalibrateModalCommand = new RelayCommand(_ => IsInkCalibrateModalOpen = false);

        SaveInkCalibrationCommand = new RelayCommand(_ =>
        {
            _auditService.CalibrateInkLevels(CalibBlack, CalibCyan, CalibMagenta, CalibYellow, PreferVisualTanks);
            IsInkCalibrateModalOpen = false;
            OnPropertyChanged(nameof(InkBlack));
            OnPropertyChanged(nameof(InkCyan));
            OnPropertyChanged(nameof(InkMagenta));
            OnPropertyChanged(nameof(InkYellow));
            OnPropertyChanged(nameof(IsInkVisual));
            ShowMessage("✅ Ink tank visual calibration saved successfully!", "Calibration Saved", MessageBoxButton.OK, MessageBoxImage.Information);
        });

        RefillAllTanks100Command = new RelayCommand(_ =>
        {
            CalibBlack = 100;
            CalibCyan = 100;
            CalibMagenta = 100;
            CalibYellow = 100;
            PreferVisualTanks = true;
            _auditService.CalibrateInkLevels(100, 100, 100, 100, preferVisual: true);
            IsInkCalibrateModalOpen = false;
            OnPropertyChanged(nameof(InkBlack));
            OnPropertyChanged(nameof(InkCyan));
            OnPropertyChanged(nameof(InkMagenta));
            OnPropertyChanged(nameof(InkYellow));
            OnPropertyChanged(nameof(IsInkVisual));
            ShowMessage("✅ All 4 ink tanks set to 100% Full!", "Tanks Refilled", MessageBoxButton.OK, MessageBoxImage.Information);
        });

        RefreshDrawerStatsCommand = new RelayCommand(_ => RefreshDrawerStats());
        ToggleMonitoringCommand = new RelayCommand(_ => AutoMonitoringEnabled = !AutoMonitoringEnabled);
        PollNowCommand = new RelayCommand(_ => _tracker.PollPrintQueues());
        LinkExcelFileCommand = new RelayCommand(_ => ExecuteLinkExcelFile());
        SyncExcelNowCommand = new RelayCommand(_ => ExecuteSyncExcelNow());
        OpenAttachedExcelCommand = new RelayCommand(_ => ExecuteOpenAttachedExcel());

        // Initialize Brother hardware meter state
        _printerIp = _auditService.CurrentIp;
        var todayMeter = _auditService.GetTodayMeter();
        _openingMeter = todayMeter.OpeningMeter;
        _currentHardwareMeter = todayMeter.ClosingMeter > todayMeter.OpeningMeter ? todayMeter.ClosingMeter : todayMeter.OpeningMeter;
        RecalculateMeterAudit();

        _auditService.OnAuditUpdated += () =>
        {
            RunOnUI(() =>
            {
                RecalculateMeterAudit();
                OnPropertyChanged(nameof(LiveStatus));
                OnPropertyChanged(nameof(DeviceStatusText));
                OnPropertyChanged(nameof(InkBlack));
                OnPropertyChanged(nameof(InkCyan));
                OnPropertyChanged(nameof(InkMagenta));
                OnPropertyChanged(nameof(InkYellow));
                OnPropertyChanged(nameof(IsInkVisual));
            });
        };

        _auditService.OnPrinterIpDiscovered += (newIp) =>
        {
            RunOnUI(() =>
            {
                PrinterIp = newIp;
                DiscoveryNote = $"✅ Auto-discovered @ {newIp}";
                OnPropertyChanged(nameof(PrinterIp));
                OnPropertyChanged(nameof(DeviceStatusText));
            });
        };

        _cashDrawer.OnRegisterUpdated += () =>
        {
            RunOnUI(() =>
            {
                RefreshDrawerStats();
                RefreshAnalytics();
            });
        };

        // Query printer status in background
        _ = RefreshStatusAsync();

        // Initialize state
        RefreshCart();
        RefreshAnalytics();
        ApplyHistoryFilter();
    }

    public void RecalculateMeterAudit()
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

    public async Task RefreshStatusAsync()
    {
        try
        {
            await _auditService.FetchPrinterStatusAsync(PrinterIp);
            if (LiveStatus.HardwarePageCount > 0)
            {
                CurrentHardwareMeter = LiveStatus.HardwarePageCount;
                if (OpeningMeter <= 0)
                {
                    OpeningMeter = LiveStatus.HardwarePageCount;
                }
                RecalculateMeterAudit();
            }
        }
        catch { }
        finally
        {
            OnPropertyChanged(nameof(LiveStatus));
            OnPropertyChanged(nameof(DeviceStatusText));
            OnPropertyChanged(nameof(InkBlack));
            OnPropertyChanged(nameof(InkCyan));
            OnPropertyChanged(nameof(InkMagenta));
            OnPropertyChanged(nameof(InkYellow));
            OnPropertyChanged(nameof(IsInkVisual));
            OnPropertyChanged(nameof(CurrentHardwareMeter));
            OnPropertyChanged(nameof(OpeningMeter));
        }
    }

    public async Task AutoReadMeterAsync(bool showToast = true)
    {
        try
        {
            int pageCount = await _auditService.FetchHardwarePageCountAsync(PrinterIp);
            if (pageCount > 0)
            {
                CurrentHardwareMeter = pageCount;
                if (OpeningMeter <= 0)
                {
                    OpeningMeter = pageCount;
                }
                RecalculateMeterAudit();
                OnPropertyChanged(nameof(CurrentHardwareMeter));
                OnPropertyChanged(nameof(OpeningMeter));

                if (showToast)
                {
                    ShowMessage($"✅ Live Meter Synced: {pageCount} pages read directly from Brother DCP-T530DW over Wi-Fi!\n(Zero manual typing, zero LCD checks needed)", "Wi-Fi Hardware Meter Synced", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            else if (showToast)
            {
                ShowMessage("Could not query hardware page count from Brother printer over Wi-Fi.\nPlease ensure the printer is turned on and connected to your Wi-Fi network.", "Wi-Fi Printer Notice", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "AutoReadMeterAsync failed");
        }
    }

    public async Task AutoDetectIpAsync()
    {
        if (IsAutoDetectingIp) return;
        IsAutoDetectingIp = true;
        DiscoveryNote = "🔍 Scanning network for Brother DCP-T530DW...";
        try
        {
            string? found = await _auditService.AutoDiscoverPrinterIpAsync(PrinterIp);
            if (!string.IsNullOrWhiteSpace(found))
            {
                PrinterIp = found;
                DiscoveryNote = $"✅ Discovered Brother DCP-T530DW @ {found}";
                await RefreshStatusAsync();
            }
            else
            {
                DiscoveryNote = "⚠️ Printer not detected on Wi-Fi (ensure printer is ON and connected).";
            }
        }
        catch (Exception ex)
        {
            DiscoveryNote = $"Detection error: {ex.Message}";
        }
        finally
        {
            IsAutoDetectingIp = false;
        }
    }

    public void RefreshDrawerStats()
    {
        OnPropertyChanged(nameof(DrawerOpeningTill));
        OnPropertyChanged(nameof(DrawerCashBalance));
        OnPropertyChanged(nameof(DrawerUpiTotal));
        OnPropertyChanged(nameof(DrawerTotalIncome));
        OnPropertyChanged(nameof(DrawerTotalExpense));
        OnPropertyChanged(nameof(DrawerCustomerDue));
        OnPropertyChanged(nameof(DrawerTodayNet));

        DrawerRecentTransactions.Clear();
        foreach (var tx in _cashDrawer.Today.Transactions.OrderByDescending(t => t.Timestamp).Take(20))
        {
            DrawerRecentTransactions.Add(tx);
        }
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

    private async Task ExportExcelAsync()
    {
        try
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "Excel Spreadsheet (*.xlsx)|*.xlsx",
                FileName = $"Cyber_Cafe_Finance_{DateTime.Now:yyyyMMdd}.xlsx",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
            };

            if (dialog.ShowDialog() == true)
            {
                var bills = _tracker.CompletedBillSessions.ToList();
                await Task.Run(() => BillExcelExporter.ExportToExcel(bills, _tracker.Settings, dialog.FileName));

                var askOpen = MessageBox.Show(
                    $"✅ Finance Excel workbook exported successfully!\n\nFile saved to:\n{dialog.FileName}\n\nWould you like to open it now?",
                    "Excel Exported", MessageBoxButton.YesNo, MessageBoxImage.Information);

                if (askOpen == MessageBoxResult.Yes)
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo(dialog.FileName) { UseShellExecute = true });
                    }
                    catch { }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to export Excel report");
            MessageBox.Show($"Failed to export Excel: {ex.Message}", "Export Failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ExecuteLinkExcelFile()
    {
        try
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Select or Create Linked Excel Accounts Workbook",
                Filter = "Excel Spreadsheet (*.xlsx)|*.xlsx",
                FileName = string.IsNullOrWhiteSpace(AttachedExcelPath)
                    ? $"Cyber_Cafe_Accounts_{DateTime.Now:yyyy}.xlsx"
                    : Path.GetFileName(AttachedExcelPath),
                InitialDirectory = string.IsNullOrWhiteSpace(AttachedExcelPath)
                    ? Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
                    : Path.GetDirectoryName(AttachedExcelPath)
            };

            if (dialog.ShowDialog() == true)
            {
                AttachedExcelPath = dialog.FileName;
                AutoSyncToExcel = true;

                // Sync immediately
                _tracker.TriggerExcelAutoSync();

                MessageBox.Show(
                    $"✅ Excel Workbook Linked Successfully!\n\nFile:\n{dialog.FileName}\n\nAuto-Sync is now active. All bills, photocopies, cashouts, and cash transactions will automatically reflect into this spreadsheet.",
                    "Excel Auto-Sync Connected", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to link Excel file in PrintTrackerViewModel");
            MessageBox.Show($"Failed to link Excel file: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ExecuteSyncExcelNow()
    {
        if (string.IsNullOrWhiteSpace(AttachedExcelPath))
        {
            ExecuteLinkExcelFile();
            return;
        }

        try
        {
            var bills = _tracker.CompletedBillSessions.ToList();
            var regs = CashDrawerService.Instance.AllDays.ToList();
            bool ok = BillExcelExporter.AutoSyncAttachedExcel(_tracker.Settings, bills, regs);

            if (ok)
            {
                MessageBox.Show(
                    $"✅ All accounts & bills synced successfully to:\n{AttachedExcelPath}",
                    "Sync Complete", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show(
                    $"⚠️ Could not write to Excel file.\nPlease verify the file is not currently open and locked by another application.",
                    "Sync Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Manual Excel sync failed in PrintTrackerViewModel");
            MessageBox.Show($"Sync error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ExecuteOpenAttachedExcel()
    {
        if (string.IsNullOrWhiteSpace(AttachedExcelPath) || !File.Exists(AttachedExcelPath))
        {
            ExecuteLinkExcelFile();
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(AttachedExcelPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to open attached Excel file {Path}", AttachedExcelPath);
            MessageBox.Show($"Could not open Excel file: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static bool IsTestEnvironment()
    {
        try
        {
            var proc = System.Diagnostics.Process.GetCurrentProcess().ProcessName;
            return proc.Contains("Test", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static MessageBoxResult ShowMessage(string message, string title, MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage icon = MessageBoxImage.Information)
    {
        if (IsTestEnvironment())
        {
            Log.Information("[SilentTest] MessageBox: {Title} - {Msg}", title, message);
            return buttons is MessageBoxButton.YesNo or MessageBoxButton.YesNoCancel ? MessageBoxResult.Yes : MessageBoxResult.OK;
        }
        return MessageBox.Show(message, title, buttons, icon);
    }

    private static void RunOnUI(Action action)
    {
        if (IsTestEnvironment())
        {
            try { action(); } catch { }
            return;
        }

        var app = Application.Current;
        var dispatcher = app?.Dispatcher;
        if (dispatcher != null && dispatcher.Thread.IsAlive && !dispatcher.HasShutdownStarted)
        {
            if (dispatcher.CheckAccess())
            {
                action();
            }
            else
            {
                try
                {
                    var op = dispatcher.BeginInvoke(action);
                    var status = op.Wait(TimeSpan.FromMilliseconds(300));
                    if (status != System.Windows.Threading.DispatcherOperationStatus.Completed)
                    {
                        action();
                    }
                }
                catch
                {
                    action();
                }
            }
        }
        else
        {
            action();
        }
    }
}
