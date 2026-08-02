using System;

namespace QuantEdge.Domain.Entities;

public class AutoTradeSettings
{
    public int Id { get; set; }
    public string UserId { get; set; } = "default_user";
    public bool IsAutoTradeEnabled { get; set; } = false;
    public decimal AvailableCapital { get; set; } = 100000.00m;
    public decimal ProfitTargetPct { get; set; } = 5.00m;
    public decimal StopLossPct { get; set; } = 3.00m;
    public int MaxDurationDays { get; set; } = 20;
    public int MaxTradesPerDay { get; set; } = 5;
    public decimal FixedAmountPerTrade { get; set; } = 20000.00m;
    public int MinConditionsMatch { get; set; } = 12;
    public string TradingWindowStart { get; set; } = "09:15";
    public string TradingWindowEnd { get; set; } = "15:30";
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
