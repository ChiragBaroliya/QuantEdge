using System;
using System.Collections.Generic;
using System.Linq;
using QuantEdge.Domain.Entities;
using QuantEdge.Infrastructure.DTOs;

namespace QuantEdge.Infrastructure.Services;

/// <summary>
/// Auto Short Selling signal engine - <see cref="SwingDecisionEngine"/> mirrored for a falling stock, built from the same
/// indicators (IndicatorCalculator), timeframes (1D hard filters, 15m / 60m scoring), weights and score thresholds, so
/// a SHORT means exactly what a BUY means with the direction reversed:
///   Hard filters  - NIFTY 50 bearish (Close &lt; 50 DMA &amp; EMA20 &lt; EMA50; missing data fails closed - never short
///                   blind), daily downtrend (Price &lt; EMA20 &lt; EMA50, both falling, EMA200 not rising), ADX(14) &gt;= 20.
///   Scoring (100) - Breakdown 20, Volume 15, Relative weakness vs NIFTY 15, 60m downtrend 15, RSI 25-50 10,
///                   MACD below signal 10, Bearish candle 8, Risk:reward 7.
/// Score &gt;= BuyScoreThreshold -> Decision "SHORT" (IsSellSignal). The long engine is not changed by this.
/// </summary>
public static class SwingShortDecisionEngine
{
    public const string ShortDecision = "SHORT";

