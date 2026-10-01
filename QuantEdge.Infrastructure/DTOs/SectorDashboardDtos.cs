using System;
using System.Collections.Generic;

namespace QuantEdge.Infrastructure.DTOs;

/// <summary>Sector Dashboard: every active sector, ranked, for the 9:15-9:30 market-open check.</summary>
public class SectorOverviewDto
{
    public DateTime AsOfUtc { get; set; } = DateTime.UtcNow;
    /// <summary>NIFTY 50 market filter (Close > SMA50 and EMA20 > EMA50) - the bot buys nothing while it fails.</summary>
    public bool MarketPassed { get; set; }
    public bool MarketRequired { get; set; }
    public List<SectorSummaryDto> Sectors { get; set; } = new();
}

public class SectorSummaryDto
{
    public int SectorId { get; set; }
    public string Name { get; set; } = string.Empty;
    /// <summary>The sector index's last value, when its candles are stored under the sector name (e.g. "NIFTY BANK").</summary>
    public decimal? IndexValue { get; set; }
    /// <summary>The index's day change when available, otherwise the equal-weight average of the linked stocks.</summary>
    public decimal? ChangePct { get; set; }
    /// <summary>INDEX or STOCKS - where ChangePct came from.</summary>
    public string ChangeSource { get; set; } = "STOCKS";
    /// <summary>STRONG, NEUTRAL, WEAK or NO_DATA.</summary>
    public string Strength { get; set; } = "NO_DATA";
    /// <summary>0-100 ranking score: half day change, half breadth (share of stocks up).</summary>
    public int StrengthScore { get; set; }
    /// <summary>Share (0-100) of scored stocks passing the bot's daily trend filters (EMA trend + ADX).</summary>
    public int MomentumPct { get; set; }
    public int TotalStocks { get; set; }
    public int ScoredStocks { get; set; }
    public int AdvancingStocks { get; set; }
    public int DecliningStocks { get; set; }
    public int BuyCount { get; set; }
    public int WatchCount { get; set; }
    public int NoTradeCount { get; set; }
    public int NoDataCount { get; set; }
}

/// <summary>One sector and the trade status of every stock linked to it.</summary>
public class SectorDetailDto
{
    public DateTime AsOfUtc { get; set; } = DateTime.UtcNow;
    public bool MarketPassed { get; set; }
    public bool MarketRequired { get; set; }
    public int BuyThreshold { get; set; }
    public int WatchThreshold { get; set; }
    public int MinConditionsMatch { get; set; }
    public SectorSummaryDto Sector { get; set; } = new();
    public List<SectorStockSignalDto> Stocks { get; set; } = new();
}

public class SectorStockSignalDto
{
    public string Symbol { get; set; } = string.Empty;
    public string? Name { get; set; }
    /// <summary>BUY, WATCH, NO_TRADE or NO_DATA - the final status on the Sector Dashboard.</summary>
    public string Signal { get; set; } = "NO_DATA";
    /// <summary>What the Signal Dashboard and the bot say (BUY, WAIT, AVOID or NO_DATA), before the sector check.</summary>
    public string BotVerdict { get; set; } = "NO_DATA";
    /// <summary>The engine's own decision: BUY, WATCH, NO SIGNAL or REJECT.</summary>
    public string EngineDecision { get; set; } = string.Empty;
    /// <summary>One line on why the stock got its signal.</summary>
    public string Summary { get; set; } = string.Empty;
    /// <summary>Short tag for the candidate list, e.g. "Breakout + Volume".</summary>
    public string Highlight { get; set; } = string.Empty;

    public int Score { get; set; }
    public int MetCount { get; set; }
    public int TotalConditions { get; set; }

    public decimal LastPrice { get; set; }
    public decimal? DayChangePct { get; set; }
    public decimal VolumeMultiple { get; set; }
    public decimal Rsi15m { get; set; }
    public decimal? Rsi60m { get; set; }
    public decimal Adx1d { get; set; }
    /// <summary>15-min timing points earned (breakout, volume, RSI, MACD, candle) out of TimingMaxPoints.</summary>
    public int TimingPoints { get; set; }
    public int TimingMaxPoints { get; set; }

    public decimal Entry { get; set; }
    public decimal StopLoss { get; set; }
    public decimal Target1 { get; set; }
    public decimal RiskReward { get; set; }

    /// <summary>Trend / Momentum / Volume / Breakout / Risk:Reward at a glance.</summary>
    public List<SectorStockPillarDto> Pillars { get; set; } = new();
    /// <summary>The sector check first, then the engine's 11 conditions in order.</summary>
    public List<SectorConditionDto> Conditions { get; set; } = new();
}

public class SectorStockPillarDto
{
    public string Label { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    /// <summary>PASS, FAIL or NA (not checked - the stock was rejected before this part was scored).</summary>
    public string State { get; set; } = "NA";
}

public class SectorConditionDto
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    /// <summary>What the rule asks for.</summary>
    public string Detail { get; set; } = string.Empty;
    /// <summary>The stock's current reading against the rule.</summary>
    public string Value { get; set; } = string.Empty;
    public bool IsMet { get; set; }
    /// <summary>False when a hard filter rejected the stock first, so this scoring rule was never evaluated.</summary>
    public bool IsChecked { get; set; } = true;
    /// <summary>True for conditions that block a BUY on their own (the hard filters and the sector check).</summary>
    public bool IsGate { get; set; }
}
