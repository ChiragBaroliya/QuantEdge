using System;
using System.Collections.Generic;
using System.Linq;
using QuantEdge.Domain.Entities;
using QuantEdge.Infrastructure.Constants;

namespace QuantEdge.Infrastructure.Services.Backtest;

/// <summary>
/// A symbol's 15-minute bars in compact form (about 44 bytes a bar), kept after signal generation so trades can be
/// simulated lazily when the portfolio considers them.
/// </summary>
public sealed class IntradayTape
{
    public string Symbol { get; }
    public long[] TimeTicks { get; }     // bar start, UTC ticks
    public double[] Open { get; }
    public double[] High { get; }
    public double[] Low { get; }
    public double[] Close { get; }
    public int[] Session { get; }        // index into the session calendar
    public HashSet<int> SplitSessions { get; }

    public IntradayTape(string symbol, IReadOnlyList<MarketCandle> bars, Func<DateTime, int> sessionOf, HashSet<int> splitSessions)
    {
        Symbol = symbol;
        int n = bars.Count;
        TimeTicks = new long[n]; Open = new double[n]; High = new double[n]; Low = new double[n]; Close = new double[n]; Session = new int[n];
        for (int i = 0; i < n; i++)
        {
            var b = bars[i];
            TimeTicks[i] = b.CandleTime.Ticks;
            Open[i] = (double)b.Open; High[i] = (double)b.High; Low[i] = (double)b.Low; Close[i] = (double)b.Close;
            Session[i] = sessionOf(BacktestEngine.IstDate(b.CandleTime));
        }
        SplitSessions = splitSessions;
    }

    public int Count => TimeTicks.Length;
    public DateTime TimeUtc(int i) => new(TimeTicks[i], DateTimeKind.Utc);
    // NSE prices have at most 2 decimals; the round trip through double is exact after rounding.
    public static decimal Price(double v) => Math.Round((decimal)v, 2);
}

/// <summary>Everything that is the same for every symbol in one run.</summary>
public sealed class BacktestContext
{
    public SwingStrategySettings EngineSettings { get; init; } = SwingStrategySettings.Default;
    public bool RegimeMode { get; init; }
    public int MinConditionsMatch { get; init; } = 10;
    /// <summary>Hard-filter-passing scans with at least this score are kept too, for the threshold sweep.</summary>
    public int SweepFloor { get; init; } = 60;
    public SwingTradeParams TradeParams { get; init; } = null!;
    public string WindowStart { get; init; } = "09:15";
    public string WindowEnd { get; init; } = "15:30";
    public int EntryDelayMinutes { get; init; } = 15;
    public int MaxDurationDays { get; init; } = 20;
    public DateTime FromDate { get; init; }
    public DateTime ToDate { get; init; }
    public IReadOnlyList<MarketCandle> NiftyDaily { get; init; } = Array.Empty<MarketCandle>();
    public IReadOnlyDictionary<DateTime, List<MarketCandle>> Nifty15mByDate { get; init; } = new Dictionary<DateTime, List<MarketCandle>>();
    /// <summary>Regime in force on a trading day = the reading of the previous session (computed the evening before).</summary>
    public Func<DateTime, (string? Regime, RegimePolicy? Policy)> RegimeFor { get; init; } = _ => (null, null);
    public Func<DateTime, int> SessionOf { get; init; } = _ => 0;
}

public sealed class SymbolBacktestResult
{
    public List<BacktestSignal> Signals { get; } = new();
    public IntradayTape? Tape { get; set; }
    public List<DateTime> SuspectedSplits { get; } = new();
    public long Scans { get; set; }
}

/// <summary>
/// Point-in-time replay of the live 15-minute scan for one symbol (Plan Phase 7). At every 15m bar close it hands
/// <see cref="SwingDecisionEngine.Evaluate"/> exactly what the live scan would have had at that moment: the last 100
/// closed 15m bars, the last 100 closed 60m bars, 299 earlier daily bars plus today's daily bar built from the 15m bars
/// so far, and NIFTY the same way. Nothing from later in the day is visible. Exits use <see cref="SwingTradeRules.EvaluateExit"/>,
/// the function the live monitors call.
/// </summary>
public static class BacktestEngine
{
    public const int SplitExclusionSessions = 200;
    private static readonly TimeSpan Ist = TimeSpan.FromMinutes(330);
    private static readonly TimeSpan Bar15 = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan Bar60 = TimeSpan.FromMinutes(60);