    /// <param name="skipHardFilterGate">Score all 8 factors even when a hard filter fails (the flags are still filled in) -
    /// only for <see cref="TradeConditionRules"/>, whose user profile may switch those filters off.</param>
    public static SwingEvaluationResult Evaluate(
        StockMaster stock,
        List<MarketCandle> stockCandles1d,
        List<MarketCandle>? stockCandles15m,
        List<MarketCandle>? stockCandles60m,
        List<MarketCandle>? niftyCandles1d,
        SwingStrategySettings? settings = null,
        bool skipHardFilterGate = false)
    {
        settings ??= SwingStrategySettings.Default;
        var rules = settings.Rules;
        var result = new SwingEvaluationResult { Sector = string.Empty };

        // Finished candles only, as in the long engine - a still-forming bar repaints.
        DateTime nowUtc = DateTime.UtcNow;
        stockCandles15m = SwingDecisionEngine.ClosedCandlesOnly(stockCandles15m, TimeSpan.FromMinutes(15), nowUtc);
        stockCandles60m = SwingDecisionEngine.ClosedCandlesOnly(stockCandles60m, TimeSpan.FromMinutes(60), nowUtc);

        if (stockCandles1d == null || stockCandles1d.Count < rules.MinDailyCandles)
        {
            result.Decision = "REJECT";
            result.Reason = $"Insufficient daily candle history (minimum {rules.MinDailyCandles} required).";
            result.Checklist = new ConditionChecklistDto(0, 1, new List<ConditionItemDto>
            {
                new("DATA_CHECK", "Data Integrity", "Insufficient daily data", "Failed", "Sufficient Candles", false)
            });
            return result;
        }

        var closes1d = stockCandles1d.Select(c => c.Close).ToList();
        var highs1d = stockCandles1d.Select(c => c.High).ToList();
        var lows1d = stockCandles1d.Select(c => c.Low).ToList();
        int idx1d = closes1d.Count - 1;
        decimal price1d = closes1d[idx1d];

        decimal currentPrice = stockCandles15m.Count > 0 ? stockCandles15m.Last().Close : price1d;
        result.EntryPrice = currentPrice;

        var ema20_1d = IndicatorCalculator.CalculateEma(closes1d, rules.TrendEmaFast);
        var ema50_1d = IndicatorCalculator.CalculateEma(closes1d, rules.TrendEmaSlow);
        var ema200_1d = IndicatorCalculator.CalculateEma(closes1d, rules.TrendEmaLong);
        var adx14_1d = IndicatorCalculator.CalculateAdx(highs1d, lows1d, closes1d, rules.AdxPeriod);
        decimal curEma20 = ema20_1d[idx1d];
        decimal curEma50 = ema50_1d[idx1d];
        decimal curAdx = adx14_1d[idx1d];
        result.DailyAtr = Math.Round(IndicatorCalculator.CalculateAtr(highs1d, lows1d, closes1d, 14)[idx1d], 4);

        // ---------------- STAGE A: HARD FILTERS ----------------
        bool niftyBearish = IsNiftyBearishFilterPassed(niftyCandles1d, settings);
        result.IsMarketFilterPassed = niftyBearish;

        bool ema20Falling = idx1d >= rules.TrendSlopeBars && ema20_1d[idx1d] < ema20_1d[idx1d - rules.TrendSlopeBars];
        bool ema50Falling = idx1d >= rules.TrendSlopeBars && ema50_1d[idx1d] < ema50_1d[idx1d - rules.TrendSlopeBars];
        bool hasEma200History = closes1d.Count >= rules.MinBarsForLongEma;
        result.HasEma200History = hasEma200History;
        bool ema200NotRising = !hasEma200History || (idx1d >= rules.LongEmaSlopeBars && ema200_1d[idx1d] <= ema200_1d[idx1d - rules.LongEmaSlopeBars] * (1m + rules.LongEmaToleranceFactor));
        bool downtrendPassed = price1d < curEma20 && curEma20 < curEma50 && ema20Falling && ema50Falling && ema200NotRising;
        bool adxPassed = curAdx >= rules.AdxMin;

        result.EmaTrendPassed = downtrendPassed;
        result.AdxPassed = adxPassed;
        result.Adx1d = Math.Round(curAdx, 2);
        result.Ema20_1d = Math.Round(curEma20, 2);
        result.Ema50_1d = Math.Round(curEma50, 2);

        bool hardFiltersPassed = niftyBearish && downtrendPassed && adxPassed;
        if (!hardFiltersPassed && !skipHardFilterGate)
        {
            var hardFailed = new List<string>();
            if (!niftyBearish) hardFailed.Add($"NIFTY_BEARISH_FILTER (Nifty 50 not below {rules.NiftySmaPeriod} DMA / EMA{rules.NiftyEmaFast} not below EMA{rules.NiftyEmaSlow}, or data unavailable - no new shorts)");
            if (!downtrendPassed) hardFailed.Add($"EMA_DOWNTREND (Price not below EMA{rules.TrendEmaFast}/EMA{rules.TrendEmaSlow} or slopes not falling)");
            if (!adxPassed) hardFailed.Add($"ADX_STRENGTH (ADX {curAdx:F1} < {rules.AdxMin:0.0} - Choppy / Weak Trend)");

            result.HardFiltersPassed = false;
            result.Decision = "REJECT";
            result.FailedRules = hardFailed;
            result.Reason = $"REJECTED by Short Hard Filter: {string.Join("; ", hardFailed)}";
            result.Checklist = BuildChecklist(niftyBearish, downtrendPassed, adxPassed, false, false, false, false, false, false, false, false,
                currentPrice, curEma20, curEma50, 0m, curAdx, 0m, 0m, rules);
            return result;
        }

        result.HardFiltersPassed = hardFiltersPassed;
        var passedRules = new List<string>();
        if (niftyBearish) passedRules.Add("Hard Filter 1: NIFTY Bearish Market Filter (Passed)");
        if (downtrendPassed) passedRules.Add("Hard Filter 2: EMA Downtrend Alignment (Passed 1D)");
        if (adxPassed) passedRules.Add($"Hard Filter 3: ADX Trend Strength (Passed {curAdx:F1} >= {rules.AdxMin:0.##})");
        var failedRules = new List<string>();

        // ---------------- STAGE B: WEIGHTED SCORING (100 pts) ----------------
        int score = 0;
        int scoreBefore = 0;
        void AddFactor(string code, string name, int max, string timeframe)
        {
            result.Factors.Add(new SwingFactorScore(code, name, score - scoreBefore, max, timeframe));
            scoreBefore = score;
        }

        var refCandles = stockCandles15m.Count >= 20 ? stockCandles15m : stockCandles1d;
        var refCloses = refCandles.Select(c => c.Close).ToList();
        var refHighs = refCandles.Select(c => c.High).ToList();
        var refLows = refCandles.Select(c => c.Low).ToList();
        var refOpens = refCandles.Select(c => c.Open).ToList();
        var refVolumes = refCandles.Select(c => c.Volume).ToList();
        int refIdx = refCloses.Count - 1;

        // BREAKDOWN_GROUP (20): below the previous day low / recent swing low, a consolidation breakdown, or near the 52W low.
        decimal prevDayLow = idx1d >= 1 ? lows1d[idx1d - 1] : lows1d[idx1d];
        decimal swingLow = refIdx >= rules.SwingLookbackBars ? refLows.Skip(Math.Max(0, refIdx - rules.SwingLookbackBars)).Take(rules.SwingLookbackBars).Min() : refLows[refIdx];
        bool isPrevLowBreakdown = currentPrice < prevDayLow || currentPrice <= swingLow;

        bool isConsolidationBreakdown = false;
        if (idx1d >= rules.ConsolidationBars + 1)
        {
            var consHighs = highs1d.Skip(Math.Max(0, idx1d - (rules.ConsolidationBars + 1))).Take(rules.ConsolidationBars).ToList();
            var consLows = lows1d.Skip(Math.Max(0, idx1d - (rules.ConsolidationBars + 1))).Take(rules.ConsolidationBars).ToList();
            decimal cMax = consHighs.Max();
            decimal cMin = consLows.Min();
            decimal consRangePct = cMin > 0m ? (cMax - cMin) / cMin * 100m : 99m;
            isConsolidationBreakdown = consRangePct <= rules.ConsolidationMaxRangePct && currentPrice < cMin;
        }

        decimal low52W = lows1d.Skip(Math.Max(0, lows1d.Count - 250)).Min();
        bool isNear52WLow = low52W > 0m && currentPrice <= (1m + rules.Near52WeekPct / 100m) * low52W;

        bool breakdownPassed = isPrevLowBreakdown || isConsolidationBreakdown || isNear52WLow;
        var breakdownSubTypes = new List<string>();
        if (isPrevLowBreakdown) breakdownSubTypes.Add("PRIOR_LOW");
        if (isConsolidationBreakdown) breakdownSubTypes.Add("CONSOLIDATION");
        if (isNear52WLow) breakdownSubTypes.Add("NEAR_52W_LOW");
        result.BreakdownSubType = breakdownSubTypes.Count > 0 ? string.Join(",", breakdownSubTypes) : "NONE";
        if (breakdownPassed) { score += rules.BreakoutPoints; passedRules.Add($"BREAKDOWN_GROUP (+{rules.BreakoutPoints} pts): Breakdown below PDL/Swing Low/Consolidation/near 52W Low"); }
        else failedRules.Add($"BREAKDOWN_GROUP (0/{rules.BreakoutPoints} pts): Inside trading range, no breakdown detected");
        AddFactor("BREAKDOWN_GROUP", "Breakdown", rules.BreakoutPoints, "15m");

        // VOL_CONFIRMATION: same volume-spike test as the long engine (the candle rule below supplies the
        // direction), but its own (SHORT) point weights - see ShortVolumePoints/ShortVolumeGoodPoints.
        long curVol = refVolumes[refIdx];
        decimal avgVol = SwingDecisionEngine.SameSlotAverageVolume(refCandles, refIdx)
            ?? (refIdx > 0 ? (decimal)refVolumes.Skip(Math.Max(0, refIdx - rules.VolumeAverageBars)).Take(Math.Min(rules.VolumeAverageBars, refIdx)).Average(v => (double)v) : 0m);
        decimal volMult = avgVol > 0m ? Math.Round(curVol / avgVol, 2) : 0m;
        bool volSpikePassed = volMult >= rules.VolumeGoodMultiple;
        bool volGreaterPrev = refIdx >= 1 && curVol > refVolumes[refIdx - 1];
        if (volMult >= rules.VolumeHeavyMultiple && volGreaterPrev) { score += rules.ShortVolumePoints; passedRules.Add($"VOL_CONFIRMATION (+{rules.ShortVolumePoints} pts): Heavy Volume Surge ({volMult:F1}x Avg Volume)"); }
        else if (volSpikePassed) { score += rules.ShortVolumeGoodPoints; passedRules.Add($"VOL_CONFIRMATION (+{rules.ShortVolumeGoodPoints} pts): Good Volume ({volMult:F1}x Avg Volume)"); }
        else failedRules.Add($"VOL_CONFIRMATION (0/{rules.ShortVolumePoints} pts): Volume low ({volMult:F1}x Avg Volume)");
        AddFactor("VOL_CONFIRMATION", "Volume", rules.ShortVolumePoints, "15m");
        result.VolumeMultiple = volMult;

        // RELATIVE_WEAKNESS (15): stock 1M return below NIFTY's over the same dates.
        bool rwPassed = false;
        if (idx1d >= rules.RelativeStrengthBars && niftyCandles1d != null && niftyCandles1d.Count >= rules.RelativeStrengthBars + 1)
        {
            decimal stockRet1M = (price1d - closes1d[idx1d - rules.RelativeStrengthBars]) / closes1d[idx1d - rules.RelativeStrengthBars];
            decimal? niftyStart = SwingDecisionEngine.NiftyCloseOnOrBefore(niftyCandles1d, stockCandles1d[idx1d - rules.RelativeStrengthBars].CandleTime);
            decimal? niftyEnd = SwingDecisionEngine.NiftyCloseOnOrBefore(niftyCandles1d, stockCandles1d[idx1d].CandleTime);
            if (niftyStart > 0m && niftyEnd > 0m)
            {
                rwPassed = stockRet1M < (niftyEnd.Value - niftyStart.Value) / niftyStart.Value;
            }
        }
        if (rwPassed) { score += rules.RelativeStrengthPoints; passedRules.Add($"RELATIVE_WEAKNESS (+{rules.RelativeStrengthPoints} pts): Underperforming NIFTY 50 Benchmark"); }
        else failedRules.Add($"RELATIVE_WEAKNESS (0/{rules.RelativeStrengthPoints} pts): Not weaker than NIFTY 50");
        AddFactor("RELATIVE_WEAKNESS", "Relative weakness", rules.RelativeStrengthPoints, "1d");

        // MULTITIMEFRAME (15): 60m Close < 60m EMA20 and 60m RSI <= 60. No 60m data = no points (fail closed).
        bool hasMtfData = stockCandles60m.Count >= rules.Mtf60mEmaPeriod;
        bool mtfPassed = false;
        if (hasMtfData)
        {
            var closes60m = stockCandles60m.Select(c => c.Close).ToList();
            int idx60m = closes60m.Count - 1;
            decimal ema20_60m = IndicatorCalculator.CalculateEma(closes60m, rules.Mtf60mEmaPeriod)[idx60m];
            decimal rsi60m = IndicatorCalculator.CalculateRsi(closes60m, rules.Mtf60mRsiPeriod)[idx60m];
            mtfPassed = closes60m[idx60m] < ema20_60m && rsi60m <= rules.Mtf60mRsiMaxShort;
            result.Rsi60m = Math.Round(rsi60m, 2);
            result.Is60mAboveEma20 = closes60m[idx60m] > ema20_60m;
        }
        result.Has60mData = hasMtfData;
        if (mtfPassed) { score += rules.Mtf60mPoints; passedRules.Add($"MULTITIMEFRAME (+{rules.Mtf60mPoints} pts): 60m Hourly Downtrend (Close < EMA{rules.Mtf60mEmaPeriod})"); }
        else failedRules.Add(hasMtfData ? $"MULTITIMEFRAME (0/{rules.Mtf60mPoints} pts): 60m Hourly Trend not bearish" : $"MULTITIMEFRAME (0/{rules.Mtf60mPoints} pts): Insufficient 60m data - confirmation withheld");
        AddFactor("MULTITIMEFRAME", "60m trend", rules.Mtf60mPoints, "60m");

        // RSI_MOMENTUM (10): RSI(14) between 25 and 50 (sweet spot 30-45) - weak, but not yet oversold.
        decimal curRsi = IndicatorCalculator.CalculateRsi(refCloses, rules.RsiPeriod)[refIdx];
        bool rsiPassed = curRsi >= rules.RsiShortOkLow && curRsi <= rules.RsiShortOkHigh;
        if (curRsi >= rules.RsiShortBestLow && curRsi <= rules.RsiShortBestHigh) { score += rules.RsiPoints; passedRules.Add($"RSI_MOMENTUM (+{rules.RsiPoints} pts): Bearish Sweet Spot ({curRsi:F1})"); }
        else if (rsiPassed) { score += rules.RsiGoodPoints; passedRules.Add($"RSI_MOMENTUM (+{rules.RsiGoodPoints} pts): Acceptable Bearish Zone ({curRsi:F1})"); }
        else failedRules.Add($"RSI_MOMENTUM (0/{rules.RsiPoints} pts): Outside {rules.RsiShortOkLow:0.##}-{rules.RsiShortOkHigh:0.##} Zone ({curRsi:F1})");
        AddFactor("RSI_MOMENTUM", "RSI", rules.RsiPoints, "15m");
        result.Rsi15m = Math.Round(curRsi, 2);

        // MACD_BEARISH (10): MACD line below its signal line.
        var (macd, macdSignal) = IndicatorCalculator.CalculateMacd(refCloses, rules.MacdFast, rules.MacdSlow, rules.MacdSignal);
        bool macdPassed = macd[refIdx] < macdSignal[refIdx];
        if (macdPassed) { score += rules.MacdPoints; passedRules.Add($"MACD_BEARISH (+{rules.MacdPoints} pts): MACD Bearish Alignment ({macd[refIdx]:F2} < Signal {macdSignal[refIdx]:F2})"); }
        else failedRules.Add($"MACD_BEARISH (0/{rules.MacdPoints} pts): MACD Line above Signal");
        AddFactor("MACD_BEARISH", "MACD", rules.MacdPoints, "15m");

        // BEARISH_CANDLE (8): red candle closing near its low, or a breakdown candle (>= 1.2x ATR body).
        decimal curAtr = Math.Max(0.1m, IndicatorCalculator.CalculateAtr(refHighs, refLows, refCloses, rules.AtrPeriod)[refIdx]);
        decimal openPrice = refOpens[refIdx];
        decimal range = refHighs[refIdx] - refLows[refIdx];
        bool isCloseNearLow = range > 0m && (refHighs[refIdx] - currentPrice) / range >= 1m - rules.CandleCloseZonePct / 100m;
        bool isBreakdownCandle = (openPrice - currentPrice) >= rules.CandleBodyAtrMultiple * curAtr;
        bool candlePassed = currentPrice < openPrice && (isCloseNearLow || isBreakdownCandle);
        if (candlePassed) { score += rules.CandlePoints; passedRules.Add($"BEARISH_CANDLE (+{rules.CandlePoints} pts): Strong Bearish Candle (Close near Low / ATR Expansion)"); }
        else failedRules.Add($"BEARISH_CANDLE (0/{rules.CandlePoints} pts): Weak or indecisive candle pattern");
        AddFactor("BEARISH_CANDLE", "Candle", rules.CandlePoints, "15m");

        // RISK_REWARD (7): Stop 1.5 x ATR above, Target 2R below.
        result.StopLoss = Math.Round(currentPrice + rules.StopAtrMultiple * curAtr, 2);
        decimal risk = Math.Max(0.01m, result.StopLoss - currentPrice);
        result.Target1 = Math.Round(Math.Max(0.01m, currentPrice - rules.Target1R * risk), 2);
        result.Target2 = Math.Round(Math.Max(0.01m, currentPrice - rules.Target2R * risk), 2);
        result.RiskRewardRatio = Math.Round((currentPrice - result.Target1) / risk, 2);
        bool rrPassed = result.RiskRewardRatio >= rules.MinRiskReward;
        if (rrPassed) { score += rules.RiskRewardPoints; passedRules.Add($"RISK_REWARD (+{rules.RiskRewardPoints} pts): Valid R:R Ratio (1:{result.RiskRewardRatio:F1} >= 1:{rules.MinRiskReward:0.0})"); }
        else failedRules.Add($"RISK_REWARD (0/{rules.RiskRewardPoints} pts): Low R:R Ratio (1:{result.RiskRewardRatio:F1})");
        AddFactor("RISK_REWARD", "Risk:reward", rules.RiskRewardPoints, "15m");

        // ---------------- STAGE C: DECISION ----------------
        result.Score = Math.Min(100, Math.Max(0, score));
        result.ConfidencePct = result.Score;
        result.PassedRules = passedRules;
        result.FailedRules = failedRules;
        result.CalculatedRiskAmount = Math.Round(risk, 2);

        if (result.Score >= settings.BuyScoreThreshold)
        {
            result.Decision = ShortDecision;
            result.IsSellSignal = true;
            result.Reason = $"SHORT Signal Confirmed (Score: {result.Score}/100). Entry: ₹{currentPrice:F2}, SL: ₹{result.StopLoss:F2}, Target 1: ₹{result.Target1:F2} (1:{result.RiskRewardRatio:F1} R:R).";
        }
        else if (result.Score >= settings.WatchScoreThreshold)
        {
            result.Decision = "WATCH";
            result.Reason = $"SHORT WATCHLIST Candidate (Score: {result.Score}/100). Passed Hard Filters, pending breakdown / volume momentum.";
        }
        else
        {
            result.Decision = "NO SIGNAL";
            result.Reason = $"NO SHORT SIGNAL (Score: {result.Score}/100 < {settings.WatchScoreThreshold} threshold). Failed factors: {string.Join("; ", failedRules.Take(3))}";
        }

        result.Checklist = BuildChecklist(niftyBearish, downtrendPassed, adxPassed, breakdownPassed, volSpikePassed, rwPassed,
            mtfPassed, rsiPassed, macdPassed, candlePassed, rrPassed,
            currentPrice, curEma20, curEma50, volMult, curAdx, curRsi, result.RiskRewardRatio, rules);
        return result;
    }

