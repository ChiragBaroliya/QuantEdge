using System;

namespace QuantEdge.Domain.Entities;

/// <summary>
/// Manual Paper Trading settings (manual_paper_trade_settings) - separate from AutoTradeSettings so the
/// Manual Trading page has its own switch, limits and exit rules without affecting Auto Paper Trading.
/// </summary>
public class ManualPaperTradeSettings
{
    public int Id { get; set; }
    public int UserId { get; set; } = 1;
    public bool IsManualTradeEnabled { get; set; } = true;
    // Used for the Daily Loss Circuit Breaker fallback (10% of this when MaxDailyLossLimit is null).
    public decimal AvailableCapital { get; set; } = 100000.00m;
    public decimal ProfitTargetPct { get; set; } = 5.00m;
    // Default trade-wise Stop Loss % / Trailing SL % pre-filled in the order ticket.
    public decimal StopLossPct { get; set; } = 3.00m;
    public decimal TrailingSlPct { get; set; } = 2.00m;
    public int MaxDurationDays { get; set; } = 20;
    public int MaxTradesPerDay { get; set; } = 5;
    // Used to pre-fill the order ticket's Quantity (amount / LTP).
    public decimal FixedAmountPerTrade { get; set; } = 20000.00m;
    public string TradingWindowStart { get; set; } = "09:15";
    public string TradingWindowEnd { get; set; } = "15:30";
    public int EntryDelayMinutes { get; set; } = 15;
    public decimal? MaxDailyLossLimit { get; set; }
    public string ExitMode { get; set; } = "SWING_CLOSE";
    public string CloseCheckTime { get; set; } = "15:15";
    public decimal StopLossAtrMult { get; set; } = 1.5m;
    public decimal TrailAtrMult { get; set; } = 3.0m;
    public decimal TargetAtrMult { get; set; } = 3.0m;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
