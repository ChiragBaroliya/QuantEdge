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
    public bool TestBuy { get; init; } = true;
    public bool TestShort { get; init; }
    public string ShortEntryCutoff { get; init; } = "15:00";
    public string ShortSquareOffTime { get; init; } = "15:15";
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
    /// <summary>
    /// Price slippage per side, % (0 = disabled - the default here is intentionally not the run default of 0.10,
    /// so every test that builds a BacktestContext without setting this keeps its exact pre-fix behavior; the real
    /// pipeline (BacktestService) always sets this explicitly from the run's BacktestParams.SlippagePct).
    /// </summary>
    public decimal SlippagePct { get; init; } = 0m;
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
/// the function the live monitors call. Shorts go the same way through <see cref="SwingShortDecisionEngine.Evaluate"/>
/// and <see cref="SwingTradeRules.EvaluateShortExit"/>.
/// </summary>
public static class BacktestEngine
{
    public const int SplitExclusionSessions = 200;
    private const string DriftSkipReason = "price moved ≥2% before the order (guard 12)";
    private const string MissingBarSquareOffReason = "Auto Square-off (no bar stored at the square-off time - last close of the day used)";
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
            var precheck = DailyEmaTrendPrecheck.For(dailyPrev, ctx.EngineSettings.Rules);

            int niftyPtr = LowerBoundByDate(ctx.NiftyDaily, day);
            var niftyPrev = Slice(ctx.NiftyDaily, Math.Max(0, niftyPtr - 299), Math.Min(299, niftyPtr));
            ctx.Nifty15mByDate.TryGetValue(day, out var nifty15);

            int session = ctx.SessionOf(day);
            bool inSplitZone = splitSessions.Any(s => session >= s && session - s < SplitExclusionSessions);
            var (regime, regimePolicy) = ctx.RegimeFor(day);
            // In NIFTY_FILTER mode the regime is only reported, as live: its limits on positions, risk and exposure apply in REGIME mode alone.
            var policy = ctx.RegimeMode ? regimePolicy : null;

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
                // Where the engine would REJECT on the EMA trend anyway, skip the full evaluation.
                bool scanBuy = ctx.TestBuy && (precheck == null || precheck.Passes(bar.Close));
                bool scanShort = ctx.TestShort && SwingTradeRules.IsBeforeShortEntryCutoff(ctx.ShortEntryCutoff, scanIst)
                    && (precheck == null || precheck.PassesDowntrend(bar.Close));
                if (!scanBuy && !scanShort) { result.Scans++; continue; }

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

                result.Scans++;
                // Live order goes in ~20 s after the bar close, i.e. at the next bar's open.
                decimal entryRaw = m15[k + 1].Open;
                // Slippage always works against the trade (same direction/formula as BacktestPortfolio's exit-side
                // slippage): a BUY fills higher, a SHORT (sell to open) fills lower.
                decimal slip = ctx.SlippagePct / 100m;
                string? splitSkip = inSplitZone ? "suspected split/bonus in the indicator history" : null;

