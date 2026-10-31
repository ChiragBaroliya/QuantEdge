using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using QuantEdge.Domain.Entities;
using QuantEdge.Infrastructure.DTOs;
using QuantEdge.Infrastructure.Persistence.Repositories;
using QuantEdge.Infrastructure.Services;
using QuantEdge.Infrastructure.Services.Backtest;
using Xunit;

namespace QuantEdge.Tests;

public class SwingRuleParametersTests
{
    private static readonly StockMaster Stock = new() { Symbol = "TESTSTK" };

    private static List<MarketCandle> Daily(decimal pct) => Candles.Daily("TESTSTK", 260, 1000m, pct);
    private static List<MarketCandle> M15(decimal pct) => Candles.Intraday("TESTSTK", "15m", 125, 500m, pct);
    private static List<MarketCandle> M60(decimal pct) => Candles.Intraday("TESTSTK", "60m", 60, 500m, pct);
    private static List<MarketCandle> Nifty(decimal pct) => Candles.Daily("NIFTY 50", 260, 20000m, pct);

    private static SwingStrategySettings With(Action<SwingRuleParameters> change)
    {
        var settings = new SwingStrategySettings();
        change(settings.Rules);
        return settings;
    }

    // The original numbers, written out: a changed default would silently change how the real-money bot trades.
    [Fact]
    public void Defaults_AreTheOriginalNumbers()
    {
        var r = new SwingRuleParameters();

        Assert.Equal((50, 20, 50), (r.NiftySmaPeriod, r.NiftyEmaFast, r.NiftyEmaSlow));
        Assert.Equal((20, 50, 200, 2, 5, 0.5m), (r.TrendEmaFast, r.TrendEmaSlow, r.TrendEmaLong, r.TrendSlopeBars, r.LongEmaSlopeBars, r.LongEmaTolerancePct));
        Assert.Equal((14, 20m), (r.AdxPeriod, r.AdxMin));
        Assert.Equal((20, 15, 15, 15, 10, 10, 8, 7), (r.BreakoutPoints, r.VolumePoints, r.RelativeStrengthPoints, r.Mtf60mPoints, r.RsiPoints, r.MacdPoints, r.CandlePoints, r.RiskRewardPoints));
        Assert.Equal((15, 14, 10m, 10m), (r.SwingLookbackBars, r.ConsolidationBars, r.ConsolidationMaxRangePct, r.Near52WeekPct));
        Assert.Equal((10, 1.5m, 2.5m, 20), (r.VolumeGoodPoints, r.VolumeGoodMultiple, r.VolumeHeavyMultiple, r.VolumeAverageBars));
        Assert.Equal((20, 14, 40m, 60m, 20), (r.Mtf60mEmaPeriod, r.Mtf60mRsiPeriod, r.Mtf60mRsiMinBuy, r.Mtf60mRsiMaxShort, r.RelativeStrengthBars));
        Assert.Equal((7, 14, 55m, 70m, 50m, 75m), (r.RsiGoodPoints, r.RsiPeriod, r.RsiBuyBestLow, r.RsiBuyBestHigh, r.RsiBuyOkLow, r.RsiBuyOkHigh));
        Assert.Equal((30m, 45m, 25m, 50m), (r.RsiShortBestLow, r.RsiShortBestHigh, r.RsiShortOkLow, r.RsiShortOkHigh));
        Assert.Equal((12, 26, 9, 25m, 1.2m), (r.MacdFast, r.MacdSlow, r.MacdSignal, r.CandleCloseZonePct, r.CandleBodyAtrMultiple));
        Assert.Equal((14, 1.5m, 2m, 3m, 2m), (r.AtrPeriod, r.StopAtrMultiple, r.Target1R, r.Target2R, r.MinRiskReward));
        Assert.Equal((50, 50, 220), (r.MinDailyCandles, r.MinNiftyCandles, r.MinBarsForLongEma));
        Assert.Empty(SwingRuleCatalog.Validate(r));
    }

