using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using QuantEdge.Domain.Entities;

namespace QuantEdge.Infrastructure.Services;

/// <summary>One editable number: its limits and labels. The Rule numbers page is drawn from this list.</summary>
public sealed record SwingRuleField(string Key, string Group, string Label, string Help, string Unit, decimal Min, decimal Max, decimal Step, bool IsInteger,
    [property: System.Text.Json.Serialization.JsonIgnore] Func<SwingRuleParameters, decimal> Read);

/// <summary>
/// The limits and labels of <see cref="SwingRuleParameters"/>, and the check every save (and every load) goes through.
/// The limits keep each value usable by the live data: 100 intraday and 300 daily candles are loaded per scan, and a
/// backtest stores each factor's points in 6 bits (so at most 63).
/// </summary>
public static class SwingRuleCatalog
{
    public const int RequiredTotalPoints = 100;

    private static SwingRuleField Int(string name, string group, string label, string help, int min, int max, string unit, Func<SwingRuleParameters, int> read) =>
        new(JsonNamingPolicy.CamelCase.ConvertName(name), group, label, help, unit, min, max, 1m, true, r => read(r));

    private static SwingRuleField Num(string name, string group, string label, string help, decimal min, decimal max, decimal step, string unit, Func<SwingRuleParameters, decimal> read) =>
        new(JsonNamingPolicy.CamelCase.ConvertName(name), group, label, help, unit, min, max, step, false, read);

