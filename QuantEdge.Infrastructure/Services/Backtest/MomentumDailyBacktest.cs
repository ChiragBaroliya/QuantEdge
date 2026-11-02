using System;
using System.Collections.Generic;
using System.Linq;
using QuantEdge.Domain.Entities;

namespace QuantEdge.Infrastructure.Services.Backtest;

/// <summary>
/// Replays <see cref="MomentumDailyRules"/> on daily bars. A signal uses bars up to that day's close and enters at the next
/// session's open. Close-based exits fill at the next open too; only the emergency stop acts inside a day. The result is the
/// same <see cref="BacktestSignal"/> / <see cref="BacktestExit"/> as the score rule, so the portfolio, charges and report are shared.
/// </summary>
public static class MomentumDailyBacktest
{
    public const int SplitExclusionSessions = MomentumDailyRules.YearHighLookback;
    public const string SubType = "MOMENTUM_DAILY";
    private const int OpenMinutes = 9 * 60 + 15, CloseMinutes = 15 * 60 + 30;

    public sealed class Context
    {
        public DateTime FromDate { get; init; }
        public DateTime ToDate { get; init; }
        public decimal SlippagePct { get; init; }
        public Func<DateTime, int> SessionOf { get; init; } = _ => 0;
        public Func<DateTime, string?> RegimeFor { get; init; } = _ => null;
        /// <summary>Was NIFTY above its 200-day average at this day's close?</summary>
        public Func<DateTime, bool> NiftyAboveAverage { get; init; } = _ => false;
        /// <summary>The stock's 6-month return rank that day among the most-traded list: 0-99, higher is stronger; null when it isn't on the list.</summary>
        public Func<string, DateTime, int?> StrengthRank { get; init; } = (_, _) => null;
    }

    /// <summary>A time <paramref name="minutes"/> after 00:00 IST on that trading day.</summary>
    private static DateTime At(DateTime day, int minutes) => BacktestEngine.DailyStamp(day).AddMinutes(minutes);

    public static decimal? SixMonthReturnPct(IReadOnlyList<MarketCandle> bars, int i)
    {
        if (i < MomentumDailyRules.ReturnLookback) return null;
        decimal then = bars[i - MomentumDailyRules.ReturnLookback].Close;
        return then > 0m ? (bars[i].Close - then) / then * 100m : null;
    }

    /// <summary>For each day, each listed stock's rank by 6-month return among that day's list: 0-99, where 80+ is the top 20%.</summary>
    public static Dictionary<DateTime, Dictionary<string, int>> StrengthRanks(IReadOnlyDictionary<string, List<MarketCandle>> daily,
        IReadOnlyDictionary<DateTime, HashSet<string>> universe)
    {
        var byDay = new Dictionary<DateTime, List<(string Symbol, decimal Return)>>();
        foreach (var (symbol, bars) in daily)
        {
            for (int i = 0; i < bars.Count; i++)
            {
                DateTime day = BacktestEngine.IstDate(bars[i].CandleTime);
                if (!universe.TryGetValue(day, out var list) || !list.Contains(symbol) || SixMonthReturnPct(bars, i) is not decimal ret) continue;
                if (!byDay.TryGetValue(day, out var rows)) byDay[day] = rows = new List<(string, decimal)>();
                rows.Add((symbol, ret));
            }
        }
        return byDay.ToDictionary(kv => kv.Key, kv =>
        {
            var sorted = kv.Value.OrderBy(x => x.Return).ThenBy(x => x.Symbol, StringComparer.Ordinal).ToList();
            return sorted.Select((x, pos) => (x.Symbol, Rank: pos * 100 / sorted.Count)).ToDictionary(x => x.Symbol, x => x.Rank);
        });
    }

