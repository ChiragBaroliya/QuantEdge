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
    // Trailing SL is mandatory in AutoRealTradeService regardless of this flag's value - kept only
    // for backward API/DB compatibility, not read by the service's exit/ratchet logic anymore.
    public bool TrailingSlEnabled { get; set; } = true;
    // Trailing Stop Loss % - shown as a default rather than blank because AutoRealTradeService now
    // enforces this same fallback whenever it's left null, so trailing SL is always attached.
    public decimal? TrailingSlPct { get; set; } = 2.00m;
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
    // Minutes after TradingWindowStart during which new BUY signals are held back, to let the
    // opening auction's gap/volatility resolve before committing capital. Exits are NOT gated by
    // this - only new entries (see AutoRealTradeService.EvaluateAndExecuteRealBuyCoreAsync).
    public int EntryDelayMinutes { get; set; } = 15;
    // Swing exit policy - shared with AutoTradeSettings and applied by SwingTradeRules, so Paper and
    // Real always run the same buy/sell rules. "SWING_CLOSE" (stops judged near the close) or
    // "INTRADAY" (previous behavior: every stop checked live).
    public string ExitMode { get; set; } = "SWING_CLOSE";
    public string CloseCheckTime { get; set; } = "15:15";
    public decimal StopLossAtrMult { get; set; } = 1.5m;
    public decimal TrailAtrMult { get; set; } = 3.0m;
    public decimal TargetAtrMult { get; set; } = 3.0m;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
