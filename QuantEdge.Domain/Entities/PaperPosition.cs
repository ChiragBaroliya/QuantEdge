using System;

namespace QuantEdge.Domain.Entities;

public class PaperPosition
{
    public int Id { get; set; }
    public int AccountId { get; set; }
    public string Symbol { get; set; } = string.Empty;
    public TradeSide Side { get; set; } = TradeSide.BUY;
    public int Quantity { get; set; }
    public decimal AverageEntryPrice { get; set; }
    public decimal CurrentPrice { get; set; }
    public decimal UnrealizedPnl { get; set; }
    // Display only (never stored): estimated round-trip charges if closed now at CurrentPrice (ChargesCalculator), and the
    // resulting net. Set by the services that build position lists for the trading pages.
    public decimal EstimatedCharges { get; set; }
    public decimal NetUnrealizedPnl => UnrealizedPnl - EstimatedCharges;
    // Display only: where CurrentPrice came from - LIVE (fresh tick), BROKER (REST quote now), STORED (DB value or last
    // stored candle, may be old) or NONE (no price: P&L is not meaningful) - and when it was observed, if known.
    public string? PriceSource { get; set; }
    public DateTime? PriceAsOfUtc { get; set; }
    public decimal? StopLoss { get; set; }
    public decimal? TakeProfit { get; set; }
    public decimal? TrailingStopLoss { get; set; }
    // Effective SL%/Trailing SL% this position was opened with (Auto Paper Trade), so later edits to
    // the global settings don't silently change the risk parameters of an already-open position.
    public decimal? StopLossPct { get; set; }
    public decimal? TrailingSlPct { get; set; }
    public PositionStatus Status { get; set; } = PositionStatus.OPEN;
    public TradeType TradeType { get; set; } = TradeType.Manual;
    public string? ExitReason { get; set; }
    public DateTime OpenedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ClosedAt { get; set; }
    public decimal RealizedPnl { get; set; }
}

