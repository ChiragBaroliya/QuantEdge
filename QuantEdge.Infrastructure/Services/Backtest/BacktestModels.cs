using System;
using System.Collections.Generic;

namespace QuantEdge.Infrastructure.Services.Backtest;

/// <summary>
/// One backtest's inputs (Plan Phase 7). Blank fields are filled from the live settings when the run is queued
/// (swing_strategy_settings, real_trade_settings, regime_policy), and the fully resolved copy is stored with the run,
/// so a result can always be traced back to the exact rules that produced it.
/// </summary>
public class BacktestParams
{
    public string? Label { get; set; }
    /// <summary>First and last trading day to test (IST dates).</summary>
    public DateTime FromDate { get; set; }
    public DateTime ToDate { get; set; }
    /// <summary>Optional subset of symbols; empty = every active stock.</summary>
    public List<string> Symbols { get; set; } = new();

    // Strategy (swing_strategy_settings)
    /// <summary>NIFTY_FILTER or REGIME.</summary>
    public string? GateMode { get; set; }
    public int? BuyScoreThreshold { get; set; }
    public int? WatchScoreThreshold { get; set; }
    /// <summary>NIFTY_FILTER mode: the bot also buys when at least this many of the 11 conditions are met (live rule).</summary>
    public int? MinConditionsMatch { get; set; }

    // Portfolio
    public decimal Capital { get; set; } = 100_000m;
    public decimal AmountPerTrade { get; set; } = 10_000m;
    /// <summary>RISK = smaller of the fixed amount and capital × RiskPct ÷ (entry − stop); FIXED = amount ÷ price.</summary>
    public string SizingMode { get; set; } = "RISK";
    public decimal RiskPct { get; set; } = 1.0m;
    public int? MaxPositions { get; set; }
    public int? MaxTradesPerDay { get; set; }
    /// <summary>Realised loss in a day that stops new buys for the rest of that day (₹). Null = 10% of capital (live default).</summary>
    public decimal? DailyLossLimit { get; set; }
    /// <summary>Price slippage per side, % (paid on entry and on exit).</summary>
    public decimal SlippagePct { get; set; } = 0.10m;

    // Execution / exits (real_trade_settings)
    public string? TradingWindowStart { get; set; }
    public string? TradingWindowEnd { get; set; }
    public int? EntryDelayMinutes { get; set; }
    public string? ExitMode { get; set; }
    public string? CloseCheckTime { get; set; }
    public decimal? StopLossAtrMult { get; set; }
    public decimal? TrailAtrMult { get; set; }
    public decimal? TargetAtrMult { get; set; }
    public decimal? ProfitTargetPct { get; set; }
    public int? MaxDurationDays { get; set; }
}

/// <summary>A 15-minute scan that the strategy would have acted on (or that the threshold sweep needs), with its simulated trade.</summary>
public sealed class BacktestSignal
{
    public string Symbol { get; init; } = string.Empty;
    public DateTime SignalTimeUtc { get; init; }      // close of the signal bar = scan time
    public DateTime EntryTimeUtc { get; init; }       // open of the next bar
    public DateTime SessionDate { get; init; }        // IST date of the entry
    public int Score { get; init; }
    public int MetCount { get; init; }
    public bool HardFiltersPassed { get; init; }
    public bool BeatsNifty { get; init; }
    /// <summary>What the live bot would have done with this scan (before the portfolio limits).</summary>
    public bool IsLiveCandidate { get; init; }
    public string? Regime { get; init; }
    public bool GateAllows { get; init; }
    public bool PolicyRequiresRs { get; init; }
    public int PolicyMaxPositions { get; init; }
    public decimal PolicyRiskPct { get; init; }
    /// <summary>Points per scoring factor, 6 bits each in <see cref="BacktestEngine.FactorCodes"/> order (kept small: a run can hold 100k+ signals).</summary>
    public long FactorPoints { get; init; }

    public decimal SignalPrice { get; init; }
    public decimal EntryRaw { get; init; }
    public decimal StopLoss { get; init; }
    public decimal Target { get; init; }
    /// <summary>Index of the entry bar in the symbol's <see cref="IntradayTape"/>.</summary>
    public int EntryIndex { get; init; }
    /// <summary>Set when the trade can't be taken (price drift guard, suspected split); the signal is then never taken.</summary>
    public string? SkipReason { get; set; }
    /// <summary>Simulated lazily - only for signals the portfolio actually considers - and then reused by every sweep run.</summary>
    public BacktestExit? Exit { get; set; }
}

public sealed record BacktestExit(DateTime ExitTimeUtc, DateTime SessionDate, decimal ExitRaw, string Reason, string Category,
    int SessionsHeld, decimal MfePct, decimal MaePct, bool EndOfData);

