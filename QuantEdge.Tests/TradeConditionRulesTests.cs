using QuantEdge.Domain.Entities;
using QuantEdge.Infrastructure.Services;
using Xunit;

namespace QuantEdge.Tests;

public class TradeConditionRulesTests
{
    private static readonly StockMaster Stock = new() { Symbol = "TESTSTK" };
    private static readonly SwingStrategySettings Settings = SwingStrategySettings.Default;

    private static TradeConditionProfile Profile(string side, int? min = null, params string[] off) => new()
    {
        UserRef = "1",
        TradeMode = TradeConditionProfile.ModeAutoReal,
        Side = side,
        DisabledConditions = off.ToList(),
        MinConditionsMatch = min
    };

    private static SwingEvaluationResult Long(decimal stockPct, decimal niftyPct, decimal barPct, bool skipGate = false) =>
        SwingDecisionEngine.Evaluate(Stock,
            Candles.Daily("TESTSTK", 260, 1000m, stockPct),
            Candles.Intraday("TESTSTK", "15m", 125, 500m, barPct),
            Candles.Intraday("TESTSTK", "60m", 60, 500m, barPct),
            Candles.Daily("NIFTY 50", 260, 20000m, niftyPct), Settings, skipGate);

    private static SwingEvaluationResult Short(decimal stockPct, decimal niftyPct, decimal barPct, bool skipGate = false) =>
        SwingShortDecisionEngine.Evaluate(Stock,
            Candles.Daily("TESTSTK", 260, 1000m, stockPct),
            Candles.Intraday("TESTSTK", "15m", 125, 500m, barPct),
            Candles.Intraday("TESTSTK", "60m", 60, 500m, barPct),
            Candles.Daily("NIFTY 50", 260, 20000m, niftyPct), Settings, skipGate);

    // A result with chosen hard-filter flags and factor points (BUY factor codes).
    private static SwingEvaluationResult Synthetic(bool market = true, bool trend = true, bool adx = true,
        int breakout = 0, int volume = 0, int rs = 0, int mtf = 0, int rsi = 0, int macd = 0, int candle = 0, int rr = 0) => new()
    {
        IsMarketFilterPassed = market,
        EmaTrendPassed = trend,
        AdxPassed = adx,
        Factors = new()
        {
            new("BREAKOUT_GROUP", "Breakout", breakout, 20, "15m"),
            new("VOL_CONFIRMATION", "Volume", volume, 15, "15m"),
            new("RELATIVE_STRENGTH", "Relative strength", rs, 15, "1d"),
            new("MULTITIMEFRAME", "60m trend", mtf, 15, "60m"),
            new("RSI_MOMENTUM", "RSI", rsi, 10, "15m"),
            new("MACD_BULLISH", "MACD", macd, 10, "15m"),
            new("BULLISH_CANDLE", "Candle", candle, 8, "15m"),
            new("RISK_REWARD", "Risk:reward", rr, 7, "15m")
        }
    };

    [Theory]
    [InlineData(0.6, 0.2, 0.15)]   // strong uptrend in a rising market
    [InlineData(0.3, 0.2, 0.02)]   // weaker setup
    [InlineData(0.6, -0.2, 0.15)]  // NIFTY filter fails
    [InlineData(-0.6, 0.2, -0.15)] // stock in a downtrend
    public void DefaultProfile_MatchesTheBuyEngine(decimal stockPct, decimal niftyPct, decimal barPct)
    {
        var r = Long(stockPct, niftyPct, barPct);
        var e = TradeConditionRules.Apply(Profile("BUY"), r, Settings, regimeMode: false, fallbackMinMatch: 10);

        Assert.Equal(r.HardFiltersPassed, e.HardFiltersPassed);
        Assert.Equal(11, e.TotalCount);
        if (r.HardFiltersPassed)
        {
            Assert.Equal(r.Score, e.Score);
            Assert.Equal(r.Checklist.MetCount, e.MetCount);
            Assert.Equal(r.IsBuySignal, e.IsSignal);
            Assert.Equal(r.IsBuySignal || r.Checklist.MetCount >= 10, e.Qualifies);
        }
        else
        {
            Assert.False(e.Qualifies);
        }
    }

    [Theory]
    [InlineData(-0.6, -0.1, -0.15)] // falling stock in a falling market
    [InlineData(-0.6, 0.2, -0.15)]  // NIFTY not bearish
    [InlineData(0.6, -0.1, 0.15)]   // rising stock
    public void DefaultProfile_MatchesTheShortEngine(decimal stockPct, decimal niftyPct, decimal barPct)
    {
        var r = Short(stockPct, niftyPct, barPct);
        var e = TradeConditionRules.Apply(Profile("SHORT"), r, Settings, regimeMode: false, fallbackMinMatch: 10);

        Assert.Equal(r.HardFiltersPassed, e.HardFiltersPassed);
        if (r.HardFiltersPassed)
        {
            Assert.Equal(r.Score, e.Score);
            Assert.Equal(r.Checklist.MetCount, e.MetCount);
            Assert.Equal(r.IsSellSignal, e.IsSignal);
        }
    }

    [Fact]
    public void SkipHardFilterGate_ScoresTheStockButKeepsTheFlags()
    {
        var gated = Short(-0.6m, 0.2m, -0.15m);
        var ungated = Short(-0.6m, 0.2m, -0.15m, skipGate: true);

        Assert.Equal("REJECT", gated.Decision);
        Assert.Empty(gated.Factors);
        Assert.False(ungated.IsMarketFilterPassed);
        Assert.False(ungated.HardFiltersPassed);
        Assert.Equal(8, ungated.Factors.Count);
    }

