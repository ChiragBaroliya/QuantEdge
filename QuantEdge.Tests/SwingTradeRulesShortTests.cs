using QuantEdge.Domain.Entities;
using QuantEdge.Infrastructure.DTOs;
using QuantEdge.Infrastructure.Services;
using Xunit;

namespace QuantEdge.Tests;

public class SwingTradeRulesShortTests : ClockedTest
{
    private static SwingTradeParams SwingParams() => SwingTradeParams.From(new AutoTradeSettings());          // SWING_CLOSE, SL 1.5 / Trail 3 / Target 3 ATR
    private static SwingTradeParams IntradayParams() => SwingTradeParams.From(new AutoTradeSettings { ExitMode = "INTRADAY" });

    private static ExitPositionView Short(decimal entry, decimal? sl, decimal? tp, decimal? tsl = null, DateTime? openedUtc = null) =>
        new(entry, sl, tp, tsl, null, openedUtc ?? IstToUtc(TradingDayIst.AddHours(-1)));

    // ---------------- Entry levels / sizing / drift ----------------

    [Fact]
    public void ShortLevels_SwingClose_PutStopAboveAndTargetBelowEntry()
    {
        var levels = SwingTradeRules.ComputeShortEntryLevels(100m, dailyAtr: 2m, null, null, SwingParams());

        Assert.Equal(101.2m, levels.StopLoss);   // 100 + 0.6 x 2 (shorts use their own, smaller ATR multiples)
        Assert.Equal(97.6m, levels.TakeProfit);  // 100 - 1.2 x 2
        Assert.Null(levels.TrailingStopLoss);    // trail only after the trade moves 1 ATR in our favour
    }

    [Fact]
    public void ShortLevels_CapStopAtMaxStopLossPct()
    {
        var levels = SwingTradeRules.ComputeShortEntryLevels(100m, dailyAtr: 20m, null, null, SwingParams());

        Assert.Equal(108m, levels.StopLoss);     // 0.6 x 20 = 12% capped at 8%
        Assert.True(levels.TakeProfit < 100m);
    }

    [Fact]
    public void ShortLevels_Intraday_UseEngineLevelsOnlyWhenOnTheShortSide()
    {
        var valid = SwingTradeRules.ComputeShortEntryLevels(100m, null, engineStopLoss: 102m, engineTarget: 96m, IntradayParams());
        Assert.Equal(102m, valid.StopLoss);
        Assert.Equal(96m, valid.TakeProfit);
        Assert.Equal(102m, valid.TrailingStopLoss); // entry + 2%

        // A long-style engine level (stop below / target above entry) must never be used for a short.
        var wrongSide = SwingTradeRules.ComputeShortEntryLevels(100m, null, engineStopLoss: 98m, engineTarget: 104m, IntradayParams());
        Assert.Equal(103m, wrongSide.StopLoss);  // fallback +3%
        Assert.Equal(95m, wrongSide.TakeProfit); // ProfitTargetPct 5% below
    }

    [Fact]
    public void LongLevels_AreUnchanged()
    {
        var levels = SwingTradeRules.ComputeEntryLevels(100m, 2m, null, null, null, null, SwingParams());
        Assert.Equal(97m, levels.StopLoss);
        Assert.Equal(106m, levels.TakeProfit);
    }

    [Fact]
    public void ShortAndLongAtrMultiples_AreIndependentlyConfigurable()
    {
        // A much wider LONG swing setting must not change the SHORT's (still intraday-sized) levels, and vice versa.
        var p = SwingTradeParams.From(new AutoTradeSettings
        {
            StopLossAtrMult = 1.5m, TargetAtrMult = 3.0m,               // long (swing) - unchanged
            ShortStopLossAtrMult = 0.6m, ShortTargetAtrMult = 1.2m      // short (intraday) - independent
        });

        var longLevels = SwingTradeRules.ComputeEntryLevels(100m, 2m, null, null, null, null, p);
        Assert.Equal(97m, longLevels.StopLoss);      // 100 - 1.5 x 2
        Assert.Equal(106m, longLevels.TakeProfit);   // 100 + 3.0 x 2

        var shortLevels = SwingTradeRules.ComputeShortEntryLevels(100m, 2m, null, null, p);
        Assert.Equal(101.2m, shortLevels.StopLoss);  // 100 + 0.6 x 2 - much tighter than the long's 1.5x
        Assert.Equal(97.6m, shortLevels.TakeProfit); // 100 - 1.2 x 2 - much closer than the long's 3.0x
    }