    public static DateTime ToIst(DateTime utc) => DateTime.SpecifyKind(utc + Ist, DateTimeKind.Unspecified);
    public static DateTime IstDate(DateTime utc) => (utc + Ist).Date;
    /// <summary>Kite's stamp for a daily bar: 00:00 IST = 18:30 UTC the day before.</summary>
    public static DateTime DailyStamp(DateTime istDate) => DateTime.SpecifyKind(istDate.Date - Ist, DateTimeKind.Utc);

    /// <summary>
    /// Overnight gaps of −35% / +60% or more: on the stocks this bot trades these are almost always a split or bonus that
    /// the stored history was never adjusted for. Indicators straddling one are meaningless, so signals in the following
    /// <see cref="SplitExclusionSessions"/> sessions and trades held across it are excluded (and listed in the report).
    /// </summary>
    public static List<DateTime> SuspectedSplits(IReadOnlyList<MarketCandle> daily)
    {
        var list = new List<DateTime>();
        for (int i = 1; i < daily.Count; i++)
        {
            decimal prev = daily[i - 1].Close;
            if (prev <= 0m) continue;
            decimal ratio = daily[i].Open / prev;
            if (ratio <= 0.65m || ratio >= 1.6m) list.Add(IstDate(daily[i].CandleTime));
        }
        return list;
    }