    [Fact]
    public void MarketConditionOff_ShortIsJudgedWithoutTheNiftyFilter()
    {
        var gated = Short(-0.6m, 0.2m, -0.15m);
        int calls = 0;

        var (e, scored) = TradeConditionRules.Evaluate(Profile("SHORT", null, TradeConditionRules.Market), gated,
            () => { calls++; return Short(-0.6m, 0.2m, -0.15m, skipGate: true); }, Settings, false, 10);

        Assert.Equal(1, calls);
        Assert.True(e.HardFiltersPassed, e.Reason);
        Assert.Equal(10, e.TotalCount);
        Assert.Equal(8, scored.Factors.Count);
        Assert.True(scored.StopLoss > scored.EntryPrice);
        Assert.Equal(e.Score >= Settings.BuyScoreThreshold, e.IsSignal);
    }

    [Fact]
    public void RescoringIsSkippedWhenAnEnabledHardFilterFailed()
    {
        var gated = Short(-0.6m, 0.2m, -0.15m); // NIFTY not bearish
        int calls = 0;

        var (e, _) = TradeConditionRules.Evaluate(Profile("SHORT", null, TradeConditionRules.Adx), gated,
            () => { calls++; return gated; }, Settings, false, 10);

        Assert.Equal(0, calls);
        Assert.False(e.Qualifies);
        Assert.Contains("NIFTY Bearish Filter", e.FailedHardFilters);
    }

    [Fact]
    public void ScoreIsRescaledToTheScoringConditionsThatAreOn()
    {
        var r = Synthetic(rsi: 10, macd: 0, breakout: 20, volume: 15);
        var onlyRsiMacd = Profile("BUY", null, "BREAKOUT", "VOLUME", "REL_STRENGTH", "MTF_60M", "CANDLE", "RISK_REWARD");

        var e = TradeConditionRules.Apply(onlyRsiMacd, r, Settings, false, 10);

        Assert.Equal(20, e.EnabledMaxPoints);
        Assert.Equal(50, e.Score);          // 10 of 20 points
        Assert.Equal(5, e.TotalCount);      // 3 hard + RSI + MACD
        Assert.Equal(4, e.MetCount);        // 3 hard + RSI
        Assert.Equal(5, e.MinConditionsMatch); // the mode's 10, clamped to the 5 conditions ON
        Assert.False(e.IsSignal);
        Assert.False(e.Qualifies);
    }

    [Fact]
    public void MinConditionsMatch_IsClampedToTheConditionsThatAreOn()
    {
        var r = Synthetic(rsi: 10, macd: 10);
        var p = Profile("BUY", 10, "BREAKOUT", "VOLUME", "REL_STRENGTH", "MTF_60M", "CANDLE", "RISK_REWARD");

        var e = TradeConditionRules.Apply(p, r, Settings, false, 10);

        Assert.Equal(5, e.MinConditionsMatch);
        Assert.Equal(100, e.Score);
        Assert.True(e.IsSignal);
        Assert.True(e.Qualifies);
    }

    [Fact]
    public void HardFilterOff_NoLongerRejects()
    {
        var r = Synthetic(adx: false, breakout: 20, volume: 15, rs: 15, mtf: 15, rsi: 10, macd: 10, candle: 8, rr: 7);

        Assert.False(TradeConditionRules.Apply(Profile("BUY"), r, Settings, false, 10).Qualifies);

        var e = TradeConditionRules.Apply(Profile("BUY", null, TradeConditionRules.Adx), r, Settings, false, 10);
        Assert.True(e.HardFiltersPassed);
        Assert.Equal(100, e.Score);
        Assert.True(e.Qualifies);
    }

    [Fact]
    public void MetCountPath_UsesTheProfileMinimum()
    {
        // 3 hard + 5 scoring met = 8 of 11, score 59: no signal, but 8 >= min 8.
        var r = Synthetic(breakout: 20, volume: 10, mtf: 15, rsi: 7, rr: 7);

        var e = TradeConditionRules.Apply(Profile("BUY", 8), r, Settings, false, 10);

        Assert.False(e.IsSignal);
        Assert.Equal(8, e.MetCount);
        Assert.True(e.Qualifies, e.Reason);
        Assert.False(TradeConditionRules.Apply(Profile("BUY", 9), r, Settings, false, 10).Qualifies);
    }

    [Fact]
    public void NoScoringConditionOn_NeverQualifies()
    {
        var r = Synthetic(breakout: 20);
        var p = Profile("BUY", 1, "BREAKOUT", "VOLUME", "REL_STRENGTH", "MTF_60M", "RSI", "MACD", "CANDLE", "RISK_REWARD");

        var e = TradeConditionRules.Apply(p, r, Settings, false, 10);

        Assert.False(e.Qualifies);
        Assert.Equal(0, e.EnabledMaxPoints);
    }

    [Fact]
    public void RegimeMode_BuyMarketConditionIsTheRegimes()
    {
        var p = Profile("BUY");
        var policy = new RegimePolicy { Regime = MarketRegimes.Sideways, MinStockScore = 78, RequireRelativeStrength = true };
        var r = Synthetic(market: false, breakout: 20, volume: 15, mtf: 15, rsi: 10, macd: 10, candle: 8, rr: 7, rs: 0);

        var e = TradeConditionRules.Apply(p, r, Settings, regimeMode: true, fallbackMinMatch: 10, policy);

        Assert.Equal(10, e.TotalCount);
        Assert.True(e.HardFiltersPassed);   // NIFTY is not the profile's in REGIME mode
        Assert.Equal(85, e.Score);
        Assert.False(e.Qualifies);          // policy requires beating NIFTY
        Assert.Equal(10, TradeConditionRules.ConfigurableCount(isShort: false, regimeMode: true));
        Assert.Equal(11, TradeConditionRules.ConfigurableCount(isShort: true, regimeMode: true));
    }
}