    public static readonly IReadOnlyList<SwingRuleField> Fields = new[]
    {
        Int(nameof(SwingRuleParameters.NiftySmaPeriod), TradeConditionRules.Market, "NIFTY average period", "NIFTY's close must be above this simple average (SHORT: below).", 5, 200, "days", r => r.NiftySmaPeriod),
        Int(nameof(SwingRuleParameters.NiftyEmaFast), TradeConditionRules.Market, "NIFTY fast EMA", "Must be above the slow EMA (SHORT: below).", 2, 100, "days", r => r.NiftyEmaFast),
        Int(nameof(SwingRuleParameters.NiftyEmaSlow), TradeConditionRules.Market, "NIFTY slow EMA", "", 5, 200, "days", r => r.NiftyEmaSlow),

        Int(nameof(SwingRuleParameters.TrendEmaFast), TradeConditionRules.Trend, "Fast EMA", "Close > fast EMA > slow EMA (SHORT: the reverse).", 2, 100, "days", r => r.TrendEmaFast),
        Int(nameof(SwingRuleParameters.TrendEmaSlow), TradeConditionRules.Trend, "Slow EMA", "", 5, 200, "days", r => r.TrendEmaSlow),
        Int(nameof(SwingRuleParameters.TrendEmaLong), TradeConditionRules.Trend, "Long EMA", "Must not be moving against the trade.", 50, 250, "days", r => r.TrendEmaLong),
        Int(nameof(SwingRuleParameters.TrendSlopeBars), TradeConditionRules.Trend, "Rising / falling over", "Fast and slow EMA compared with this many days ago.", 1, 20, "days", r => r.TrendSlopeBars),
        Int(nameof(SwingRuleParameters.LongEmaSlopeBars), TradeConditionRules.Trend, "Long EMA checked over", "", 1, 30, "days", r => r.LongEmaSlopeBars),
        Num(nameof(SwingRuleParameters.LongEmaTolerancePct), TradeConditionRules.Trend, "Long EMA tolerance", "How far it may move against the trade over that time.", 0m, 5m, 0.1m, "%", r => r.LongEmaTolerancePct),

        Int(nameof(SwingRuleParameters.AdxPeriod), TradeConditionRules.Adx, "ADX period", "", 5, 50, "days", r => r.AdxPeriod),
        Num(nameof(SwingRuleParameters.AdxMin), TradeConditionRules.Adx, "Minimum ADX", "Lower lets choppier stocks through.", 0m, 60m, 1m, "", r => r.AdxMin),

        Int(nameof(SwingRuleParameters.BreakoutPoints), TradeConditionRules.Breakout, "Points", "", 1, 60, "pts", r => r.BreakoutPoints),
        Int(nameof(SwingRuleParameters.SwingLookbackBars), TradeConditionRules.Breakout, "Swing high / low lookback", "", 2, 60, "bars", r => r.SwingLookbackBars),
        Int(nameof(SwingRuleParameters.ConsolidationBars), TradeConditionRules.Breakout, "Range length", "Daily bars that make up the consolidation range.", 5, 60, "days", r => r.ConsolidationBars),
        Num(nameof(SwingRuleParameters.ConsolidationMaxRangePct), TradeConditionRules.Breakout, "Range at most", "Height of that range.", 1m, 50m, 0.5m, "%", r => r.ConsolidationMaxRangePct),
        Num(nameof(SwingRuleParameters.Near52WeekPct), TradeConditionRules.Breakout, "Near 52-week high / low", "Within this distance counts as a breakout.", 0m, 50m, 0.5m, "%", r => r.Near52WeekPct),

        Int(nameof(SwingRuleParameters.VolumePoints), TradeConditionRules.Volume, "Points: heavy volume", "", 1, 60, "pts", r => r.VolumePoints),
        Int(nameof(SwingRuleParameters.VolumeGoodPoints), TradeConditionRules.Volume, "Points: good volume", "", 1, 60, "pts", r => r.VolumeGoodPoints),
        Num(nameof(SwingRuleParameters.VolumeHeavyMultiple), TradeConditionRules.Volume, "Heavy volume at least", "Times the usual volume, and above the previous bar.", 0.1m, 20m, 0.1m, "×", r => r.VolumeHeavyMultiple),
        Num(nameof(SwingRuleParameters.VolumeGoodMultiple), TradeConditionRules.Volume, "Good volume at least", "Times the usual volume.", 0.1m, 10m, 0.1m, "×", r => r.VolumeGoodMultiple),
        Int(nameof(SwingRuleParameters.VolumeAverageBars), TradeConditionRules.Volume, "Fallback average", "Used only when a time slot has too little history.", 5, 60, "bars", r => r.VolumeAverageBars),

        Int(nameof(SwingRuleParameters.RelativeStrengthPoints), TradeConditionRules.RelativeStrength, "Points", "", 1, 60, "pts", r => r.RelativeStrengthPoints),
        Int(nameof(SwingRuleParameters.RelativeStrengthBars), TradeConditionRules.RelativeStrength, "Compare returns over", "20 trading days is about 1 month.", 5, 120, "days", r => r.RelativeStrengthBars),

        Int(nameof(SwingRuleParameters.Mtf60mPoints), TradeConditionRules.Mtf60m, "Points", "", 1, 60, "pts", r => r.Mtf60mPoints),
        Int(nameof(SwingRuleParameters.Mtf60mEmaPeriod), TradeConditionRules.Mtf60m, "60m EMA", "", 5, 50, "bars", r => r.Mtf60mEmaPeriod),
        Int(nameof(SwingRuleParameters.Mtf60mRsiPeriod), TradeConditionRules.Mtf60m, "60m RSI period", "", 2, 30, "bars", r => r.Mtf60mRsiPeriod),
        Num(nameof(SwingRuleParameters.Mtf60mRsiMinBuy), TradeConditionRules.Mtf60m, "BUY: 60m RSI at least", "", 0m, 100m, 1m, "", r => r.Mtf60mRsiMinBuy),
        Num(nameof(SwingRuleParameters.Mtf60mRsiMaxShort), TradeConditionRules.Mtf60m, "SHORT: 60m RSI at most", "", 0m, 100m, 1m, "", r => r.Mtf60mRsiMaxShort),

        Int(nameof(SwingRuleParameters.RsiPoints), TradeConditionRules.Rsi, "Points: best zone", "", 1, 60, "pts", r => r.RsiPoints),
        Int(nameof(SwingRuleParameters.RsiGoodPoints), TradeConditionRules.Rsi, "Points: acceptable zone", "", 1, 60, "pts", r => r.RsiGoodPoints),
        Int(nameof(SwingRuleParameters.RsiPeriod), TradeConditionRules.Rsi, "RSI period", "", 2, 50, "bars", r => r.RsiPeriod),
        Num(nameof(SwingRuleParameters.RsiBuyBestLow), TradeConditionRules.Rsi, "BUY best zone from", "", 0m, 100m, 1m, "", r => r.RsiBuyBestLow),
        Num(nameof(SwingRuleParameters.RsiBuyBestHigh), TradeConditionRules.Rsi, "BUY best zone to", "", 0m, 100m, 1m, "", r => r.RsiBuyBestHigh),
        Num(nameof(SwingRuleParameters.RsiBuyOkLow), TradeConditionRules.Rsi, "BUY acceptable from", "", 0m, 100m, 1m, "", r => r.RsiBuyOkLow),
        Num(nameof(SwingRuleParameters.RsiBuyOkHigh), TradeConditionRules.Rsi, "BUY acceptable to", "", 0m, 100m, 1m, "", r => r.RsiBuyOkHigh),
        Num(nameof(SwingRuleParameters.RsiShortBestLow), TradeConditionRules.Rsi, "SHORT best zone from", "", 0m, 100m, 1m, "", r => r.RsiShortBestLow),
        Num(nameof(SwingRuleParameters.RsiShortBestHigh), TradeConditionRules.Rsi, "SHORT best zone to", "", 0m, 100m, 1m, "", r => r.RsiShortBestHigh),
        Num(nameof(SwingRuleParameters.RsiShortOkLow), TradeConditionRules.Rsi, "SHORT acceptable from", "", 0m, 100m, 1m, "", r => r.RsiShortOkLow),
        Num(nameof(SwingRuleParameters.RsiShortOkHigh), TradeConditionRules.Rsi, "SHORT acceptable to", "", 0m, 100m, 1m, "", r => r.RsiShortOkHigh),

        Int(nameof(SwingRuleParameters.MacdPoints), TradeConditionRules.Macd, "Points", "", 1, 60, "pts", r => r.MacdPoints),
        Int(nameof(SwingRuleParameters.MacdFast), TradeConditionRules.Macd, "Fast EMA", "", 2, 50, "bars", r => r.MacdFast),
        Int(nameof(SwingRuleParameters.MacdSlow), TradeConditionRules.Macd, "Slow EMA", "", 5, 60, "bars", r => r.MacdSlow),
        Int(nameof(SwingRuleParameters.MacdSignal), TradeConditionRules.Macd, "Signal EMA", "", 2, 30, "bars", r => r.MacdSignal),

        Int(nameof(SwingRuleParameters.CandlePoints), TradeConditionRules.Candle, "Points", "", 1, 60, "pts", r => r.CandlePoints),
        Num(nameof(SwingRuleParameters.CandleCloseZonePct), TradeConditionRules.Candle, "Close within the top / bottom", "Share of the candle's range.", 1m, 50m, 1m, "%", r => r.CandleCloseZonePct),
        Num(nameof(SwingRuleParameters.CandleBodyAtrMultiple), TradeConditionRules.Candle, "Or body at least", "Times ATR.", 0.1m, 5m, 0.1m, "× ATR", r => r.CandleBodyAtrMultiple),

        Int(nameof(SwingRuleParameters.RiskRewardPoints), TradeConditionRules.RiskReward, "Points", "", 1, 60, "pts", r => r.RiskRewardPoints),
        Int(nameof(SwingRuleParameters.AtrPeriod), TradeConditionRules.RiskReward, "ATR period", "", 2, 50, "bars", r => r.AtrPeriod),
        Num(nameof(SwingRuleParameters.StopAtrMultiple), TradeConditionRules.RiskReward, "Signal stop", "Used for this signal's R:R. The open trade's stop comes from each bot's own settings.", 0.25m, 10m, 0.25m, "× ATR", r => r.StopAtrMultiple),
        Num(nameof(SwingRuleParameters.Target1R), TradeConditionRules.RiskReward, "Target 1", "", 0.5m, 20m, 0.5m, "R", r => r.Target1R),
        Num(nameof(SwingRuleParameters.Target2R), TradeConditionRules.RiskReward, "Target 2", "", 0.5m, 30m, 0.5m, "R", r => r.Target2R),
        Num(nameof(SwingRuleParameters.MinRiskReward), TradeConditionRules.RiskReward, "Minimum R:R", "Cannot be above Target 1, or the condition could never pass.", 0.5m, 20m, 0.5m, "R", r => r.MinRiskReward),
    };

