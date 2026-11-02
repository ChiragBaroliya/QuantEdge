using System.Text.Json;
using QuantEdge.Domain.Entities;
using QuantEdge.Infrastructure.Services;
using QuantEdge.Infrastructure.Services.Backtest;
using Xunit;

namespace QuantEdge.Tests;

/// <summary>The two backtest-only rules (daily momentum, intraday opening range) and what they share with the score rule.</summary>
public class BacktestStrategyRulesTests
{
    // ---------------- helpers ----------------

    private static MarketCandle DailyBar(DateTime day, decimal o, decimal h, decimal l, decimal c, long volume = 1000) =>
        new() { Symbol = "T", Timeframe = "1d", CandleTime = BacktestEngine.DailyStamp(day), Open = o, High = h, Low = l, Close = c, Volume = volume };

    /// <summary>Weekdays from 1-Jan-2024 at a flat 100, then whatever the test appends.</summary>
    private static List<MarketCandle> FlatDaily(int count, decimal price = 100m, long volume = 1000)
    {
        var bars = new List<MarketCandle>();
        var day = new DateTime(2024, 1, 1);
        while (bars.Count < count)
        {
            if (day.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday))
                bars.Add(DailyBar(day, price, price + 1m, price - 1m, price, volume));
            day = day.AddDays(1);
        }
        return bars;
    }

    private static DateTime NextWeekday(DateTime day)
    {
        do day = day.AddDays(1); while (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday);
        return day;
    }

    private static DateTime LastDay(List<MarketCandle> bars) => BacktestEngine.IstDate(bars[^1].CandleTime);

    private static int Session(DateTime day)
    {
        int count = 0;
        for (var d = new DateTime(2024, 1, 1); d < day; d = d.AddDays(1))
            if (d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)) count++;
        return count;
    }

    private static MomentumDailyBacktest.Context MomentumCtx(int rank = 90, bool niftyAbove = true, decimal slippage = 0m) => new()
    {
        FromDate = new DateTime(2024, 1, 1),
        ToDate = new DateTime(2030, 1, 1),
        SlippagePct = slippage,
        SessionOf = Session,
        NiftyAboveAverage = _ => niftyAbove,
        StrengthRank = (_, _) => rank
    };

    /// <summary>A flat year, then a breakout close at 110 on 3 × volume, then the next day's bar.</summary>
    private static List<MarketCandle> BreakoutBars(decimal nextOpen = 110.5m, long breakoutVolume = 3000)
    {
        var bars = FlatDaily(MomentumDailyRules.YearHighLookback + 1);
        var day = NextWeekday(LastDay(bars));
        bars.Add(DailyBar(day, 101m, 111m, 100m, 110m, breakoutVolume));
        bars.Add(DailyBar(NextWeekday(day), nextOpen, nextOpen + 1m, nextOpen - 1m, nextOpen));
        return bars;
    }

    private static BacktestSignal MomentumSignal(decimal entry = 100m, decimal atr = 2m, decimal? fill = null) => new()
    {
        Symbol = "T",
        SessionDate = new DateTime(2025, 1, 6),
        EntryRaw = entry,
        EntryFill = fill ?? entry,
        SignalDailyAtr = atr,
        StopLoss = MomentumDailyRules.Levels(entry, atr).Stop,
        EntryIndex = 0
    };

    /// <summary>Daily bars from 6-Jan-2025 (a Monday) with these closes; highs and lows stay 0.5 around the close.</summary>
    private static List<MarketCandle> Walk(params decimal[] closes)
    {
        var bars = new List<MarketCandle>();
        var day = new DateTime(2025, 1, 6);
        foreach (decimal c in closes)
        {
            bars.Add(DailyBar(day, c, c + 0.5m, c - 0.5m, c));
            day = NextWeekday(day);
        }
        return bars;
    }

    private static BacktestExit MomentumExit(List<MarketCandle> bars, BacktestSignal? s = null) =>
        MomentumDailyBacktest.SimulateExit(bars, s ?? MomentumSignal(), new HashSet<int>(), Session, out _)!;

    // A Monday. 09:15 IST = 03:45 UTC.
    private static readonly DateTime OrbDay = new(2025, 1, 6);

    private static DateTime Slot(DateTime day, int slot) => DateTime.SpecifyKind(day, DateTimeKind.Utc).AddHours(3).AddMinutes(45 + 15 * slot);

    private static MarketCandle Bar15(DateTime day, int slot, decimal o, decimal h, decimal l, decimal c, long volume = 1000) =>
        new() { Symbol = "T", Timeframe = "15m", CandleTime = Slot(day, slot), Open = o, High = h, Low = l, Close = c, Volume = volume };

    private static BacktestSignal OrbSignal(bool isShort, decimal entry, decimal stop, decimal target, int entryIndex = 0) => new()
    {
        Symbol = "T", IsShort = isShort, SessionDate = OrbDay, EntryRaw = entry, EntryFill = entry, StopLoss = stop, Target = target, EntryIndex = entryIndex
    };

    /// <summary>25 quiet sessions before <see cref="OrbDay"/> (opening range 98-102), then that day's opening range and later bars.</summary>
    private static List<MarketCandle> OrbBars(long openingVolume, params MarketCandle[] later)
    {
        var bars = new List<MarketCandle>();
        var day = OrbDay.AddDays(-50);
        var quiet = new List<DateTime>();
        for (var d = day; d < OrbDay; d = d.AddDays(1))
            if (d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)) quiet.Add(d);
        foreach (var d in quiet.TakeLast(25))
        {
            bars.Add(Bar15(d, 0, 100m, 102m, 99m, 100m));
            bars.Add(Bar15(d, 1, 100m, 101m, 98m, 100m));
            bars.Add(Bar15(d, 2, 100m, 101m, 99m, 100m));
        }
        bars.Add(Bar15(OrbDay, 0, 100m, 102m, 99m, 100m, openingVolume));
        bars.Add(Bar15(OrbDay, 1, 100m, 101m, 98m, 100m, openingVolume));
        bars.AddRange(later);
        return bars;
    }

    /// <summary>Daily bars up to the day before <see cref="OrbDay"/>, all closing at <paramref name="close"/> after a run at <paramref name="earlier"/>, so the last close sits above or below its average.</summary>
    private static List<MarketCandle> OrbDaily(decimal earlier, decimal close)
    {
        var bars = new List<MarketCandle>();
        var days = new List<DateTime>();
        for (var d = OrbDay.AddDays(-1); days.Count < 30; d = d.AddDays(-1))
            if (d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)) days.Insert(0, d);
        foreach (var d in days)
        {
            decimal c = d == days[^1] ? close : earlier;
            bars.Add(DailyBar(d, c, c + 1m, c - 1m, c));
        }
        return bars;
    }

    private static IntradayOrbBacktest.Context OrbCtx(bool buy = true, bool shortSide = false, decimal niftyPrev = 100m, decimal niftyNow = 101m) => new()
    {
        FromDate = OrbDay,
        ToDate = OrbDay,
        TestBuy = buy,
        TestShort = shortSide,
        SessionOf = Session,
        NiftyPrevClose = _ => niftyPrev,
        NiftyAtBar = _ => niftyNow
    };

    private static List<BacktestSignal> OrbSignals(List<MarketCandle> m15, List<MarketCandle> daily, IntradayOrbBacktest.Context ctx) =>
        IntradayOrbBacktest.GenerateSignals("T", m15, daily, new HashSet<int>(), ctx);

    // ---------------- the trading calendar ----------------

    [Fact]
    public void TradingSessions_OneStocksStraySaturdayIsNotASession()
    {
        var monday = new DateTime(2025, 5, 5);
        var saturday = new DateTime(2025, 5, 10);
        var stray = new List<MarketCandle> { DailyBar(monday, 1, 1, 1, 1), DailyBar(saturday, 1, 1, 1, 1) };
        var symbols = new List<IReadOnlyList<MarketCandle>> { stray };
        for (int i = 0; i < 9; i++) symbols.Add(new List<MarketCandle> { DailyBar(monday, 1, 1, 1, 1) });

        var sessions = BacktestEngine.TradingSessions(symbols, new List<MarketCandle> { DailyBar(monday, 1, 1, 1, 1) });

        Assert.Equal(new[] { monday }, sessions);
    }

    [Fact]
    public void TradingSessions_KeepsASpecialSaturdaySessionMostStocksTraded()
    {
        var budgetSaturday = new DateTime(2025, 2, 1);
        var symbols = Enumerable.Range(0, 4).Select(_ => (IReadOnlyList<MarketCandle>)new List<MarketCandle> { DailyBar(budgetSaturday, 1, 1, 1, 1) }).ToList();

        var sessions = BacktestEngine.TradingSessions(symbols, new List<MarketCandle> { DailyBar(budgetSaturday, 1, 1, 1, 1) });

        Assert.Equal(new[] { budgetSaturday }, sessions);
    }

    [Fact]
    public void TradingSessions_AWeekdayNiftyTradedCountsEvenWhenFewStocksHaveHistory()
    {
        var monday = new DateTime(2021, 5, 3);
        var symbols = new List<IReadOnlyList<MarketCandle>> { new List<MarketCandle>(), new List<MarketCandle>(), new List<MarketCandle>(), new List<MarketCandle>(), new List<MarketCandle>() };

        var sessions = BacktestEngine.TradingSessions(symbols, new List<MarketCandle> { DailyBar(monday, 1, 1, 1, 1) });

        Assert.Equal(new[] { monday }, sessions);
    }

    // ---------------- the stock list ----------------

    [Theory]
    [InlineData("BANKBETF", null, true)]
    [InlineData("NIFTYBEES", null, true)]
    [InlineData("HDFCSILVER", null, true)]
    [InlineData("IDFNIFTYET", null, true)]
    [InlineData("XYZ", "Some Exchange Traded Fund", true)]
    [InlineData("ABC", "ABC GOLD ETF", true)]
    [InlineData("TCS", "TATA CONSULTANCY SERV LT", false)]
    [InlineData("GOLDIAM", "GOLDIAM INTERNATIONAL", false)]
    [InlineData("SILVERTUC", "SILVER TOUCH TECH", false)]
    public void IsEtfLike_LeavesOutFundsButNotStocks(string symbol, string? name, bool expected) =>
        Assert.Equal(expected, BacktestUniverse.IsEtfLike(symbol, name));

    [Fact]
    public void TopByTurnover_PicksTheMostTradedUsingOnlyDataUpToThatDay()
    {
        var big = FlatDaily(5, 100m, 1000);
        var small = FlatDaily(5, 100m, 10);
        var later = FlatDaily(6, 100m, 10);
        later[5].Volume = 100_000_000;       // a huge day after day 5 can't affect day 5's list
        var daily = new Dictionary<string, List<MarketCandle>> { ["BIG"] = big, ["SMALL"] = small, ["LATER"] = later };

        var list = BacktestUniverse.TopByTurnover(daily, 1, 3);

        Assert.Equal(new[] { "BIG" }, list[BacktestEngine.IstDate(big[4].CandleTime)]);
    }

    // ---------------- two daily bars on one date ----------------

    private static List<MarketCandle> WithSecondBarOnLastDay(List<MarketCandle> bars, int minutesAfterStamp, decimal close)
    {
        var last = bars[^1];
        bars.Add(new MarketCandle { Symbol = last.Symbol, Timeframe = "1d", CandleTime = last.CandleTime.AddMinutes(minutesAfterStamp), Open = close, High = close, Low = close, Close = close, Volume = 5 });
        return bars;
    }

    [Fact]
    public void StrengthRanks_TwoBarsOnOneDateDoNotBreakTheRanking()
    {
        var bars = BacktestEngine.OneBarPerDay(WithSecondBarOnLastDay(FlatDaily(MomentumDailyRules.ReturnLookback + 1), 330, 150m));
        var day = BacktestEngine.IstDate(bars[^1].CandleTime);
        var daily = new Dictionary<string, List<MarketCandle>> { ["INFY"] = bars };
        var universe = new Dictionary<DateTime, HashSet<string>> { [day] = new() { "INFY" } };

        var ranks = MomentumDailyBacktest.StrengthRanks(daily, universe);

        Assert.Equal(0, ranks[day]["INFY"]);
    }

    [Fact]
    public void OneBarPerDay_KeepsTheStandardDailyStampOverAnyOther()
    {
        var bars = BacktestEngine.OneBarPerDay(WithSecondBarOnLastDay(FlatDaily(3), 330, 150m));

        Assert.Equal(3, bars.Count);
        Assert.Equal(100m, bars[^1].Close);
        Assert.Equal(BacktestEngine.DailyStamp(BacktestEngine.IstDate(bars[^1].CandleTime)), bars[^1].CandleTime);
    }

    private static DateTime IstStamp(DateTime day, int hour, int minute) => BacktestEngine.DailyStamp(day).AddHours(hour).AddMinutes(minute);

    [Theory]
    [InlineData(20, 30)]    // loaded in European summer time
    [InlineData(19, 30)]    // loaded in European winter time
    public void OneBarPerDay_AnEveningStampBelongsToTheNextTradingDay(int hour, int minute)
    {
        var sunday = new DateTime(2025, 8, 10);
        var monday = new DateTime(2025, 8, 11);

        var bar = Assert.Single(BacktestEngine.OneBarPerDay(new List<MarketCandle> { new() { CandleTime = IstStamp(sunday, hour, minute), Close = 5m } }));

        Assert.Equal(monday, BacktestEngine.IstDate(bar.CandleTime));
        Assert.Equal(BacktestEngine.DailyStamp(monday), bar.CandleTime);
        Assert.Equal(5m, bar.Close);
    }

    [Fact]
    public void OneBarPerDay_TheEveningAndMorningRowsOfADayAreOneDayAndTheEveningRowWins()
    {
        // Real pattern in the table: "03-Aug 20:30" and "04-Aug 05:30" are both 04-Aug's candle.
        var bars = BacktestEngine.OneBarPerDay(new List<MarketCandle>
        {
            new() { CandleTime = IstStamp(new DateTime(2026, 8, 3), 20, 30), Close = 24614.9m },
            new() { CandleTime = IstStamp(new DateTime(2026, 8, 4), 5, 30), Close = 24614.8m },
            new() { CandleTime = IstStamp(new DateTime(2026, 8, 4), 20, 30), Close = 24624.65m },
            new() { CandleTime = IstStamp(new DateTime(2026, 8, 5), 5, 30), Close = 24624.6m }
        });

        Assert.Equal(new[] { new DateTime(2026, 8, 4), new DateTime(2026, 8, 5) }, bars.Select(b => BacktestEngine.IstDate(b.CandleTime)));
        Assert.Equal(new[] { 24614.9m, 24624.65m }, bars.Select(b => b.Close));
    }

    [Fact]
    public void OneBarPerDay_DoesNotChangeTheStoredRow()
    {
        var stored = new MarketCandle { CandleTime = IstStamp(new DateTime(2025, 8, 10), 20, 30), Close = 5m };

        BacktestEngine.OneBarPerDay(new[] { stored });

        Assert.Equal(IstStamp(new DateTime(2025, 8, 10), 20, 30), stored.CandleTime);
    }

    [Fact]
    public void Replay_AnEveningStampedCandleIsNotVisibleOnItsOwnTradingDay()
    {
        // The Monday candle stored as "Sunday 20:30" used to count as an earlier day, so a replay of Monday could read its final values.
        var monday = new DateTime(2025, 8, 11);
        var bars = BacktestEngine.OneBarPerDay(new List<MarketCandle>
        {
            new() { CandleTime = IstStamp(new DateTime(2025, 8, 8), 0, 0), Close = 1m },
            new() { CandleTime = IstStamp(new DateTime(2025, 8, 10), 20, 30), Close = 2m }
        });

        Assert.Equal(new[] { 1m }, bars.Where(b => BacktestEngine.IstDate(b.CandleTime) < monday).Select(b => b.Close));
    }

    // ---------------- the 15m data check ----------------

    private static List<MarketCandle> FullDay(DateTime day, decimal open = 100m, int bars = 25)
    {
        var list = new List<MarketCandle>();
        for (int i = 0; i < bars; i++) list.Add(Bar15(day, i, i == 0 ? open : 100m, 101m, 99m, 100m));
        return list;
    }

    private static readonly List<MarketCandle> OneDaily = new() { DailyBar(OrbDay, 100m, 101m, 99m, 100m) };

    [Fact]
    public void DataCheck_AFullDayMatchingTheDailyOpenIsFine() =>
        Assert.Equal(new IntradayDataCheck(1, 0, 0), BacktestDataCheck.Check15m(FullDay(OrbDay), OneDaily, OrbDay, OrbDay));

    [Fact]
    public void DataCheck_AfternoonBarsStoredAsMorningAreBad()
    {
        // 11 bars ending 11:45, and a "09:15" open that isn't the day's open.
        var check = BacktestDataCheck.Check15m(FullDay(OrbDay, open: 103m, bars: 11), OneDaily, OrbDay, OrbDay);

        Assert.Equal(new IntradayDataCheck(1, 1, 1), check);
    }

    [Fact]
    public void DataCheck_FullLengthButWrongOpenIsBad() =>
        Assert.Equal(1, BacktestDataCheck.Check15m(FullDay(OrbDay, open: 101m), OneDaily, OrbDay, OrbDay).BadDays);

    [Fact]
    public void DataCheck_OnlyTheCloseBarMissingIsCountedButNotBad() =>
        Assert.Equal(new IntradayDataCheck(1, 0, 1), BacktestDataCheck.Check15m(FullDay(OrbDay, bars: 24), OneDaily, OrbDay, OrbDay));

    [Fact]
    public void DataCheck_DaysOutsideTheTestAreIgnored() =>
        Assert.Equal(0, BacktestDataCheck.Check15m(FullDay(OrbDay, bars: 5), OneDaily, OrbDay.AddDays(1), OrbDay.AddDays(2)).Days);

    private static BacktestSummary SummaryWithVerdict(string verdict) => new()
    {
        Verdict = verdict, VerdictReason = "original",
        Sides = new List<BacktestSideResult> { new() { Verdict = verdict, VerdictReason = "original" } }
    };

    [Theory]
    [InlineData("FAIL")]
    [InlineData("PASS")]
    public void DataCheck_TooManyBadDaysMakesAnyVerdictNotProven(string verdict)
    {
        var summary = SummaryWithVerdict(verdict);

        BacktestDataCheck.Apply(summary, new[] { new IntradayDataCheck(100, 40, 40) });

        Assert.Equal("NOT_PROVEN", summary.Verdict);
        Assert.Equal("NOT_PROVEN", summary.Sides[0].Verdict);
        Assert.Contains("40", summary.DataWarning);
        Assert.Equal(40, summary.Coverage.StockDaysBad);
    }

    [Fact]
    public void DataCheck_ASmallShareOfBadDaysLeavesTheVerdictAlone()
    {
        var summary = SummaryWithVerdict("FAIL");

        BacktestDataCheck.Apply(summary, new[] { new IntradayDataCheck(1000, 5, 0) });

        Assert.Equal("FAIL", summary.Verdict);
        Assert.Null(summary.DataWarning);
        Assert.Equal(1000, summary.Coverage.StockDaysChecked);
    }

    // ---------------- daily momentum: entry ----------------

    private static MomentumDailyRules.EntryFacts GoodFacts() => new(Close: 110m, PriorHigh: 105m, YearHigh: 111m, VolumeMultiple: 2m, StrengthRank: 90, NiftyAboveAverage: true);

    [Fact]
    public void Momentum_AllRulesMetIsASignal() => Assert.Null(MomentumDailyRules.EntryBlock(GoodFacts()));

    [Fact]
    public void Momentum_EachRuleOnItsOwnBlocksTheSignal()
    {
        Assert.NotNull(MomentumDailyRules.EntryBlock(GoodFacts() with { NiftyAboveAverage = false }));
        Assert.NotNull(MomentumDailyRules.EntryBlock(GoodFacts() with { StrengthRank = 79 }));
        Assert.NotNull(MomentumDailyRules.EntryBlock(GoodFacts() with { YearHigh = 120m }));
        Assert.NotNull(MomentumDailyRules.EntryBlock(GoodFacts() with { PriorHigh = 110m }));
        Assert.NotNull(MomentumDailyRules.EntryBlock(GoodFacts() with { VolumeMultiple = 1.49m }));
    }

    [Fact]
    public void Momentum_EntersAtTheNextOpenWithSlippageAndNoFixedTarget()
    {
        var bars = BreakoutBars();
        var signal = Assert.Single(MomentumDailyBacktest.GenerateSignals("T", bars, new HashSet<int>(), MomentumCtx(slippage: 0.1m)));

        var signalDay = BacktestEngine.IstDate(bars[^2].CandleTime);
        var entryDay = BacktestEngine.IstDate(bars[^1].CandleTime);
        Assert.Equal(entryDay, signal.SessionDate);
        Assert.Equal(110.5m, signal.EntryRaw);
        Assert.Equal(110.61m, signal.EntryFill);
        Assert.Equal(0m, signal.Target);
        Assert.Equal(BacktestEngine.DailyStamp(entryDay).AddMinutes(9 * 60 + 15), signal.EntryTimeUtc);
        Assert.Equal(BacktestEngine.DailyStamp(signalDay).AddMinutes(15 * 60 + 30), signal.SignalTimeUtc);
        Assert.True(signal.EntryTimeUtc > signal.SignalTimeUtc);
        Assert.Equal(MomentumDailyBacktest.SubType, signal.SubType);
    }

    [Theory]
    [InlineData(50, true)]     // weak rank
    [InlineData(90, false)]    // NIFTY below its average
    public void Momentum_MarketAndStrengthFiltersBlockInTheReplay(int rank, bool niftyAbove) =>
        Assert.Empty(MomentumDailyBacktest.GenerateSignals("T", BreakoutBars(), new HashSet<int>(), MomentumCtx(rank, niftyAbove)));

    [Fact]
    public void Momentum_LowVolumeBreakoutIsNotASignal() =>
        Assert.Empty(MomentumDailyBacktest.GenerateSignals("T", BreakoutBars(breakoutVolume: 1000), new HashSet<int>(), MomentumCtx()));

    [Fact]
    public void Momentum_OpenGappedTooFarIsMarkedSkipped()
    {
        var signal = Assert.Single(MomentumDailyBacktest.GenerateSignals("T", BreakoutBars(nextOpen: 113m), new HashSet<int>(), MomentumCtx()));
        Assert.Equal(BacktestEngine.DriftSkipReason, signal.SkipReason);
    }

    [Fact]
    public void Momentum_ASuspectedSplitInTheLookbackBlocksTheSignal()
    {
        var bars = BreakoutBars();
        var signal = Assert.Single(MomentumDailyBacktest.GenerateSignals("T", bars, new HashSet<int> { Session(LastDay(bars)) - 5 }, MomentumCtx()));
        Assert.Contains("split", signal.SkipReason);
    }

    [Fact]
    public void Momentum_ChangingAFutureBarNeverChangesAnEarlierSignal()
    {
        var bars = BreakoutBars();
        var before = MomentumDailyBacktest.GenerateSignals("T", bars, new HashSet<int>(), MomentumCtx()).Single();
        var withFuture = BreakoutBars();
        var day = LastDay(withFuture);
        for (int i = 0; i < 5; i++)
        {
            day = NextWeekday(day);
            withFuture.Add(DailyBar(day, 500m, 900m, 1m, 2m, 99_000_000));
        }

        var after = MomentumDailyBacktest.GenerateSignals("T", withFuture, new HashSet<int>(), MomentumCtx())
            .First(s => s.SessionDate == before.SessionDate);

        Assert.Equal(before.EntryRaw, after.EntryRaw);
        Assert.Equal(before.StopLoss, after.StopLoss);
        Assert.Equal(before.SignalDailyAtr, after.SignalDailyAtr);
        Assert.Equal(before.Score, after.Score);
    }

    [Fact]
    public void StrengthRanks_Top20PercentOfTheListGetsAtLeast80()
    {
        var daily = new Dictionary<string, List<MarketCandle>>();
        DateTime day = default;
        for (int s = 0; s < 5; s++)
        {
            var bars = FlatDaily(MomentumDailyRules.ReturnLookback + 1);
            bars[^1].Close = 100m + s;
            day = BacktestEngine.IstDate(bars[^1].CandleTime);
            daily[$"S{s}"] = bars;
        }
        var universe = new Dictionary<DateTime, HashSet<string>> { [day] = daily.Keys.ToHashSet() };

        var ranks = MomentumDailyBacktest.StrengthRanks(daily, universe)[day];

        Assert.Equal(1, ranks.Count(r => r.Value >= MomentumDailyRules.MinStrengthRank));
        Assert.True(ranks["S4"] >= MomentumDailyRules.MinStrengthRank);
        Assert.Equal(0, ranks["S0"]);
    }

    // ---------------- daily momentum: exits ----------------

    [Fact]
    public void MomentumLevels_AreAtrBasedWithinTheLiveCaps()
    {
        Assert.Equal((96m, 94m), MomentumDailyRules.Levels(100m, 2m));
        Assert.Equal((92m, 88m), MomentumDailyRules.Levels(100m, 10m));      // capped at 8% and 12%
        Assert.Equal((98m, 97m), MomentumDailyRules.Levels(100m, 0.1m));     // ATR floored at 1% of the price
    }

    [Fact]
    public void MomentumExit_CloseBelowStopSellsAtTheNextOpen()
    {
        var bars = Walk(100m, 95.8m, 95m);       // stop 96; day 2 closes below it, low stays above the emergency stop at 94
        var exit = MomentumExit(bars);

        Assert.Equal(MomentumDailyRules.StopCategory, exit.Category);
        Assert.Equal(95m, exit.ExitRaw);
        Assert.Equal(BacktestEngine.IstDate(bars[2].CandleTime), exit.SessionDate);
        Assert.Equal(2, exit.SessionsHeld);
    }

    [Fact]
    public void MomentumExit_EmergencyStopFillsAtTheLevelInsideTheDay()
    {
        var bars = Walk(100m, 100m);
        bars[1].Low = 93m;

        var exit = MomentumExit(bars);

        Assert.Equal("Emergency stop", exit.Category);
        Assert.Equal(94m, exit.ExitRaw);
    }

    [Fact]
    public void MomentumExit_GapThroughTheEmergencyStopFillsAtTheOpen()
    {
        var bars = Walk(100m, 90m);
        var exit = MomentumExit(bars);

        Assert.Equal("Emergency stop", exit.Category);
        Assert.Equal(90m, exit.ExitRaw);
        Assert.Equal(BacktestEngine.DailyStamp(exit.SessionDate).AddMinutes(9 * 60 + 15), exit.ExitTimeUtc);
    }

    [Fact]
    public void MomentumExit_AnEmergencyInsideTheDayFreesTheSlotOnlyAtTheClose()
    {
        var bars = Walk(100m, 100m);
        bars[1].Low = 93m;
        var exit = MomentumExit(bars);
        Assert.Equal(BacktestEngine.DailyStamp(exit.SessionDate).AddMinutes(15 * 60 + 30), exit.ExitTimeUtc);
    }

    [Fact]
    public void MomentumTrail_StartsOnlyAfterTwoAtrAndNeverFalls()
    {
        var t = new MomentumDailyRules.OpenTrade { EntryRaw = 100m, EntryFill = 100m, Stop = 96m, Atr = 2m, HighestClose = 100m };

        Assert.Null(MomentumDailyRules.AtClose(t, 103.9m, 1));
        Assert.Null(t.Trail);

        Assert.Null(MomentumDailyRules.AtClose(t, 104m, 2));
        Assert.Equal(98m, t.Trail);              // 104 - 3 x 2

        Assert.Null(MomentumDailyRules.AtClose(t, 110m, 3));
        Assert.Equal(104m, t.Trail);

        Assert.Null(MomentumDailyRules.AtClose(t, 106m, 4));
        Assert.Equal(104m, t.Trail);             // the price fell, the trail didn't
    }

    [Fact]
    public void MomentumTrail_ACloseAtTheTrailSellsAtTheNextOpen()
    {
        var bars = Walk(100m, 110m, 104m, 103m);
        var exit = MomentumExit(bars);

        Assert.Equal(MomentumDailyRules.TrailCategory, exit.Category);
        Assert.Equal(103m, exit.ExitRaw);
    }

    [Fact]
    public void MomentumExit_OutAfterTenSessionsOnlyIfNotAboveTheEntry()
    {
        var flat = new decimal[12];
        Array.Fill(flat, 99.9m);
        var exit = MomentumExit(Walk(flat));
        Assert.Equal(MomentumDailyRules.NoProgressCategory, exit.Category);
        Assert.Equal(11, exit.SessionsHeld);

        var up = new decimal[12];
        Array.Fill(up, 101m);
        Assert.True(MomentumExit(Walk(up)).EndOfData);
    }

    [Fact]
    public void MomentumExit_TheTenSessionCheckHappensOnce()
    {
        var t = new MomentumDailyRules.OpenTrade { EntryRaw = 100m, EntryFill = 100m, Stop = 90m, Atr = 5m, HighestClose = 100m };

        Assert.Null(MomentumDailyRules.AtClose(t, 101m, MomentumDailyRules.NoProgressSessions));
        Assert.Null(MomentumDailyRules.AtClose(t, 99m, MomentumDailyRules.NoProgressSessions + 1));
    }

    [Fact]
    public void MomentumExit_OutAfterSixtySessions()
    {
        var closes = new decimal[MomentumDailyRules.MaxSessions + 2];
        Array.Fill(closes, 101m);
        var exit = MomentumExit(Walk(closes));

        Assert.Equal(MomentumDailyRules.MaxDaysCategory, exit.Category);
        Assert.Equal(MomentumDailyRules.MaxSessions + 1, exit.SessionsHeld);
    }

    [Fact]
    public void MomentumExit_TradeHeldAcrossASplitIsSkipped()
    {
        var bars = Walk(100m, 100m, 100m);
        var exit = MomentumDailyBacktest.SimulateExit(bars, MomentumSignal(), new HashSet<int> { Session(BacktestEngine.IstDate(bars[2].CandleTime)) }, Session, out var skip);

        Assert.Null(exit);
        Assert.Contains("split", skip);
    }

    // ---------------- intraday opening range: signals ----------------

    [Fact]
    public void Orb_RangeIsTheFirstTwoBars()
    {
        var range = IntradayOrbRules.Range(new[] { Bar15(OrbDay, 0, 100m, 102m, 99m, 100m, 500), Bar15(OrbDay, 1, 100m, 101m, 98m, 100m, 700), Bar15(OrbDay, 2, 100m, 200m, 1m, 100m, 9000) });

        Assert.Equal(new IntradayOrbRules.OpeningRange(102m, 98m, 1200), range);
        Assert.Null(IntradayOrbRules.Range(new[] { Bar15(OrbDay, 0, 100m, 102m, 99m, 100m) }));
    }

    [Theory]
    [InlineData(false, 101, 100, 105, 100, true)]
    [InlineData(false, 99, 100, 105, 100, false)]    // NIFTY below yesterday's close
    [InlineData(false, 101, 100, 95, 100, false)]    // stock below its average
    [InlineData(true, 99, 100, 95, 100, true)]
    [InlineData(true, 101, 100, 95, 100, false)]
    [InlineData(true, 99, 100, 105, 100, false)]
    public void Orb_DirectionMustAgreeWithNiftyAndTheStocksTrend(bool isShort, decimal niftyNow, decimal niftyPrev, decimal prevClose, decimal average, bool expected) =>
        Assert.Equal(expected, IntradayOrbRules.DirectionAllowed(isShort, niftyNow, niftyPrev, prevClose, average));

    [Theory]
    [InlineData(100.0, 99.5, null, null)]     // 0.5%: too close
    [InlineData(100.0, 99.0, 99.0, 102.0)]    // 1%: target 2R
    [InlineData(100.0, 97.0, null, null)]     // 3%: too far
    [InlineData(100.0, 101.0, null, null)]    // already past the stop
    public void Orb_LongStopMustBeWithinLimits(double entryValue, double stopValue, double? expectedStop, double? expectedTarget)
    {
        var levels = IntradayOrbRules.Levels(false, (decimal)entryValue, new IntradayOrbRules.OpeningRange(105m, (decimal)stopValue, 0));
        Assert.Equal(expectedStop == null ? null : (decimal)expectedStop, levels?.Stop);
        Assert.Equal(expectedTarget == null ? null : (decimal)expectedTarget, levels?.Target);
    }

    [Fact]
    public void Orb_ShortLevelsMirrorTheLong()
    {
        var levels = IntradayOrbRules.Levels(true, 100m, new IntradayOrbRules.OpeningRange(101m, 95m, 0));
        Assert.Equal((101m, 98m), levels);
    }

    private static readonly List<MarketCandle> UpDaily = OrbDaily(90m, 100m);
    private static readonly List<MarketCandle> DownDaily = OrbDaily(110m, 100m);

    [Fact]
    public void Orb_FirstCloseAboveTheRangeWithVolumeEntersAtTheNextBar()
    {
        var bars = OrbBars(5000, Bar15(OrbDay, 2, 101m, 103m, 100m, 102.5m), Bar15(OrbDay, 3, 100m, 101m, 99.5m, 100.5m), Bar15(OrbDay, 4, 100.5m, 101m, 100m, 100.5m));

        var s = Assert.Single(OrbSignals(bars, UpDaily, OrbCtx()));

        Assert.False(s.IsShort);
        Assert.Equal("ORB_LONG", s.SubType);
        Assert.Equal(100m, s.EntryRaw);
        Assert.Equal(98m, s.StopLoss);
        Assert.Equal(104m, s.Target);
        Assert.Equal(Slot(OrbDay, 3), s.EntryTimeUtc);
        Assert.Equal(Slot(OrbDay, 3), s.SignalTimeUtc);
        Assert.Null(s.SkipReason);
        Assert.NotNull(s.Exit);
    }

    [Fact]
    public void Orb_UsualVolumeIsNotEnough()
    {
        var bars = OrbBars(1500, Bar15(OrbDay, 2, 101m, 103m, 100m, 102.5m), Bar15(OrbDay, 3, 101m, 104m, 101m, 103m));
        Assert.Empty(OrbSignals(bars, UpDaily, OrbCtx()));
    }

    [Fact]
    public void Orb_ABreakoutAfterTheCutOffIsNotASignal()
    {
        // Slot 12 starts 12:15 and closes 12:30 (the last allowed); slot 13 closes 12:45.
        var late = OrbBars(5000, Bar15(OrbDay, 13, 101m, 103m, 100m, 102.5m), Bar15(OrbDay, 14, 101m, 104m, 101m, 103m));
        Assert.Empty(OrbSignals(late, UpDaily, OrbCtx()));

        var lastAllowed = OrbBars(5000, Bar15(OrbDay, 12, 101m, 103m, 100m, 102.5m), Bar15(OrbDay, 13, 101m, 104m, 101m, 103m));
        Assert.Single(OrbSignals(lastAllowed, UpDaily, OrbCtx()));
    }

    [Fact]
    public void Orb_ATooWideRangeIsMarkedSkipped()
    {
        var bars = OrbBars(5000, Bar15(OrbDay, 2, 110m, 111m, 109m, 110m), Bar15(OrbDay, 3, 110m, 112m, 109m, 111m));
        var s = Assert.Single(OrbSignals(bars, UpDaily, OrbCtx()));
        Assert.Contains("stop too", s.SkipReason);
    }

    [Fact]
    public void Orb_ShortNeedsTheShortSideToBeTested()
    {
        var bars = OrbBars(5000, Bar15(OrbDay, 2, 99m, 100m, 96m, 97m), Bar15(OrbDay, 3, 100m, 100.5m, 99m, 99.5m), Bar15(OrbDay, 4, 99.5m, 100m, 99m, 99.5m));

        Assert.Empty(OrbSignals(bars, DownDaily, OrbCtx(niftyNow: 99m)));
        var s = Assert.Single(OrbSignals(bars, DownDaily, OrbCtx(buy: false, shortSide: true, niftyNow: 99m)));
        Assert.True(s.IsShort);
        Assert.Equal("ORB_SHORT", s.SubType);
        Assert.Equal(102m, s.StopLoss);
        Assert.Equal(96m, s.Target);
    }

    [Fact]
    public void Orb_ADayGetsOnlyItsFirstBreakout()
    {
        // The first close beyond the range is down; a short-only test takes it, and a later up close isn't a second trade.
        var bars = OrbBars(5000, Bar15(OrbDay, 2, 99m, 100m, 96m, 97m), Bar15(OrbDay, 3, 99m, 104m, 99m, 103m), Bar15(OrbDay, 4, 103m, 104m, 102m, 103m));

        Assert.Empty(OrbSignals(bars, UpDaily, OrbCtx()));   // long-only: the first breakout was down, so no later long
    }

    [Fact]
    public void Orb_TheUsualVolumeLeavesOutTodayAndTheSignalIgnoresLaterBars()
    {
        var before = OrbBars(5000, Bar15(OrbDay, 2, 101m, 103m, 100m, 102.5m), Bar15(OrbDay, 3, 101m, 104m, 101m, 103m));
        var after = OrbBars(5000, Bar15(OrbDay, 2, 101m, 103m, 100m, 102.5m), Bar15(OrbDay, 3, 101m, 104m, 101m, 103m), Bar15(OrbDay, 4, 500m, 900m, 1m, 2m, 99_000_000));

        var a = Assert.Single(OrbSignals(before, UpDaily, OrbCtx()));
        var b = Assert.Single(OrbSignals(after, UpDaily, OrbCtx()));

        Assert.Equal(a.EntryRaw, b.EntryRaw);
        Assert.Equal(a.StopLoss, b.StopLoss);
        Assert.Equal(a.Score, b.Score);
    }

    // ---------------- intraday opening range: exits ----------------

    [Fact]
    public void OrbExit_LongTargetFillsAtTheLevel()
    {
        var exit = IntradayOrbBacktest.SimulateExit(new[] { Bar15(OrbDay, 3, 100m, 103m, 99.5m, 102m) }, OrbSignal(false, 100m, 99m, 102m));
        Assert.Equal("Target", exit.Category);
        Assert.Equal(102m, exit.ExitRaw);
    }

    [Fact]
    public void OrbExit_ABarTouchingBothCountsAsAStop()
    {
        var longExit = IntradayOrbBacktest.SimulateExit(new[] { Bar15(OrbDay, 3, 100m, 103m, 98m, 102m) }, OrbSignal(false, 100m, 99m, 102m));
        Assert.Equal("Stop loss", longExit.Category);
        Assert.Equal(99m, longExit.ExitRaw);

        var shortExit = IntradayOrbBacktest.SimulateExit(new[] { Bar15(OrbDay, 3, 100m, 102m, 97m, 98m) }, OrbSignal(true, 100m, 101m, 98m));
        Assert.Equal("Stop loss", shortExit.Category);
        Assert.Equal(101m, shortExit.ExitRaw);
    }

    [Fact]
    public void OrbExit_AGapThroughTheStopFillsAtTheOpen()
    {
        var exit = IntradayOrbBacktest.SimulateExit(new[] { Bar15(OrbDay, 3, 97m, 98m, 96m, 97m) }, OrbSignal(false, 100m, 99m, 102m));
        Assert.Equal("Stop loss", exit.Category);
        Assert.Equal(97m, exit.ExitRaw);
    }

    [Fact]
    public void OrbExit_ForcedOutAtTheSquareOffBarsOpen()
    {
        // Slot 24 starts at 15:15.
        var exit = IntradayOrbBacktest.SimulateExit(new[] { Bar15(OrbDay, 23, 100m, 100.5m, 99.5m, 100m), Bar15(OrbDay, 24, 100.2m, 110m, 90m, 100m) }, OrbSignal(false, 100m, 99m, 102m));

        Assert.Equal("Square-off", exit.Category);
        Assert.Equal(100.2m, exit.ExitRaw);
        Assert.Equal(0, exit.SessionsHeld);
    }

    [Fact]
    public void OrbExit_MissingSquareOffBarUsesTheDaysLastClose()
    {
        var exit = IntradayOrbBacktest.SimulateExit(new[] { Bar15(OrbDay, 20, 100m, 100.5m, 99.5m, 100.1m) }, OrbSignal(false, 100m, 99m, 102m));

        Assert.Equal("Square-off (missing bar)", exit.Category);
        Assert.Equal(100.1m, exit.ExitRaw);
    }

    [Fact]
    public void OrbExit_ShortGainIsPositiveWhenThePriceFalls()
    {
        var exit = IntradayOrbBacktest.SimulateExit(new[] { Bar15(OrbDay, 3, 100m, 100.5m, 98m, 99m) }, OrbSignal(true, 100m, 101m, 98m));

        Assert.Equal("Target", exit.Category);
        Assert.Equal(98m, exit.ExitRaw);
        Assert.True(exit.MfePct > 0m);
        Assert.True(exit.MaePct < 0m);
    }

    // ---------------- the portfolio limits ----------------

    private static BacktestSignal PortfolioSignal(string symbol, DateTime day, decimal exit = 100m, int holdDays = 30) => new()
    {
        Symbol = symbol,
        SessionDate = day,
        SignalTimeUtc = BacktestEngine.DailyStamp(day),
        EntryTimeUtc = BacktestEngine.DailyStamp(day).AddMinutes(9 * 60 + 15),
        IsLiveCandidate = true,
        PolicyMaxPositions = int.MaxValue,
        EntryRaw = 100m,
        EntryFill = 100m,
        StopLoss = 90m,
        Exit = new BacktestExit(BacktestEngine.DailyStamp(day).AddDays(holdDays), day.AddDays(holdDays), exit, "x", "x", holdDays, 0m, 0m, false)
    };

    private static PortfolioResult RunPortfolio(IEnumerable<BacktestSignal> signals, PortfolioSettings settings, Func<string, string?>? sectorOf = null) =>
        BacktestPortfolio.Run(signals, s => s.Exit, settings, sectorOf ?? (_ => null));

    [Fact]
    public void Portfolio_SectorCapLimitsOpenPositionsInASector()
    {
        var day = new DateTime(2025, 1, 6);
        var signals = new[] { PortfolioSignal("A", day), PortfolioSignal("B", day), PortfolioSignal("C", day), PortfolioSignal("D", day) };
        var settings = new PortfolioSettings { MaxPositions = 10, MaxTradesPerDay = 10, MaxPerSector = 2, RiskSizing = false, AmountPerTrade = 1000m };

        var result = RunPortfolio(signals, settings, s => s == "D" ? null : "BANK");

        Assert.Equal(new[] { "A", "B", "D" }, result.Trades.Select(t => t.Symbol).OrderBy(x => x));
        Assert.Equal(1, result.Skipped["max positions in the sector"]);
    }

    [Fact]
    public void Portfolio_NoSectorCapByDefault()
    {
        var day = new DateTime(2025, 1, 6);
        var signals = new[] { PortfolioSignal("A", day), PortfolioSignal("B", day), PortfolioSignal("C", day) };
        var settings = new PortfolioSettings { MaxPositions = 10, MaxTradesPerDay = 10, RiskSizing = false, AmountPerTrade = 1000m };

        Assert.Equal(3, RunPortfolio(signals, settings, _ => "BANK").Trades.Count);
    }

    [Fact]
    public void Portfolio_DrawdownHaltBlocksThenReleasesAfterTheCoolOff()
    {
        var d0 = new DateTime(2025, 1, 6);
        var signals = new[]
        {
            PortfolioSignal("LOSS", d0, exit: 50m, holdDays: 1),            // loses 50% of ₹10,000 on ₹30,000 capital
            PortfolioSignal("BLOCKED", d0.AddDays(2), holdDays: 1),
            PortfolioSignal("STILL", d0.AddDays(20), holdDays: 1),
            PortfolioSignal("RELEASED", d0.AddDays(40), holdDays: 1)
        };
        var settings = new PortfolioSettings
        {
            Capital = 30_000m, MaxPositions = 10, MaxTradesPerDay = 10, DailyLossLimit = 1_000_000m, RiskSizing = false, AmountPerTrade = 10_000m,
            DrawdownHaltPct = 10m, DrawdownResumePct = 5m, DrawdownCoolOffDays = 30
        };

        var result = RunPortfolio(signals, settings);

        Assert.Equal(new[] { "LOSS", "RELEASED" }, result.Trades.Select(t => t.Symbol));
        Assert.Equal(2, result.Skipped["account too far below its peak"]);
    }

    [Fact]
    public void Portfolio_DrawdownHaltIsOffByDefault()
    {
        var d0 = new DateTime(2025, 1, 6);
        var signals = new[] { PortfolioSignal("LOSS", d0, exit: 50m, holdDays: 1), PortfolioSignal("NEXT", d0.AddDays(2), holdDays: 1) };
        var settings = new PortfolioSettings { Capital = 30_000m, MaxPositions = 10, MaxTradesPerDay = 10, DailyLossLimit = 1_000_000m, RiskSizing = false, AmountPerTrade = 10_000m };

        Assert.Equal(2, RunPortfolio(signals, settings).Trades.Count);
    }

    // ---------------- the verdict ----------------

    private static BacktestKpis Kpis(int trades, decimal expectancy) => new() { Trades = trades, ExpectancyR = expectancy };
    private static List<BacktestGroupRow> BothHalvesPositive() => new() { new BacktestGroupRow { ExpectancyR = 0.3m }, new BacktestGroupRow { ExpectancyR = 0.3m } };

    private static List<BacktestTrade> Trades(int count, decimal r, string symbol = "S")
    {
        var day = new DateTime(2025, 1, 6);
        return Enumerable.Range(0, count).Select(i => new BacktestTrade
        {
            Symbol = $"{symbol}{i}", EntryTime = BacktestEngine.DailyStamp(day.AddDays(i)), RMultiple = r, NetPnl = r * 100m
        }).ToList();
    }

    [Fact]
    public void Verdict_ScoreRuleStillNeedsOnly30Trades() =>
        Assert.Equal("PASS", BacktestPortfolio.Verdict(Kpis(30, 0.3m), BothHalvesPositive()).Verdict);

    [Fact]
    public void RuleVerdict_NeedsAHundredTrades() =>
        Assert.Equal("NOT_PROVEN", BacktestPortfolio.RuleVerdict(Kpis(99, 0.3m), BothHalvesPositive(), Trades(99, 0.3m), new List<BacktestLogicCheck>(), 100).Verdict);

    [Fact]
    public void RuleVerdict_PassesWhenEverythingHolds()
    {
        var (verdict, reason) = BacktestPortfolio.RuleVerdict(Kpis(100, 0.3m), BothHalvesPositive(), Trades(100, 0.3m), new List<BacktestLogicCheck>(), 100);
        Assert.Equal("PASS", verdict);
        Assert.Contains("Without the best stock", reason);
    }

    [Fact]
    public void RuleVerdict_AFailedRuleCheckBlocksAPass()
    {
        var checks = new List<BacktestLogicCheck> { new() { Rule = "r", Checked = 1, Failed = 1 } };
        Assert.Equal("NOT_PROVEN", BacktestPortfolio.RuleVerdict(Kpis(100, 0.3m), BothHalvesPositive(), Trades(100, 0.3m), checks, 100).Verdict);
    }

    [Fact]
    public void RuleVerdict_AResultThatDependsOnOneStockIsNotAPass()
    {
        var trades = Trades(100, -0.1m);
        trades[0].Symbol = "HERO";
        trades[0].RMultiple = 50m;
        trades[0].NetPnl = 5000m;

        var (verdict, reason) = BacktestPortfolio.RuleVerdict(Kpis(100, 0.4m), BothHalvesPositive(), trades, new List<BacktestLogicCheck>(), 100);

        Assert.Equal("NOT_PROVEN", verdict);
        Assert.Contains("HERO", reason);
    }

    // ---------------- rule checks ----------------

    private static BacktestTrade AuditTrade(string symbol, DateTime entryUtc, DateTime exitUtc, string side = BacktestSide.Buy, decimal target = 0m, string? sector = null, int sessions = 0) => new()
    {
        Symbol = symbol, Side = side, EntryTime = entryUtc, SignalTime = entryUtc, ExitTime = exitUtc, EntryRaw = 100m, EntryPrice = 100m, ExitRaw = 101m, ExitPrice = 101m,
        StopLoss = side == BacktestSide.Short ? 105m : 95m, Target = target, Quantity = 1, GrossPnl = side == BacktestSide.Short ? -1m : 1m,
        NetPnl = side == BacktestSide.Short ? -1m : 1m, ExitCategory = "x", Sector = sector, SessionsHeld = sessions
    };

    [Fact]
    public void Audit_NoFixedTargetIsNotAFailure()
    {
        var entry = BacktestEngine.DailyStamp(new DateTime(2025, 1, 6)).AddMinutes(9 * 60 + 15);
        var checks = BacktestLogicAudit.Run(new[] { AuditTrade("A", entry, entry.AddDays(3)) }, "15:00", "15:15");
        Assert.Equal(0, checks.Single(c => c.Rule.StartsWith("Stop below")).Failed);
    }

    [Fact]
    public void AuditLimits_CountBreaksOfEachLimit()
    {
        var entry = BacktestEngine.DailyStamp(new DateTime(2025, 1, 6)).AddMinutes(9 * 60 + 15);
        var trades = new[]
        {
            AuditTrade("A", entry, entry.AddDays(3), sector: "BANK"),
            AuditTrade("B", entry, entry.AddDays(3), sector: "BANK"),
            AuditTrade("C", entry, entry.AddDays(3), sector: "BANK")
        };

        var checks = BacktestLogicAudit.RunRuleLimits(trades, maxPositions: 2, maxPerDay: 2, maxPerSector: 2, intradayLastSignal: null);

        Assert.True(checks.Single(c => c.Rule.Contains("positions open")).Failed > 0);
        Assert.True(checks.Single(c => c.Rule.Contains("new entries")).Failed > 0);
        Assert.True(checks.Single(c => c.Rule.Contains("sector")).Failed > 0);
        Assert.DoesNotContain(checks, c => c.Rule.Contains("same day"));
    }

    [Fact]
    public void AuditLimits_IntradayRuleChecksTheCutOffAndTheSameDayClose()
    {
        var at = new Func<int, DateTime>(minutes => BacktestEngine.DailyStamp(new DateTime(2025, 1, 6)).AddMinutes(minutes));
        var ok = AuditTrade("A", at(12 * 60 + 30), at(15 * 60 + 15));
        var late = AuditTrade("B", at(12 * 60 + 45), at(15 * 60 + 15));
        var overnight = AuditTrade("C", at(10 * 60), at(15 * 60 + 15).AddDays(1), sessions: 1);

        var checks = BacktestLogicAudit.RunRuleLimits(new[] { ok, late, overnight }, 5, 5, 0, IntradayOrbRules.LastSignalText);

        Assert.Equal(1, checks.Single(c => c.Rule.StartsWith("No signal after")).Failed);
        Assert.Equal(1, checks.Single(c => c.Rule.Contains("same day")).Failed);
    }

    // ---------------- signal to portfolio to rule checks ----------------

    private static PortfolioSettings RuleSettings(int maxPositions, int maxPerDay, int maxPerSector) => new()
    {
        Capital = 100_000m, AmountPerTrade = 20_000m, RiskPct = 0.5m, MaxPositions = maxPositions, MaxTradesPerDay = maxPerDay,
        MaxPerSector = maxPerSector, DailyLossLimit = 10_000m, SlippagePct = 0.1m
    };

    [Fact]
    public void Pipeline_AMomentumTradeObeysEveryRuleCheck()
    {
        var bars = BreakoutBars();
        var day = LastDay(bars);
        foreach (decimal close in new[] { 112m, 114m, 118m, 125m, 120m, 113m, 112m })
        {
            day = NextWeekday(day);
            bars.Add(DailyBar(day, close, close + 0.5m, close - 0.5m, close));
        }
        var signals = MomentumDailyBacktest.GenerateSignals("T", bars, new HashSet<int>(), MomentumCtx(slippage: 0.1m));
        var first = signals.First();
        first.Exit = MomentumDailyBacktest.SimulateExit(bars, first, new HashSet<int>(), Session, out _);

        var result = BacktestPortfolio.Run(new[] { first }, s => s.Exit, RuleSettings(5, 2, 2), _ => "BANK");
        var trade = Assert.Single(result.Trades);
        var checks = BacktestLogicAudit.Run(result.Trades, IntradayOrbRules.ShortCutoffText, IntradayOrbRules.SquareOffText);
        checks.AddRange(BacktestLogicAudit.RunRuleLimits(result.Trades, 5, 2, 2, null));

        Assert.All(checks, c => Assert.Equal(0, c.Failed));
        Assert.Equal(0m, trade.Target);
        Assert.Equal(MomentumDailyBacktest.SubType, trade.SubType);
        Assert.StartsWith("RET6M:", trade.Factors);
        Assert.True(trade.Charges > 0m);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Pipeline_AnOrbTradeObeysEveryRuleCheckAndPaysIntradayCharges(bool isShort)
    {
        var bars = isShort
            ? OrbBars(5000, Bar15(OrbDay, 2, 99m, 100m, 96m, 97m), Bar15(OrbDay, 3, 100m, 100.5m, 95m, 95.5m), Bar15(OrbDay, 4, 95.5m, 96m, 95m, 95.5m))
            : OrbBars(5000, Bar15(OrbDay, 2, 101m, 103m, 100m, 102.5m), Bar15(OrbDay, 3, 100m, 105m, 99.5m, 104.5m), Bar15(OrbDay, 4, 104.5m, 105m, 104m, 104.5m));
        var ctx = isShort ? OrbCtx(buy: false, shortSide: true, niftyNow: 99m) : OrbCtx();
        var signals = OrbSignals(bars, isShort ? DownDaily : UpDaily, ctx);

        var result = BacktestPortfolio.Run(signals, s => s.Exit, RuleSettings(3, 3, 0), _ => null);
        var trade = Assert.Single(result.Trades);
        var checks = BacktestLogicAudit.Run(result.Trades, IntradayOrbRules.ShortCutoffText, IntradayOrbRules.SquareOffText);
        checks.AddRange(BacktestLogicAudit.RunRuleLimits(result.Trades, 3, 3, 0, IntradayOrbRules.LastSignalText));

        Assert.All(checks, c => Assert.Equal(0, c.Failed));
        Assert.Equal("Target", trade.ExitCategory);
        Assert.True(trade.NetPnl > 0m);
        Assert.Equal(0, trade.SessionsHeld);
    }

    // ---------------- runs and params ----------------

    [Theory]
    [InlineData(null, BacktestStrategy.Score)]
    [InlineData("", BacktestStrategy.Score)]
    [InlineData("momentum_daily", BacktestStrategy.MomentumDaily)]
    public void Strategy_BlankMeansScoreSoOldRunsStillRead(string? stored, string expected) =>
        Assert.Equal(expected, BacktestStrategy.Normalize(stored));

    [Fact]
    public void Strategy_AnOldRunsStoredParamsLoadWithoutAStrategy()
    {
        var p = JsonSerializer.Deserialize<BacktestParams>("{\"side\":\"BUY\",\"gateMode\":\"NIFTY_FILTER\"}", new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

        Assert.Null(p.Strategy);
        Assert.Equal(BacktestStrategy.Score, BacktestStrategy.Normalize(p.Strategy));
    }

    [Fact]
    public void ApplyFixedRules_ReplacesTheFormsNumbersForANewRule()
    {
        var p = new BacktestParams { Strategy = BacktestStrategy.MomentumDaily, Capital = 100_000m, RiskPct = 5m, AmountPerTrade = 99_000m, MaxPositions = 50, MaxTradesPerDay = 50, SizingMode = "FIXED" };

        BacktestService.ApplyFixedRules(p);

        Assert.Equal(MomentumDailyRules.RiskPct, p.RiskPct);
        Assert.Equal(MomentumDailyRules.MaxPerTrade, p.AmountPerTrade);
        Assert.Equal(MomentumDailyRules.MaxPositions, p.MaxPositions);
        Assert.Equal(MomentumDailyRules.MaxEntriesPerDay, p.MaxTradesPerDay);
        Assert.Equal("RISK", p.SizingMode);
        Assert.Equal(MomentumDailyRules.Numbers(), p.RuleSet);
    }

    [Fact]
    public void ApplyFixedRules_LeavesTheScoreRuleAlone()
    {
        var p = new BacktestParams { Strategy = BacktestStrategy.Score, RiskPct = 2m, AmountPerTrade = 5000m, MaxPositions = 7, SizingMode = "FIXED" };

        BacktestService.ApplyFixedRules(p);

        Assert.Equal(2m, p.RiskPct);
        Assert.Equal(5000m, p.AmountPerTrade);
        Assert.Equal(7, p.MaxPositions);
        Assert.Equal("FIXED", p.SizingMode);
        Assert.Null(p.RuleSet);
    }
}
