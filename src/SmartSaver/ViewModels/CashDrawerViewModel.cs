using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using SmartSaver.Models;
using SmartSaver.Services;
using Serilog;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace SmartSaver.ViewModels;

public class CashTransactionItemViewModel
{
    private readonly CashTransaction _tx;

    public CashTransactionItemViewModel(CashTransaction tx)
    {
        _tx = tx;
    }

    public string Id => _tx.Id;
    public DateTimeOffset Timestamp => _tx.Timestamp;
    public string TimeFormatted => _tx.Timestamp.ToString("hh:mm tt");
    public double Amount => _tx.Amount;
    public double Commission => _tx.CommissionFee;
    public string CustomerName => _tx.CustomerName;
    public string CustomerPhone => _tx.CustomerPhone;
    public string Description => _tx.Description;
    public bool IsCleared => _tx.IsCleared;
    public CashCategory Category => _tx.Category;

    public string CategoryBadge => _tx.Category switch
    {
        CashCategory.CustomerUpiCashPayout    => "📱 ➔ 💵 UPI to Cashout",
        CashCategory.CustomerCashBankDeposit  => "💵 ➔ 📱 Cash to Online",
        CashCategory.CustomerBorrowCredit     => "🤝 Customer Borrow (Due)",
        CashCategory.CustomerDebtRepaid       => "✅ Debt Cleared",
        CashCategory.PrintSales               => "🖨️ Print Sales",
        CashCategory.XeroxPhotocopy           => "📄 Xerox / Photocopy",
        CashCategory.OnlineFormFillup         => "💻 Online Form Fillup",
        CashCategory.LaminationPhotos         => "🌟 Lamination / Photos",
        CashCategory.OwnerInvestment          => "🏦 Owner Investment",
        CashCategory.OwnerWithdrawal          => "💸 Owner Withdrawal",
        CashCategory.ShopExpensePaperInk      => "📦 Paper & Ink Stock",
        CashCategory.ShopExpenseBillsRent     => "💡 Electricity / Net / Rent",
        _                                     => "📌 Other"
    };

    public string BadgeColor => _tx.Category switch
    {
        CashCategory.CustomerUpiCashPayout    => "#00BCD4", // Cyan
        CashCategory.CustomerCashBankDeposit  => "#3B82F6", // Blue
        CashCategory.CustomerBorrowCredit     => "#EF4444", // Red warning
        CashCategory.CustomerDebtRepaid       => "#10B981", // Emerald
        CashCategory.PrintSales or CashCategory.XeroxPhotocopy => "#10B981",
        CashCategory.OnlineFormFillup         => "#8B5CF6", // Purple
        CashCategory.OwnerInvestment          => "#F59E0B", // Amber
        CashCategory.OwnerWithdrawal or CashCategory.ShopExpensePaperInk or CashCategory.ShopExpenseBillsRent => "#F97316",
        _                                     => "#94A3B8"
    };

    public string AmountDisplay
    {
        get
        {
            if (_tx.Category == CashCategory.CustomerUpiCashPayout)
            {
                string fee = _tx.CommissionFee > 0 ? $" (+₹{_tx.CommissionFee:G} fee)" : "";
                return $"₹{_tx.Amount:F2} cash given{fee}";
            }
            if (_tx.Category == CashCategory.CustomerBorrowCredit)
            {
                return $"₹{_tx.Amount:F2} UNPAID";
            }
            string sign = _tx.Direction == TransactionDirection.Income ? "+" : "-";
            return $"{sign} ₹{_tx.Amount:F2}";
        }
    }

    public string AmountColor => _tx.Category switch
    {
        CashCategory.CustomerBorrowCredit => "#EF4444",
        CashCategory.CustomerUpiCashPayout => "#38BDF8",
        _ => _tx.Direction == TransactionDirection.Income ? "#10B981" : "#F87171"
    };

    public string MediumDisplay => _tx.Medium == PaymentMedium.CashInDrawer ? "💵 Cash Drawer" : "📱 Online / UPI";
}

public class CashDrawerViewModel : ViewModelBase
{
    private readonly CashDrawerService _service = CashDrawerService.Instance;

    // ── Metric Displays ──
    public string CashInDrawerDisplay => $"₹{_service.Today.CurrentCashInDrawer:F2}";
    public string OnlineBalanceDisplay => $"₹{_service.Today.CurrentOnlineBalance:F2}";
    public string TodayRevenueDisplay => $"₹{_service.Today.TodayTotalRevenue:F2}";
    public string TodayExpensesDisplay => $"₹{_service.Today.TodayTotalExpenses:F2}";
    public string TodayNetProfitDisplay => $"₹{_service.Today.TodayNetProfit:F2}";
    public string UnpaidDebtDisplay => $"₹{_service.Today.TotalCustomerUnpaidDebt:F2}";

    public double CashInToday => _service.Today.TotalCashIn;
    public double CashOutToday => _service.Today.TotalCashOut;
    public double OnlineInToday => _service.Today.TotalOnlineIn;
    public double OnlineOutToday => _service.Today.TotalOnlineOut;

