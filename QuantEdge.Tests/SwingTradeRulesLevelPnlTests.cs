using QuantEdge.Infrastructure.Services;
using Xunit;

namespace QuantEdge.Tests;

public class SwingTradeRulesLevelPnlTests
{
    [Fact]
    public void PnlAtLevel_Long_GainsAboveEntry_LosesBelow()
    {
        Assert.Equal(50m, SwingTradeRules.PnlAtLevel(100m, 105m, 10, isShort: false));
        Assert.Equal(-30m, SwingTradeRules.PnlAtLevel(100m, 97m, 10, isShort: false));
    }

    [Fact]
    public void PnlAtLevel_Short_GainsBelowEntry_LosesAbove()
    {
        // PSB short from the live screen: 273 @ 18.30, Target 16.37, SL 19.27.
        Assert.Equal(526.89m, SwingTradeRules.PnlAtLevel(18.30m, 16.37m, 273, isShort: true));
        Assert.Equal(-264.81m, SwingTradeRules.PnlAtLevel(18.30m, 19.27m, 273, isShort: true));
    }

    [Fact]
    public void PnlAtLevel_NoLevelOrQuantity_IsNull()
    {
        Assert.Null(SwingTradeRules.PnlAtLevel(100m, null, 10, isShort: false));
        Assert.Null(SwingTradeRules.PnlAtLevel(100m, 0m, 10, isShort: false));
        Assert.Null(SwingTradeRules.PnlAtLevel(100m, 105m, 0, isShort: false));
    }

    [Fact]
    public void NetPnlAtLevel_SubtractsCharges()
    {
        decimal gross = SwingTradeRules.PnlAtLevel(18.30m, 16.37m, 273, isShort: true)!.Value;
        decimal? net = SwingTradeRules.NetPnlAtLevel(18.30m, 16.37m, 273, isShort: true, DateTime.UtcNow);

        Assert.NotNull(net);
        Assert.True(net < gross);
        Assert.True(net > 0m);
        Assert.Null(SwingTradeRules.NetPnlAtLevel(18.30m, null, 273, isShort: true, DateTime.UtcNow));
    }

    [Fact]
    public void PositionLossLimit_ExitsOnceTheLossReachesTheLimit()
    {
        Assert.Null(SwingTradeRules.PositionLossLimitReason(100m, 98.6m, 10, isShort: false, 15m));     // loss 14
        Assert.NotNull(SwingTradeRules.PositionLossLimitReason(100m, 98.5m, 10, isShort: false, 15m));  // loss 15
        Assert.NotNull(SwingTradeRules.PositionLossLimitReason(100m, 101.5m, 10, isShort: true, 15m));  // short loss 15
        Assert.Null(SwingTradeRules.PositionLossLimitReason(100m, 98.5m, 10, isShort: true, 15m));      // short in profit
    }

    [Fact]
    public void PositionLossLimit_NotSet_NeverExits()
    {
        Assert.Null(SwingTradeRules.PositionLossLimitReason(100m, 50m, 10, isShort: false, null));
        Assert.Null(SwingTradeRules.PositionLossLimitReason(100m, 50m, 10, isShort: false, 0m));
    }

    [Fact]
    public void AccountDailyLossLimit_IsTenPercentOfCapital()
    {
        Assert.Equal(5000m, SwingTradeRules.AccountDailyLossLimit(50000m));
    }

    [Theory]
    // SWING_CLOSE: only once the trail has crossed the entry.
    [InlineData(101, false, true, true)]
    [InlineData(99, false, true, false)]
    [InlineData(99, true, true, true)]
    [InlineData(101, true, true, false)]
    // INTRADAY: whenever set.
    [InlineData(99, false, false, true)]
    [InlineData(101, true, false, true)]
    public void IsTrailActive_FollowsExitMode(double trail, bool isShort, bool isSwingClose, bool expected)
    {
        Assert.Equal(expected, SwingTradeRules.IsTrailActive((decimal)trail, 100m, isShort, isSwingClose));
    }

    [Fact]
    public void IsTrailActive_NoTrail_IsFalse()
    {
        Assert.False(SwingTradeRules.IsTrailActive(null, 100m, isShort: false, isSwingClose: false));
    }
}