    public static SymbolBacktestResult GenerateSignals(string symbol, List<MarketCandle> daily, List<MarketCandle> h60, List<MarketCandle> m15,
        BacktestContext ctx)
    {
        var result = new SymbolBacktestResult();
        if (daily.Count < RealTradeSchedule.MinDailyCandles || m15.Count < 2) return result;

        result.SuspectedSplits.AddRange(SuspectedSplits(daily));
        var splitSessions = result.SuspectedSplits.Select(ctx.SessionOf).ToHashSet();
        var tape = new IntradayTape(symbol, m15, ctx.SessionOf, splitSessions);
        var stock = new StockMaster { Symbol = symbol };

        int dailyPtr = 0, h60Ptr = 0;
        int i = 0;
        while (i < m15.Count)
        {
            DateTime day = IstDate(m15[i].CandleTime);
            int dayStart = i;
            while (i < m15.Count && IstDate(m15[i].CandleTime) == day) i++;
            int dayEnd = i - 1;
            if (day < ctx.FromDate.Date || day > ctx.ToDate.Date) continue;

            while (dailyPtr < daily.Count && IstDate(daily[dailyPtr].CandleTime) < day) dailyPtr++;
            if (dailyPtr < RealTradeSchedule.MinDailyCandles - 1) continue;
            var dailyPrev = daily.GetRange(Math.Max(0, dailyPtr - 299), Math.Min(299, dailyPtr));
            var precheck = DailyEmaTrendPrecheck.For(dailyPrev);

            int niftyPtr = LowerBoundByDate(ctx.NiftyDaily, day);
            var niftyPrev = Slice(ctx.NiftyDaily, Math.Max(0, niftyPtr - 299), Math.Min(299, niftyPtr));
            ctx.Nifty15mByDate.TryGetValue(day, out var nifty15);

            int session = ctx.SessionOf(day);
            bool inSplitZone = splitSessions.Any(s => session >= s && session - s < SplitExclusionSessions);
            var (regime, policy) = ctx.RegimeFor(day);

            // Today's daily bar so far, built from the 15m bars (the live daily row is rebuilt from 15m the same way).
            decimal dHigh = decimal.MinValue, dLow = decimal.MaxValue; long dVol = 0;
            decimal nOpen = 0m, nHigh = decimal.MinValue, nLow = decimal.MaxValue, nClose = 0m; int nPtr = 0;

            for (int k = dayStart; k <= dayEnd; k++)
            {
                var bar = m15[k];
                dHigh = Math.Max(dHigh, bar.High); dLow = Math.Min(dLow, bar.Low); dVol += bar.Volume;
                if (nifty15 != null)
                {
                    while (nPtr < nifty15.Count && nifty15[nPtr].CandleTime <= bar.CandleTime)
                    {
                        var nb = nifty15[nPtr];
                        if (nPtr == 0) nOpen = nb.Open;
                        nHigh = Math.Max(nHigh, nb.High); nLow = Math.Min(nLow, nb.Low); nClose = nb.Close;
                        nPtr++;
                    }
                }

                if (k == dayEnd) break;                       // no bar left today to enter on
                DateTime scanUtc = bar.CandleTime + Bar15;
                DateTime scanIst = ToIst(scanUtc);
                if (!SwingTradeRules.IsWithinTradingWindow(ctx.WindowStart, ctx.WindowEnd, scanIst)) continue;
                if (!SwingTradeRules.IsPastEntryDelay(ctx.WindowStart, ctx.EntryDelayMinutes, scanIst)) continue;
                if (precheck != null && !precheck.Passes(bar.Close)) { result.Scans++; continue; }   // engine would REJECT on the EMA trend

                var dailyNow = new List<MarketCandle>(dailyPrev.Count + 1);
                dailyNow.AddRange(dailyPrev);
                dailyNow.Add(new MarketCandle { Symbol = symbol, Timeframe = "1d", CandleTime = DailyStamp(day),
                    Open = m15[dayStart].Open, High = dHigh, Low = dLow, Close = bar.Close, Volume = dVol });

                List<MarketCandle> niftyNow = niftyPrev;
                if (nPtr > 0)
                {
                    niftyNow = new List<MarketCandle>(niftyPrev.Count + 1);
                    niftyNow.AddRange(niftyPrev);
                    niftyNow.Add(new MarketCandle { Symbol = "NIFTY 50", Timeframe = "1d", CandleTime = DailyStamp(day),
                        Open = nOpen, High = nHigh, Low = nLow, Close = nClose });
                }

                int from15 = Math.Max(0, k - (RealTradeSchedule.CandleHistoryCount - 1));
                var m15Now = m15.GetRange(from15, k - from15 + 1);
                while (h60Ptr < h60.Count && h60[h60Ptr].CandleTime + Bar60 <= scanUtc) h60Ptr++;
                int from60 = Math.Max(0, h60Ptr - RealTradeSchedule.CandleHistoryCount);
                var h60Now = h60.GetRange(from60, h60Ptr - from60);

                var r = SwingDecisionEngine.Evaluate(stock, dailyNow, m15Now, h60Now, niftyNow, ctx.EngineSettings);
                result.Scans++;
                int met = r.Checklist?.MetCount ?? 0;
                bool beatsNifty = r.Factors.Any(f => f.Code == "RELATIVE_STRENGTH" && f.Points > 0);

                bool gateAllows = !ctx.RegimeMode || (policy != null && policy.MaxPositions > 0);
                bool live = ctx.RegimeMode
                    ? gateAllows && RegimeGate.Evaluate(r, policy!).Allowed
                    : r.IsBuySignal || met >= ctx.MinConditionsMatch;
                bool sweep = r.HardFiltersPassed && gateAllows && r.Score >= ctx.SweepFloor;
                if (!live && !sweep) continue;

                // Live order goes in ~20 s after the bar close, i.e. at the next bar's open.
                decimal entryRaw = m15[k + 1].Open;
                string? skip = SwingTradeRules.CheckSignalDrift(r.EntryPrice, entryRaw) != null ? "price moved ≥2% before the order (guard 12)"
                    : inSplitZone ? "suspected split/bonus in the indicator history"
                    : null;
                decimal delta = entryRaw - r.EntryPrice;
                var levels = SwingTradeRules.ComputeEntryLevels(entryRaw, r.DailyAtr > 0m ? r.DailyAtr : null,
                    r.StopLoss > 0m ? r.StopLoss + delta : null, r.Target1 > 0m ? r.Target1 + delta : null, null, null, ctx.TradeParams);

                result.Signals.Add(new BacktestSignal
                {
                    Symbol = symbol,
                    SignalTimeUtc = scanUtc,
                    EntryTimeUtc = m15[k + 1].CandleTime,
                    SessionDate = day,
                    Score = r.Score,
                    MetCount = met,
                    HardFiltersPassed = r.HardFiltersPassed,
                    BeatsNifty = beatsNifty,
                    IsLiveCandidate = live,
                    Regime = regime,
                    GateAllows = gateAllows,
                    PolicyRequiresRs = policy?.RequireRelativeStrength ?? false,
                    PolicyMaxPositions = policy?.MaxPositions ?? int.MaxValue,
                    PolicyRiskPct = policy?.RiskPct ?? 0m,
                    FactorPoints = PackFactors(r.Factors),
                    SignalPrice = r.EntryPrice,
                    EntryRaw = entryRaw,
                    StopLoss = levels.StopLoss,
                    Target = levels.TakeProfit,
                    EntryIndex = k + 1,
                    SkipReason = skip
                });
            }
        }

        if (result.Signals.Count > 0) result.Tape = tape;
        return result;
    }

