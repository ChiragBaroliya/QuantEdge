using QuantEdge.Domain.Entities;
using QuantEdge.Infrastructure.Services;
using QuantEdge.Infrastructure.Services.Backtest;
using Xunit;

namespace QuantEdge.Tests;

/// <summary>The backtest's SHORT side: signals, exits, money, and the rule checks - each against the BUY side it mirrors.</summary>
public class BacktestShortSellingTests
{
    // A Wednesday. 09:15 IST = 03:45 UTC.
    private static readonly DateTime Day1 = new(2026, 10, 7);
    private const decimal Entry = 100m;

    private static readonly SwingTradeParams Swing = SwingTradeParams.From(new RealTradeSettings());

    private static BacktestContext Ctx(string squareOff = "15:15") => new()
    {
        TradeParams = Swing,
        TestBuy = true,
        TestShort = true,
        ShortSquareOffTime = squareOff,
        MaxDurationDays = 20
    };

    private static DateTime Bar(DateTime day, int slot) => DateTime.SpecifyKind(day, DateTimeKind.Utc).AddHours(3).AddMinutes(45 + 15 * slot);

    private static MarketCandle Candle(DateTime day, int slot, decimal o, decimal h, decimal l, decimal c) =>
        new() { Symbol = "T", Timeframe = "15m", CandleTime = Bar(day, slot), Open = o, High = h, Low = l, Close = c, Volume = 1000 };

    private static IntradayTape Tape(params MarketCandle[] bars)
    {
        var days = bars.Select(b => BacktestEngine.IstDate(b.CandleTime)).Distinct().OrderBy(d => d).ToList();
        return new IntradayTape("T", bars, d => days.IndexOf(d.Date), new HashSet<int>());
    }

    private static BacktestSignal Signal(bool isShort, decimal stop, decimal target, int entrySlot = 2, decimal? entryFill = null) => new()
    {
        Symbol = "T",
        IsShort = isShort,
        SignalTimeUtc = Bar(Day1, entrySlot),
        EntryTimeUtc = Bar(Day1, entrySlot),
        SessionDate = Day1,
        IsLiveCandidate = true,
        PolicyMaxPositions = int.MaxValue,
        EntryRaw = Entry,
        EntryFill = entryFill ?? Entry, // no slippage unless a test asks for it - matches every pre-existing test's assumption
        StopLoss = stop,
        Target = target,
        EntryIndex = 0
    };

    private static BacktestExit Exit(IntradayTape tape, BacktestSignal s, BacktestContext? ctx = null) =>
        BacktestEngine.SimulateExit(tape, s, ctx ?? Ctx(), out _)!;

    // ---------------- run label ----------------