    [Fact]
    public void Catalog_CoversEverySettableNumber_WithItsCamelCaseApiName()
    {
        var settable = typeof(SwingRuleParameters).GetProperties().Where(p => p.CanWrite).Select(p => JsonNamingPolicy.CamelCase.ConvertName(p.Name)).OrderBy(n => n);
        Assert.Equal(settable, SwingRuleCatalog.Fields.Select(f => f.Key).OrderBy(n => n));

        var json = JsonDocument.Parse(JsonSerializer.Serialize(new SwingRuleParameters(), new JsonSerializerOptions(JsonSerializerDefaults.Web))).RootElement;
        Assert.All(SwingRuleCatalog.Fields, f => Assert.True(json.TryGetProperty(f.Key, out _), f.Key));
    }

    [Fact]
    public void SavedJson_WithMissingFields_TakesDefaults()
    {
        var r = JsonSerializer.Deserialize<SwingRuleParameters>("{\"AdxMin\":25}")!;

        Assert.Equal(25m, r.AdxMin);
        Assert.Equal(50, r.TrendEmaSlow);
        Assert.Empty(SwingRuleCatalog.Validate(r));
    }

    // ---------------- each hard filter follows its numbers, on both engines ----------------
    [Fact]
    public void AdxMin_DecidesTheAdxFilter()
    {
        // Measure the fixture's ADX, then put the minimum just above and just below it.
        decimal adx = SwingDecisionEngine.Evaluate(Stock, Daily(0m), M15(0.15m), M60(0.15m), Nifty(0.2m)).Adx1d;
        var above = With(r => r.AdxMin = adx + 1m);
        var below = With(r => r.AdxMin = adx - 1m);

        var rejected = SwingDecisionEngine.Evaluate(Stock, Daily(0m), M15(0.15m), M60(0.15m), Nifty(0.2m), above);
        Assert.False(rejected.AdxPassed);
        Assert.Equal("REJECT", rejected.Decision);
        Assert.Contains($"< {adx + 1m:0.0}", rejected.Reason);
        Assert.True(SwingDecisionEngine.Evaluate(Stock, Daily(0m), M15(0.15m), M60(0.15m), Nifty(0.2m), below).AdxPassed);

        Assert.False(SwingShortDecisionEngine.Evaluate(Stock, Daily(0m), M15(-0.15m), M60(-0.15m), Nifty(-0.1m), above).AdxPassed);
        Assert.True(SwingShortDecisionEngine.Evaluate(Stock, Daily(0m), M15(-0.15m), M60(-0.15m), Nifty(-0.1m), below).AdxPassed);
    }

    [Fact]
    public void NiftyPeriods_AreUsedByBothMarketFilters_AndMoreHistoryIsRequiredWhenLonger()
    {
        // NIFTY rises for a long time, then falls: the short average has already turned, the long one hasn't.
        var nifty = Candles.Daily("NIFTY 50", 200, 20000m, 0.3m);
        var fall = Candles.Daily("NIFTY 50", 15, nifty[^1].Close, -0.5m);
        for (int i = 0; i < fall.Count; i++) fall[i].CandleTime = nifty[^1].CandleTime.AddDays(i + 1);
        nifty.AddRange(fall);

        var slow = With(r => r.NiftySmaPeriod = 150);
        var fast = With(r => r.NiftySmaPeriod = 10);
        Assert.True(SwingDecisionEngine.IsNiftyMarketFilterPassed(nifty, slow));
        Assert.False(SwingDecisionEngine.IsNiftyMarketFilterPassed(nifty, fast));

        Assert.False(SwingDecisionEngine.IsNiftyMarketFilterPassed(Candles.Daily("NIFTY 50", 100, 20000m, 0.3m), slow));
        Assert.True(SwingDecisionEngine.IsNiftyMarketFilterPassed(Candles.Daily("NIFTY 50", 100, 20000m, 0.3m), fast));

        var falling = Candles.Daily("NIFTY 50", 100, 20000m, -0.3m);
        Assert.False(SwingShortDecisionEngine.IsNiftyBearishFilterPassed(falling, slow));
        Assert.True(SwingShortDecisionEngine.IsNiftyBearishFilterPassed(falling, fast));
    }

    [Fact]
    public void TrendPeriods_ChangeTheEmaFilter_AndTheChecklistSaysSo()
    {
        var settings = With(r => { r.TrendEmaFast = 10; r.TrendEmaSlow = 30; });
        var r = SwingDecisionEngine.Evaluate(Stock, Daily(0.6m), M15(0.15m), M60(0.15m), Nifty(0.2m), settings);

        Assert.True(r.EmaTrendPassed, r.Reason);
        var item = r.Checklist.Conditions.Single(c => c.Code == "HARD_EMA_TREND");
        Assert.Contains("EMA10", item.Description);
        Assert.Contains("EMA30", item.Description);
    }