    /// <summary>
    /// Walks the 15m bars from the entry bar through the live exit rules. Inside a bar the price is assumed to move
    /// open → low → high → close on an up bar and open → high → low → close on a down bar - except that a bar reaching the
    /// target always visits its low first, so a bar touching both the stop and the target counts as a stop (the cautious
    /// choice). A level crossed inside a bar fills at the level; a gap through it fills at the bar's open.
    /// </summary>
    public static BacktestExit? SimulateExit(IntradayTape tape, BacktestSignal s, BacktestContext ctx, out string? skipReason)
    {
        skipReason = null;
        var p = ctx.TradeParams;
        var levels = SwingTradeRules.ComputeEntryLevels(s.EntryRaw, null, null, null, null, null, p);   // only for the INTRADAY trail start
        var pos = new ExitPositionView(s.EntryRaw, s.StopLoss, s.Target, p.IsSwingClose ? null : levels.TrailingStopLoss, null, s.EntryTimeUtc);
        int entrySession = tape.Session[s.EntryIndex];
        decimal hi = s.EntryRaw, lo = s.EntryRaw;
        Span<decimal> path = stackalloc decimal[4];

        for (int j = s.EntryIndex; j < tape.Count; j++)
        {
            int session = tape.Session[j];
            if (session > entrySession && tape.SplitSessions.Contains(session))
            {
                skipReason = "trade held across a suspected split/bonus";
                return null;
            }
            DateTime barIst = ToIst(tape.TimeUtc(j));
            if (!SwingTradeRules.IsWithinTradingWindow(ctx.WindowStart, ctx.WindowEnd, barIst)) continue;

            decimal o = IntradayTape.Price(tape.Open[j]), h = IntradayTape.Price(tape.High[j]),
                    l = IntradayTape.Price(tape.Low[j]), c = IntradayTape.Price(tape.Close[j]);
            path[0] = o;
            bool lowFirst = c >= o || (pos.TakeProfit is decimal tp && h >= tp);
            if (lowFirst) { path[1] = l; path[2] = h; } else { path[1] = h; path[2] = l; }
            path[3] = c;

            int daysOpen = session - entrySession;
            decimal prev = o;
            for (int q = 0; q < 4; q++)
            {
                decimal price = path[q];
                hi = Math.Max(hi, price); lo = Math.Min(lo, price);
                var d = SwingTradeRules.EvaluateExit(pos, price, barIst, daysOpen, ctx.MaxDurationDays, p);
                if (d.ShouldExit)
                {
                    decimal fill = FillPrice(d.Reason, pos, prev, price, p);
                    return new BacktestExit(tape.TimeUtc(j), IstDate(tape.TimeUtc(j)), fill, d.Reason, Category(d.Reason), daysOpen,
                        Pct(hi, s.EntryRaw), Pct(lo, s.EntryRaw), false);
                }
                if (d.NewTrailingStopLoss.HasValue) pos = pos with { TrailingStopLoss = d.NewTrailingStopLoss };
                prev = price;
            }
        }

        int last = tape.Count - 1;
        return new BacktestExit(tape.TimeUtc(last) + Bar15, IstDate(tape.TimeUtc(last)), IntradayTape.Price(tape.Close[last]),
            "Still open at the end of the test (valued at the last close)", "End of test", tape.Session[last] - entrySession,
            Pct(hi, s.EntryRaw), Pct(lo, s.EntryRaw), true);
    }

    private static decimal FillPrice(string reason, ExitPositionView pos, decimal prev, decimal price, SwingTradeParams p)
    {
        if (reason.StartsWith("Target", StringComparison.Ordinal) && pos.TakeProfit is decimal tp)
            return prev < tp && price >= tp ? tp : price;

        decimal? level = reason.StartsWith("Emergency", StringComparison.Ordinal) ? SwingTradeRules.GetEmergencyStop(pos, p)
            : reason.StartsWith("Trailing SL", StringComparison.Ordinal) ? pos.TrailingStopLoss
            : reason.StartsWith("Stop Loss", StringComparison.Ordinal) ? pos.StopLoss
            : null;
        return level is decimal lv && prev > lv && price <= lv ? lv : price;
    }

