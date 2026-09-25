using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace SmartSaver.Models;

/// <summary>
/// Direction of fund movement.
/// </summary>
public enum TransactionDirection
{
    Income = 1,   // Money came into the business
    Expense = 2,  // Money paid out for business expenses
    Transfer = 3  // Swap between Drawer Cash and Online Bank (e.g. Customer UPI -> Cash out)
}

/// <summary>
/// Payment medium for the transaction.
/// </summary>
public enum PaymentMedium
{
    CashInDrawer = 1,
    OnlineUPI = 2
}

/// <summary>
/// Broad categorization of Cyber Cafe financial events.
/// </summary>
public enum CashCategory
{
    PrintSales,            // Auto or manual print revenue
    XeroxPhotocopy,        // Offline photocopy revenue
    OnlineFormFillup,      // Online applications, job forms, Admit Cards
    LaminationPhotos,      // Lamination & Passport photos
    AadhaarPanService,     // Aadhaar print, PAN card services
    CustomerUpiCashPayout, // Customer UPI transferred to cafe -> Cafe gave physical cash to customer
    CustomerCashBankDeposit,// Customer gave cash -> Cafe paid online bill/form
    CustomerBorrowCredit,  // Customer took service on credit (Khata / Due)
    CustomerDebtRepaid,    // Customer cleared previous borrow
    OwnerInvestment,       // Owner added capital into drawer/account
    OwnerWithdrawal,       // Owner took money out for personal use
    ShopExpensePaperInk,   // Bought paper reams, ink cartridges, lamination pouches
    ShopExpenseBillsRent,  // Electricity, internet WiFi, rent
    Other
}

/// <summary>
/// Single financial transaction entry in the daily cash book.
/// </summary>
public class CashTransaction
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.Now;
    public TransactionDirection Direction { get; set; } = TransactionDirection.Income;
    public PaymentMedium Medium { get; set; } = PaymentMedium.CashInDrawer;
    public CashCategory Category { get; set; } = CashCategory.PrintSales;

    public double Amount { get; set; } = 0;
    public double CommissionFee { get; set; } = 0; // e.g. ₹10 extra commission on UPI cashouts
    public string Description { get; set; } = string.Empty;
    public string CustomerName { get; set; } = string.Empty;
    public string CustomerPhone { get; set; } = string.Empty;
    public string? LinkedBillNumber { get; set; }

    /// <summary>
    /// For credit/borrow records: whether the customer has cleared this payment.
    /// </summary>
    public bool IsCleared { get; set; } = true;
}

/// <summary>
/// Full daily accounting session for the Cyber Cafe.
/// Persisted per calendar date.
/// </summary>
public class DailyCashRegister
{
    public string DateKey { get; set; } = DateTime.Today.ToString("yyyy-MM-dd");
    public DateTime Date { get; set; } = DateTime.Today;

    // ── Opening Floats at Start of Day ──
    public double OpeningCashInDrawer { get; set; } = 0;
    public double OpeningOnlineBalance { get; set; } = 0;

    public List<CashTransaction> Transactions { get; set; } = new();

    // ── Computed Real-time Balances ──
    [JsonIgnore]
    public double TotalCashIn =>
        Transactions.Where(t => t.Medium == PaymentMedium.CashInDrawer && t.Direction == TransactionDirection.Income).Sum(t => t.Amount)
        + Transactions.Where(t => t.Category == CashCategory.CustomerCashBankDeposit).Sum(t => t.Amount);

    [JsonIgnore]
    public double TotalCashOut =>
        Transactions.Where(t => t.Medium == PaymentMedium.CashInDrawer && t.Direction == TransactionDirection.Expense).Sum(t => t.Amount)
        + Transactions.Where(t => t.Category == CashCategory.CustomerUpiCashPayout).Sum(t => t.Amount);

    [JsonIgnore]
    public double TotalOnlineIn =>
        Transactions.Where(t => t.Medium == PaymentMedium.OnlineUPI && t.Direction == TransactionDirection.Income).Sum(t => t.Amount)
        + Transactions.Where(t => t.Category == CashCategory.CustomerUpiCashPayout).Sum(t => t.Amount + t.CommissionFee);

    [JsonIgnore]
    public double TotalOnlineOut =>
        Transactions.Where(t => t.Medium == PaymentMedium.OnlineUPI && t.Direction == TransactionDirection.Expense).Sum(t => t.Amount)
        + Transactions.Where(t => t.Category == CashCategory.CustomerCashBankDeposit).Sum(t => t.Amount);

    /// <summary>
    /// Exact physical cash money currently sitting in the cash drawer right now.
    /// </summary>
    [JsonIgnore]
    public double CurrentCashInDrawer => OpeningCashInDrawer + TotalCashIn - TotalCashOut;

    /// <summary>
    /// Exact money currently in the Online Bank / UPI Account right now.
    /// </summary>
    [JsonIgnore]
    public double CurrentOnlineBalance => OpeningOnlineBalance + TotalOnlineIn - TotalOnlineOut;

    /// <summary>
    /// Gross business revenue earned today (excluding capital transfers/investments).
    /// </summary>
    [JsonIgnore]
    public double TodayTotalRevenue => Transactions.Where(t =>
        t.Direction == TransactionDirection.Income &&
        t.Category != CashCategory.OwnerInvestment &&
        t.Category != CashCategory.CustomerDebtRepaid).Sum(t => t.Amount)
        + Transactions.Where(t => t.Category == CashCategory.CustomerUpiCashPayout).Sum(t => t.CommissionFee);

    /// <summary>
    /// Total expenses paid today (paper, ink, rent, personal withdrawals).
    /// </summary>
    [JsonIgnore]
    public double TodayTotalExpenses => Transactions.Where(t =>
        t.Direction == TransactionDirection.Expense &&
        t.Category != CashCategory.CustomerBorrowCredit).Sum(t => t.Amount);

    /// <summary>
    /// Outstanding customer debt / borrow / credit balance unpaid today.
    /// </summary>
    [JsonIgnore]
    public double TotalCustomerUnpaidDebt => Transactions.Where(t =>
        t.Category == CashCategory.CustomerBorrowCredit && !t.IsCleared).Sum(t => t.Amount);

    /// <summary>
    /// Net earnings after expenses for today.
    /// </summary>
    [JsonIgnore]
    public double TodayNetProfit => TodayTotalRevenue - TodayTotalExpenses;

    /// <summary>
    /// Total extra commission/fees earned today from UPI cashouts and service fees.
    /// </summary>
    [JsonIgnore]
    public double TodayTotalCommission => Transactions.Sum(t => t.CommissionFee);
}