    [Theory]
    [InlineData("", "All stocks")]
    [InlineData("TCS", "TCS")]
    [InlineData("TCS,WIPRO,INFY", "TCS, WIPRO, INFY")]
    [InlineData("TCS,WIPRO,INFY,SBIN,ITC", "TCS, WIPRO, INFY +2 more")]
    public void RunLabel_SaysWhichStocksWereTested(string symbols, string expected)
    {
        var list = symbols.Split(',', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(expected, BacktestService.StocksText(list));
    }

    // ---------------- exits ----------------

    [Fact]
    public void ShortExit_PriceFalls_TargetFillsAtTheLevel()
    {
        var exit = Exit(Tape(Candle(Day1, 2, 100, 100.5m, 93m, 94m)), Signal(true, 103m, 95m));

        Assert.Equal("Target", exit.Category);
        Assert.Equal(95m, exit.ExitRaw);
        Assert.Equal(7m, exit.MfePct);     // fell to 93: a 7% gain at best
        Assert.Equal(-0.5m, exit.MaePct);  // rose to 100.5 first
    }

    [Fact]
    public void ShortExit_PriceRises_StopFillsAtTheLevel_DuringTheDay()
    {
        var exit = Exit(Tape(Candle(Day1, 2, 100, 104m, 99.5m, 103.5m)), Signal(true, 103m, 95m));

        Assert.Equal("Stop loss", exit.Category);
        Assert.Equal(103m, exit.ExitRaw);
    }

    [Fact]
    public void ShortExit_GapThroughTheStop_FillsAtTheOpenNotTheStop()
    {
        var exit = Exit(Tape(Candle(Day1, 2, 100, 100, 100, 100), Candle(Day1, 3, 106m, 107m, 105m, 106m)), Signal(true, 103m, 95m));

        Assert.Equal("Stop loss", exit.Category);
        Assert.Equal(106m, exit.ExitRaw);
    }

    [Fact]
    public void ShortExit_BarTouchingBothStopAndTarget_CountsAsTheStop()
    {
        // Up-closing bar, which would otherwise visit its low first: the cautious rule still takes the stop.
        var exit = Exit(Tape(Candle(Day1, 2, 100, 104m, 94m, 101m)), Signal(true, 103m, 95m));

        Assert.Equal("Stop loss", exit.Category);
    }

    [Fact]
    public void ShortExit_NothingHit_IsBoughtBackAtTheSquareOffBarOpen()
    {
        var exit = Exit(Tape(
            Candle(Day1, 2, 100, 100.5m, 99.5m, 100m),
            Candle(Day1, 23, 99m, 99.5m, 98.5m, 99m),     // 15:00
            Candle(Day1, 24, 98.7m, 99m, 98m, 98.5m)),    // 15:15
            Signal(true, 103m, 95m));

        Assert.Equal("Square-off", exit.Category);
        Assert.Equal(98.7m, exit.ExitRaw);
        Assert.Equal(0, exit.SessionsHeld);
        Assert.Equal(Bar(Day1, 24), exit.ExitTimeUtc);
    }

    [Fact]
    public void ShortExit_MissingSquareOffBar_GoesAtThatDaysLastClose_NeverOvernight()
    {
        var day2 = Day1.AddDays(1);
        var exit = Exit(Tape(Candle(Day1, 2, 100, 100.5m, 99.5m, 99.8m), Candle(day2, 0, 120m, 121m, 119m, 120m)), Signal(true, 103m, 95m));

        Assert.Equal("Square-off (missing bar)", exit.Category);
        Assert.Equal(99.8m, exit.ExitRaw);       // not next day's gap to 120
        Assert.Equal(0, exit.SessionsHeld);
        Assert.Equal(Day1, exit.SessionDate);
    }

    [Fact]
    public void LongExit_MissingClosingBar_StillRunsTheClosingChecksThatDay()
    {
        var day2 = Day1.AddDays(1);
        var day3 = Day1.AddDays(2);
        var ctx = new BacktestContext { TradeParams = Swing, MaxDurationDays = 1 };

        var exit = Exit(Tape(
            Candle(Day1, 2, 100, 100.5m, 99.5m, 100m),
            Candle(day2, 5, 101m, 101.5m, 100.5m, 101m),     // day 2 has no bar in the closing window
            Candle(day3, 0, 110m, 111m, 109m, 110m)),
            Signal(false, 97m, 130m), ctx);

        Assert.Equal("Max days", exit.Category);
        Assert.Equal(101m, exit.ExitRaw);
        Assert.Equal(1, exit.SessionsHeld);
    }

    [Fact]
    public void LongExit_MissingClosingBar_StillStopsOutOnTheClosingBasis()
    {
        var day2 = Day1.AddDays(1);

        var exit = Exit(Tape(Candle(Day1, 5, 96m, 96.5m, 95.5m, 96m), Candle(day2, 0, 100m, 101m, 99m, 100m)), Signal(false, 97m, 130m));

        Assert.Equal("Stop loss", exit.Category);
        Assert.Equal(96m, exit.ExitRaw);
        Assert.Equal(0, exit.SessionsHeld);
    }

    [Fact]
    public void ShortExit_TrailActivatesThenStopsTheTradeOut_AtOrBelowEntry()
    {
        // ATR from the stop: 3 / 0.6 (ShortStopLossAtrMult) = 5. Falls past 95 (activation) -> trail at breakeven, then the price recovers.
        var exit = Exit(Tape(Candle(Day1, 2, 100, 100, 94m, 94.5m), Candle(Day1, 3, 95m, 100.5m, 95m, 100.2m)), Signal(true, 103m, 90m));

        Assert.Equal("Trailing stop", exit.Category);
        Assert.Equal(100m, exit.ExitRaw);
    }

    [Fact]
    public void LongAndShort_OnMirroredBars_EndTheSameWay()
    {
        // Same distances, price reflected around the entry: a short must behave as the long does.
        var longExit = Exit(Tape(Candle(Day1, 2, 100, 106.5m, 99.5m, 106m)), Signal(false, 97m, 106m));
        var shortExit = Exit(Tape(Candle(Day1, 2, 100, 100.5m, 93.5m, 94m)), Signal(true, 103m, 94m));

        Assert.Equal(longExit.Category, shortExit.Category);
        Assert.Equal(longExit.ExitRaw - Entry, Entry - shortExit.ExitRaw);
        Assert.Equal(longExit.MfePct, shortExit.MfePct);
        Assert.Equal(longExit.MaePct, shortExit.MaePct);
    }

    // ---------------- money ----------------

    private static BacktestTrade RunOne(BacktestSignal s, decimal exitRaw, decimal slippagePct = 0.10m)
    {
        var exit = new BacktestExit(s.EntryTimeUtc.AddHours(1), s.SessionDate, exitRaw, "x", "Target", 0, 0m, 0m, false);
        var ps = new PortfolioSettings { Capital = 100_000m, AmountPerTrade = 10_000m, RiskSizing = false, SlippagePct = slippagePct };
        return BacktestPortfolio.Run(new[] { s }, _ => exit, ps, _ => null).Trades.Single();
    }

    [Fact]
    public void ShortPortfolio_MakesMoneyWhenThePriceFalls_AndSlippageGoesAgainstIt()
    {
        // Entry slippage is now baked into the signal itself (BacktestEngine computes EntryFill at signal time,
        // the same way live would via BacktestContext.SlippagePct) rather than reapplied inside the portfolio run -
        // 100 x (1 - 0.10%) = 99.90, same number this test always expected.
        var t = RunOne(Signal(true, 103m, 95m, entryFill: 99.90m), 95m);

        Assert.Equal(BacktestSide.Short, t.Side);
        Assert.Equal(100, t.Quantity);
        Assert.Equal(99.90m, t.EntryPrice);    // sold 0.10% lower
        Assert.Equal(95.10m, t.ExitPrice);     // bought back 0.10% higher
        Assert.Equal(480m, t.GrossPnl);
        Assert.True(t.Charges > 0m);
        Assert.Equal(t.GrossPnl - t.Charges, t.NetPnl);
        Assert.True(t.RMultiple > 1m);         // risk 300, net about 470
    }

    [Fact]
    public void ShortPortfolio_LosesMoneyWhenThePriceRises()
    {
        var t = RunOne(Signal(true, 103m, 95m), 103m);

        Assert.True(t.GrossPnl < 0m);
        Assert.True(t.NetPnl < t.GrossPnl);
        Assert.True(t.RMultiple < -1m);        // a full stop plus costs
    }

    [Fact]
    public void ShortPortfolio_ChargesTheSellLegAsTheEntry()
    {
        // STT on an intraday trade is on the sell side only, so a short pays it on its entry value.
        var t = RunOne(Signal(true, 103m, 95m), 95m, slippagePct: 0m);
        var rates = ChargesCalculator.RatesFor(ChargeRates.Defaults, "MIS", Day1);
        var expected = ChargesCalculator.RoundTrip(buyValue: 9_500m, sellValue: 10_000m, rates).Total;

        Assert.Equal(expected, t.Charges);
    }

    [Fact]
    public void ShortPortfolio_RiskSizing_UsesTheStopAboveTheEntry()
    {
        var s = Signal(true, 105m, 90m);
        var exit = new BacktestExit(s.EntryTimeUtc.AddHours(1), s.SessionDate, 95m, "x", "Target", 0, 0m, 0m, false);
        var ps = new PortfolioSettings { Capital = 100_000m, AmountPerTrade = 50_000m, RiskSizing = true, RiskPct = 1m, SlippagePct = 0m };

        var t = BacktestPortfolio.Run(new[] { s }, _ => exit, ps, _ => null).Trades.Single();

        Assert.Equal(200, t.Quantity);         // ₹1,000 risk ÷ ₹5 a share
    }

    // ---------------- entry-fill (slippage-adjusted entry) bug fix ----------------
    // Regression coverage for: the exit-decision engine used to receive EntryRaw (the pre-slippage signal
    // price) instead of EntryFill (the real simulated cost basis) as "entry" for anything measured from
    // entry - breakeven floor/ceiling, ATR inference, emergency stop, trail activation. Each pair below holds
    // everything constant except EntryFill, and shows the exit decision actually changes - proving the fix is
    // wired end to end through BacktestEngine.SimulateExit/SimulateShortExit, not just correct in isolation.

    // These two candles put the discriminating check at the bar's open (the very first price point evaluated),
    // deliberately before any within-bar ratcheting - the INTRADAY trail only ever ratchets towards the current
    // price, so a later check in the same bar would converge both the fixed and buggy seed to the same value
    // and hide the bug. Checking at the open isolates exactly the seed value itself.

    [Fact]
    public void LongIntradayTrail_SeedsFromTheRealEntryFill_NotTheRawSignalPrice()
    {
        // EntryFill 105 (as if 5% slippage) -> INTRADAY trail seeds at 105 x 0.98 = 102.90. The bar opens at
        // 100, already below that seed, so it must exit on the very first check.
        var intraday = SwingTradeParams.From(new RealTradeSettings { ExitMode = "INTRADAY" });
        var ctx = new BacktestContext { TradeParams = intraday, MaxDurationDays = 20 };
        var tape = Tape(Candle(Day1, 2, 100m, 101m, 100m, 100.5m));
        var s = Signal(false, 50m, 200m, entryFill: 105m);

        var exit = Exit(tape, s, ctx);

        Assert.Contains("Trailing SL Hit", exit.Reason);
    }

    [Fact]
    public void LongIntradayTrail_WithoutSlippage_DoesNotBreachAtTheSamePrice()
    {
        // Same bar, but EntryFill == EntryRaw (no slippage) -> seed is 100 x 0.98 = 98, below every price in
        // the bar - this is the old (pre-fix) EntryRaw-only behavior, kept as the contrast case.
        var intraday = SwingTradeParams.From(new RealTradeSettings { ExitMode = "INTRADAY" });
        var ctx = new BacktestContext { TradeParams = intraday, MaxDurationDays = 20 };
        var tape = Tape(Candle(Day1, 2, 100m, 101m, 100m, 100.5m));
        var s = Signal(false, 50m, 200m); // entryFill defaults to EntryRaw (100)

        var exit = Exit(tape, s, ctx);

        Assert.DoesNotContain("Trailing SL Hit", exit.Reason);
    }

    [Fact]
    public void ShortIntradayTrail_SeedsFromTheRealEntryFill_NotTheRawSignalPrice()
    {
        // EntryFill 95 (as if 5% slippage) -> INTRADAY trail seeds at 95 x 1.02 = 96.90 (short trail sits
        // above entry). The bar opens at 97, already above that seed, so it must exit on the very first check.
        var intraday = SwingTradeParams.From(new RealTradeSettings { ExitMode = "INTRADAY" });
        var ctx = new BacktestContext { TradeParams = intraday, MaxDurationDays = 20 };
        var tape = Tape(Candle(Day1, 2, 97m, 97m, 94m, 95m));
        var s = Signal(true, 150m, 10m, entryFill: 95m);

        var exit = Exit(tape, s, ctx);

        Assert.Contains("Trailing SL Hit", exit.Reason);
    }

    [Fact]
    public void ShortIntradayTrail_WithoutSlippage_DoesNotBreachAtTheSamePrice()
    {
        // Same bar, but EntryFill == EntryRaw (no slippage) -> seed is 100 x 1.02 = 102, above every price in
        // the bar - this is the old (pre-fix) EntryRaw-only behavior, kept as the contrast case.
        var intraday = SwingTradeParams.From(new RealTradeSettings { ExitMode = "INTRADAY" });
        var ctx = new BacktestContext { TradeParams = intraday, MaxDurationDays = 20 };
        var tape = Tape(Candle(Day1, 2, 97m, 97m, 94m, 95m));
        var s = Signal(true, 150m, 10m); // entryFill defaults to EntryRaw (100)

        var exit = Exit(tape, s, ctx);

        Assert.DoesNotContain("Trailing SL Hit", exit.Reason);
    }

    [Fact]
    public void LongEmergencyStop_UsesTheRealEntryFill_NotTheRawSignalPrice()
    {
        // EntryFill 105, StopLoss 98 -> ATR = (105-98)/1.5 = 4.667, emergency = 105 - 2.5x4.667 = 93.33
        // (well inside the 12%-of-entry cap, so the ATR-based value governs, not the cap). A live dip to 95
        // at 09:45 IST (well outside the closing window, so only the live emergency check applies) must NOT
        // trigger once the floor is anchored to the real fill.
        var tape = Tape(Candle(Day1, 2, 105m, 106m, 95m, 100m));
        var s = Signal(false, 98m, 200m, entryFill: 105m);

        var exit = Exit(tape, s); // default Ctx() uses SWING_CLOSE (Swing)

        Assert.DoesNotContain("Emergency Stop", exit.Reason);
    }

    [Fact]
    public void LongEmergencyStop_WithoutSlippage_TriggersAtTheSamePrice()
    {
        // Same dip to 95, but EntryFill == EntryRaw (no slippage) -> ATR = (100-98)/1.5 = 1.333, emergency =
        // 100 - 2.5x1.333 = 96.67, so 95 breaches it - this is the old (pre-fix) EntryRaw-only behavior, kept
        // as the contrast case.
        var tape = Tape(Candle(Day1, 2, 105m, 106m, 95m, 100m));
        var s = Signal(false, 98m, 200m); // entryFill defaults to EntryRaw (100)

        var exit = Exit(tape, s);

        Assert.Contains("Emergency Stop", exit.Reason);
    }

    [Fact]
    public void Portfolio_RMultiple_UsesTheRealEntryFill_NotTheRawSignalPrice()
    {
        // EntryRaw 100, EntryFill 105 (as if 5% slippage), StopLoss 95 -> risk-per-share must be the real
        // cost basis to the stop, 105-95=10, not the pre-fix 100-95=5 (half the true risk, so double the
        // reported R-multiple).
        var s = Signal(false, 95m, 200m, entryFill: 105m);
        var t = RunOne(s, 115m, slippagePct: 0m); // clean exit fill (no exit-side slippage) isolates this check

        decimal expectedRiskPerShare = 105m - 95m;
        Assert.Equal(Math.Round(t.NetPnl / (t.Quantity * expectedRiskPerShare), 3), t.RMultiple);
    }

    [Fact]
    public void Portfolio_DoesNotHoldALongAndAShortInTheSameStock()
    {
        var first = Signal(false, 97m, 106m);
        var second = Signal(true, 103m, 95m, entrySlot: 3);
        var exit = new BacktestExit(Bar(Day1, 10), Day1, 106m, "x", "Target", 0, 0m, 0m, false);
        var ps = new PortfolioSettings { RiskSizing = false, AmountPerTrade = 10_000m };

        var r = BacktestPortfolio.Run(new[] { first, second }, _ => exit, ps, _ => null);

        Assert.Single(r.Trades);
        Assert.Equal(1, r.Skipped["already holding the stock (guard 9)"]);
    }

    [Fact]
    public void EquityCurve_MarksAnOpenShort_GainingAsThePriceFalls()
    {
        var day2 = Day1.AddDays(1);
        var t = new BacktestTrade { Symbol = "T", Side = BacktestSide.Short, Quantity = 10, EntryPrice = 100m, NetPnl = 0m,
            EntryTime = Bar(Day1, 2), ExitTime = Bar(day2, 2) };

        var curve = BacktestPortfolio.EquityCurve(new[] { t }, 1000m, new[] { Day1 }, (_, _) => 90m, out _);

        Assert.Equal(1100m, curve[0].Equity);
    }

    [Fact]
    public void FactorEdge_KeepsEachSidesFactorsApart()
    {
        var longTrade = new BacktestTrade { Side = BacktestSide.Buy, RMultiple = 1m, NetPnl = 10m, Factors = "BREAKOUT_GROUP:20,VOL_CONFIRMATION:15" };
        var shortTrade = new BacktestTrade { Side = BacktestSide.Short, RMultiple = -1m, NetPnl = -10m, Factors = "BREAKDOWN_GROUP:20,VOL_CONFIRMATION:0" };

        var rows = BacktestPortfolio.FactorEdge(new[] { longTrade, shortTrade });

        var longVol = rows.Single(r => r.Side == BacktestSide.Buy && r.Code == "VOL_CONFIRMATION");
        var shortVol = rows.Single(r => r.Side == BacktestSide.Short && r.Code == "VOL_CONFIRMATION");
        Assert.Equal((1, 0), (longVol.TradesWith, longVol.TradesWithout));
        Assert.Equal((0, 1), (shortVol.TradesWith, shortVol.TradesWithout));
        Assert.Contains(rows, r => r.Code == "BREAKDOWN_GROUP" && r.Side == BacktestSide.Short);
    }

    [Fact]
    public void FactorPacking_RoundTripsTheShortEnginesCodes()
    {
        var factors = new[] { new SwingFactorScore("BREAKDOWN_GROUP", "", 20, 20, "15m"), new SwingFactorScore("MACD_BEARISH", "", 10, 10, "15m") };

        string text = BacktestEngine.FactorsText(BacktestEngine.PackFactors(factors, BacktestEngine.ShortFactorCodes), isShort: true);

        Assert.Contains("BREAKDOWN_GROUP:20", text);
        Assert.Contains("MACD_BEARISH:10", text);
        Assert.Contains("BEARISH_CANDLE:0", text);
    }

    // ---------------- direction ----------------

    [Fact]
    public void Direction_ShortIsRightWhenThePriceFalls_AndCountsAStockOnceADay()
    {
        var tape = Tape(
            Candle(Day1, 2, 100, 100, 100, 100), Candle(Day1, 3, 100, 100, 99, 99),
            Candle(Day1, 4, 99, 99, 98, 98), Candle(Day1, 5, 98, 98, 96, 96),
            Candle(Day1, 24, 96, 96, 95, 95));
        var first = Signal(true, 103m, 90m);
        var refire = new BacktestSignal { Symbol = "T", IsShort = true, IsLiveCandidate = true, SessionDate = Day1, EntryRaw = 96m,
            EntryIndex = 3, EntryTimeUtc = Bar(Day1, 5), StopLoss = 99m, Target = 90m };

        var rows = BacktestEngine.DirectionAccuracy(new[] { first, refire }, _ => tape);

        var hour = rows.Single(r => r.Horizon == "1 hour later");
        Assert.Equal((BacktestSide.Short, 1, 100m, 4m), (hour.Side, hour.Signals, hour.RightPct, hour.AvgMovePct));
        Assert.Equal(5m, rows.Single(r => r.Horizon == "Same-day close").AvgMovePct);
        Assert.DoesNotContain(rows, r => r.Horizon == "Next-day close");   // no next day in the data
    }

    [Fact]
    public void Direction_LongIsWrongWhenThePriceFalls()
    {
        var tape = Tape(Candle(Day1, 2, 100, 100, 100, 100), Candle(Day1, 24, 96, 96, 95, 95));

        var row = BacktestEngine.DirectionAccuracy(new[] { Signal(false, 97m, 106m) }, _ => tape).Single();

        Assert.Equal((0m, -5m), (row.RightPct, row.AvgMovePct));
    }

    // ---------------- rule checks ----------------

    private static BacktestTrade Good(bool isShort) => new()
    {
        Symbol = isShort ? "S" : "L",
        Side = BacktestSide.Of(isShort),
        SignalTime = Bar(Day1, 2), EntryTime = Bar(Day1, 2), ExitTime = Bar(Day1, 6),
        EntryRaw = 100m, ExitRaw = isShort ? 95m : 105m,
        EntryPrice = 100m, ExitPrice = isShort ? 95m : 105m,
        StopLoss = isShort ? 103m : 97m, Target = isShort ? 95m : 105m,
        Quantity = 10, GrossPnl = 50m, Charges = 5m, NetPnl = 45m, ExitCategory = "Target"
    };

    private static List<BacktestLogicCheck> Audit(params BacktestTrade[] trades) => BacktestLogicAudit.Run(trades, "15:00", "15:15");

    [Fact]
    public void Audit_PassesCorrectTradesOnBothSides()
    {
        var checks = Audit(Good(false), Good(true));

        Assert.All(checks, c => Assert.Equal(0, c.Failed));
        Assert.Contains(checks, c => c.Side == BacktestSide.Buy);
        Assert.Contains(checks, c => c.Side == BacktestSide.Short);
    }

    [Fact]
    public void Audit_CatchesAShortWithLongStyleLevels()
    {
        var t = Good(true);
        t.StopLoss = 97m; t.Target = 105m;

        var failed = Audit(t).Single(c => c.Failed > 0 && c.Rule.StartsWith("Stop above"));

        Assert.Contains("S ", failed.Examples.Single());
    }

    [Fact]
    public void Audit_CatchesAShortWhosePnlHasTheLongSign()
    {
        var t = Good(true);
        t.GrossPnl = -50m; t.NetPnl = -55m;

        Assert.Contains(Audit(t), c => c.Failed == 1 && c.Rule.StartsWith("Profit when the price fell"));
    }

    [Fact]
    public void Audit_CatchesAShortHeldOvernight_AndALateEntry()
    {
        var t = Good(true);
        t.SessionsHeld = 1;
        t.SignalTime = Bar(Day1, 23);   // 15:00
        t.EntryTime = Bar(Day1, 24);
        t.ExitTime = Bar(Day1.AddDays(1), 0);

        var failed = Audit(t).Where(c => c.Failed > 0).Select(c => c.Rule).ToList();

        Assert.Contains(failed, r => r.StartsWith("Every short was bought back"));
        Assert.Contains(failed, r => r.StartsWith("No new short"));
    }

    [Fact]
    public void Audit_CatchesASquareOffBeforeItsTime_AndOverlappingPositions()
    {
        var early = Good(true);
        early.ExitCategory = "Square-off";
        var overlap = Good(true);
        overlap.EntryTime = Bar(Day1, 3);
        overlap.SignalTime = Bar(Day1, 3);

        var failed = Audit(early, overlap).Where(c => c.Failed > 0).Select(c => c.Rule).ToList();

        Assert.Contains(failed, r => r.StartsWith("The square-off happened"));
        Assert.Contains(failed, r => r.StartsWith("Only one open position"));
    }

    // ---------------- signals, through the real engines ----------------

    /// <summary>Daily, 15m and 60m bars all trending by the given rates, ending before the 15m test day's last bars.</summary>
    private static SymbolBacktestResult Generate(decimal dailyPct, decimal niftyPct, decimal barPct, bool testBuy, bool testShort, string cutoff = "15:00",
        Func<DateTime, (string? Regime, RegimePolicy? Policy)>? regimeFor = null, bool regimeMode = false)
    {
        var testDay = Day1;
        var daily = new List<MarketCandle>();
        var nifty = new List<MarketCandle>();
        decimal close = 1000m, niftyClose = 20000m;
        var dates = new List<DateTime>();
        for (var d = testDay.AddDays(-400); d < testDay; d = d.AddDays(1))
        {
            if (d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) continue;
            dates.Add(d);
            decimal open = close, nOpen = niftyClose;
            decimal wiggle = dates.Count % 2 == 0 ? 0.03m : -0.03m;
            close *= 1m + (dailyPct + wiggle) / 100m;
            niftyClose *= 1m + (niftyPct + wiggle) / 100m;
            daily.Add(new MarketCandle { Symbol = "T", Timeframe = "1d", CandleTime = BacktestEngine.DailyStamp(d), Open = open, Close = close,
                High = Math.Max(open, close) * 1.006m, Low = Math.Min(open, close) * 0.994m, Volume = 100000 });
            nifty.Add(new MarketCandle { Symbol = "NIFTY 50", Timeframe = "1d", CandleTime = BacktestEngine.DailyStamp(d), Open = nOpen, Close = niftyClose,
                High = Math.Max(nOpen, niftyClose) * 1.003m, Low = Math.Min(nOpen, niftyClose) * 0.997m });
        }

        var m15 = new List<MarketCandle>();
        decimal price = close;
        foreach (var d in dates.TakeLast(4).Append(testDay))
        {
            for (int slot = 0; slot < 25; slot++)
            {
                decimal open = price;
                price *= 1m + barPct / 100m;
                m15.Add(Candle(d, slot, open, Math.Max(open, price) * 1.0005m, Math.Min(open, price) * 0.9995m, price));
            }
        }
        var h60 = m15.Where((_, i) => i % 4 == 0).Select(c => new MarketCandle { Symbol = "T", Timeframe = "60m", CandleTime = c.CandleTime,
            Open = c.Open, High = c.High, Low = c.Low, Close = c.Close, Volume = c.Volume }).ToList();

        var settings = new SwingStrategySettings();
        var ctx = new BacktestContext
        {
            EngineSettings = settings,
            TradeParams = Swing,
            TestBuy = testBuy,
            TestShort = testShort,
            ShortEntryCutoff = cutoff,
            MinConditionsMatch = 3,   // the trend fixture has flat volume: let the hard filters alone qualify it
            FromDate = testDay,
            ToDate = testDay,
            NiftyDaily = nifty,
            SessionOf = d => dates.Append(testDay).ToList().IndexOf(d.Date),
            RegimeFor = regimeFor ?? (_ => (null, null)),
            RegimeMode = regimeMode,
            EntryDelayMinutes = 15
        };
        return BacktestEngine.GenerateSignals("T", daily, h60, m15, ctx);
    }

    [Fact]
    public void Signals_FallingStockInAFallingMarket_GiveShortsWithLevelsOnTheShortSide()
    {
        var r = Generate(-0.25m, -0.2m, -0.02m, testBuy: true, testShort: true);

        var live = r.Signals.Where(s => s.IsLiveCandidate).ToList();
        Assert.NotEmpty(live);
        Assert.All(live, s => Assert.True(s.IsShort));
        Assert.All(live, s => Assert.True(s.StopLoss > s.EntryRaw && s.Target < s.EntryRaw));
    }

    private static readonly RegimePolicy TightPolicy = new() { Regime = "BULLISH", MaxPositions = 2, RiskPct = 0.25m, MaxExposurePct = 5m, MinStockScore = 0, RequireRelativeStrength = true };

    [Fact]
    public void Signals_NiftyFilterMode_ReportTheRegimeButIgnoreItsLimits()
    {
        var r = Generate(0.25m, 0.2m, 0.02m, testBuy: true, testShort: false, regimeFor: _ => ("BULLISH", TightPolicy), regimeMode: false);

        Assert.NotEmpty(r.Signals);
        Assert.All(r.Signals, s =>
        {
            Assert.Equal("BULLISH", s.Regime);
            Assert.Equal(int.MaxValue, s.PolicyMaxPositions);
            Assert.Equal(0m, s.PolicyRiskPct);
            Assert.Null(s.PolicyMaxExposurePct);
            Assert.False(s.PolicyRequiresRs);
        });
    }

    [Fact]
    public void Signals_RegimeMode_ApplyTheRegimeLimits()
    {
        var r = Generate(0.25m, 0.2m, 0.02m, testBuy: true, testShort: false, regimeFor: _ => ("BULLISH", TightPolicy), regimeMode: true);

        Assert.NotEmpty(r.Signals);
        Assert.All(r.Signals, s =>
        {
            Assert.Equal(2, s.PolicyMaxPositions);
            Assert.Equal(0.25m, s.PolicyRiskPct);
            Assert.Equal(5m, s.PolicyMaxExposurePct);
        });
    }

    [Fact]
    public void Signals_RisingStockInARisingMarket_GiveNoShorts()
    {
        var r = Generate(0.25m, 0.2m, 0.02m, testBuy: false, testShort: true);

        Assert.Empty(r.Signals);
    }

    [Fact]
    public void Signals_FallingStockInARisingMarket_GiveNoShorts_TheNiftyFilterHolds()
    {
        var r = Generate(-0.25m, 0.2m, -0.02m, testBuy: false, testShort: true);

        Assert.Empty(r.Signals);
    }

    [Fact]
    public void Signals_BuyOnlyRun_NeverShorts()
    {
        var r = Generate(-0.25m, -0.2m, -0.02m, testBuy: true, testShort: false);

        Assert.DoesNotContain(r.Signals, s => s.IsShort);
    }

    [Fact]
    public void Signals_RespectTheShortEntryCutoff()
    {
        var r = Generate(-0.25m, -0.2m, -0.02m, testBuy: false, testShort: true, cutoff: "12:00");

        Assert.NotEmpty(r.Signals);
        Assert.All(r.Signals, s => Assert.True(BacktestEngine.ToIst(s.SignalTimeUtc).TimeOfDay < new TimeSpan(12, 0, 0)));
    }

    [Fact]
    public void DowntrendPrecheck_AgreesWithTheShortEngine()
    {
        var settings = new SwingStrategySettings();
        foreach (decimal pct in new[] { -0.6m, -0.05m, 0m, 0.05m, 0.6m })
        {
            var daily = Candles.Daily("T", 260, 1000m, pct);
            var pre = BacktestEngine.DailyEmaTrendPrecheck.For(daily.Take(daily.Count - 1).ToList(), settings.Rules)!;
            var full = SwingShortDecisionEngine.Evaluate(new StockMaster { Symbol = "T" }, daily, null, null, Candles.Daily("NIFTY 50", 260, 20000m, -0.2m), settings, skipHardFilterGate: true);

            Assert.Equal(full.EmaTrendPassed, pre.PassesDowntrend(daily[^1].Close));
        }
    }
}
