using System;
using System.Collections.Generic;
using System.Linq;
using QuantEdge.Domain.Entities;

namespace QuantEdge.Infrastructure.Services.Backtest;

/// <summary>
/// Replays <see cref="IntradayOrbRules"/> on 15m bars: at most one breakout per stock per day, entered at the next bar's open.
/// Inside a bar the price follows the score rule's path, and a bar touching both the stop and the target counts as a stop.
/// A signal's exit is simulated here, while the day's bars are at hand, so a long run doesn't keep every stock's 15m bars in memory.
/// </summary>
public static class IntradayOrbBacktest
{
    public const int SplitExclusionSessions = 30;

    public sealed class Context
    {
        public DateTime FromDate { get; init; }
        public DateTime ToDate { get; init; }
        public bool TestBuy { get; init; } = true;
        public bool TestShort { get; init; }
        public decimal SlippagePct { get; init; }
        public Func<DateTime, int> SessionOf { get; init; } = _ => 0;
        public Func<DateTime, string?> RegimeFor { get; init; } = _ => null;
        /// <summary>Was the stock in the most-traded list at the previous session's close? Today's turnover isn't known yet.</summary>
        public Func<string, DateTime, bool> InUniverse { get; init; } = (_, _) => true;
        /// <summary>NIFTY's previous daily close before this day; null if unknown.</summary>
        public Func<DateTime, decimal?> NiftyPrevClose { get; init; } = _ => null;
        /// <summary>NIFTY's latest 15m close at the close of the bar starting at this time; null if unknown.</summary>
        public Func<DateTime, decimal?> NiftyAtBar { get; init; } = _ => null;
    }

    public static List<BacktestSignal> GenerateSignals(string symbol, IReadOnlyList<MarketCandle> m15, IReadOnlyList<MarketCandle> daily, HashSet<int> splitSessions, Context ctx)
    {
        var signals = new List<BacktestSignal>();
        var dailyDays = daily.Select(b => BacktestEngine.IstDate(b.CandleTime)).ToList();
        var dailyAverage = IndicatorCalculator.CalculateSma(daily.Select(b => b.Close).ToList(), IntradayOrbRules.StockAverageDays);
        var pastRangeVolumes = new List<long>();

        foreach (var dayBars in m15.GroupBy(b => BacktestEngine.IstDate(b.CandleTime)).OrderBy(g => g.Key))
        {
            DateTime day = dayBars.Key;
            var bars = dayBars.OrderBy(b => b.CandleTime).ToList();
            var range = IntradayOrbRules.Range(bars);
            if (range == null) continue;

            if (day >= ctx.FromDate.Date && day <= ctx.ToDate.Date && ctx.InUniverse(symbol, day)
                && SignalOfDay(symbol, day, bars, range, pastRangeVolumes, dailyDays, daily, dailyAverage, splitSessions, ctx) is BacktestSignal signal)
            {
                if (signal.SkipReason == null) signal.Exit = SimulateExit(bars, signal);
                signals.Add(signal);
            }
            pastRangeVolumes.Add(range.Volume);
        }
        return signals;
    }

    private static BacktestSignal? SignalOfDay(string symbol, DateTime day, List<MarketCandle> bars, IntradayOrbRules.OpeningRange range, List<long> pastVolumes,
        List<DateTime> dailyDays, IReadOnlyList<MarketCandle> daily, List<decimal> dailyAverage, HashSet<int> splitSessions, Context ctx)
    {
        if (pastVolumes.Count < IntradayOrbRules.VolumeLookback) return null;
        decimal usual = (decimal)pastVolumes.Skip(pastVolumes.Count - IntradayOrbRules.VolumeLookback).Average();
        decimal volumeMultiple = usual > 0m ? range.Volume / usual : 0m;
        if (volumeMultiple < IntradayOrbRules.MinVolumeMultiple) return null;

        int prevDaily = dailyDays.BinarySearch(day);
        prevDaily = (prevDaily >= 0 ? prevDaily : ~prevDaily) - 1;
        if (prevDaily < IntradayOrbRules.StockAverageDays - 1) return null;

        for (int k = 0; k < bars.Count - 1; k++)
        {
            var bar = bars[k];
            TimeSpan start = BacktestEngine.ToIst(bar.CandleTime).TimeOfDay;
            if (start < IntradayOrbRules.RangeEnd) continue;
            if (start + TimeSpan.FromMinutes(15) > IntradayOrbRules.LastSignal) return null;

            int direction = IntradayOrbRules.Breakout(bar.Close, range);
            if (direction == 0) continue;

            // Only the day's first close beyond the range counts, so Buy, Short and Both test the same breakouts.
            bool isShort = direction < 0;
            if (isShort ? !ctx.TestShort : !ctx.TestBuy) return null;
            if (ctx.NiftyPrevClose(day) is not decimal niftyPrev || ctx.NiftyAtBar(bar.CandleTime) is not decimal niftyNow) return null;
            if (!IntradayOrbRules.DirectionAllowed(isShort, niftyNow, niftyPrev, daily[prevDaily].Close, dailyAverage[prevDaily])) return null;

            var next = bars[k + 1];
            decimal entryRaw = Math.Round(next.Open, 2);
            decimal slip = ctx.SlippagePct / 100m;
            int session = ctx.SessionOf(day);
            var levels = IntradayOrbRules.Levels(isShort, entryRaw, range);
            string? skip = levels == null ? "stop too close or too far (opening range rule)"
                : splitSessions.Any(s => session >= s && session - s < SplitExclusionSessions) ? "suspected split/bonus in the indicator history"
                : null;
            return new BacktestSignal
            {
                Symbol = symbol,
                IsShort = isShort,
                SignalTimeUtc = bar.CandleTime.AddMinutes(15),
                EntryTimeUtc = next.CandleTime,
                SessionDate = day,
                Score = (int)Math.Min(100m, Math.Floor(volumeMultiple * 10m)),
                HardFiltersPassed = true,
                IsLiveCandidate = true,
                GateAllows = true,
                Regime = ctx.RegimeFor(day),
                PolicyMaxPositions = int.MaxValue,
                SubType = isShort ? "ORB_SHORT" : "ORB_LONG",
                FactorsText = $"RANGE:{Math.Round(range.Low, 2)}-{Math.Round(range.High, 2)},VOLX:{volumeMultiple:0.##}",
                VolumeMultiple = Math.Round(volumeMultiple, 2),
                SignalPrice = Math.Round(bar.Close, 2),
                EntryRaw = entryRaw,
                EntryFill = Math.Round(entryRaw * (isShort ? 1m - slip : 1m + slip), 2),
                StopLoss = levels?.Stop ?? 0m,
                Target = levels?.Target ?? 0m,
                EntryIndex = k + 1,
                SkipReason = skip
            };
        }
        return null;
    }