    public static List<BacktestSignal> GenerateSignals(string symbol, IReadOnlyList<MarketCandle> bars, HashSet<int> splitSessions, Context ctx)
    {
        var signals = new List<BacktestSignal>();
        if (bars.Count <= MomentumDailyRules.YearHighLookback) return signals;
        var atr = IndicatorCalculator.CalculateAtr(bars.Select(b => b.High).ToList(), bars.Select(b => b.Low).ToList(),
            bars.Select(b => b.Close).ToList(), MomentumDailyRules.AtrPeriod);
        decimal slip = ctx.SlippagePct / 100m;

        for (int i = MomentumDailyRules.YearHighLookback; i < bars.Count - 1; i++)
        {
            var bar = bars[i];
            DateTime day = BacktestEngine.IstDate(bar.CandleTime);
            if (day < ctx.FromDate.Date || day > ctx.ToDate.Date) continue;
            if (ctx.StrengthRank(symbol, day) is not int rank) continue;

            decimal priorHigh = decimal.MinValue;
            for (int k = i - MomentumDailyRules.BreakoutLookback; k < i; k++) priorHigh = Math.Max(priorHigh, bars[k].High);
            decimal yearHigh = decimal.MinValue;
            for (int k = i - MomentumDailyRules.YearHighLookback + 1; k <= i; k++) yearHigh = Math.Max(yearHigh, bars[k].High);
            decimal avgVolume = 0m;
            for (int k = i - MomentumDailyRules.VolumeLookback; k < i; k++) avgVolume += bars[k].Volume;
            avgVolume /= MomentumDailyRules.VolumeLookback;
            decimal volumeMultiple = avgVolume > 0m ? bar.Volume / avgVolume : 0m;

            var facts = new MomentumDailyRules.EntryFacts(bar.Close, priorHigh, yearHigh, volumeMultiple, rank, ctx.NiftyAboveAverage(day));
            if (MomentumDailyRules.EntryBlock(facts) != null) continue;

            var next = bars[i + 1];
            DateTime entryDay = BacktestEngine.IstDate(next.CandleTime);
            decimal entryRaw = Math.Round(next.Open, 2);
            var (stop, _) = MomentumDailyRules.Levels(entryRaw, atr[i]);
            int session = ctx.SessionOf(day);
            string? skip = SwingTradeRules.CheckSignalDrift(Math.Round(bar.Close, 2), entryRaw) != null ? BacktestEngine.DriftSkipReason
                : splitSessions.Any(s => session >= s && session - s < SplitExclusionSessions) ? "suspected split/bonus in the indicator history"
                : null;

            signals.Add(new BacktestSignal
            {
                Symbol = symbol,
                SignalTimeUtc = At(day, CloseMinutes),
                EntryTimeUtc = At(entryDay, OpenMinutes),
                SessionDate = entryDay,
                Score = rank,
                HardFiltersPassed = true,
                IsLiveCandidate = true,
                GateAllows = true,
                Regime = ctx.RegimeFor(entryDay),
                PolicyMaxPositions = int.MaxValue,
                SubType = SubType,
                FactorsText = $"RET6M:{SixMonthReturnPct(bars, i):0.#},VOLX:{volumeMultiple:0.##},RANK:{rank}",
                VolumeMultiple = Math.Round(volumeMultiple, 2),
                SignalDailyAtr = Math.Round(atr[i], 4),
                SignalPrice = Math.Round(bar.Close, 2),
                EntryRaw = entryRaw,
                EntryFill = Math.Round(entryRaw * (1m + slip), 2),
                StopLoss = stop,
                Target = 0m,
                EntryIndex = i + 1,
                SkipReason = skip
            });
        }
        return signals;
    }

    public static BacktestExit? SimulateExit(IReadOnlyList<MarketCandle> bars, BacktestSignal s, HashSet<int> splitSessions, Func<DateTime, int> sessionOf, out string? skipReason)
    {
        skipReason = null;
        var (stop, emergency) = MomentumDailyRules.Levels(s.EntryRaw, s.SignalDailyAtr);
        var trade = new MomentumDailyRules.OpenTrade
        {
            EntryRaw = s.EntryRaw, EntryFill = s.EntryFill, Stop = stop, Atr = s.SignalDailyAtr, HighestClose = s.EntryRaw
        };
        int entrySession = sessionOf(s.SessionDate);
        decimal hi = s.EntryFill, lo = s.EntryFill;
        decimal Mfe() => Math.Round((hi - s.EntryFill) / s.EntryFill * 100m, 2);
        decimal Mae() => Math.Round((lo - s.EntryFill) / s.EntryFill * 100m, 2);

        for (int j = s.EntryIndex; j < bars.Count; j++)
        {
            var bar = bars[j];
            DateTime day = BacktestEngine.IstDate(bar.CandleTime);
            int session = sessionOf(day);
            if (session > entrySession && splitSessions.Contains(session))
            {
                skipReason = "trade held across a suspected split/bonus";
                return null;
            }
            int held = session - entrySession;
            decimal open = Math.Round(bar.Open, 2), low = Math.Round(bar.Low, 2), high = Math.Round(bar.High, 2), close = Math.Round(bar.Close, 2);

            if (MomentumDailyRules.EmergencyFill(open, low, emergency) is decimal fill)
            {
                lo = Math.Min(lo, fill);
                if (open > emergency) hi = Math.Max(hi, high);
                // A stop inside the day frees its slot from the next session: the day's other entries were at the open, before it happened.
                return new BacktestExit(At(day, open <= emergency ? OpenMinutes : CloseMinutes), day, fill,
                    $"Emergency Stop Hit @ ₹{emergency:F2}", "Emergency stop", held, Mfe(), Mae(), false);
            }
            hi = Math.Max(hi, high); lo = Math.Min(lo, low);

            var decision = MomentumDailyRules.AtClose(trade, close, held);
            if (decision == null) continue;
            if (j + 1 >= bars.Count) break;
            var next = bars[j + 1];
            DateTime nextDay = BacktestEngine.IstDate(next.CandleTime);
            int nextSession = sessionOf(nextDay);
            if (splitSessions.Contains(nextSession))
            {
                skipReason = "trade held across a suspected split/bonus";
                return null;
            }
            decimal nextOpen = Math.Round(next.Open, 2);
            hi = Math.Max(hi, nextOpen); lo = Math.Min(lo, nextOpen);
            return new BacktestExit(At(nextDay, OpenMinutes), nextDay, nextOpen, decision.Value.Reason, decision.Value.Category,
                nextSession - entrySession, Mfe(), Mae(), false);
        }

        var last = bars[^1];
        DateTime lastDay = BacktestEngine.IstDate(last.CandleTime);
        return new BacktestExit(At(lastDay, CloseMinutes), lastDay, Math.Round(last.Close, 2), "Still open at the end of the test (valued at the last close)",
            "End of test", sessionOf(lastDay) - entrySession, Mfe(), Mae(), true);
    }
}