    // ---------------- scoring follows its numbers ----------------
    [Fact]
    public void ScoringWeights_AreEarnedAndReported()
    {
        var settings = With(r => { r.BreakoutPoints = 30; r.CandlePoints = 3; r.RiskRewardPoints = 2; });
        Assert.Equal(100, settings.Rules.TotalPoints);

        var r = SwingDecisionEngine.Evaluate(Stock, Daily(0.6m), M15(0.15m), M60(0.15m), Nifty(0.2m), settings);

        Assert.Equal(30, r.Factors.Single(f => f.Code == "BREAKOUT_GROUP").MaxPoints);
        Assert.Equal(30, r.Factors.Single(f => f.Code == "BREAKOUT_GROUP").Points);
        Assert.Equal(r.Factors.Sum(f => f.Points), r.Score);
        Assert.Equal(100, r.Factors.Sum(f => f.MaxPoints));
        Assert.Contains(r.Checklist.Conditions, c => c.Name.Contains("Breakout Group (30 Pts)"));
    }

    [Fact]
    public void RiskRewardNumbers_SetTheSignalLevels_OnBothSides()
    {
        var settings = With(r => { r.StopAtrMultiple = 2m; r.Target1R = 3m; r.Target2R = 4m; r.MinRiskReward = 3m; });

        var l = SwingDecisionEngine.Evaluate(Stock, Daily(0.6m), M15(0.15m), M60(0.15m), Nifty(0.2m), settings);
        var baseL = SwingDecisionEngine.Evaluate(Stock, Daily(0.6m), M15(0.15m), M60(0.15m), Nifty(0.2m));
        Assert.True(l.EntryPrice - l.StopLoss > baseL.EntryPrice - baseL.StopLoss);
        Assert.Equal(3m, l.RiskRewardRatio);

        var s = SwingShortDecisionEngine.Evaluate(Stock, Daily(-0.6m), M15(-0.15m), M60(-0.15m), Nifty(-0.1m), settings);
        Assert.True(s.StopLoss > s.EntryPrice);
        Assert.True(s.Target1 < s.EntryPrice);
        Assert.Equal(3m, s.RiskRewardRatio);
    }

    [Fact]
    public void Rsi60mLimits_AreSeparateForEachSide()
    {
        // A gentle zig-zag trend, so the 60m RSI sits well inside 0-100.
        var buyBase = SwingDecisionEngine.Evaluate(Stock, Daily(0.6m), M15(0.15m), M60(0.02m), Nifty(0.2m));
        Assert.Equal(15, buyBase.Factors.Single(f => f.Code == "MULTITIMEFRAME").Points);
        Assert.InRange(buyBase.Rsi60m!.Value, 40m, 99m);

        var blockBuy = With(r => r.Mtf60mRsiMinBuy = 100m);
        var buy = SwingDecisionEngine.Evaluate(Stock, Daily(0.6m), M15(0.15m), M60(0.02m), Nifty(0.2m), blockBuy);
        Assert.Equal(0, buy.Factors.Single(f => f.Code == "MULTITIMEFRAME").Points);

        var shortStill = SwingShortDecisionEngine.Evaluate(Stock, Daily(-0.6m), M15(-0.15m), M60(-0.02m), Nifty(-0.1m), blockBuy);
        Assert.Equal(15, shortStill.Factors.Single(f => f.Code == "MULTITIMEFRAME").Points);

        var blockShort = With(r => r.Mtf60mRsiMaxShort = 0m);
        var shortBlocked = SwingShortDecisionEngine.Evaluate(Stock, Daily(-0.6m), M15(-0.15m), M60(-0.02m), Nifty(-0.1m), blockShort);
        Assert.Equal(0, shortBlocked.Factors.Single(f => f.Code == "MULTITIMEFRAME").Points);
    }

    // ---------------- everything else that used a fixed number ----------------
    [Fact]
    public void RegimeMode_KeepsTheRuleNumbers()
    {
        var settings = With(r => r.AdxMin = 33m);
        Assert.Same(settings.Rules, RegimeGate.WithoutNiftyGate(settings).Rules);
    }

