using QuantEdge.Domain.Entities;
using QuantEdge.Infrastructure.Services;
using Xunit;

namespace QuantEdge.Tests;

/// <summary>
/// LONG (swing) trailing-SL activation is configurable via TrailActivationPct (0 = disabled, falls back to
/// the fixed 1x-ATR activation). SHORT's activation must stay on the fixed ATR default regardless of this
/// setting - see SwingTradeRules.GetTrailActivationPrice / EvaluateShortExit.
/// </summary>
public class SwingTradeRulesTrailActivationTests : ClockedTest
{
    // Entry 100, StopLoss 97 -> InferAtr = max((100-97)/1.5, 1) = 2. Default activation (0 = disabled) is
    // entry + 1x ATR = 102. Opened 2 trading days ago so the entry-day guard and max-duration don't interfere,
    // and evaluated inside the closing window (CloseCheckTime 15:15) where trail management runs.
    private static ExitPositionView LongPos() => new(100m, 97m, null, null, null, IstToUtc(TradingDayIst.AddDays(-2)));
    private static readonly DateTime ClosingWindow = TradingDayIst.Date.AddHours(15).AddMinutes(20);

    [Fact]
    public void TrailActivationPct_Zero_UsesDefaultOneAtrActivation()
    {
        var p = SwingTradeParams.From(new AutoTradeSettings { TrailActivationPct = 0m });

        var beforeActivation = SwingTradeRules.EvaluateExit(LongPos(), 101.9m, ClosingWindow, 2, 20, p);
        Assert.False(beforeActivation.ShouldExit);
        Assert.Null(beforeActivation.NewTrailingStopLoss); // not yet activated

        var atActivation = SwingTradeRules.EvaluateExit(LongPos(), 102m, ClosingWindow, 2, 20, p);
        Assert.False(atActivation.ShouldExit);
        Assert.Equal(100m, atActivation.NewTrailingStopLoss); // activates at breakeven
    }

    [Theory]
    [InlineData(1.0, 101.0)]   // entry x 1.01
    [InlineData(1.5, 101.5)]   // entry x 1.015
    public void TrailActivationPct_Set_ActivatesAtThatFlatGainInstead(double pct, double activationPrice)
    {
        var p = SwingTradeParams.From(new AutoTradeSettings { TrailActivationPct = (decimal)pct });
        decimal justBelow = (decimal)activationPrice - 0.1m;

        var beforeActivation = SwingTradeRules.EvaluateExit(LongPos(), justBelow, ClosingWindow, 2, 20, p);
        Assert.False(beforeActivation.ShouldExit);
        Assert.Null(beforeActivation.NewTrailingStopLoss);

        var atActivation = SwingTradeRules.EvaluateExit(LongPos(), (decimal)activationPrice, ClosingWindow, 2, 20, p);
        Assert.False(atActivation.ShouldExit);
        Assert.Equal(100m, atActivation.NewTrailingStopLoss); // still floors at breakeven, same as the default path
    }

    [Fact]
    public void TrailActivationPct_DoesNotAffectShortActivation()
    {
        // Entry 100, StopLoss 103 -> InferShortAtr = max((103-100)/0.6, 1) = 5. Fixed SHORT activation stays at
        // entry - 1x ATR = 95 no matter what TrailActivationPct is set to.
        var pos = new ExitPositionView(100m, 103m, 90m, null, null, IstToUtc(TradingDayIst.AddHours(-1)));
        var disabled = SwingTradeParams.From(new AutoTradeSettings { TrailActivationPct = 0m });
        var extreme = SwingTradeParams.From(new AutoTradeSettings { TrailActivationPct = 5m });

        var holdDisabled = SwingTradeRules.EvaluateShortExit(pos, 95.1m, TradingDayIst, "23:59", disabled);
        var holdExtreme = SwingTradeRules.EvaluateShortExit(pos, 95.1m, TradingDayIst, "23:59", extreme);
        Assert.False(holdDisabled.ShouldExit);
        Assert.Null(holdDisabled.NewTrailingStopLoss);
        Assert.Equal(holdDisabled, holdExtreme);

        var activeDisabled = SwingTradeRules.EvaluateShortExit(pos, 95m, TradingDayIst, "23:59", disabled);
        var activeExtreme = SwingTradeRules.EvaluateShortExit(pos, 95m, TradingDayIst, "23:59", extreme);
        Assert.Equal(100m, activeDisabled.NewTrailingStopLoss); // breakeven ceiling
        Assert.Equal(activeDisabled, activeExtreme);
    }
}
