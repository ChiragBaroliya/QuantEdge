namespace QuantEdge.Domain.Entities;

/// <summary>
/// Every number the 11 swing conditions use, editable on the Trade Conditions page ("Rule numbers") and stored as JSON in
/// swing_strategy_settings.rule_parameters. The defaults are the engines' original values, so a database with no saved
/// rules trades exactly as before. One set applies to Auto Paper, Auto Real, the dashboards and backtests alike; BUY and
/// SHORT share it, except where a value has its own SHORT mirror. Limits and labels: SwingRuleCatalog.
/// </summary>
public class SwingRuleParameters
{
    // 1. NIFTY market filter (daily)
    public int NiftySmaPeriod { get; set; } = 50;
    public int NiftyEmaFast { get; set; } = 20;
    public int NiftyEmaSlow { get; set; } = 50;

    // 2. EMA trend alignment (daily)
    public int TrendEmaFast { get; set; } = 20;
    public int TrendEmaSlow { get; set; } = 50;
    public int TrendEmaLong { get; set; } = 200;
    /// <summary>Fast and slow EMA must be higher (SHORT: lower) than this many daily bars ago.</summary>
    public int TrendSlopeBars { get; set; } = 2;
    public int LongEmaSlopeBars { get; set; } = 5;
    /// <summary>How far the long EMA may move against the trade over <see cref="LongEmaSlopeBars"/>, %.</summary>
    public decimal LongEmaTolerancePct { get; set; } = 0.5m;

    // 3. ADX trend strength (daily)
    public int AdxPeriod { get; set; } = 14;
    public decimal AdxMin { get; set; } = 20m;

    // 4. Breakout / breakdown
    public int BreakoutPoints { get; set; } = 20;
    public int SwingLookbackBars { get; set; } = 15;
    public int ConsolidationBars { get; set; } = 14;
    public decimal ConsolidationMaxRangePct { get; set; } = 10m;
    public decimal Near52WeekPct { get; set; } = 10m;

    // 5. Volume confirmation
    public int VolumePoints { get; set; } = 15;
    public int VolumeGoodPoints { get; set; } = 10;
    public decimal VolumeGoodMultiple { get; set; } = 1.5m;
    public decimal VolumeHeavyMultiple { get; set; } = 2.5m;
    public int VolumeAverageBars { get; set; } = 20;
    /// <summary>SHORT's own mirror of VolumePoints/VolumeGoodPoints - defaults match them exactly, so an
    /// existing saved configuration scores SHORT volume confirmation identically until changed. The volume
    /// multiples/lookback above stay shared (BUY and SHORT use the same "how much is a volume spike" test).</summary>
    public int ShortVolumePoints { get; set; } = 15;
    public int ShortVolumeGoodPoints { get; set; } = 10;

    // 6. Relative strength / weakness vs NIFTY
    public int RelativeStrengthPoints { get; set; } = 15;
    public int RelativeStrengthBars { get; set; } = 20;

    // 7. 60m confirmation
    public int Mtf60mPoints { get; set; } = 15;
    public int Mtf60mEmaPeriod { get; set; } = 20;
    public int Mtf60mRsiPeriod { get; set; } = 14;
    public decimal Mtf60mRsiMinBuy { get; set; } = 40m;
    public decimal Mtf60mRsiMaxShort { get; set; } = 60m;

    // 8. RSI zone
    public int RsiPoints { get; set; } = 10;
    public int RsiGoodPoints { get; set; } = 7;
    public int RsiPeriod { get; set; } = 14;
    public decimal RsiBuyBestLow { get; set; } = 55m;
    public decimal RsiBuyBestHigh { get; set; } = 70m;
    public decimal RsiBuyOkLow { get; set; } = 50m;
    public decimal RsiBuyOkHigh { get; set; } = 75m;
    public decimal RsiShortBestLow { get; set; } = 30m;
    public decimal RsiShortBestHigh { get; set; } = 45m;
    public decimal RsiShortOkLow { get; set; } = 25m;
    public decimal RsiShortOkHigh { get; set; } = 50m;

    // 9. MACD
    public int MacdPoints { get; set; } = 10;
    public int MacdFast { get; set; } = 12;
    public int MacdSlow { get; set; } = 26;
    public int MacdSignal { get; set; } = 9;

    // 10. Candle
    public int CandlePoints { get; set; } = 8;
    /// <summary>The close must be inside this top (SHORT: bottom) share of the candle's range, %.</summary>
    public decimal CandleCloseZonePct { get; set; } = 25m;
    public decimal CandleBodyAtrMultiple { get; set; } = 1.2m;

    // 11. Risk : reward
    public int RiskRewardPoints { get; set; } = 7;
    public int AtrPeriod { get; set; } = 14;
    public decimal StopAtrMultiple { get; set; } = 1.5m;
    public decimal Target1R { get; set; } = 2m;
    public decimal Target2R { get; set; } = 3m;
    public decimal MinRiskReward { get; set; } = 2m;

    /// <summary>Daily candles a stock needs before it is judged at all (never below the original 50).</summary>
    public int MinDailyCandles => Math.Max(50, Math.Max(Math.Max(TrendEmaFast, TrendEmaSlow), AdxPeriod * 2));

    /// <summary>NIFTY daily candles the market filter needs; fewer fails it (never below the original 50).</summary>
    public int MinNiftyCandles => Math.Max(50, Math.Max(NiftySmaPeriod, Math.Max(NiftyEmaFast, NiftyEmaSlow)));

    /// <summary>Daily bars before the long EMA is trusted (its period + warm-up); a newer listing skips that check.</summary>
    public int MinBarsForLongEma => TrendEmaLong + 20;

    /// <summary>The long EMA may be this fraction of its old value lower (BUY) or higher (SHORT).</summary>
    public decimal LongEmaToleranceFactor => LongEmaTolerancePct / 100m;

    public int TotalPoints => BreakoutPoints + VolumePoints + RelativeStrengthPoints + Mtf60mPoints + RsiPoints + MacdPoints + CandlePoints + RiskRewardPoints;
}