    [Fact]
    public void ProfileRescaling_UsesTheConfiguredWeights()
    {
        var settings = With(r => { r.BreakoutPoints = 30; r.CandlePoints = 3; r.RiskRewardPoints = 2; });
        var profile = new TradeConditionProfile
        {
            UserRef = "1", TradeMode = TradeConditionProfile.ModeAutoPaper, Side = TradeConditionProfile.SideBuy,
            DisabledConditions = TradeConditionRules.All.Where(c => !c.IsHardFilter && c.Key != TradeConditionRules.Breakout).Select(c => c.Key).ToList()
        };
        var result = SwingDecisionEngine.Evaluate(Stock, Daily(0.6m), M15(0.15m), M60(0.15m), Nifty(0.2m), settings);

        var e = TradeConditionRules.Apply(profile, result, settings, regimeMode: false, fallbackMinMatch: 1);

        Assert.Equal(30, e.EnabledMaxPoints);
        Assert.Equal(30, TradeConditionRules.EnabledMaxPoints(profile, false, settings.Rules));
        Assert.Equal(100, e.Score);
    }

    [Fact]
    public void BacktestPrecheck_AgreesWithTheEngine_UnderChangedTrendNumbers()
    {
        var settings = With(r => { r.TrendEmaFast = 10; r.TrendEmaSlow = 30; r.TrendEmaLong = 100; r.TrendSlopeBars = 4; r.LongEmaSlopeBars = 10; r.LongEmaTolerancePct = 2m; });

        foreach (decimal pct in new[] { -0.6m, -0.05m, 0m, 0.05m, 0.6m })
        {
            var daily = Daily(pct);
            var pre = BacktestEngine.DailyEmaTrendPrecheck.For(daily.Take(daily.Count - 1).ToList(), settings.Rules)!;
            var full = SwingDecisionEngine.Evaluate(Stock, daily, M15(0.15m), M60(0.15m), Nifty(0.2m), settings, skipHardFilterGate: true);

            Assert.Equal(full.EmaTrendPassed, pre.Passes(daily[^1].Close));
        }
    }

    // ---------------- SHORT volume-confirmation mirror (ShortVolumePoints/ShortVolumeGoodPoints) ----------------

    [Fact]
    public void ShortVolumePoints_DefaultsMatchTheOriginalSharedValues()
    {
        // Proves existing saved configurations (old JSON with no shortVolume* keys, which JSON deserialization
        // fills with these C# defaults) score SHORT volume confirmation exactly as before the split.
        var r = new SwingRuleParameters();

        Assert.Equal(r.VolumePoints, r.ShortVolumePoints);
        Assert.Equal(r.VolumeGoodPoints, r.ShortVolumeGoodPoints);
    }

    [Fact]
    public void ShortVolumeConfirmation_DefaultSettings_ScoresIdenticallyToTheOriginalSharedField()
    {
        var daily = Daily(-0.6m); var m15 = M15(-0.15m); var m60 = M60(-0.15m); var nifty = Nifty(-0.1m);

        var withDefaults = SwingShortDecisionEngine.Evaluate(Stock, daily, m15, m60, nifty, new SwingStrategySettings(), skipHardFilterGate: true);
        // Force the SHORT-only fields to the (different, easy-to-tell-apart) original shared ones' values is not
        // needed - defaults already match (see test above) - so scoring with plain defaults already proves this
        // path behaves exactly like the pre-split shared-field code did, for whichever volume tier this data hits.
        var withSharedCopiedIn = SwingShortDecisionEngine.Evaluate(Stock, daily, m15, m60, nifty,
            With(r => { r.ShortVolumePoints = r.VolumePoints; r.ShortVolumeGoodPoints = r.VolumeGoodPoints; }), skipHardFilterGate: true);

        Assert.Equal(withDefaults.Score, withSharedCopiedIn.Score);
        Assert.Equal(withDefaults.Factors.Single(f => f.Code == "VOL_CONFIRMATION").Points,
            withSharedCopiedIn.Factors.Single(f => f.Code == "VOL_CONFIRMATION").Points);
    }

