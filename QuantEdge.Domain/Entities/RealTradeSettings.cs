using System;

namespace QuantEdge.Domain.Entities;

public class RealTradeSettings
{
    public int Id { get; set; }
    public int UserId { get; set; } = 1;
    public bool IsRealTradeEnabled { get; set; } = false;
    public decimal AvailableCapital { get; set; } = 2000.00m;
    public decimal ProfitTargetPct { get; set; } = 5.00m;
    public decimal? StopLossPct { get; set; } // Optional Stop Loss %
    public bool TrailingSlEnabled { get; set; } = false; // Optional Trailing SL toggle
    public decimal? TrailingSlPct { get; set; } // Optional Trailing SL %
    public int MaxDurationDays { get; set; } = 20;
    public int MaxTradesPerDay { get; set; } = 5;
    public decimal FixedAmountPerTrade { get; set; } = 400.00m;
    public decimal? MaxDailyLossLimit { get; set; } // Optional Daily Loss Circuit Breaker
    public string ProductType { get; set; } = "CNC"; // CNC (Delivery) or MIS (Intraday)
    public int MinConditionsMatch { get; set; } = 10;
    public string TradingWindowStart { get; set; } = "09:15";
    public string TradingWindowEnd { get; set; } = "15:30";
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
