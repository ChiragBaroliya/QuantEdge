using QuantEdge.Domain.Entities;
using QuantEdge.Infrastructure.Services;
using Xunit;

namespace QuantEdge.Tests;

public class SwingShortDecisionEngineTests
{
    private static readonly StockMaster Stock = new() { Symbol = "TESTSTK" };

    private static SwingEvaluationResult EvaluateShort(decimal stockDailyPct, decimal niftyDailyPct, decimal barPct, bool withNifty = true) =>
        SwingShortDecisionEngine.Evaluate(Stock,
            Candles.Daily("TESTSTK", 260, 1000m, stockDailyPct),
            Candles.Intraday("TESTSTK", "15m", 125, 500m, barPct),
            Candles.Intraday("TESTSTK", "60m", 60, 500m, barPct),
            withNifty ? Candles.Daily("NIFTY 50", 260, 20000m, niftyDailyPct) : new List<MarketCandle>());

    [Fact]
    public void FallingStockInFallingMarket_IsAShortSignal()
    {
        var r = EvaluateShort(stockDailyPct: -0.6m, niftyDailyPct: -0.1m, barPct: -0.15m);

        Assert.True(r.HardFiltersPassed, r.Reason);
        Assert.True(r.IsSellSignal, r.Reason);
        Assert.False(r.IsBuySignal);
        Assert.Equal(SwingShortDecisionEngine.ShortDecision, r.Decision);
        Assert.Equal(11, r.Checklist.TotalCount);
        // Mirrored levels: stop above the entry, targets below.
        Assert.True(r.StopLoss > r.EntryPrice);
        Assert.True(r.Target1 < r.EntryPrice);
        Assert.True(r.DailyAtr > 0m);
    }

    [Fact]
    public void BullishMarket_BlocksShorts()
    {
        var r = EvaluateShort(stockDailyPct: -0.6m, niftyDailyPct: 0.2m, barPct: -0.15m);

        Assert.False(r.HardFiltersPassed);
        Assert.False(r.IsSellSignal);
        Assert.Equal("REJECT", r.Decision);
    }

    [Fact]
    public void MissingNiftyData_FailsClosed()
    {
        var r = EvaluateShort(stockDailyPct: -0.6m, niftyDailyPct: -0.1m, barPct: -0.15m, withNifty: false);

        Assert.False(r.IsSellSignal);
        Assert.False(SwingShortDecisionEngine.IsNiftyBearishFilterPassed(null));
        Assert.False(SwingShortDecisionEngine.IsNiftyBearishFilterPassed(new List<MarketCandle>()));
    }

    [Fact]
    public void RisingStock_IsNeverShorted()
    {
        var r = EvaluateShort(stockDailyPct: 0.6m, niftyDailyPct: -0.1m, barPct: 0.15m);

        Assert.False(r.HardFiltersPassed);
        Assert.False(r.IsSellSignal);
    }

    [Fact]
    public void LongEngine_DoesNotBuyTheShortSetup_AndStillBuysAnUptrend()
    {
        var down = SwingDecisionEngine.Evaluate(Stock,
            Candles.Daily("TESTSTK", 260, 1000m, -0.6m),
            Candles.Intraday("TESTSTK", "15m", 125, 500m, -0.15m),
            Candles.Intraday("TESTSTK", "60m", 60, 500m, -0.15m),
            Candles.Daily("NIFTY 50", 260, 20000m, -0.1m));
        Assert.False(down.IsBuySignal);

        var up = SwingDecisionEngine.Evaluate(Stock,
            Candles.Daily("TESTSTK", 260, 1000m, 0.6m),
            Candles.Intraday("TESTSTK", "15m", 125, 500m, 0.15m),
            Candles.Intraday("TESTSTK", "60m", 60, 500m, 0.15m),
            Candles.Daily("NIFTY 50", 260, 20000m, 0.1m));
        Assert.True(up.HardFiltersPassed, up.Reason);
        Assert.False(up.IsSellSignal); // the long engine never sets the short flag
    }
}