    /// <summary><paramref name="dayBars"/> are the signal day's bars in time order; <see cref="BacktestSignal.EntryIndex"/> is an index into them.</summary>
    public static BacktestExit SimulateExit(IReadOnlyList<MarketCandle> dayBars, BacktestSignal s)
    {
        bool isShort = s.IsShort;
        decimal hi = s.EntryFill, lo = s.EntryFill;
        decimal Gain(decimal price) => Math.Round((isShort ? s.EntryFill - price : price - s.EntryFill) / s.EntryFill * 100m, 2);
        BacktestExit Done(DateTime time, decimal price, string reason, string category) =>
            new(time, s.SessionDate, price, reason, category, 0, Gain(isShort ? lo : hi), Gain(isShort ? hi : lo), false);
        Span<decimal> path = stackalloc decimal[4];

        for (int j = s.EntryIndex; j < dayBars.Count; j++)
        {
            var bar = dayBars[j];
            decimal o = Math.Round(bar.Open, 2), h = Math.Round(bar.High, 2), l = Math.Round(bar.Low, 2), c = Math.Round(bar.Close, 2);
            if (BacktestEngine.ToIst(bar.CandleTime).TimeOfDay >= IntradayOrbRules.SquareOff)
            {
                hi = Math.Max(hi, o); lo = Math.Min(lo, o);
                return Done(bar.CandleTime, o, SwingTradeRules.ShortSquareOffReason, BacktestEngine.Category(SwingTradeRules.ShortSquareOffReason));
            }

            // Same path as the score rule: a bar reaching the target visits the stop side first, so touching both counts as a stop.
            path[0] = o;
            bool stopSideFirst = isShort ? c < o || l <= s.Target : c >= o || h >= s.Target;
            bool highFirst = isShort == stopSideFirst;
            path[1] = highFirst ? h : l;
            path[2] = highFirst ? l : h;
            path[3] = c;

            decimal prev = o;
            for (int q = 0; q < 4; q++)
            {
                decimal price = path[q];
                hi = Math.Max(hi, price); lo = Math.Min(lo, price);
                if (isShort ? price >= s.StopLoss : price <= s.StopLoss)
                {
                    bool crossed = isShort ? prev < s.StopLoss : prev > s.StopLoss;
                    // Stamped at the bar's close: an entry at this bar's open came before the exit, so it can't use the freed slot.
                    return Done(bar.CandleTime.AddMinutes(15), crossed ? s.StopLoss : price, $"Stop Loss Hit @ ₹{s.StopLoss:F2}", "Stop loss");
                }
                if (isShort ? price <= s.Target : price >= s.Target)
                {
                    bool crossed = isShort ? prev > s.Target : prev < s.Target;
                    return Done(bar.CandleTime.AddMinutes(15), crossed ? s.Target : price, "Target Hit", "Target");
                }
                prev = price;
            }
        }

        var last = dayBars[^1];
        return Done(last.CandleTime.AddMinutes(15), Math.Round(last.Close, 2), BacktestEngine.MissingBarSquareOffReason,
            BacktestEngine.Category(BacktestEngine.MissingBarSquareOffReason));
    }
}