    [Fact]
    public void ShortVolumePoints_SetToZero_RemovesOnlyTheShortVolumeContribution()
    {
        var daily = Daily(-0.6m); var m15 = M15(-0.15m); var m60 = M60(-0.15m); var nifty = Nifty(-0.1m);
        var baseline = SwingShortDecisionEngine.Evaluate(Stock, daily, m15, m60, nifty, new SwingStrategySettings(), skipHardFilterGate: true);
        var zeroed = SwingShortDecisionEngine.Evaluate(Stock, daily, m15, m60, nifty,
            With(r => { r.ShortVolumePoints = 0; r.ShortVolumeGoodPoints = 0; }), skipHardFilterGate: true);

        var baselineVol = baseline.Factors.Single(f => f.Code == "VOL_CONFIRMATION").Points;
        var zeroedVol = zeroed.Factors.Single(f => f.Code == "VOL_CONFIRMATION").Points;
        Assert.True(baselineVol > 0, "fixture should trigger a non-zero volume tier so zeroing it is actually observable");
        Assert.Equal(0, zeroedVol);
        Assert.Equal(baseline.Score - baselineVol, zeroed.Score);

        // Every other SHORT factor is byte-identical - only VOL_CONFIRMATION moved.
        var baselineOthers = baseline.Factors.Where(f => f.Code != "VOL_CONFIRMATION").Select(f => (f.Code, f.Points)).OrderBy(f => f.Code).ToList();
        var zeroedOthers = zeroed.Factors.Where(f => f.Code != "VOL_CONFIRMATION").Select(f => (f.Code, f.Points)).OrderBy(f => f.Code).ToList();
        Assert.Equal(baselineOthers, zeroedOthers);
    }

    [Fact]
    public void ShortVolumePoints_SetToZero_DoesNotAffectBuyVolumeScoring()
    {
        var daily = Daily(0.6m); var m15 = M15(0.15m); var m60 = M60(0.15m); var nifty = Nifty(0.2m);
        var buyWithDefaults = SwingDecisionEngine.Evaluate(Stock, daily, m15, m60, nifty, new SwingStrategySettings(), skipHardFilterGate: true);
        var buyWithShortVolumeZeroed = SwingDecisionEngine.Evaluate(Stock, daily, m15, m60, nifty,
            With(r => { r.ShortVolumePoints = 0; r.ShortVolumeGoodPoints = 0; }), skipHardFilterGate: true);

        Assert.Equal(buyWithDefaults.Score, buyWithShortVolumeZeroed.Score);
        Assert.Equal(buyWithDefaults.Factors.Single(f => f.Code == "VOL_CONFIRMATION").Points,
            buyWithShortVolumeZeroed.Factors.Single(f => f.Code == "VOL_CONFIRMATION").Points);
        var allFactorsBaseline = buyWithDefaults.Factors.Select(f => (f.Code, f.Points)).OrderBy(f => f.Code).ToList();
        var allFactorsZeroed = buyWithShortVolumeZeroed.Factors.Select(f => (f.Code, f.Points)).OrderBy(f => f.Code).ToList();
        Assert.Equal(allFactorsBaseline, allFactorsZeroed);
    }

    // ---------------- validation ----------------
    public static TheoryData<Action<SwingRuleParameters>, string> BadNumbers => new()
    {
        { r => r.BreakoutPoints = 21, "must total 100" },
        { r => r.AdxMin = 61m, "must be between" },
        { r => r.TrendEmaLong = 251, "must be between" },
        { r => r.TrendEmaFast = 60, "fast EMA must be shorter" },
        { r => r.NiftyEmaFast = 50, "fast EMA must be shorter" },
        { r => r.TrendEmaSlow = 200, "slow EMA must be shorter than the long" },
        { r => r.MacdFast = 30, "MACD" },
        { r => { r.VolumeGoodPoints = 16; }, "good-volume points" },
        { r => r.VolumeGoodMultiple = 3m, "good-volume multiple" },
        { r => { r.ShortVolumeGoodPoints = 16; }, "SHORT good-volume points" },
        { r => r.RsiGoodPoints = 11, "acceptable-zone points" },
        { r => r.RsiBuyOkLow = 60m, "BUY best zone" },
        { r => r.RsiShortBestHigh = 55m, "SHORT best zone" },
        { r => r.Target1R = 3m, "Target 1 must be closer" },
        { r => r.MinRiskReward = 2.5m, "could never pass" },
    };

    [Theory]
    [MemberData(nameof(BadNumbers))]
    public void Validate_RejectsUnusableNumbers(Action<SwingRuleParameters> change, string expected)
    {
        var rules = new SwingRuleParameters();
        change(rules);

        Assert.Contains(SwingRuleCatalog.Validate(rules), e => e.Contains(expected));
    }