    [Fact]
    public void ShortSizing_UsesStopMinusEntryAsRisk()
    {
        // Capital 100000 x 1% = 1000 risk; risk/share = 105 - 100 = 5 -> 200 shares; amount cap 50000/100 = 500.
        var (qty, _) = SwingTradeRules.RiskSizedShortQuantity(100m, 105m, 100000m, 50000m);
        Assert.Equal(200, qty);

        var (longQty, _) = SwingTradeRules.RiskSizedQuantity(100m, 95m, 100000m, 50000m);
        Assert.Equal(qty, longQty); // same distance, same size as the mirrored long
    }

    [Theory]
    [InlineData(100, 101, false)]   // small move - tradeable
    [InlineData(100, 99, false)]
    [InlineData(100, 102.5, true)]  // bounced up - breakdown failed
    [InlineData(100, 97.5, true)]   // already fallen - too extended
    public void ShortDrift_RejectsMovesBeyondTwoPercent(decimal scan, decimal live, bool rejected)
    {
        Assert.Equal(rejected, SwingTradeRules.CheckShortSignalDrift(scan, live) != null);
    }

    [Fact]
    public void ShortEntryCutoff_BlocksAtAndAfterTheCutoff()
    {
        Assert.True(SwingTradeRules.IsBeforeShortEntryCutoff("15:00", TradingDayIst));                       // 11:00
        Assert.False(SwingTradeRules.IsBeforeShortEntryCutoff("15:00", TradingDayIst.Date.AddHours(15)));    // 15:00
        Assert.False(SwingTradeRules.IsBeforeShortEntryCutoff("bad", TradingDayIst.Date.AddHours(15.5)));    // falls back to 15:00
    }

    // ---------------- Exit policy ----------------

    [Fact]
    public void ShortExit_TargetHitWhenPriceFallsToTarget()
    {
        var d = SwingTradeRules.EvaluateShortExit(Short(100m, 103m, 94m), 93.9m, TradingDayIst, "15:15", SwingParams());
        Assert.True(d.ShouldExit);
        Assert.StartsWith("Target Hit", d.Reason);
    }

    [Fact]
    public void ShortExit_StopHitLiveWhenPriceRises_EvenOutsideClosingWindow()
    {
        var d = SwingTradeRules.EvaluateShortExit(Short(100m, 103m, 94m), 103.2m, TradingDayIst, "15:15", SwingParams());
        Assert.True(d.ShouldExit);
        Assert.StartsWith("Stop Loss Hit", d.Reason);
        Assert.False(d.IsGap);

        var gap = SwingTradeRules.EvaluateShortExit(Short(100m, 103m, 94m), 106m, TradingDayIst, "15:15", SwingParams());
        Assert.True(gap.IsGap);
    }

    [Fact]
    public void ShortExit_HoldsBetweenStopAndTarget()
    {
        var d = SwingTradeRules.EvaluateShortExit(Short(100m, 103m, 94m), 101m, TradingDayIst, "15:15", SwingParams());
        Assert.False(d.ShouldExit);
        Assert.Null(d.NewTrailingStopLoss); // price above entry - no trail yet
    }

    [Fact]
    public void ShortExit_SquaresOffAtSquareOffTime()
    {
        var at1515 = TradingDayIst.Date.AddHours(15).AddMinutes(15);
        var d = SwingTradeRules.EvaluateShortExit(Short(100m, 103m, 94m), 101m, at1515, "15:15", SwingParams());
        Assert.True(d.ShouldExit);
        Assert.Equal(SwingTradeRules.ShortSquareOffReason, d.Reason);

        var before = SwingTradeRules.EvaluateShortExit(Short(100m, 103m, 94m), 101m, at1515.AddMinutes(-1), "15:15", SwingParams());
        Assert.False(before.ShouldExit);
    }

