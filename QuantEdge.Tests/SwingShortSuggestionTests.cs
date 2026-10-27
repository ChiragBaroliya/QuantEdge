using QuantEdge.Domain.Entities;
using QuantEdge.Infrastructure.DTOs;
using QuantEdge.Infrastructure.Services;
using Xunit;

namespace QuantEdge.Tests;

/// <summary>Swing Trading suggestions: which side (long / short) a stock is shown as, and the short levels shown.</summary>
public class SwingShortSuggestionTests
{
    private static readonly StockMaster Stock = new() { Symbol = "TESTSTK" };

    private static (SwingEvaluationResult Result, string Side) Suggest(decimal stockDailyPct, decimal niftyDailyPct, decimal barPct)
    {
        var daily = Candles.Daily("TESTSTK", 260, 1000m, stockDailyPct);
        var c15 = Candles.Intraday("TESTSTK", "15m", 125, 500m, barPct);
        var c60 = Candles.Intraday("TESTSTK", "60m", 60, 500m, barPct);
        var nifty = Candles.Daily("NIFTY 50", 260, 20000m, niftyDailyPct);
        return SwingTradingService.PickSuggestion(
            SwingDecisionEngine.Evaluate(Stock, daily, c15, c60, nifty),
            SwingShortDecisionEngine.Evaluate(Stock, daily, c15, c60, nifty));
    }

    [Fact]
    public void FallingStockInFallingMarket_IsSuggestedAsShort()
    {
        var (r, side) = Suggest(stockDailyPct: -0.6m, niftyDailyPct: -0.1m, barPct: -0.15m);

        Assert.Equal(SwingSignalSide.Short, side);
        Assert.Equal(SwingShortDecisionEngine.ShortDecision, r.Decision);
        Assert.True(r.IsSellSignal);
    }

    [Fact]
    public void RisingStockInRisingMarket_IsSuggestedAsLong()
    {
        var (r, side) = Suggest(stockDailyPct: 0.6m, niftyDailyPct: 0.1m, barPct: 0.15m);

        Assert.Equal(SwingSignalSide.Long, side);
        Assert.True(r.HardFiltersPassed, r.Reason);
        Assert.False(r.IsSellSignal);
    }

    [Fact]
    public void NoSetupOnEitherSide_KeepsTheLongRejection()
    {
        // Rising stock in a falling market: the long engine's NIFTY gate rejects it, the short engine's downtrend filter too.
        var (r, side) = Suggest(stockDailyPct: 0.6m, niftyDailyPct: -0.1m, barPct: 0.15m);

        Assert.Equal(SwingSignalSide.Long, side);
        Assert.Equal("REJECT", r.Decision);
        Assert.DoesNotContain("Short", r.Reason);
    }

    [Fact]
    public void ShortWatch_IsPickedWithShortSide()
    {
        var longReject = new SwingEvaluationResult { Decision = "REJECT" };
        var shortWatch = new SwingEvaluationResult { Decision = "WATCH", HardFiltersPassed = true, Score = 55 };

        var (r, side) = SwingTradingService.PickSuggestion(longReject, shortWatch);

        Assert.Same(shortWatch, r);
        Assert.Equal(SwingSignalSide.Short, side);
    }

    [Fact]
    public void ShortBotLevels_StopAboveEntry_TargetBelow()
    {
        var r = new SwingEvaluationResult { EntryPrice = 1000m, DailyAtr = 20m, StopLoss = 1030m, Target1 = 940m };
        var p = SwingTradeParams.From(new RealTradeSettings());

        var (sl, target, rr) = SwingTradeRules.ShortBotLevelsFor(r, p);

        Assert.True(sl > r.EntryPrice);
        Assert.True(target < r.EntryPrice);
        Assert.True(rr > 0m);
        // Same levels a short entry would actually be placed with.
        var placed = SwingTradeRules.ComputeShortEntryLevels(1000m, 20m, 1030m, 940m, p);
        Assert.Equal(placed.StopLoss, sl);
        Assert.Equal(placed.TakeProfit, target);
    }

    [Fact]
    public void ShortBotLevels_NoEntryPrice_IsZero()
    {
        var levels = SwingTradeRules.ShortBotLevelsFor(new SwingEvaluationResult(), SwingTradeParams.From(new RealTradeSettings()));
        Assert.Equal((0m, 0m, 0m), levels);
    }
}