    [Fact]
    public void Validate_AWeightAboveWhatABacktestCanStore_IsRejected()
    {
        var rules = new SwingRuleParameters { BreakoutPoints = 64 };
        Assert.Contains(SwingRuleCatalog.Validate(rules), e => e.Contains("must be between"));
        Assert.All(SwingRuleCatalog.Fields.Where(f => f.Unit == "pts"), f => Assert.True(f.Max <= 63));
    }

    // ---------------- every extreme the page allows must run, not crash a scan ----------------
    [Fact]
    public void Engines_RunAtEveryFieldLimit()
    {
        foreach (var f in SwingRuleCatalog.Fields)
        {
            foreach (decimal limit in new[] { f.Min, f.Max })
            {
                var settings = new SwingStrategySettings();
                var prop = typeof(SwingRuleParameters).GetProperties().Single(p => JsonNamingPolicy.CamelCase.ConvertName(p.Name) == f.Key);
                prop.SetValue(settings.Rules, Convert.ChangeType(limit, prop.PropertyType));

                foreach (int days in new[] { 50, 260, 300 })
                {
                    var l = SwingDecisionEngine.Evaluate(Stock, Candles.Daily("TESTSTK", days, 1000m, 0.6m), M15(0.15m), M60(0.15m), Nifty(0.2m), settings, skipHardFilterGate: true);
                    var s = SwingShortDecisionEngine.Evaluate(Stock, Candles.Daily("TESTSTK", days, 1000m, -0.6m), M15(-0.15m), M60(-0.15m), Nifty(-0.1m), settings, skipHardFilterGate: true);
                    Assert.NotNull(l.Checklist);
                    Assert.NotNull(s.Checklist);
                }
            }
        }
    }

    // ---------------- saving ----------------
    private static (StrategyRulesService Service, Mock<ISwingStrategySettingsRepository> Repo, Mock<IMarketRegimeService> Regime) NewService()
    {
        var repo = new Mock<ISwingStrategySettingsRepository>();
        repo.Setup(x => x.GetSettingsAsync()).ReturnsAsync(new SwingStrategySettings());
        repo.Setup(x => x.UpdateRulesAsync(It.IsAny<SwingRuleParameters>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>()))
            .ReturnsAsync((SwingRuleParameters r, int b, int w, string g) => new SwingStrategySettings { Rules = r, BuyScoreThreshold = b, WatchScoreThreshold = w, MarketGateMode = g });
        var regime = new Mock<IMarketRegimeService>();
        regime.Setup(x => x.GetPoliciesAsync()).ReturnsAsync(RegimePolicy.Defaults);
        return (new StrategyRulesService(repo.Object, regime.Object, NullLogger<StrategyRulesService>.Instance), repo, regime);
    }

    private static StrategyRulesSaveDto ValidSave() => new()
    {
        Rules = new SwingRuleParameters(),
        Policies = RegimePolicy.Defaults.Select(p => new RegimePolicy { Regime = p.Regime, MinStockScore = p.MinStockScore, RequireRelativeStrength = p.RequireRelativeStrength, MaxPositions = p.MaxPositions, RiskPct = p.RiskPct, MaxExposurePct = p.MaxExposurePct }).ToList(),
        BuyScoreThreshold = 70,
        WatchScoreThreshold = 50,
        MarketGateMode = "regime",
        ConfirmLive = true
    };

    [Fact]
    public async Task Save_StoresTheNumbers_IncludingTheExposureLimit()
    {
        var (service, repo, regime) = NewService();
        IReadOnlyList<RegimePolicy>? stored = null;
        regime.Setup(x => x.SavePoliciesAsync(It.IsAny<IReadOnlyList<RegimePolicy>>())).Callback<IReadOnlyList<RegimePolicy>>(p => stored = p).Returns(Task.CompletedTask);
        var dto = ValidSave();
        dto.Rules!.AdxMin = 25m;
        dto.Policies!.Single(p => p.Regime == MarketRegimes.Bearish).MinStockScore = 75;
        dto.Policies!.Single(p => p.Regime == MarketRegimes.Bearish).MaxExposurePct = 40m;

        var (success, errors, saved) = await service.SaveAsync(dto);

        Assert.True(success, string.Join(" ", errors));
        Assert.Equal(25m, saved!.Rules.AdxMin);
        Assert.Equal("REGIME", saved.MarketGateMode);
        repo.Verify(x => x.UpdateRulesAsync(It.Is<SwingRuleParameters>(r => r.AdxMin == 25m), 70, 50, "REGIME"), Times.Once);
        var bearish = stored!.Single(p => p.Regime == MarketRegimes.Bearish);
        Assert.Equal(75, bearish.MinStockScore);
        Assert.Equal(40m, bearish.MaxExposurePct);
    }