    /// <summary>Everything wrong with these numbers, in plain words; empty when they can be used.</summary>
    public static List<string> Validate(SwingRuleParameters? r)
    {
        var errors = new List<string>();
        if (r == null)
        {
            errors.Add("Rule numbers are required.");
            return errors;
        }

        foreach (var f in Fields)
        {
            decimal value = f.Read(r);
            if (value < f.Min || value > f.Max) errors.Add($"{GroupTitle(f.Group)} - {f.Label}: must be between {f.Min:0.##} and {f.Max:0.##} (got {value:0.##}).");
        }

        void Require(bool ok, string message) { if (!ok) errors.Add(message); }

        Require(r.TotalPoints == RequiredTotalPoints, $"Scoring points must total {RequiredTotalPoints} (now {r.TotalPoints}), so the BUY and WATCH thresholds keep their meaning.");
        Require(r.NiftyEmaFast < r.NiftyEmaSlow, "NIFTY Market Filter: the fast EMA must be shorter than the slow EMA.");
        Require(r.TrendEmaFast < r.TrendEmaSlow, "EMA Trend: the fast EMA must be shorter than the slow EMA.");
        Require(r.TrendEmaSlow < r.TrendEmaLong, "EMA Trend: the slow EMA must be shorter than the long EMA.");
        Require(r.MacdFast < r.MacdSlow, "MACD: the fast EMA must be shorter than the slow EMA.");
        Require(r.VolumeGoodPoints <= r.VolumePoints, "Volume: good-volume points cannot be more than heavy-volume points.");
        Require(r.VolumeGoodMultiple <= r.VolumeHeavyMultiple, "Volume: the good-volume multiple cannot be above the heavy-volume multiple.");
        Require(r.RsiGoodPoints <= r.RsiPoints, "RSI: acceptable-zone points cannot be more than best-zone points.");
        Require(r.RsiBuyOkLow <= r.RsiBuyBestLow && r.RsiBuyBestLow < r.RsiBuyBestHigh && r.RsiBuyBestHigh <= r.RsiBuyOkHigh, "RSI: the BUY best zone must sit inside the BUY acceptable zone.");
        Require(r.RsiShortOkLow <= r.RsiShortBestLow && r.RsiShortBestLow < r.RsiShortBestHigh && r.RsiShortBestHigh <= r.RsiShortOkHigh, "RSI: the SHORT best zone must sit inside the SHORT acceptable zone.");
        Require(r.Target1R < r.Target2R, "Risk : Reward: Target 1 must be closer than Target 2.");
        Require(r.MinRiskReward <= r.Target1R, "Risk : Reward: the minimum R:R cannot be above Target 1, or this condition could never pass.");
        return errors;
    }

    public static string GroupTitle(string group) => TradeConditionRules.All.FirstOrDefault(c => c.Key == group)?.BuyName ?? group;
}