    [Fact]
    public void ShortExit_SquaresOffAShortOpenedOnAnEarlierDay()
    {
        var yesterday = IstToUtc(TradingDayIst.AddDays(-1));
        var d = SwingTradeRules.EvaluateShortExit(Short(100m, 103m, 94m, openedUtc: yesterday), 100m, TradingDayIst, "15:15", SwingParams());
        Assert.True(d.ShouldExit);
        Assert.Equal(SwingTradeRules.ShortSquareOffReason, d.Reason);
    }

    [Fact]
    public void ShortExit_SwingTrail_ActivatesAtBreakevenAfterOneAtrAndOnlyMovesDown()
    {
        // ATR inferred from the stop: (103 - 100) / 0.6 (ShortStopLossAtrMult) = 5. Activation at 100 - 5 = 95.
        var notYet = SwingTradeRules.EvaluateShortExit(Short(100m, 103m, 90m), 95.1m, TradingDayIst, "15:15", SwingParams());
        Assert.Null(notYet.NewTrailingStopLoss);

        var activated = SwingTradeRules.EvaluateShortExit(Short(100m, 103m, 90m), 94.9m, TradingDayIst, "15:15", SwingParams());
        Assert.False(activated.ShouldExit);
        Assert.Equal(100m, activated.NewTrailingStopLoss); // ltp + 1.2 x 5 ATR = 100.9 -> capped at breakeven

        // Price far lower: trail follows down (ltp + 1.2 x 5 = 6 -> 98 when ltp is 92).
        var lower = SwingTradeRules.EvaluateShortExit(Short(100m, 103m, null, tsl: 100m), 92m, TradingDayIst, "15:15", SwingParams());
        Assert.Equal(98m, lower.NewTrailingStopLoss);

        // Never raised again.
        var up = SwingTradeRules.EvaluateShortExit(Short(100m, 103m, null, tsl: 98m), 93m, TradingDayIst, "15:15", SwingParams());
        Assert.Null(up.NewTrailingStopLoss);
    }

    [Fact]
    public void ShortExit_TrailHitWhenPriceRisesBackToIt()
    {
        var d = SwingTradeRules.EvaluateShortExit(Short(100m, 103m, null, tsl: 98m), 98.2m, TradingDayIst, "15:15", SwingParams());
        Assert.True(d.ShouldExit);
        Assert.StartsWith("Trailing SL Hit", d.Reason);
    }

    [Fact]
    public void ShortExit_IntradayTrail_RatchetsDownFromLtp()
    {
        var d = SwingTradeRules.EvaluateShortExit(Short(100m, 103m, 90m, tsl: 102m), 99m, TradingDayIst, "15:15", IntradayParams());
        Assert.False(d.ShouldExit);
        Assert.Equal(100.98m, d.NewTrailingStopLoss); // 99 x 1.02
    }

    [Fact]
    public void LongExit_IsUnchanged()
    {
        var pos = new ExitPositionView(100m, 97m, 106m, null, null, IstToUtc(TradingDayIst.AddDays(-2)));
        var target = SwingTradeRules.EvaluateExit(pos, 106.5m, TradingDayIst, 2, 20, SwingParams());
        Assert.True(target.ShouldExit);

        // Closing-basis stop: a dip below the stop at 11:00 does not sell (only the emergency stop acts intraday).
        var dip = SwingTradeRules.EvaluateExit(pos, 96.5m, TradingDayIst, 2, 20, SwingParams());
        Assert.False(dip.ShouldExit);
    }

    // ---------------- Settings validation ----------------

    [Theory]
    [InlineData("15:00", "15:15", 0)]
    [InlineData("15:15", "15:15", 1)]   // cut-off not before square-off
    [InlineData("15:00", "15:25", 1)]   // after the broker's own MIS square-off
    [InlineData("15:30", "15:25", 2)]
    public void ShortTimes_AreValidated(string cutoff, string squareOff, int errors)
    {
        Assert.Equal(errors, ShortSellingSettingsValidator.Validate(cutoff, squareOff).Count());
    }
}
