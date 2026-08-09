using System;

namespace QuantEdge.Domain.Entities;

public class PaperAccount
{
    public int Id { get; set; }
    public string UserId { get; set; } = "default_user";
    public string AccountName { get; set; } = "Virtual Trading Account";
    public decimal InitialBalance { get; set; } = 100000m;
    public decimal CurrentBalance { get; set; } = 100000m;
    public decimal UsedMargin { get; set; } = 0m;
    public decimal AvailableMargin => CurrentBalance - UsedMargin;
    public decimal RealizedPnl { get; set; } = 0m;
    public bool IsAutoTradeEnabled { get; set; } = false;
    public string TradingMode { get; set; } = "Paper"; // "Paper" or "Live"
    public string AutoTradeTimeframe { get; set; } = "1m";
    public decimal AutoTradeMinSignalStrength { get; set; } = 70m;
    public int AutoTradeQuantity { get; set; } = 25;
    public decimal AutoTradeStopLossPercent { get; set; } = 1.0m;
    public decimal AutoTradeTakeProfitPercent { get; set; } = 2.0m;
    public int MaxOpenPositions { get; set; } = 5;
    public decimal DailyMaxLossLimit { get; set; } = 2000m;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