                if (scanBuy)
                {
                    var r = SwingDecisionEngine.Evaluate(stock, dailyNow, m15Now, h60Now, niftyNow, ctx.EngineSettings);
                    int met = r.Checklist?.MetCount ?? 0;
                    bool beatsNifty = r.Factors.Any(f => f.Code == "RELATIVE_STRENGTH" && f.Points > 0);

                    bool gateAllows = !ctx.RegimeMode || (policy != null && policy.MaxPositions > 0);
                    bool live = ctx.RegimeMode
                        ? gateAllows && RegimeGate.Evaluate(r, policy!).Allowed
                        : r.IsBuySignal || met >= ctx.MinConditionsMatch;
                    bool sweep = r.HardFiltersPassed && gateAllows && r.Score >= ctx.SweepFloor;
                    if (live || sweep)
                    {
                        string? skip = SwingTradeRules.CheckSignalDrift(r.EntryPrice, entryRaw) != null ? DriftSkipReason : splitSkip;
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
                            PolicyMaxExposurePct = policy?.MaxExposurePct,
                            FactorPoints = PackFactors(r.Factors, FactorCodes),
                            SubType = r.BreakoutSubType,
                            Rsi15m = r.Rsi15m,
                            Rsi60m = r.Rsi60m,
                            VolumeMultiple = r.VolumeMultiple,
                            Adx1d = r.Adx1d,
                            SignalDailyAtr = r.DailyAtr,
                            SignalPrice = r.EntryPrice,
                            EntryRaw = entryRaw,
                            EntryFill = Math.Round(entryRaw * (1m + slip), 2),
                            StopLoss = levels.StopLoss,
                            Target = levels.TakeProfit,
                            EntryIndex = k + 1,
                            SkipReason = skip
                        });
                    }
                }

                if (scanShort)
                {
                    // Shorts have their own NIFTY-bearish gate in every gate mode; in REGIME mode they are also
                    // throttled by the regime policy's own (mirrored) Short* columns, same mechanism as BUYs.
                    var r = SwingShortDecisionEngine.Evaluate(stock, dailyNow, m15Now, h60Now, niftyNow, ctx.EngineSettings);
                    int met = r.Checklist?.MetCount ?? 0;
                    bool shortGateAllows = !ctx.RegimeMode || (policy != null && policy.ShortMaxPositions > 0);
                    bool live = ctx.RegimeMode
                        ? shortGateAllows && RegimeGate.EvaluateShort(r, policy!).Allowed
                        : r.HardFiltersPassed && (r.IsSellSignal || met >= ctx.MinConditionsMatch);
                    bool sweep = r.HardFiltersPassed && shortGateAllows && r.Score >= ctx.SweepFloor;
                    if (live || sweep)
                    {
                        string? skip = SwingTradeRules.CheckShortSignalDrift(r.EntryPrice, entryRaw) != null ? DriftSkipReason : splitSkip;
                        decimal delta = entryRaw - r.EntryPrice;
                        var levels = SwingTradeRules.ComputeShortEntryLevels(entryRaw, r.DailyAtr > 0m ? r.DailyAtr : null,
                            r.StopLoss > 0m ? r.StopLoss + delta : null, r.Target1 > 0m ? r.Target1 + delta : null, ctx.TradeParams);

                        result.Signals.Add(new BacktestSignal
                        {
                            Symbol = symbol,
                            IsShort = true,
                            SignalTimeUtc = scanUtc,
                            EntryTimeUtc = m15[k + 1].CandleTime,
                            SessionDate = day,
                            Score = r.Score,
                            MetCount = met,
                            HardFiltersPassed = r.HardFiltersPassed,
                            IsLiveCandidate = live,
                            Regime = regime,
                            GateAllows = shortGateAllows,
                            PolicyMaxPositions = policy?.ShortMaxPositions ?? int.MaxValue,
                            FactorPoints = PackFactors(r.Factors, ShortFactorCodes),
                            SubType = r.BreakdownSubType,
                            Rsi15m = r.Rsi15m,
                            Rsi60m = r.Rsi60m,
                            VolumeMultiple = r.VolumeMultiple,
                            Adx1d = r.Adx1d,
                            SignalDailyAtr = r.DailyAtr,
                            SignalPrice = r.EntryPrice,
                            EntryRaw = entryRaw,
                            EntryFill = Math.Round(entryRaw * (1m - slip), 2),
                            StopLoss = levels.StopLoss,
                            Target = levels.TakeProfit,
                            EntryIndex = k + 1,
                            SkipReason = skip
                        });
                    }
                }
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
        if (s.IsShort) return SimulateShortExit(tape, s, ctx, out skipReason);

        skipReason = null;
        var p = ctx.TradeParams;
        // EntryFill (not EntryRaw) is the real simulated cost basis, including entry slippage - everything the exit
        // rules measure "from entry" (breakeven floor, ATR inference, emergency stop, trail activation) must use it.
        // StopLoss/Target stay anchored to the raw signal price: they're the position's already-placed order levels,
        // not a live cost-basis reference - same split live trading already has (ExitPositionView.From(RealPosition)).
        var levels = SwingTradeRules.ComputeEntryLevels(s.EntryFill, null, null, null, null, null, p);   // only for the INTRADAY trail start
        var pos = new ExitPositionView(s.EntryFill, s.StopLoss, s.Target, p.IsSwingClose ? null : levels.TrailingStopLoss, null, s.EntryTimeUtc);
        int entrySession = tape.Session[s.EntryIndex];
        decimal hi = s.EntryFill, lo = s.EntryFill;
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
                        Pct(hi, s.EntryFill), Pct(lo, s.EntryFill), false);
                }
                if (d.NewTrailingStopLoss.HasValue) pos = pos with { TrailingStopLoss = d.NewTrailingStopLoss };
                prev = price;
            }

            // The day's closing bar is missing from the stored candles. Live, the closing-basis checks still run on ticks, so
            // they run here on the day's last close instead of waiting for the next day.
            TimeSpan closingStart = SwingTradeRules.GetClosingWindowStart(p);
            if (j + 1 < tape.Count && tape.Session[j + 1] > session && barIst.TimeOfDay < closingStart)
            {
                var d = SwingTradeRules.EvaluateExit(pos, c, barIst.Date + closingStart, daysOpen, ctx.MaxDurationDays, p);
                if (d.ShouldExit)
                    return new BacktestExit(tape.TimeUtc(j), IstDate(tape.TimeUtc(j)), c, d.Reason, Category(d.Reason), daysOpen,
                        Pct(hi, s.EntryFill), Pct(lo, s.EntryFill), false);
                if (d.NewTrailingStopLoss.HasValue) pos = pos with { TrailingStopLoss = d.NewTrailingStopLoss };
            }
        }

        int last = tape.Count - 1;
        return new BacktestExit(tape.TimeUtc(last) + Bar15, IstDate(tape.TimeUtc(last)), IntradayTape.Price(tape.Close[last]),
            "Still open at the end of the test (valued at the last close)", "End of test", tape.Session[last] - entrySession,
            Pct(hi, s.EntryFill), Pct(lo, s.EntryFill), true);
    }

    /// <summary>
    /// <see cref="SimulateExit"/> for a short, through <see cref="SwingTradeRules.EvaluateShortExit"/> - mirrored, so a bar
    /// reaching the target visits its high first and a bar touching both still counts as a stop. Like the live monitor it
    /// isn't limited to the trading window. Bars are 15 minutes, so the square-off happens at the open of the first bar
    /// starting at/after the square-off time. If the stored candles have no such bar that day, it goes at the day's last
    /// close: live, the square-off runs on ticks and never depends on a candle, so a short is never carried overnight.
    /// </summary>
    private static BacktestExit? SimulateShortExit(IntradayTape tape, BacktestSignal s, BacktestContext ctx, out string? skipReason)
    {
        skipReason = null;
        var p = ctx.TradeParams;
        // EntryFill (not EntryRaw) is the real simulated cost basis, including entry slippage - see the LONG-side
        // comment in SimulateExit for the full rationale; StopLoss/Target stay anchored to the raw signal price.
        var levels = SwingTradeRules.ComputeShortEntryLevels(s.EntryFill, null, null, null, p);   // only for the INTRADAY trail start
        var pos = new ExitPositionView(s.EntryFill, s.StopLoss, s.Target, p.IsSwingClose ? null : levels.TrailingStopLoss, null, s.EntryTimeUtc);
        int entrySession = tape.Session[s.EntryIndex];
        decimal hi = s.EntryFill, lo = s.EntryFill;
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

            decimal o = IntradayTape.Price(tape.Open[j]), h = IntradayTape.Price(tape.High[j]),
                    l = IntradayTape.Price(tape.Low[j]), c = IntradayTape.Price(tape.Close[j]);
            path[0] = o;
            bool highFirst = c < o || (pos.TakeProfit is decimal tp && l <= tp);
            if (highFirst) { path[1] = h; path[2] = l; } else { path[1] = l; path[2] = h; }
            path[3] = c;

            int daysOpen = session - entrySession;
            decimal prev = o;
            for (int q = 0; q < 4; q++)
            {
                decimal price = path[q];
                hi = Math.Max(hi, price); lo = Math.Min(lo, price);
                var d = SwingTradeRules.EvaluateShortExit(pos, price, barIst, ctx.ShortSquareOffTime, p);
                if (d.ShouldExit)
                {
                    decimal fill = ShortFillPrice(d.Reason, pos, prev, price);
                    return new BacktestExit(tape.TimeUtc(j), IstDate(tape.TimeUtc(j)), fill, d.Reason, Category(d.Reason), daysOpen,
                        ShortPct(lo, s.EntryFill), ShortPct(hi, s.EntryFill), false);
                }
                if (d.NewTrailingStopLoss.HasValue) pos = pos with { TrailingStopLoss = d.NewTrailingStopLoss };
                prev = price;
            }

            if (j + 1 < tape.Count && tape.Session[j + 1] > session)
                return new BacktestExit(tape.TimeUtc(j) + Bar15, IstDate(tape.TimeUtc(j)), c, MissingBarSquareOffReason, Category(MissingBarSquareOffReason), daysOpen,
                    ShortPct(lo, s.EntryFill), ShortPct(hi, s.EntryFill), false);
        }

        int last = tape.Count - 1;
        return new BacktestExit(tape.TimeUtc(last) + Bar15, IstDate(tape.TimeUtc(last)), IntradayTape.Price(tape.Close[last]),
            "Still open at the end of the test (valued at the last close)", "End of test", tape.Session[last] - entrySession,
            ShortPct(lo, s.EntryFill), ShortPct(hi, s.EntryFill), true);
    }

    private static decimal ShortFillPrice(string reason, ExitPositionView pos, decimal prev, decimal price)
    {
        if (reason.StartsWith("Target", StringComparison.Ordinal) && pos.TakeProfit is decimal tp)
            return prev > tp && price <= tp ? tp : price;

        decimal? level = reason.StartsWith("Trailing SL", StringComparison.Ordinal) ? pos.TrailingStopLoss
            : reason.StartsWith("Stop Loss", StringComparison.Ordinal) ? pos.StopLoss
            : null;
        return level is decimal lv && prev < lv && price >= lv ? lv : price;
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
        : reason == SwingTradeRules.ShortSquareOffReason ? "Square-off"
        : reason == MissingBarSquareOffReason ? "Square-off (missing bar)"
        : "Other";

    /// <summary>
    /// The engine's daily EMA-trend hard filter (Price &gt; fast EMA &gt; slow EMA, both rising, long EMA stable - the same rule numbers),
    /// computed incrementally: the 299 earlier daily bars are the same for every scan of a day, so their EMAs are computed
    /// once and only today's partial bar is stepped on - the same arithmetic, in the same order, as
    /// <see cref="IndicatorCalculator.CalculateEma"/>, so the answer is identical. A scan that fails it is rejected by the
    /// engine whatever else it holds, so the backtest skips the full evaluation (about 0.6 ms) for it. Scans that pass go
    /// through <see cref="SwingDecisionEngine.Evaluate"/> as usual. Used only when there are enough earlier bars for the long EMA.
    /// </summary>
    public sealed class DailyEmaTrendPrecheck
    {
        private readonly List<decimal> _e20, _e50, _e200;
        private readonly int _n;
        private readonly SwingRuleParameters _rules;

        public DailyEmaTrendPrecheck(List<decimal> previousCloses, SwingRuleParameters rules)
        {
            _n = previousCloses.Count;
            _rules = rules;
            _e20 = IndicatorCalculator.CalculateEma(previousCloses, rules.TrendEmaFast);
            _e50 = IndicatorCalculator.CalculateEma(previousCloses, rules.TrendEmaSlow);
            _e200 = IndicatorCalculator.CalculateEma(previousCloses, rules.TrendEmaLong);
        }

        public static DailyEmaTrendPrecheck? For(List<MarketCandle> previousDaily, SwingRuleParameters? rules = null)
        {
            rules ??= new SwingRuleParameters();
            int needed = Math.Max(200, Math.Max(rules.TrendEmaLong, Math.Max(rules.TrendSlopeBars, rules.LongEmaSlopeBars) + 1));
            return previousDaily.Count >= needed ? new DailyEmaTrendPrecheck(previousDaily.Select(c => c.Close).ToList(), rules) : null;
        }

        private static decimal Step(List<decimal> ema, decimal price, int period)
        {
            decimal multiplier = 2.0m / (period + 1);
            return (price * multiplier) + (ema[^1] * (1 - multiplier));
        }

        public bool Passes(decimal todayClose)
        {
            decimal e20 = Step(_e20, todayClose, _rules.TrendEmaFast), e50 = Step(_e50, todayClose, _rules.TrendEmaSlow), e200 = Step(_e200, todayClose, _rules.TrendEmaLong);
            bool rising20 = e20 > _e20[_n - _rules.TrendSlopeBars];
            bool rising50 = e50 > _e50[_n - _rules.TrendSlopeBars];
            bool hasEma200History = _n + 1 >= _rules.MinBarsForLongEma;
            bool ema200Stable = !hasEma200History || e200 >= _e200[_n - _rules.LongEmaSlopeBars] * (1m - _rules.LongEmaToleranceFactor);
            return todayClose > e20 && e20 > e50 && rising20 && rising50 && ema200Stable;
        }

        /// <summary><see cref="Passes"/> mirrored: the short engine's daily downtrend hard filter (Price &lt; fast EMA &lt; slow EMA, both falling, long EMA not rising).</summary>
        public bool PassesDowntrend(decimal todayClose)
        {
            decimal e20 = Step(_e20, todayClose, _rules.TrendEmaFast), e50 = Step(_e50, todayClose, _rules.TrendEmaSlow), e200 = Step(_e200, todayClose, _rules.TrendEmaLong);
            bool falling20 = e20 < _e20[_n - _rules.TrendSlopeBars];
            bool falling50 = e50 < _e50[_n - _rules.TrendSlopeBars];
            bool hasEma200History = _n + 1 >= _rules.MinBarsForLongEma;
            bool ema200NotRising = !hasEma200History || e200 <= _e200[_n - _rules.LongEmaSlopeBars] * (1m + _rules.LongEmaToleranceFactor);
            return todayClose < e20 && e20 < e50 && falling20 && falling50 && ema200NotRising;
        }
    }

    /// <summary>The engine's scoring factors, in the order <see cref="PackFactors"/> stores them.</summary>
    public static readonly string[] FactorCodes =
    {
        "BREAKOUT_GROUP", "VOL_CONFIRMATION", "RELATIVE_STRENGTH", "MULTITIMEFRAME", "RSI_MOMENTUM", "MACD_BULLISH", "BULLISH_CANDLE", "RISK_REWARD"
    };

    /// <summary>The short engine's scoring factors, each in the same slot as its BUY counterpart.</summary>
    public static readonly string[] ShortFactorCodes =
    {
        "BREAKDOWN_GROUP", "VOL_CONFIRMATION", "RELATIVE_WEAKNESS", "MULTITIMEFRAME", "RSI_MOMENTUM", "MACD_BEARISH", "BEARISH_CANDLE", "RISK_REWARD"
    };

    public static long PackFactors(IEnumerable<SwingFactorScore> factors, string[] codes)
    {
        long packed = 0;
        foreach (var f in factors)
        {
            int idx = Array.IndexOf(codes, f.Code);
            if (idx >= 0) packed |= (long)Math.Clamp(f.Points, 0, 63) << (6 * idx);
        }
        return packed;
    }

    public static string FactorsText(long packed, bool isShort) =>
        string.Join(",", (isShort ? ShortFactorCodes : FactorCodes).Select((code, idx) => $"{code}:{(packed >> (6 * idx)) & 63}"));

    /// <summary>
    /// For each side and horizon: did the price go the signal's way? Counts each stock once a day (its first signal) so a
    /// setup that re-fires every 15 minutes isn't counted many times, and leaves out signals the live guards would skip.
    /// Horizons past the end of the data or across a suspected split are left out. Before costs.
    /// </summary>
    public static List<BacktestDirectionRow> DirectionAccuracy(IEnumerable<BacktestSignal> signals, Func<string, IntradayTape?> tapeOf)
    {
        var horizons = new (string Name, Func<IntradayTape, int, int> IndexOf)[]
        {
            ("1 hour later", (t, e) => e + 3 < t.Count && t.Session[e + 3] == t.Session[e] ? e + 3 : -1),
            ("Same-day close", (t, e) => LastBarOfSession(t, e, 0)),
            ("Next-day close", (t, e) => LastBarOfSession(t, e, 1)),
            ("5 days later", (t, e) => LastBarOfSession(t, e, 5))
        };

        var rows = new List<BacktestDirectionRow>();
        var firstPerDay = signals.Where(s => s.IsLiveCandidate && s.SkipReason == null)
            .GroupBy(s => (s.IsShort, s.Symbol, s.SessionDate))
            .Select(g => g.OrderBy(s => s.EntryTimeUtc).First())
            .ToList();

        foreach (bool isShort in new[] { false, true })
        {
            var ofSide = firstPerDay.Where(s => s.IsShort == isShort).ToList();
            if (ofSide.Count == 0) continue;
            foreach (var (name, indexOf) in horizons)
            {
                int counted = 0, right = 0;
                decimal moveSum = 0m;
                foreach (var s in ofSide)
                {
                    var tape = tapeOf(s.Symbol);
                    if (tape == null || s.EntryRaw <= 0m) continue;
                    int at = indexOf(tape, s.EntryIndex);
                    if (at < 0) continue;
                    int entrySession = tape.Session[s.EntryIndex];
                    if (tape.SplitSessions.Any(x => x > entrySession && x <= tape.Session[at])) continue;

                    decimal move = (IntradayTape.Price(tape.Close[at]) - s.EntryRaw) / s.EntryRaw * 100m * (isShort ? -1m : 1m);
                    counted++;
                    if (move > 0m) right++;
                    moveSum += move;
                }
                if (counted == 0) continue;
                rows.Add(new BacktestDirectionRow
                {
                    Side = BacktestSide.Of(isShort),
                    Horizon = name,
                    Signals = counted,
                    RightPct = Math.Round(100m * right / counted, 1),
                    AvgMovePct = Math.Round(moveSum / counted, 2)
                });
            }
        }
        return rows;
    }

    private static int LastBarOfSession(IntradayTape t, int entry, int sessionsAhead)
    {
        int target = t.Session[entry] + sessionsAhead;
        int found = -1;
        for (int j = entry; j < t.Count && t.Session[j] <= target; j++)
            if (t.Session[j] == target) found = j;
        return found;
    }

    private static decimal Pct(decimal price, decimal entry) => entry > 0m ? Math.Round((price - entry) / entry * 100m, 2) : 0m;

    /// <summary>A short's gain at <paramref name="price"/>: positive when the price is below the entry.</summary>
    private static decimal ShortPct(decimal price, decimal entry) => entry > 0m ? Math.Round((entry - price) / entry * 100m, 2) : 0m;

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
