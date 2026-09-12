using System;

namespace QuantEdge.Domain.Entities;

public class RealTradeSettings
{
    public int Id { get; set; }
    public int UserId { get; set; } = 1;
    public bool IsRealTradeEnabled { get; set; } = false;
    public decimal AvailableCapital { get; set; } = 2000.00m;
    public decimal ProfitTargetPct { get; set; } = 5.00m;
    // Stop Loss % - shown as a default rather than blank because AutoRealTradeService now
    // enforces this same fallback whenever it's left null, so real positions are never unprotected.
    public decimal? StopLossPct { get; set; } = 3.00m;
    public bool TrailingSlEnabled { get; set; } = false; // Optional Trailing SL toggle
    public decimal? TrailingSlPct { get; set; } // Optional Trailing SL %
    public int MaxDurationDays { get; set; } = 20;
    public int MaxTradesPerDay { get; set; } = 5;
    public decimal FixedAmountPerTrade { get; set; } = 400.00m;
    // Daily Loss Circuit Breaker - defaults to 10% of AvailableCapital; AutoRealTradeService
    // applies the same fallback whenever this is left null, so the breaker is never inactive.
    public decimal? MaxDailyLossLimit { get; set; } = 200.00m;
    public string ProductType { get; set; } = "CNC"; // CNC (Delivery) or MIS (Intraday)
    public int MinConditionsMatch { get; set; } = 10;
    public string TradingWindowStart { get; set; } = "09:15";
    public string TradingWindowEnd { get; set; } = "15:30";
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