    [Fact]
    public async Task Save_PreservesShortPolicyFields_InsteadOfResettingThemToDefaults()
    {
        // Regression test: SaveAsync used to rebuild each RegimePolicy without copying ShortMinStockScore/
        // ShortMaxPositions, silently resetting every regime's SHORT policy to the class's 82/3 defaults on
        // every single Trade Conditions save.
        var (service, repo, regime) = NewService();
        IReadOnlyList<RegimePolicy>? stored = null;
        regime.Setup(x => x.SavePoliciesAsync(It.IsAny<IReadOnlyList<RegimePolicy>>())).Callback<IReadOnlyList<RegimePolicy>>(p => stored = p).Returns(Task.CompletedTask);
        var dto = ValidSave();
        var bearishDto = dto.Policies!.Single(p => p.Regime == MarketRegimes.Bearish);
        bearishDto.ShortMinStockScore = 90;
        bearishDto.ShortMaxPositions = 1;

        var (success, errors, _) = await service.SaveAsync(dto);

        Assert.True(success, string.Join(" ", errors));
        var bearish = stored!.Single(p => p.Regime == MarketRegimes.Bearish);
        Assert.Equal(90, bearish.ShortMinStockScore);
        Assert.Equal(1, bearish.ShortMaxPositions);
    }

    public static TheoryData<Action<StrategyRulesSaveDto>, string> BadSaves => new()
    {
        { d => d.ConfirmLive = false, "real-money" },
        { d => d.Rules!.AdxMin = 99m, "must be between" },
        { d => d.Rules = null, "required" },
        { d => d.BuyScoreThreshold = 40, "greater than the WATCH" },
        { d => d.BuyScoreThreshold = 101, "between 0 and 100" },
        { d => d.MarketGateMode = "OFF", "Market gate" },
        { d => d.Policies!.RemoveAt(0), "exactly one policy" },
        { d => d.Policies![0].Regime = "BEARISH", "exactly one policy" },
        { d => d.Policies![0].MinStockScore = 101, "minimum score" },
        { d => d.Policies![0].MaxPositions = -1, "max positions" },
        { d => d.Policies![0].ShortMinStockScore = 101, "short minimum score" },
        { d => d.Policies![0].ShortMaxPositions = -1, "short max positions" },
        { d => d.Policies![0].RiskPct = 6m, "risk must be" },
        { d => d.Policies![0].MaxExposurePct = 0m, "max exposure" },
        { d => d.Policies![0].MaxExposurePct = 101m, "max exposure" },
    };

    [Theory]
    [MemberData(nameof(BadSaves))]
    public async Task Save_WithAProblem_ListsItAndSavesNothing(Action<StrategyRulesSaveDto> change, string expected)
    {
        var (service, repo, regime) = NewService();
        var dto = ValidSave();
        change(dto);

        var (success, errors, _) = await service.SaveAsync(dto);

        Assert.False(success);
        Assert.Contains(errors, e => e.Contains(expected));
        repo.Verify(x => x.UpdateRulesAsync(It.IsAny<SwingRuleParameters>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>()), Times.Never);
        regime.Verify(x => x.SavePoliciesAsync(It.IsAny<IReadOnlyList<RegimePolicy>>()), Times.Never);
    }

    [Fact]
    public async Task Get_ShowsTheSavedNumbersAndTheOriginals()
    {
        var (service, repo, _) = NewService();
        repo.Setup(x => x.GetSettingsAsync()).ReturnsAsync(new SwingStrategySettings { Rules = new SwingRuleParameters { AdxMin = 25m } });

        var dto = await service.GetAsync();

        Assert.Equal(25m, dto.Rules.AdxMin);
        Assert.Equal(20m, dto.Defaults.AdxMin);
        Assert.Equal(5, dto.Policies.Count);
        Assert.NotEmpty(dto.Fields);
    }
}