/// <summary>A trade the simulated portfolio actually took.</summary>
public sealed class BacktestTrade
{
    public string Symbol { get; set; } = string.Empty;
    public string? Sector { get; set; }
    public string? Regime { get; set; }
    public DateTime SignalTime { get; set; }
    public DateTime EntryTime { get; set; }
    public decimal EntryPrice { get; set; }       // fill incl. slippage
    public DateTime ExitTime { get; set; }
    public decimal ExitPrice { get; set; }        // fill incl. slippage
    public string ExitReason { get; set; } = string.Empty;
    public string ExitCategory { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public decimal StopLoss { get; set; }
    public decimal Target { get; set; }
    public int Score { get; set; }
    public int MetCount { get; set; }
    public int SessionsHeld { get; set; }
    public decimal GrossPnl { get; set; }
    public decimal Charges { get; set; }
    public decimal NetPnl { get; set; }
    /// <summary>Net P&amp;L ÷ the money at risk at entry (quantity × (entry − stop)).</summary>
    public decimal RMultiple { get; set; }
    public decimal MfePct { get; set; }
    public decimal MaePct { get; set; }
    public string Factors { get; set; } = string.Empty;
    public bool EndOfData { get; set; }
}

public sealed class BacktestKpis
{
    public int Trades { get; set; }
    public int Wins { get; set; }
    public decimal WinRatePct { get; set; }
    public decimal AvgWinR { get; set; }
    public decimal AvgLossR { get; set; }
    public decimal ExpectancyR { get; set; }
    public decimal ProfitFactor { get; set; }
    public decimal GrossPnl { get; set; }
    public decimal Charges { get; set; }
    public decimal NetPnl { get; set; }
    public decimal ReturnPct { get; set; }
    public decimal CagrPct { get; set; }
    public decimal MaxDrawdownPct { get; set; }
    public decimal MaxDrawdownRs { get; set; }
    public decimal AvgSessionsHeld { get; set; }
    public decimal TradesPerMonth { get; set; }
    public decimal ExposurePct { get; set; }
    public int MaxConsecutiveLosses { get; set; }
    public decimal BestTradeR { get; set; }
    public decimal WorstTradeR { get; set; }
}

public sealed class BacktestGroupRow
{
    public string Key { get; set; } = string.Empty;
    public int Trades { get; set; }
    public decimal WinRatePct { get; set; }
    public decimal ExpectancyR { get; set; }
    public decimal NetPnl { get; set; }
    public decimal ProfitFactor { get; set; }
}

public sealed class BacktestSweepRow
{
    public int Threshold { get; set; }
    public int Trades { get; set; }
    public decimal WinRatePct { get; set; }
    public decimal ExpectancyR { get; set; }
    public decimal ProfitFactor { get; set; }
    public decimal NetPnl { get; set; }
    public decimal MaxDrawdownPct { get; set; }
}

public sealed class BacktestFactorRow
{
    public string Code { get; set; } = string.Empty;
    public int TradesWith { get; set; }
    public decimal ExpectancyWithR { get; set; }
    public decimal WinRateWithPct { get; set; }
    public int TradesWithout { get; set; }
    public decimal ExpectancyWithoutR { get; set; }
    public decimal WinRateWithoutPct { get; set; }
}

public sealed record BacktestEquityPoint(DateTime Date, decimal Equity, decimal DrawdownPct);

public sealed class BacktestSummary
{
    /// <summary>PASS, NOT_PROVEN or FAIL against the plan's go-live rule (≥ 30 trades and expectancy ≥ +0.2R after costs).</summary>
    public string Verdict { get; set; } = "NOT_PROVEN";
    public string VerdictReason { get; set; } = string.Empty;
    public BacktestKpis Kpis { get; set; } = new();
    public List<BacktestEquityPoint> Equity { get; set; } = new();
    public List<BacktestGroupRow> ByRegime { get; set; } = new();
    public List<BacktestGroupRow> ByExit { get; set; } = new();
    public List<BacktestGroupRow> ByYear { get; set; } = new();
    public List<BacktestGroupRow> ByMonth { get; set; } = new();
    public List<BacktestGroupRow> BySector { get; set; } = new();
    public List<BacktestGroupRow> ByScore { get; set; } = new();
    /// <summary>First half vs second half of the period - a strategy with an edge should not live in one half only.</summary>
    public List<BacktestGroupRow> ByHalf { get; set; } = new();
    public List<BacktestSweepRow> ThresholdSweep { get; set; } = new();
    public List<BacktestFactorRow> FactorEdge { get; set; } = new();
    /// <summary>Signals the live bot would have acted on, and why the portfolio skipped some of them.</summary>
    public int LiveSignals { get; set; }
    public Dictionary<string, int> Skipped { get; set; } = new();
    public BacktestCoverage Coverage { get; set; } = new();
    public List<string> Assumptions { get; set; } = new();
}

public sealed class BacktestCoverage
{
    public int SymbolsRequested { get; set; }
    public int SymbolsWithData { get; set; }
    public DateTime? FirstIntradayDate { get; set; }
    public DateTime? LastIntradayDate { get; set; }
    public int Sessions { get; set; }
    public long ScansEvaluated { get; set; }
    public List<string> SymbolsWithoutData { get; set; } = new();
    /// <summary>"SYMBOL dd-MMM-yyyy" for overnight gaps that look like an unadjusted split / bonus (signals around them excluded).</summary>
    public List<string> SuspectedSplits { get; set; } = new();
}