    /// <summary>
    /// NIFTY 50 bearish filter (the long engine's market filter mirrored): Close &lt; SMA50 AND EMA20 &lt; EMA50 on daily
    /// candles. Missing/insufficient data (&lt; 50 candles) always fails - shorts are never opened blind.
    /// </summary>
    public static bool IsNiftyBearishFilterPassed(List<MarketCandle>? niftyCandles, SwingStrategySettings? settings = null)
    {
        var rules = (settings ?? SwingStrategySettings.Default).Rules;
        if (niftyCandles == null || niftyCandles.Count < rules.MinNiftyCandles) return false;

        var closes = niftyCandles.Select(c => c.Close).ToList();
        int idx = closes.Count - 1;
        decimal sma50 = IndicatorCalculator.CalculateSma(closes, rules.NiftySmaPeriod)[idx];
        decimal ema20 = IndicatorCalculator.CalculateEma(closes, rules.NiftyEmaFast)[idx];
        decimal ema50 = IndicatorCalculator.CalculateEma(closes, rules.NiftyEmaSlow)[idx];
        return closes[idx] < sma50 && ema20 < ema50;
    }

    // Same 11-item shape as the long checklist, so MinConditionsMatch (x/11) means the same for a short.
    private static ConditionChecklistDto BuildChecklist(
        bool niftyBearish, bool downtrendPassed, bool adxPassed, bool breakdownPassed, bool volSpikePassed, bool rwPassed,
        bool mtfPassed, bool rsiPassed, bool macdPassed, bool candlePassed, bool rrPassed,
        decimal price, decimal ema20, decimal ema50, decimal volMult, decimal adx, decimal rsi, decimal rr, SwingRuleParameters rules)
    {
        var conditions = new List<ConditionItemDto>
        {
            new("HARD_NIFTY_BEARISH_FILTER", "1. [HARD FILTER] Nifty Bearish Filter", $"Nifty 50 Close < {rules.NiftySmaPeriod} DMA & EMA{rules.NiftyEmaFast} < EMA{rules.NiftyEmaSlow} (mandatory - no stock is shorted when this fails)",
                niftyBearish ? "Passed (Market Downtrend)" : "Failed (Market not bearish - No New Shorts)", $"Close < SMA{rules.NiftySmaPeriod} & EMA{rules.NiftyEmaFast} < EMA{rules.NiftyEmaSlow}", niftyBearish),
            new("HARD_EMA_DOWNTREND", "2. [HARD FILTER] EMA Downtrend Alignment", $"Daily Close < EMA{rules.TrendEmaFast} < EMA{rules.TrendEmaSlow} with falling slopes",
                downtrendPassed ? $"Passed (Close ₹{price:F1} < EMA{rules.TrendEmaFast} ₹{ema20:F1} < EMA{rules.TrendEmaSlow} ₹{ema50:F1})" : $"Close ₹{price:F1}, EMA{rules.TrendEmaFast} ₹{ema20:F1}",
                $"Close < EMA{rules.TrendEmaFast} < EMA{rules.TrendEmaSlow}", downtrendPassed),
            new("HARD_ADX_STRENGTH", "3. [HARD FILTER] ADX Trend Strength", $"Daily ADX ({rules.AdxPeriod}) >= {rules.AdxMin:0.0} (Filters out choppy markets)",
                $"{adx:F1} ({(adx >= rules.AdxMin ? "Passed" : "Weak/Choppy")})", $"ADX >= {rules.AdxMin:0.0}", adxPassed),
            new("BREAKDOWN_GROUP", $"4. Breakdown Group ({rules.BreakoutPoints} Pts)", "15m Close < Previous Day Low / Swing Low / Consolidation / near 52W Low",
                breakdownPassed ? "Passed (Breakdown Confirmed)" : "Inside Range", "Breakdown Confirmed", breakdownPassed),
            new("VOL_CONFIRMATION", $"5. Volume Confirmation ({rules.ShortVolumePoints} Pts)", $"15m Volume >= {rules.VolumeGoodMultiple:0.0#}x the usual volume for this time of day & > Prev Vol",
                volSpikePassed ? $"Passed ({volMult:F1}x Avg Vol)" : $"{volMult:F1}x Avg Vol", $">= {rules.VolumeGoodMultiple:0.0#}x Avg Vol", volSpikePassed),
            new("RELATIVE_WEAKNESS", $"6. Relative Weakness vs Nifty ({rules.RelativeStrengthPoints} Pts)", rules.RelativeStrengthBars == 20 ? "Stock 1M Return < Nifty 50 Return" : $"Stock {rules.RelativeStrengthBars}-day Return < Nifty 50 Return",
                rwPassed ? "Passed (Underperforming Nifty)" : "Not weaker", "Stock Return < Nifty Return", rwPassed),
            new("MULTITIMEFRAME", $"7. Multi-Timeframe Confirmation ({rules.Mtf60mPoints} Pts)", $"60m Close < 60m EMA{rules.Mtf60mEmaPeriod} & 60m RSI <= {rules.Mtf60mRsiMaxShort:0.##}",
                mtfPassed ? "Passed (60m Hourly Bearish)" : "60m not bearish", $"60m Close < EMA{rules.Mtf60mEmaPeriod}", mtfPassed),
            new("RSI_MOMENTUM", $"8. RSI Bearish Zone ({rules.RsiPoints} Pts)", $"15m RSI ({rules.RsiPeriod}) between {rules.RsiShortOkLow:0.##} and {rules.RsiShortOkHigh:0.##} (Best: {rules.RsiShortBestLow:0.##}-{rules.RsiShortBestHigh:0.##})",
                $"{rsi:F1}", $"{rules.RsiShortOkLow:0.0} - {rules.RsiShortOkHigh:0.0} Zone", rsiPassed),
            new("MACD_BEARISH", $"9. MACD Bearish Signal ({rules.MacdPoints} Pts)", "15m MACD Line < Signal Line",
                macdPassed ? "Passed (MACD < Signal)" : "MACD Bullish", "MACD < Signal Line", macdPassed),
            new("BEARISH_CANDLE", $"10. Bearish Candle Pattern ({rules.CandlePoints} Pts)", $"15m red candle closing near its Low, or a >= {rules.CandleBodyAtrMultiple:0.0#}x ATR breakdown candle",
                candlePassed ? "Passed (Bearish Candle)" : "Weak Candle", "Bearish Candle Pattern", candlePassed),
            new("RISK_REWARD", $"11. Risk/Reward Ratio ({rules.RiskRewardPoints} Pts)", $"Suggested Target 1 vs Stop Loss Ratio >= 1:{rules.MinRiskReward:0.0}",
                rrPassed ? $"Passed (1:{rr:F1} R:R)" : $"Failed (1:{rr:F1} R:R)", $"Risk Reward Ratio >= 1:{rules.MinRiskReward:0.0}", rrPassed)
        };
        return new ConditionChecklistDto(conditions.Count(c => c.IsMet), conditions.Count, conditions);
    }
}