    public static string Category(string reason) =>
        reason.StartsWith("Target", StringComparison.Ordinal) ? "Target"
        : reason.StartsWith("Emergency", StringComparison.Ordinal) ? "Emergency stop"
        : reason.StartsWith("Trailing SL", StringComparison.Ordinal) ? "Trailing stop"
        : reason.StartsWith("Stop Loss", StringComparison.Ordinal) ? "Stop loss"
        : reason.StartsWith("Max Duration", StringComparison.Ordinal) ? "Max days"
        : "Other";

    /// <summary>
    /// The engine's daily EMA-trend hard filter (Price &gt; EMA20 &gt; EMA50, both rising over 2 bars, EMA200 stable over 5),
    /// computed incrementally: the 299 earlier daily bars are the same for every scan of a day, so their EMAs are computed
    /// once and only today's partial bar is stepped on - the same arithmetic, in the same order, as
    /// <see cref="IndicatorCalculator.CalculateEma"/>, so the answer is identical. A scan that fails it is rejected by the
    /// engine whatever else it holds, so the backtest skips the full evaluation (about 0.6 ms) for it. Scans that pass go
    /// through <see cref="SwingDecisionEngine.Evaluate"/> as usual. Used only when there are at least 200 earlier bars.
    /// </summary>
    public sealed class DailyEmaTrendPrecheck
    {
        private readonly List<decimal> _e20, _e50, _e200;
        private readonly int _n;

        public DailyEmaTrendPrecheck(List<decimal> previousCloses)
        {
            _n = previousCloses.Count;
            _e20 = IndicatorCalculator.CalculateEma(previousCloses, 20);
            _e50 = IndicatorCalculator.CalculateEma(previousCloses, 50);
            _e200 = IndicatorCalculator.CalculateEma(previousCloses, 200);
        }

        public static DailyEmaTrendPrecheck? For(List<MarketCandle> previousDaily) =>
            previousDaily.Count >= 200 ? new DailyEmaTrendPrecheck(previousDaily.Select(c => c.Close).ToList()) : null;

        private static decimal Step(List<decimal> ema, decimal price, int period)
        {
            decimal multiplier = 2.0m / (period + 1);
            return (price * multiplier) + (ema[^1] * (1 - multiplier));
        }

        public bool Passes(decimal todayClose)
        {
            decimal e20 = Step(_e20, todayClose, 20), e50 = Step(_e50, todayClose, 50), e200 = Step(_e200, todayClose, 200);
            bool rising20 = e20 > _e20[_n - 2];
            bool rising50 = e50 > _e50[_n - 2];
            bool hasEma200History = _n + 1 >= SwingDecisionEngine.MinBarsForEma200;
            bool ema200Stable = !hasEma200History || e200 >= _e200[_n - 5] * 0.995m;
            return todayClose > e20 && e20 > e50 && rising20 && rising50 && ema200Stable;
        }
    }

    /// <summary>The engine's scoring factors, in the order <see cref="PackFactors"/> stores them.</summary>
    public static readonly string[] FactorCodes =
    {
        "BREAKOUT_GROUP", "VOL_CONFIRMATION", "RELATIVE_STRENGTH", "MULTITIMEFRAME", "RSI_MOMENTUM", "MACD_BULLISH", "BULLISH_CANDLE", "RISK_REWARD"
    };

    public static long PackFactors(IEnumerable<SwingFactorScore> factors)
    {
        long packed = 0;
        foreach (var f in factors)
        {
            int idx = Array.IndexOf(FactorCodes, f.Code);
            if (idx >= 0) packed |= (long)Math.Clamp(f.Points, 0, 63) << (6 * idx);
        }
        return packed;
    }

    public static string FactorsText(long packed) =>
        string.Join(",", FactorCodes.Select((code, idx) => $"{code}:{(packed >> (6 * idx)) & 63}"));

    private static decimal Pct(decimal price, decimal entry) => entry > 0m ? Math.Round((price - entry) / entry * 100m, 2) : 0m;

    private static int LowerBoundByDate(IReadOnlyList<MarketCandle> sorted, DateTime day)
    {
        int lo = 0, hi = sorted.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (IstDate(sorted[mid].CandleTime) < day) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

    private static List<MarketCandle> Slice(IReadOnlyList<MarketCandle> list, int from, int count)
    {
        var slice = new List<MarketCandle>(count);
        for (int k = from; k < from + count && k < list.Count; k++) slice.Add(list[k]);
        return slice;
    }
}
