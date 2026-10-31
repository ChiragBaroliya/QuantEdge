using System;
using System.Collections.Generic;

namespace QuantEdge.Infrastructure.Services.Backtest;

/// <summary>Which side of the strategy a run tests. Runs stored before shorts could be tested have no side and mean BUY.</summary>
public static class BacktestSide
{
    public const string Buy = "BUY";
    public const string Short = "SHORT";
    public const string Both = "BOTH";

    public static bool IncludesBuy(string? side) => side != Short;
    public static bool IncludesShort(string? side) => side == Short || side == Both;
    public static string Of(bool isShort) => isShort ? Short : Buy;
}

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
    /// <summary>BUY, SHORT or BOTH (<see cref="BacktestSide"/>). Blank = BUY.</summary>
    public string? Side { get; set; }

    // Strategy (swing_strategy_settings)
    /// <summary>NIFTY_FILTER or REGIME.</summary>
    public string? GateMode { get; set; }
    public int? BuyScoreThreshold { get; set; }
    public int? WatchScoreThreshold { get; set; }
    /// <summary>NIFTY_FILTER mode: the bot also buys when at least this many of the 11 conditions are met (live rule).</summary>
    public int? MinConditionsMatch { get; set; }
    /// <summary>The rule numbers this run used (blank = the live ones when queued; older runs have none and use the live ones).</summary>
    public QuantEdge.Domain.Entities.SwingRuleParameters? Rules { get; set; }

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
    /// <summary>No new short at/after this IST time.</summary>
    public string? ShortEntryCutoff { get; set; }
    /// <summary>Open shorts are bought back at the first 15m bar starting at/after this IST time.</summary>
    public string? ShortSquareOffTime { get; set; }
    // Shorts are intraday-only, so they get their own - much smaller - ATR multiples than StopLossAtrMult/
    // TrailAtrMult/TargetAtrMult above, which size the multi-day swing LONG.
    public decimal? ShortStopLossAtrMult { get; set; }
    public decimal? ShortTrailAtrMult { get; set; }
    public decimal? ShortTargetAtrMult { get; set; }
    // 0/null = disabled (default entry + 1x ATR activation). LONG only - see SwingTradeRules.GetTrailActivationPrice.
    public decimal? TrailActivationPct { get; set; }
}

/// <summary>A 15-minute scan that the strategy would have acted on (or that the threshold sweep needs), with its simulated trade.</summary>
public sealed class BacktestSignal
{
    public string Symbol { get; init; } = string.Empty;
    public bool IsShort { get; init; }
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
    /// <summary>The regime's limit on capital in open positions, %; null outside REGIME mode.</summary>
    public decimal? PolicyMaxExposurePct { get; init; }
    /// <summary>Points per scoring factor, 6 bits each in <see cref="BacktestEngine.FactorCodes"/> order (kept small: a run can hold 100k+ signals).</summary>
    public long FactorPoints { get; init; }
    /// <summary>Which BREAKOUT_GROUP/BREAKDOWN_GROUP sub-condition(s) fired - "NONE" otherwise (<see cref="SwingEvaluationResult.BreakoutSubType"/>/<see cref="SwingEvaluationResult.BreakdownSubType"/>).</summary>
    public string SubType { get; init; } = "NONE";
    // Indicator snapshot at the scan that produced this signal, for rule-level analysis of winners vs losers.
    public decimal Rsi15m { get; init; }
    public decimal? Rsi60m { get; init; }
    public decimal VolumeMultiple { get; init; }
    public decimal Adx1d { get; init; }
    public decimal SignalDailyAtr { get; init; }

    public decimal SignalPrice { get; init; }
    public decimal EntryRaw { get; init; }
    /// <summary>EntryRaw adjusted for entry-side slippage - the real simulated cost basis. The exit-decision engine
    /// (breakeven floor/ceiling, ATR inference, emergency stop, trail activation) must use this, not EntryRaw, for
    /// anything measured "from entry" - stop/target levels stay anchored to EntryRaw (see BacktestEngine.SimulateExit).</summary>
    public decimal EntryFill { get; init; }
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
    /// <summary>BUY or SHORT. For a short, the entry is the sell and the exit is the buy-back.</summary>
    public string Side { get; set; } = BacktestSide.Buy;
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
    public string SubType { get; set; } = "NONE";
    public decimal Rsi15m { get; set; }
    public decimal? Rsi60m { get; set; }
    public decimal VolumeMultiple { get; set; }
    public decimal Adx1d { get; set; }
    public decimal SignalDailyAtr { get; set; }
    public bool EndOfData { get; set; }
    /// <summary>Prices before slippage, for <see cref="BacktestLogicAudit"/> during the run; not stored.</summary>
    public decimal EntryRaw { get; set; }
    public decimal ExitRaw { get; set; }
    public bool IsShort => Side == BacktestSide.Short;
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
    public string Side { get; set; } = BacktestSide.Buy;
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
    public string Side { get; set; } = BacktestSide.Buy;
    public string Code { get; set; } = string.Empty;
    public int TradesWith { get; set; }
    public decimal ExpectancyWithR { get; set; }
    public decimal WinRateWithPct { get; set; }
    public int TradesWithout { get; set; }
    public decimal ExpectancyWithoutR { get; set; }
    public decimal WinRateWithoutPct { get; set; }
}

public sealed record BacktestEquityPoint(DateTime Date, decimal Equity, decimal DrawdownPct);

/// <summary>Did the price go the way the signal said? Before costs, and independent of the stop, target and portfolio limits.</summary>
public sealed class BacktestDirectionRow
{
    public string Side { get; set; } = BacktestSide.Buy;
    public string Horizon { get; set; } = string.Empty;
    public int Signals { get; set; }
    /// <summary>Share of signals where the price moved the signal's way (up for BUY, down for SHORT).</summary>
    public decimal RightPct { get; set; }
    /// <summary>Average move from the entry, positive = the signal's way.</summary>
    public decimal AvgMovePct { get; set; }
}

/// <summary>One rule the simulated trades must obey, and how many broke it.</summary>
public sealed class BacktestLogicCheck
{
    public string Side { get; set; } = BacktestSide.Buy;
    public string Rule { get; set; } = string.Empty;
    public int Checked { get; set; }
    public int Failed { get; set; }
    /// <summary>The first few trades that broke the rule.</summary>
    public List<string> Examples { get; set; } = new();
}

/// <summary>One side's own result, so BUY and SHORT each get an answer even when a run tests both.</summary>
public sealed class BacktestSideResult
{
    public string Side { get; set; } = BacktestSide.Buy;
    public string Verdict { get; set; } = "NOT_PROVEN";
    public string VerdictReason { get; set; } = string.Empty;
    public int Signals { get; set; }
    public BacktestKpis Kpis { get; set; } = new();
    public List<BacktestGroupRow> ByHalf { get; set; } = new();
    public List<BacktestGroupRow> ByExit { get; set; } = new();
}

public sealed class BacktestSummary
{
    public List<BacktestSideResult> Sides { get; set; } = new();
    public List<BacktestDirectionRow> Direction { get; set; } = new();
    public List<BacktestLogicCheck> LogicChecks { get; set; } = new();
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