    public ObservableCollection<CashTransactionItemViewModel> Transactions { get; } = new();

    // ── Opening Float Fields ──
    private string _openingCashText = string.Empty;
    public string OpeningCashText
    {
        get => _openingCashText;
        set => SetProperty(ref _openingCashText, value);
    }

    private string _openingOnlineText = string.Empty;
    public string OpeningOnlineText
    {
        get => _openingOnlineText;
        set => SetProperty(ref _openingOnlineText, value);
    }

    // ── New Transaction Form ──
    private string _amountInput = string.Empty;
    public string AmountInput
    {
        get => _amountInput;
        set => SetProperty(ref _amountInput, value);
    }

    private string _commissionInput = string.Empty;
    public string CommissionInput
    {
        get => _commissionInput;
        set => SetProperty(ref _commissionInput, value);
    }

    private string _customerNameInput = string.Empty;
    public string CustomerNameInput
    {
        get => _customerNameInput;
        set => SetProperty(ref _customerNameInput, value);
    }

    private string _customerPhoneInput = string.Empty;
    public string CustomerPhoneInput
    {
        get => _customerPhoneInput;
        set => SetProperty(ref _customerPhoneInput, value);
    }

    private string _descriptionInput = string.Empty;
    public string DescriptionInput
    {
        get => _descriptionInput;
        set => SetProperty(ref _descriptionInput, value);
    }

    private string _selectedCategoryString = "Customer UPI ➔ Cash Given";
    public string SelectedCategoryString
    {
        get => _selectedCategoryString;
        set => SetProperty(ref _selectedCategoryString, value);
    }

    public string[] AvailableCategories { get; } = new[]
    {
        "Customer UPI ➔ Cash Given",
        "Customer Cash ➔ Online Paid",
        "Customer Borrow / Credit (Due)",
        "Print & Xerox Sales",
        "Online Form Fillup Fee",
        "Lamination & Photos",
        "Owner Investment (Capital)",
        "Owner Withdrawal (Personal)",
        "Shop Expense (Paper / Ink)",
        "Bills / Rent / Electricity",
        "Other"
    };

    // ── Commands ──
    public ICommand AddTransactionCommand { get; }
    public ICommand SaveOpeningBalancesCommand { get; }
    public ICommand DeleteTransactionCommand { get; }
    public ICommand ClearDebtCommand { get; }
    public ICommand QuickUpiPayoutShortcutCommand { get; }
    public ICommand QuickBorrowShortcutCommand { get; }

