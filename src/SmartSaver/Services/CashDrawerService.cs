using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using SmartSaver.Models;
using Serilog;

namespace SmartSaver.Services;

/// <summary>
/// Professional Daily Cash Drawer, Bank / UPI Tracker, and Customer Credit Register Service.
/// Persists all financial sessions to JSON in AppData for seamless daily continuity.
/// </summary>
public sealed class CashDrawerService
{
    private static readonly Lazy<CashDrawerService> _instance = new(() => new CashDrawerService());
    public static CashDrawerService Instance => _instance.Value;

    private readonly string _dataFilePath;
    private readonly object _lock = new();
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

    private readonly Dictionary<string, DailyCashRegister> _registers = new(StringComparer.OrdinalIgnoreCase);

    public event Action? OnRegisterChanged;

    private CashDrawerService()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string dataDir = Path.Combine(appData, "DASMO CYBER CAFE TOOLS");
        Directory.CreateDirectory(dataDir);
        _dataFilePath = Path.Combine(dataDir, "cash_drawer_registers.json");

        LoadData();
        EnsureTodayRegister();
    }

    public DailyCashRegister Today
    {
        get
        {
            lock (_lock)
            {
                string key = DateTime.Today.ToString("yyyy-MM-dd");
                if (!_registers.TryGetValue(key, out var reg))
                {
                    reg = CreateNewDayRegister(DateTime.Today);
                    _registers[key] = reg;
                    SaveData();
                }
                return reg;
            }
        }
    }

    public IReadOnlyList<DailyCashRegister> AllDays
    {
        get
        {
            lock (_lock)
            {
                return _registers.Values.OrderByDescending(r => r.Date).ToList();
            }
        }
    }

    public void SetOpeningBalances(double cash, double online)
    {
        lock (_lock)
        {
            Today.OpeningCashInDrawer = Math.Round(cash, 2);
            Today.OpeningOnlineBalance = Math.Round(online, 2);
            SaveData();
        }
        OnRegisterChanged?.Invoke();
        PrintTrackerService.Instance.TriggerExcelAutoSync();
    }

    public CashTransaction AddTransaction(
        TransactionDirection direction,
        PaymentMedium medium,
        CashCategory category,
        double amount,
        string description,
        string customerName = "",
        string customerPhone = "",
        double commission = 0)
    {
        var tx = new CashTransaction
        {
            Timestamp = DateTimeOffset.Now,
            Direction = direction,
            Medium = medium,
            Category = category,
            Amount = Math.Round(Math.Abs(amount), 2),
            CommissionFee = Math.Round(Math.Max(0, commission), 2),
            Description = description,
            CustomerName = customerName,
            CustomerPhone = customerPhone,
            IsCleared = category != CashCategory.CustomerBorrowCredit
        };

        lock (_lock)
        {
            Today.Transactions.Insert(0, tx);
            SaveData();
        }

        Log.Information("Finance Entry added: [{Dir}] {Cat} Rs{Amt} (Medium: {Medium}, Cust: {Cust})",
            direction, category, tx.Amount, medium, customerName);

        OnRegisterChanged?.Invoke();
        PrintTrackerService.Instance.TriggerExcelAutoSync();
        return tx;
    }

    /// <summary>
    /// Specialized high-frequency Cyber Cafe workflow:
    /// Customer scans UPI QR code on cafe account -> Cafe owner gives physical paper cash from the drawer.
    /// Optional ₹5/₹10 service charge or fee is recorded as pure cafe income.
    /// </summary>
    public CashTransaction RecordCustomerUpiCashout(double amount, double commission, string customerName, string customerPhone, string desc)
    {
        string fullDesc = string.IsNullOrWhiteSpace(desc)
            ? $"UPI Cashout to customer ({customerName})"
            : desc;

        return AddTransaction(
            TransactionDirection.Transfer,
            PaymentMedium.CashInDrawer,
            CashCategory.CustomerUpiCashPayout,
            amount,
            fullDesc,
            customerName,
            customerPhone,
            commission);
    }

    /// <summary>
    /// Reverse workflow: Customer pays cash to cafe -> Cafe pays electricity bill, admission fee, or form online.
    /// </summary>
    public CashTransaction RecordCustomerCashBankDeposit(double amount, double commission, string customerName, string customerPhone, string desc)
    {
        string fullDesc = string.IsNullOrWhiteSpace(desc)
            ? $"Online Form / Bill Payment for {customerName}"
            : desc;

        return AddTransaction(
            TransactionDirection.Transfer,
            PaymentMedium.OnlineUPI,
            CashCategory.CustomerCashBankDeposit,
            amount,
            fullDesc,
            customerName,
            customerPhone,
            commission);
    }

    /// <summary>
    /// Records a customer taking services on credit / borrow (Khata).
    /// </summary>
    public CashTransaction RecordCustomerBorrow(double amount, string customerName, string customerPhone, string desc)
    {
        return AddTransaction(
            TransactionDirection.Expense,
            PaymentMedium.CashInDrawer,
            CashCategory.CustomerBorrowCredit,
            amount,
            string.IsNullOrWhiteSpace(desc) ? $"Credit / Due: {customerName}" : desc,
            customerName,
            customerPhone);
    }

    /// <summary>
    /// Clears a customer's outstanding credit when they return and pay.
    /// </summary>
    public bool ClearCustomerDebt(string transactionId, PaymentMedium receivedVia)
    {
        lock (_lock)
        {
            var tx = Today.Transactions.FirstOrDefault(t => t.Id == transactionId);
            if (tx == null) return false;

            tx.IsCleared = true;

            // Record the clearing payment as income today
            AddTransaction(
                TransactionDirection.Income,
                receivedVia,
                CashCategory.CustomerDebtRepaid,
                tx.Amount,
                $"Debt Repaid by {tx.CustomerName} (Ref: {tx.Description})",
                tx.CustomerName,
                tx.CustomerPhone);

            SaveData();
        }

        OnRegisterChanged?.Invoke();
        PrintTrackerService.Instance.TriggerExcelAutoSync();
        return true;
    }

    public bool DeleteTransaction(string transactionId)
    {
        lock (_lock)
        {
            var tx = Today.Transactions.FirstOrDefault(t => t.Id == transactionId);
            if (tx == null) return false;

            Today.Transactions.Remove(tx);
            SaveData();
        }

        OnRegisterChanged?.Invoke();
        PrintTrackerService.Instance.TriggerExcelAutoSync();
        return true;
    }

    /// <summary>
    /// Permanently clears/deletes all transactions for today with safety precautions.
    /// Opening float remains intact.
    /// </summary>
    public void ClearTodayTransactions()
    {
        lock (_lock)
        {
            Today.Transactions.Clear();
            SaveData();
        }

        OnRegisterChanged?.Invoke();
        PrintTrackerService.Instance.TriggerExcelAutoSync();
    }

    private void EnsureTodayRegister()
    {
        lock (_lock)
        {
            string key = DateTime.Today.ToString("yyyy-MM-dd");
            if (!_registers.ContainsKey(key))
            {
                var reg = CreateNewDayRegister(DateTime.Today);
                _registers[key] = reg;
                SaveData();
            }
        }
    }

    private DailyCashRegister CreateNewDayRegister(DateTime date)
    {
        // Automatically carry forward closing cash & online balance from most recent previous day
        var previous = _registers.Values
            .Where(r => r.Date.Date < date.Date)
            .OrderByDescending(r => r.Date)
            .FirstOrDefault();

        double carryCash = previous != null ? previous.CurrentCashInDrawer : 0;
        double carryOnline = previous != null ? previous.CurrentOnlineBalance : 0;

        return new DailyCashRegister
        {
            DateKey = date.ToString("yyyy-MM-dd"),
            Date = date.Date,
            OpeningCashInDrawer = Math.Max(0, carryCash),
            OpeningOnlineBalance = Math.Max(0, carryOnline)
        };
    }

    private void LoadData()
    {
        lock (_lock)
        {
            try
            {
                if (File.Exists(_dataFilePath))
                {
                    string json = File.ReadAllText(_dataFilePath);
                    var list = JsonSerializer.Deserialize<List<DailyCashRegister>>(json, _jsonOptions);
                    if (list != null)
                    {
                        foreach (var item in list)
                        {
                            _registers[item.DateKey] = item;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to load cash drawer registers");
            }
        }
    }

    private void SaveData()
    {
        try
        {
            var list = _registers.Values.OrderByDescending(r => r.Date).Take(365).ToList();
            string json = JsonSerializer.Serialize(list, _jsonOptions);
            File.WriteAllText(_dataFilePath, json);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to save cash drawer data");
        }
    }
}
