using QuantEdge.Domain.Entities;
using QuantEdge.Infrastructure.Services;
using Xunit;

namespace QuantEdge.Tests;

/// <summary>
/// BREAKOUT_GROUP (BUY) / BREAKDOWN_GROUP (SHORT) only ever recorded a single pass/fail for the whole group,
/// so a stock qualifying merely by being near its 52-week extreme (a short-squeeze/mean-reversion risk) looked
/// identical to a fresh breakout/breakdown. BreakoutSubType/BreakdownSubType make that distinction visible.
/// </summary>
public class SwingBreakoutSubTypeTests
{
    private static readonly StockMaster Stock = new() { Symbol = "TESTSTK" };

    [Fact]
    public void BreakoutSubType_SteadyUptrend_RecordsPriorHigh()
    {
        var daily = Candles.Daily("TESTSTK", 260, 1000m, 0.6m);
        var c15 = Candles.Intraday("TESTSTK", "15m", 125, 500m, 0.15m);
        var c60 = Candles.Intraday("TESTSTK", "60m", 60, 500m, 0.15m);
        var nifty = Candles.Daily("NIFTY 50", 260, 20000m, 0.1m);

        var r = SwingDecisionEngine.Evaluate(Stock, daily, c15, c60, nifty);

        Assert.NotEqual("NONE", r.BreakoutSubType);
        Assert.Contains("PRIOR_HIGH", r.BreakoutSubType);
        Assert.Equal("NONE", r.BreakdownSubType); // the other side's field stays untouched
    }

    [Fact]
    public void BreakoutSubType_SteadyDowntrend_IsNone()
    {
        var daily = Candles.Daily("TESTSTK", 260, 1000m, -0.6m);
        var c15 = Candles.Intraday("TESTSTK", "15m", 125, 500m, -0.15m);
        var c60 = Candles.Intraday("TESTSTK", "60m", 60, 500m, -0.15m);
        var nifty = Candles.Daily("NIFTY 50", 260, 20000m, -0.1m);

        var r = SwingDecisionEngine.Evaluate(Stock, daily, c15, c60, nifty);

        Assert.Equal("NONE", r.BreakoutSubType);
    }

    [Fact]
    public void BreakdownSubType_SteadyDowntrend_RecordsPriorLow()
    {
        var daily = Candles.Daily("TESTSTK", 260, 1000m, -0.6m);
        var c15 = Candles.Intraday("TESTSTK", "15m", 125, 500m, -0.15m);
        var c60 = Candles.Intraday("TESTSTK", "60m", 60, 500m, -0.15m);
        var nifty = Candles.Daily("NIFTY 50", 260, 20000m, -0.1m);

        var r = SwingShortDecisionEngine.Evaluate(Stock, daily, c15, c60, nifty);

        Assert.NotEqual("NONE", r.BreakdownSubType);
        Assert.Contains("PRIOR_LOW", r.BreakdownSubType);
        Assert.Equal("NONE", r.BreakoutSubType); // the other side's field stays untouched
    }

    [Fact]
    public void BreakdownSubType_SteadyUptrend_IsNone()
    {
        var daily = Candles.Daily("TESTSTK", 260, 1000m, 0.6m);
        var c15 = Candles.Intraday("TESTSTK", "15m", 125, 500m, 0.15m);
        var c60 = Candles.Intraday("TESTSTK", "60m", 60, 500m, 0.15m);
        var nifty = Candles.Daily("NIFTY 50", 260, 20000m, 0.1m);

        var r = SwingShortDecisionEngine.Evaluate(Stock, daily, c15, c60, nifty);

        Assert.Equal("NONE", r.BreakdownSubType);
    }
}