    public CashDrawerViewModel()
    {
        _openingCashText = _service.Today.OpeningCashInDrawer.ToString("G", CultureInfo.InvariantCulture);
        _openingOnlineText = _service.Today.OpeningOnlineBalance.ToString("G", CultureInfo.InvariantCulture);

        _service.OnRegisterChanged += () =>
        {
            RunOnUI(RefreshAll);
        };

        AddTransactionCommand = new RelayCommand(_ => ExecuteAddTransaction());
        SaveOpeningBalancesCommand = new RelayCommand(_ => ExecuteSaveOpeningBalances());
        DeleteTransactionCommand = new RelayCommand(p =>
        {
            if (p is string id)
            {
                var ask = MessageBox.Show("Delete this transaction entry?", "Confirm Delete",
                    MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (ask == MessageBoxResult.Yes)
                {
                    _service.DeleteTransaction(id);
                }
            }
        });

        ClearDebtCommand = new RelayCommand(p =>
        {
            if (p is string id)
            {
                var ask = MessageBox.Show("Has this customer cleared and repaid this amount in Cash?",
                    "Clear Customer Debt", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (ask == MessageBoxResult.Yes)
                {
                    _service.ClearCustomerDebt(id, PaymentMedium.CashInDrawer);
                }
            }
        });

        QuickUpiPayoutShortcutCommand = new RelayCommand(_ =>
        {
            SelectedCategoryString = "Customer UPI ➔ Cash Given";
            DescriptionInput = "Customer UPI transfer -> Cash given from drawer";
        });

        QuickBorrowShortcutCommand = new RelayCommand(_ =>
        {
            SelectedCategoryString = "Customer Borrow / Credit (Due)";
            DescriptionInput = "Due / Khata borrowed";
        });

        RefreshAll();
    }

    private void ExecuteAddTransaction()
    {
        string amtStr = AmountInput?.Trim().Replace(',', '.') ?? "";
        if (!double.TryParse(amtStr, NumberStyles.Any, CultureInfo.InvariantCulture, out double amt) || amt <= 0)
        {
            MessageBox.Show("Please enter a valid amount (e.g. 500 or 150.50).", "Invalid Amount", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        string commStr = CommissionInput?.Trim().Replace(',', '.') ?? "";
        double commission = 0;
        if (!string.IsNullOrEmpty(commStr))
        {
            double.TryParse(commStr, NumberStyles.Any, CultureInfo.InvariantCulture, out commission);
        }

        string custName = CustomerNameInput.Trim();
        string custPhone = CustomerPhoneInput.Trim();
        string desc = DescriptionInput.Trim();

        switch (SelectedCategoryString)
        {
            case "Customer UPI ➔ Cash Given":
                _service.RecordCustomerUpiCashout(amt, commission, custName, custPhone, desc);
                break;

            case "Customer Cash ➔ Online Paid":
                _service.RecordCustomerCashBankDeposit(amt, commission, custName, custPhone, desc);
                break;

            case "Customer Borrow / Credit (Due)":
                _service.RecordCustomerBorrow(amt, custName, custPhone, desc);
                break;

            case "Print & Xerox Sales":
                _service.AddTransaction(TransactionDirection.Income, PaymentMedium.CashInDrawer, CashCategory.PrintSales, amt, string.IsNullOrEmpty(desc) ? "Print / Xerox Counter Sale" : desc, custName, custPhone);
                break;

            case "Online Form Fillup Fee":
                _service.AddTransaction(TransactionDirection.Income, PaymentMedium.CashInDrawer, CashCategory.OnlineFormFillup, amt, string.IsNullOrEmpty(desc) ? "Online Form / Application Fee" : desc, custName, custPhone);
                break;

            case "Lamination & Photos":
                _service.AddTransaction(TransactionDirection.Income, PaymentMedium.CashInDrawer, CashCategory.LaminationPhotos, amt, string.IsNullOrEmpty(desc) ? "Lamination / Passport Photo" : desc, custName, custPhone);
                break;

            case "Owner Investment (Capital)":
                _service.AddTransaction(TransactionDirection.Income, PaymentMedium.CashInDrawer, CashCategory.OwnerInvestment, amt, string.IsNullOrEmpty(desc) ? "Owner Added Drawer Cash" : desc);
                break;

            case "Owner Withdrawal (Personal)":
                _service.AddTransaction(TransactionDirection.Expense, PaymentMedium.CashInDrawer, CashCategory.OwnerWithdrawal, amt, string.IsNullOrEmpty(desc) ? "Owner Personal Withdrawal" : desc);
                break;

            case "Shop Expense (Paper / Ink)":
                _service.AddTransaction(TransactionDirection.Expense, PaymentMedium.CashInDrawer, CashCategory.ShopExpensePaperInk, amt, string.IsNullOrEmpty(desc) ? "Paper Ream / Ink Purchase" : desc);
                break;

            case "Bills / Rent / Electricity":
                _service.AddTransaction(TransactionDirection.Expense, PaymentMedium.CashInDrawer, CashCategory.ShopExpenseBillsRent, amt, string.IsNullOrEmpty(desc) ? "Bills / Electricity / Internet" : desc);
                break;

            default:
                _service.AddTransaction(TransactionDirection.Income, PaymentMedium.CashInDrawer, CashCategory.Other, amt, string.IsNullOrEmpty(desc) ? "Counter Transaction" : desc, custName, custPhone);
                break;
        }

        // Clear entry inputs
        AmountInput = string.Empty;
        CommissionInput = string.Empty;
        CustomerNameInput = string.Empty;
        CustomerPhoneInput = string.Empty;
        DescriptionInput = string.Empty;
    }

    private void ExecuteSaveOpeningBalances()
    {
        string cStr = OpeningCashText?.Trim().Replace(',', '.') ?? "0";
        string oStr = OpeningOnlineText?.Trim().Replace(',', '.') ?? "0";

        double c = double.TryParse(cStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var cv) ? cv : 0;
        double o = double.TryParse(oStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var ov) ? ov : 0;

        _service.SetOpeningBalances(c, o);
        MessageBox.Show($"✅ Opening balances saved!\n\nCash Float in Drawer: ₹{c:F2}\nOnline/Bank Balance: ₹{o:F2}",
            "Opening Balances Updated", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void RefreshAll()
    {
        Transactions.Clear();
        foreach (var tx in _service.Today.Transactions)
        {
            Transactions.Add(new CashTransactionItemViewModel(tx));
        }

        OnPropertyChanged(nameof(CashInDrawerDisplay));
        OnPropertyChanged(nameof(OnlineBalanceDisplay));
        OnPropertyChanged(nameof(TodayRevenueDisplay));
        OnPropertyChanged(nameof(TodayExpensesDisplay));
        OnPropertyChanged(nameof(TodayNetProfitDisplay));
        OnPropertyChanged(nameof(UnpaidDebtDisplay));
        OnPropertyChanged(nameof(CashInToday));
        OnPropertyChanged(nameof(CashOutToday));
        OnPropertyChanged(nameof(OnlineInToday));
        OnPropertyChanged(nameof(OnlineOutToday));
    }

    private static void RunOnUI(Action action)
    {
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
                    var status = op.Wait(TimeSpan.FromMilliseconds(400));
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
