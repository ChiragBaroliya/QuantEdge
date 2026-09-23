using System;

namespace QuantEdge.Domain.Entities;

public class AutoTradeSettings
{
    public int Id { get; set; }
    public string UserId { get; set; } = "default_user";
    public bool IsAutoTradeEnabled { get; set; } = false;
    public decimal AvailableCapital { get; set; } = 100000.00m;
    public decimal ProfitTargetPct { get; set; } = 5.00m;
    // Stop Loss % - shown as a default rather than blank because AutoTradeService now enforces
    // this same fallback whenever it's left null, so auto paper positions are never unprotected.
    public decimal? StopLossPct { get; set; } = 3.00m;
    // Trailing Stop Loss % - mandatory like Stop Loss above; AutoTradeService applies the same
    // fallback whenever this is left null, so trailing SL is always attached to an auto position.
    public decimal? TrailingSlPct { get; set; } = 2.00m;
    public int MaxDurationDays { get; set; } = 20;
    public int MaxTradesPerDay { get; set; } = 5;
    public decimal FixedAmountPerTrade { get; set; } = 20000.00m;
    public int MinConditionsMatch { get; set; } = 10;
    public string TradingWindowStart { get; set; } = "09:15";
    public string TradingWindowEnd { get; set; } = "15:30";
    // Same buy/sell rule set as RealTradeSettings (see SwingTradeRules) - Paper must behave like Real.
    public int EntryDelayMinutes { get; set; } = 15;
    // Daily Loss Circuit Breaker - SwingTradeRules falls back to 10% of AvailableCapital when null.
    public decimal? MaxDailyLossLimit { get; set; }
    public string ExitMode { get; set; } = "SWING_CLOSE";
    public string CloseCheckTime { get; set; } = "15:15";
    public decimal StopLossAtrMult { get; set; } = 1.5m;
    public decimal TrailAtrMult { get; set; } = 3.0m;
    public decimal TargetAtrMult { get; set; } = 3.0m;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
