using System;
using System.Collections.Generic;
using QuantEdge.Infrastructure.Services;

namespace QuantEdge.Infrastructure.DTOs;

/// <summary>
/// One multi-timeframe verdict for a stock on the Signal Dashboard, from the same SwingDecisionEngine
/// evaluation the auto-trading bot runs - so the dashboard and the bot never disagree.
/// </summary>
public class StockVerdictDto
{
    public string Symbol { get; set; } = string.Empty;
    public DateTime AsOfUtc { get; set; } = DateTime.UtcNow;
    /// <summary>BUY, WAIT or AVOID for a new buyer; HOLD when the user already owns the stock; NO_DATA if it can't be scored.</summary>
    public string Verdict { get; set; } = "NO_DATA";
    /// <summary>The engine's own decision: BUY, WATCH, NO SIGNAL or REJECT.</summary>
    public string EngineDecision { get; set; } = string.Empty;
    /// <summary>True when the bot itself would try to buy: a BUY signal, or at least MinConditionsMatch conditions met.</summary>
    public bool IsBotCandidate { get; set; }
    public int Score { get; set; }
    public int BuyThreshold { get; set; }
    public int WatchThreshold { get; set; }
    public int MetCount { get; set; }
    public int TotalConditions { get; set; }
    public int MinConditionsMatch { get; set; }

    public decimal LastPrice { get; set; }
    public decimal? DayChangePct { get; set; }
    public decimal StopLoss { get; set; }
    public decimal Target1 { get; set; }

    // Timeframe ladder: NIFTY -> 1 day trend -> 60 min setup -> 15 min timing
    public bool MarketPassed { get; set; }
    public int MarketPenalty { get; set; }
    public bool TrendPassed { get; set; }
    public bool EmaTrendPassed { get; set; }
    public bool AdxPassed { get; set; }
    public decimal Adx1d { get; set; }
    public decimal Ema20_1d { get; set; }
    public decimal Ema50_1d { get; set; }
    public bool Has60mData { get; set; }
    public bool SetupPassed { get; set; }
    public decimal? Rsi60m { get; set; }
    public decimal Rsi15m { get; set; }
    public decimal VolumeMultiple { get; set; }
    public int TimingPoints { get; set; }
    public int TimingMaxPoints { get; set; }

    public List<SwingFactorScore> Factors { get; set; } = new();
    public string EngineReason { get; set; } = string.Empty;

    /// <summary>Set when the user owns the stock - the verdict is then HOLD and the bot's exit rules decide.</summary>
    public StockVerdictHoldingDto? Holding { get; set; }

    /// <summary>Only when Verdict is BUY: would the bot actually place the order right now?</summary>
    public BuyGuardPreviewDto? BotPreview { get; set; }
}

public class StockVerdictHoldingDto
{
    /// <summary>BOT (a monitored position) or ZERODHA (in the account, not monitored).</summary>
    public string Source { get; set; } = "BOT";
    public int Quantity { get; set; }
    public decimal AveragePrice { get; set; }
    public decimal? StopLoss { get; set; }
    public decimal? TakeProfit { get; set; }
}

/// <summary>
/// Read-only preview of the pre-trade guards for one stock - nothing is logged or ordered. Guard 12
/// (price drift since the scan) is only known at order time, so it is reported as not checked.
/// </summary>
public class BuyGuardPreviewDto
{
    public bool WillBuy { get; set; }
    public int? FailedGuardNumber { get; set; }
    public string? FailedGuardName { get; set; }
    public string? Reason { get; set; }
    public int TotalGuards { get; set; } = RealTradeGuards.TotalGuards;
    public bool IsBotEnabled { get; set; }
    public decimal? AvailableMargin { get; set; }
    public decimal TradeAmount { get; set; }
    public int Quantity { get; set; }
    public decimal EstimatedCost { get; set; }
}
