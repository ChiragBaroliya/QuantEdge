using System;
using System.Collections.Generic;
using System.Linq;
using QuantEdge.Domain.Entities;
using QuantEdge.Infrastructure.DTOs;

namespace QuantEdge.Infrastructure.Services;

public class SwingEvaluationResult
{
    public int Score { get; set; }
    public string Decision { get; set; } = "NO SIGNAL";
    public decimal ConfidencePct { get; set; }
    public bool IsMarketFilterPassed { get; set; }
    public bool HardFiltersPassed { get; set; }
    public bool IsBuySignal { get; set; }
    public bool IsSellSignal { get; set; }
    public decimal EntryPrice { get; set; }
    public decimal StopLoss { get; set; }
    public decimal Target1 { get; set; }
    public decimal Target2 { get; set; }
    public decimal RiskRewardRatio { get; set; }
    // Daily ATR(14) - the volatility unit the swing exit policy (SwingTradeRules) sizes SL/Target/Trail
    // with. StopLoss/Target1 above stay on the scoring timeframe (15m) for the signal R:R rule.
    public decimal DailyAtr { get; set; }
    public string Reason { get; set; } = string.Empty;
    public List<string> PassedRules { get; set; } = new();
    public List<string> FailedRules { get; set; } = new();
    public ConditionChecklistDto Checklist { get; set; } = null!;
    public string Sector { get; set; } = string.Empty;
    public bool IsAlreadyOpen { get; set; }
    public int RecommendedQty { get; set; }
    public decimal CalculatedRiskAmount { get; set; }
    public string TimeframeUsed { get; set; } = "1D + 15M + 60M";
    public string ExitSignalReason { get; set; } = string.Empty;

    // Breakdown for the Signal Dashboard verdict card: points per scoring factor (their sum minus
    // MarketPenaltyApplied is Score) and the key readings behind the 1D / 60m / 15m checks.
    public List<SwingFactorScore> Factors { get; set; } = new();
    public int MarketPenaltyApplied { get; set; }
    public bool EmaTrendPassed { get; set; }
    public bool AdxPassed { get; set; }
    public decimal Adx1d { get; set; }
    public decimal Ema20_1d { get; set; }
    public decimal Ema50_1d { get; set; }
    public bool Has60mData { get; set; }
    public decimal? Rsi60m { get; set; }
    public bool? Is60mAboveEma20 { get; set; }
    public decimal Rsi15m { get; set; }
    public decimal VolumeMultiple { get; set; }
    /// <summary>False when fewer than MinBarsForEma200 daily candles were given - the EMA200 check was skipped.</summary>
    public bool HasEma200History { get; set; }

    // Which BREAKOUT_GROUP (BUY) / BREAKDOWN_GROUP (SHORT) sub-condition(s) actually fired - the group as a
    // whole only records pass/fail via Factors, which can't tell a fresh breakout apart from a stock simply
    // being near its 52-week extreme (a materially different, riskier setup). "NONE" when the group failed.
    // BUY sets BreakoutSubType, SHORT sets BreakdownSubType - the other stays "NONE" on a shared result.
    public string BreakoutSubType { get; set; } = "NONE";
    public string BreakdownSubType { get; set; } = "NONE";
}

/// <summary>Points one scoring factor earned (0..MaxPoints) and the timeframe it was measured on.</summary>
public sealed record SwingFactorScore(string Code, string Name, int Points, int MaxPoints, string Timeframe);

public static class SwingDecisionEngine
{
    /// <summary>
    /// Evaluates a stock using Mandatory Hard Filters (NIFTY market filter + 2 stock-level 1D filters)
    /// and an 8-factor 100-Point Weighted Scoring Matrix (15m/60m/1d).
    /// When <see cref="QuantEdge.Domain.Entities.SwingStrategySettings.RequireNiftyMarketFilter"/> is true
    /// (default) a failed NIFTY filter - or missing/insufficient NIFTY data - REJECTS every stock.
    /// When false, the legacy soft mode applies (score penalty + reduced position size, never a reject).
    /// <paramref name="skipHardFilterGate"/>: score all 8 factors even when a hard filter fails (the hard-filter flags are
    /// still filled in) - only for <see cref="TradeConditionRules"/>, whose user profile may switch those filters off.
    /// </summary>
    public static SwingEvaluationResult Evaluate(
        StockMaster stock,
        List<MarketCandle> stockCandles1d,
        List<MarketCandle> stockCandles15m,
        List<MarketCandle> stockCandles60m,
        List<MarketCandle> niftyCandles1d,
        QuantEdge.Domain.Entities.SwingStrategySettings? settings = null,
        bool skipHardFilterGate = false)
    {
        settings ??= QuantEdge.Domain.Entities.SwingStrategySettings.Default;
        int buyScoreThreshold = settings.BuyScoreThreshold;
        int watchScoreThreshold = settings.WatchScoreThreshold;
        bool requireNifty = settings.RequireNiftyMarketFilter;
        int marketContextScorePenalty = settings.MarketContextScorePenalty;
        decimal marketContextPositionSizeFactor = settings.MarketContextPositionSizeFactor;
        var rules = settings.Rules;

        var result = new SwingEvaluationResult();
        string symbol = stock?.Symbol ?? "UNKNOWN";
        // Sector comes from stock_sectors (callers that show it fill it in); the old hard-coded 25-symbol map is gone.
        result.Sector = string.Empty;

        // Score finished candles only (Plan D2): a still-forming 15m / 60m bar repaints - its volume, candle shape,
        // RSI and MACD change until it closes, so a BUY scored on it could vanish by the bar's close.
        DateTime nowUtc = DateTime.UtcNow;
        stockCandles15m = ClosedCandlesOnly(stockCandles15m, TimeSpan.FromMinutes(15), nowUtc);
        stockCandles60m = ClosedCandlesOnly(stockCandles60m, TimeSpan.FromMinutes(60), nowUtc);

        if (stockCandles1d == null || stockCandles1d.Count < rules.MinDailyCandles)
        {
            result.Decision = "REJECT";
            result.Reason = $"Insufficient daily candle history (minimum {rules.MinDailyCandles} required).";
            result.Checklist = BuildEmptyChecklist("Insufficient daily data");
            return result;
        }

        // Align 1D arrays
        var closes1d = stockCandles1d.Select(c => c.Close).ToList();
        var highs1d = stockCandles1d.Select(c => c.High).ToList();
        var lows1d = stockCandles1d.Select(c => c.Low).ToList();

        int idx1d = closes1d.Count - 1;
        decimal price1d = closes1d[idx1d];
        result.EntryPrice = price1d;

        // Determine current execution price (Use 15m close if available, else 1d close)
        decimal currentPrice = price1d;
        if (stockCandles15m != null && stockCandles15m.Any())
        {
            currentPrice = stockCandles15m.Last().Close;
            result.EntryPrice = currentPrice;
        }

        // Calculate 1D Indicators for Hard Filters
        var ema20_1d = IndicatorCalculator.CalculateEma(closes1d, rules.TrendEmaFast);
        var ema50_1d = IndicatorCalculator.CalculateEma(closes1d, rules.TrendEmaSlow);
        var ema200_1d = IndicatorCalculator.CalculateEma(closes1d, rules.TrendEmaLong);
        var adx14_1d = IndicatorCalculator.CalculateAdx(highs1d, lows1d, closes1d, rules.AdxPeriod);

        decimal curEma20_1d = ema20_1d[idx1d];
        decimal curEma50_1d = ema50_1d[idx1d];
        decimal curEma200_1d = ema200_1d[idx1d];
        decimal curAdx_1d = adx14_1d[idx1d];
        result.DailyAtr = Math.Round(IndicatorCalculator.CalculateAtr(highs1d, lows1d, closes1d, 14)[idx1d], 4);

        // --------------------------------------------------------------------
        // STAGE A: HARD FILTERS (NIFTY market filter + stock-level filters)
        // (If any hard filter fails -> REJECT immediately. In soft mode
        // (RequireNiftyMarketFilter = false) NIFTY is not part of this gate -
        // see MarketContextScorePenalty in Stage C.)
        // --------------------------------------------------------------------

        // Hard Filter 1 (Market): NIFTY 50 Close > 50 DMA & EMA20 > EMA50.
        // Missing/insufficient NIFTY data fails closed in mandatory mode.
        bool niftyPassed = IsNiftyMarketFilterPassed(niftyCandles1d, settings);
        result.IsMarketFilterPassed = niftyPassed;
        bool niftyGateFailed = requireNifty && !niftyPassed;

        // Hard Filter 2 (Stock-Level): EMA_TREND (Price > EMA20 > EMA50, rising slopes, stable EMA200)
        bool ema20Rising = idx1d >= rules.TrendSlopeBars && ema20_1d[idx1d] > ema20_1d[idx1d - rules.TrendSlopeBars];
        bool ema50Rising = idx1d >= rules.TrendSlopeBars && ema50_1d[idx1d] > ema50_1d[idx1d - rules.TrendSlopeBars];
        // EMA200 is only meaningful with enough history (Plan D1): with fewer bars CalculateEma returns a running
        // average, not an EMA200. Callers now load 300 daily candles; a newer listing skips this check rather than
        // being judged on a fake EMA200.
        bool hasEma200History = closes1d.Count >= rules.MinBarsForLongEma;
        result.HasEma200History = hasEma200History;
        bool ema200Stable = !hasEma200History || (idx1d >= rules.LongEmaSlopeBars && ema200_1d[idx1d] >= ema200_1d[idx1d - rules.LongEmaSlopeBars] * (1m - rules.LongEmaToleranceFactor));
        bool emaTrendPassed = price1d > curEma20_1d && curEma20_1d > curEma50_1d && ema20Rising && ema50Rising && ema200Stable;

        // Hard Filter 3 (Stock-Level): ADX_STRENGTH (ADX 14 >= 20.0 - Filters out choppy markets)
        bool adxPassed = curAdx_1d >= rules.AdxMin;
        result.EmaTrendPassed = emaTrendPassed;
        result.AdxPassed = adxPassed;
        result.Adx1d = Math.Round(curAdx_1d, 2);
        result.Ema20_1d = Math.Round(curEma20_1d, 2);
        result.Ema50_1d = Math.Round(curEma50_1d, 2);

        // Check Hard Filter Gate - NIFTY (mandatory mode) + stock-level filters.
        bool hardFiltersPassed = !niftyGateFailed && emaTrendPassed && adxPassed;
        if (!hardFiltersPassed && !skipHardFilterGate)
        {
            result.HardFiltersPassed = false;
            result.Decision = "REJECT";
            result.Score = 0;
            result.ConfidencePct = 0;

            var hardFailed = new List<string>();
            if (niftyGateFailed) hardFailed.Add($"NIFTY_MARKET_FILTER (Nifty 50 below {rules.NiftySmaPeriod} DMA / EMA{rules.NiftyEmaFast} < EMA{rules.NiftyEmaSlow} or data unavailable - no new entries)");
            if (!emaTrendPassed) hardFailed.Add($"EMA_TREND (Price below EMA{rules.TrendEmaFast}/EMA{rules.TrendEmaSlow} or declining slope)");
            if (!adxPassed) hardFailed.Add($"ADX_STRENGTH (ADX {curAdx_1d:F1} < {rules.AdxMin:0.0} - Choppy / Weak Trend)");

            result.FailedRules = hardFailed;
            result.Reason = $"REJECTED by Hard Filter: {string.Join("; ", hardFailed)}";
            result.Checklist = BuildChecklist(
                requireNifty, niftyPassed, emaTrendPassed, adxPassed, false, false, false, false, false, false, false, false,
                currentPrice, curEma20_1d, curEma50_1d, 0m, curAdx_1d, 0m, 0m, rules
            );
            return result;
        }

        result.HardFiltersPassed = hardFiltersPassed;
        if (niftyPassed)
            result.PassedRules.Add(requireNifty ? "Hard Filter 1: NIFTY Market Filter (Passed)" : "Market Context: NIFTY Market Filter (Passed)");
        if (emaTrendPassed) result.PassedRules.Add("Hard Filter 2: EMA Trend Alignment (Passed 1D)");
        if (adxPassed) result.PassedRules.Add($"Hard Filter 3: ADX Trend Strength (Passed {curAdx_1d:F1} >= {rules.AdxMin:0.##})");
        if (!niftyPassed)
            result.FailedRules.Add($"Market Context: NIFTY Market Filter (Failed - Defensive Mode, -{marketContextScorePenalty} pt score penalty applied, not a block)");

        // --------------------------------------------------------------------
        // STAGE B: WEIGHTED SCORING MATRIX (100 Max Pts)
        // --------------------------------------------------------------------
        int score = 0;
        var passedRules = new List<string>(result.PassedRules);
        var failedRules = new List<string>(result.FailedRules);

        // Fallback to 1d data if 15m candles are empty
        var refCandles = (stockCandles15m != null && stockCandles15m.Count >= 20) ? stockCandles15m : stockCandles1d;
        var refCloses = refCandles.Select(c => c.Close).ToList();
        var refHighs = refCandles.Select(c => c.High).ToList();
        var refLows = refCandles.Select(c => c.Low).ToList();
        var refOpens = refCandles.Select(c => c.Open).ToList();
        var refVolumes = refCandles.Select(c => c.Volume).ToList();
        int refIdx = refCloses.Count - 1;

        // Rule 4: BREAKOUT_GROUP (20 Pts Max)
        // Max of: Close > PDH / Swing High, OR 10-20D Consolidation Breakout, OR New 52W High / within 10%
        decimal prevDayHigh = idx1d >= 1 ? highs1d[idx1d - 1] : highs1d[idx1d];
        decimal swingHigh = refIdx >= rules.SwingLookbackBars ? refHighs.Skip(Math.Max(0, refIdx - rules.SwingLookbackBars)).Take(rules.SwingLookbackBars).Max() : refHighs[refIdx];
        bool isPrevHighBreakout = currentPrice > prevDayHigh || currentPrice >= swingHigh;

        bool isConsolidationBreakout = false;
        if (idx1d >= rules.ConsolidationBars + 1)
        {
            var consHighs = highs1d.Skip(Math.Max(0, idx1d - (rules.ConsolidationBars + 1))).Take(rules.ConsolidationBars).ToList();
            var consLows = lows1d.Skip(Math.Max(0, idx1d - (rules.ConsolidationBars + 1))).Take(rules.ConsolidationBars).ToList();
            if (consHighs.Any() && consLows.Any())
            {
                decimal cMax = consHighs.Max();
                decimal cMin = consLows.Min();
                decimal consRangePct = cMin > 0m ? (cMax - cMin) / cMin * 100m : 99m;
                if (consRangePct <= rules.ConsolidationMaxRangePct && currentPrice > cMax) isConsolidationBreakout = true;
            }
        }

        var high52W = IndicatorCalculator.Calculate52WeekHigh(highs1d, Math.Min(250, highs1d.Count));
        decimal cur52wHigh = high52W[idx1d];
        bool isNear52WHigh = currentPrice >= (1m - rules.Near52WeekPct / 100m) * cur52wHigh;

        bool breakoutGroupPassed = isPrevHighBreakout || isConsolidationBreakout || isNear52WHigh;
        var breakoutSubTypes = new List<string>();
        if (isPrevHighBreakout) breakoutSubTypes.Add("PRIOR_HIGH");
        if (isConsolidationBreakout) breakoutSubTypes.Add("CONSOLIDATION");
        if (isNear52WHigh) breakoutSubTypes.Add("NEAR_52W_HIGH");
        result.BreakoutSubType = breakoutSubTypes.Count > 0 ? string.Join(",", breakoutSubTypes) : "NONE";
        int scoreBefore = score;
        void AddFactor(string code, string name, int max, string timeframe)
        {
            result.Factors.Add(new SwingFactorScore(code, name, score - scoreBefore, max, timeframe));
            scoreBefore = score;
        }
        if (breakoutGroupPassed)
        {
            score += rules.BreakoutPoints;
            passedRules.Add($"BREAKOUT_GROUP (+{rules.BreakoutPoints} pts): Confirmed Breakout over PDH/Swing/Consolidation/52W High");
        }
        else
        {
            failedRules.Add($"BREAKOUT_GROUP (0/{rules.BreakoutPoints} pts): Inside trading range, no breakout detected");
        }
        AddFactor("BREAKOUT_GROUP", "Breakout", rules.BreakoutPoints, "15m");

        // Rule 5: VOL_CONFIRMATION (15 Pts Max)
        // Volume >= 2.5x the usual volume for this bar AND Volume > Prev Volume.
        // "Usual" = the same IST time slot on earlier days (Plan D3): intraday volume is U-shaped, so comparing the
        // 09:15 bar with the previous afternoon's bars handed the opening scans this score almost for free. Falls back
        // to the previous 20 bars when there are fewer than 3 earlier same-slot bars (or on daily candles).
        long curVol = refVolumes[refIdx];
        decimal avgVol20 = SameSlotAverageVolume(refCandles, refIdx)
            ?? (refIdx > 0 ? (decimal)refVolumes.Skip(Math.Max(0, refIdx - rules.VolumeAverageBars)).Take(Math.Min(rules.VolumeAverageBars, refIdx)).Average(v => (double)v) : 0m);
        decimal volMult = avgVol20 > 0m ? Math.Round((decimal)curVol / avgVol20, 2) : 0m;
        bool volSpikePassed = volMult >= rules.VolumeGoodMultiple; // good volume, bonus for heavy volume
        bool volGreaterPrev = refIdx >= 1 && curVol > refVolumes[refIdx - 1];

        if (volMult >= rules.VolumeHeavyMultiple && volGreaterPrev)
        {
            score += rules.VolumePoints;
            passedRules.Add($"VOL_CONFIRMATION (+{rules.VolumePoints} pts): Heavy Volume Surge ({volMult:F1}x Avg Volume)");
        }
        else if (volSpikePassed)
        {
            score += rules.VolumeGoodPoints;
            passedRules.Add($"VOL_CONFIRMATION (+{rules.VolumeGoodPoints} pts): Good Volume ({volMult:F1}x Avg Volume)");
        }
        else
        {
            failedRules.Add($"VOL_CONFIRMATION (0/{rules.VolumePoints} pts): Volume low ({volMult:F1}x Avg Volume)");
        }
        AddFactor("VOL_CONFIRMATION", "Volume", rules.VolumePoints, "15m");
        result.VolumeMultiple = volMult;

        // Rule 6: RELATIVE_STRENGTH (15 Pts Max)
        // Stock 1M or 3M return > NIFTY 50 return
        // Both returns are measured over the SAME dates (Plan D11): NIFTY's close is looked up on the stock's start and
        // end dates (latest NIFTY close on or before each), so a stale or extra NIFTY row can't skew the comparison.
        bool rsPassed = false;
        if (idx1d >= rules.RelativeStrengthBars && niftyCandles1d != null && niftyCandles1d.Count >= rules.RelativeStrengthBars + 1)
        {
            decimal stockRet1M = (price1d - closes1d[idx1d - rules.RelativeStrengthBars]) / closes1d[idx1d - rules.RelativeStrengthBars];
            decimal? niftyStart = NiftyCloseOnOrBefore(niftyCandles1d, stockCandles1d[idx1d - rules.RelativeStrengthBars].CandleTime);
            decimal? niftyEnd = NiftyCloseOnOrBefore(niftyCandles1d, stockCandles1d[idx1d].CandleTime);
            if (niftyStart > 0m && niftyEnd > 0m)
            {
                decimal niftyRet1M = (niftyEnd.Value - niftyStart.Value) / niftyStart.Value;
                rsPassed = stockRet1M > niftyRet1M;
            }
        }
        if (rsPassed)
        {
            score += rules.RelativeStrengthPoints;
            passedRules.Add($"RELATIVE_STRENGTH (+{rules.RelativeStrengthPoints} pts): Outperforming NIFTY 50 Benchmark");
        }
        else
        {
            failedRules.Add($"RELATIVE_STRENGTH (0/{rules.RelativeStrengthPoints} pts): Underperforming NIFTY 50 Benchmark");
        }
        AddFactor("RELATIVE_STRENGTH", "Relative strength", rules.RelativeStrengthPoints, "1d");

        // Rule 7: MULTITIMEFRAME (15 Pts Max)
        // 60m Close > 60m EMA20 AND 60m RSI >= 40
        // Fails closed (no points) when there isn't enough 60m data to confirm - a data gap
        // should never be scored as bullish confirmation.
        bool hasMtfData = stockCandles60m != null && stockCandles60m.Count >= rules.Mtf60mEmaPeriod;
        bool mtfPassed = false;
        if (hasMtfData)
        {
            var closes60m = stockCandles60m!.Select(c => c.Close).ToList();
            var ema20_60m = IndicatorCalculator.CalculateEma(closes60m, rules.Mtf60mEmaPeriod);
            var rsi_60m = IndicatorCalculator.CalculateRsi(closes60m, rules.Mtf60mRsiPeriod);

            int idx60m = stockCandles60m!.Count - 1;
            decimal close60m = closes60m[idx60m];
            decimal ema20_60mVal = ema20_60m[idx60m];
            decimal rsi60mVal = rsi_60m[idx60m];

            mtfPassed = close60m > ema20_60mVal && rsi60mVal >= rules.Mtf60mRsiMinBuy;
            result.Rsi60m = Math.Round(rsi60mVal, 2);
            result.Is60mAboveEma20 = close60m > ema20_60mVal;
        }
        result.Has60mData = hasMtfData;
        if (mtfPassed)
        {
            score += rules.Mtf60mPoints;
            passedRules.Add($"MULTITIMEFRAME (+{rules.Mtf60mPoints} pts): 60m Hourly Trend Alignment (Close > EMA{rules.Mtf60mEmaPeriod})");
        }
        else if (!hasMtfData)
        {
            failedRules.Add($"MULTITIMEFRAME (0/{rules.Mtf60mPoints} pts): Insufficient 60m data - confirmation withheld");
        }
        else
        {
            failedRules.Add($"MULTITIMEFRAME (0/{rules.Mtf60mPoints} pts): 60m Hourly Trend Bearish");
        }
        AddFactor("MULTITIMEFRAME", "60m trend", rules.Mtf60mPoints, "60m");

        // Rule 8: RSI_MOMENTUM (10 Pts Max)
        // RSI(14) between 50 and 75 (Sweet spot: 55-70)
        var rsi14 = IndicatorCalculator.CalculateRsi(refCloses, rules.RsiPeriod);
        decimal curRsi = rsi14[refIdx];
        bool rsiPassed = curRsi >= rules.RsiBuyOkLow && curRsi <= rules.RsiBuyOkHigh;
        if (curRsi >= rules.RsiBuyBestLow && curRsi <= rules.RsiBuyBestHigh)
        {
            score += rules.RsiPoints;
            passedRules.Add($"RSI_MOMENTUM (+{rules.RsiPoints} pts): Sweet Spot Zone ({curRsi:F1})");
        }
        else if (rsiPassed)
        {
            score += rules.RsiGoodPoints;
            passedRules.Add($"RSI_MOMENTUM (+{rules.RsiGoodPoints} pts): Acceptable Zone ({curRsi:F1})");
        }
        else
        {
            failedRules.Add($"RSI_MOMENTUM (0/{rules.RsiPoints} pts): Outside {rules.RsiBuyOkLow:0.##}-{rules.RsiBuyOkHigh:0.##} Zone ({curRsi:F1})");
        }
        AddFactor("RSI_MOMENTUM", "RSI", rules.RsiPoints, "15m");
        result.Rsi15m = Math.Round(curRsi, 2);

        // Rule 9: MACD_BULLISH (10 Pts Max)
        // MACD Line > Signal Line OR fresh bullish crossover
        var (macd, macdSignal) = IndicatorCalculator.CalculateMacd(refCloses, rules.MacdFast, rules.MacdSlow, rules.MacdSignal);
        decimal curMacd = macd[refIdx];
        decimal curMacdSignal = macdSignal[refIdx];
        bool macdPassed = curMacd > curMacdSignal;
        if (macdPassed)
        {
            score += rules.MacdPoints;
            passedRules.Add($"MACD_BULLISH (+{rules.MacdPoints} pts): MACD Bullish Alignment ({curMacd:F2} > Signal {curMacdSignal:F2})");
        }
        else
        {
            failedRules.Add($"MACD_BULLISH (0/{rules.MacdPoints} pts): Bearish MACD Line below Signal");
        }
        AddFactor("MACD_BULLISH", "MACD", rules.MacdPoints, "15m");

        // Rule 10: BULLISH_CANDLE (8 Pts Max)
        // Bullish Engulfing / Marubozu / Breakout candle (>1.5x ATR) / Close near High
        var atr14 = IndicatorCalculator.CalculateAtr(refHighs, refLows, refCloses, rules.AtrPeriod);
        decimal curAtr = Math.Max(0.1m, atr14[refIdx]);

        decimal openPrice = refOpens[refIdx];
        decimal highPrice = refHighs[refIdx];
        decimal lowPrice = refLows[refIdx];
        bool isCloseNearHigh = (highPrice - lowPrice) > 0m && ((currentPrice - lowPrice) / (highPrice - lowPrice)) >= 1m - rules.CandleCloseZonePct / 100m;
        bool isBreakoutCandle = (currentPrice - openPrice) >= (rules.CandleBodyAtrMultiple * curAtr);
        bool candlePassed = (currentPrice > openPrice) && (isCloseNearHigh || isBreakoutCandle);

        if (candlePassed)
        {
            score += rules.CandlePoints;
            passedRules.Add($"BULLISH_CANDLE (+{rules.CandlePoints} pts): Strong Bullish Candle Pattern (Close near High / ATR Expansion)");
        }
        else
        {
            failedRules.Add($"BULLISH_CANDLE (0/{rules.CandlePoints} pts): Weak or indecisive candle pattern");
        }
        AddFactor("BULLISH_CANDLE", "Candle", rules.CandlePoints, "15m");

        // Rule 11: RISK_REWARD (7 Pts Max)
        // Target (Price + 2.0*curAtr) vs SL (Price - 1.5*curAtr) ratio >= 1:2.0
        result.StopLoss = Math.Round(Math.Max(0.01m, currentPrice - (rules.StopAtrMultiple * curAtr)), 2);
        decimal risk = Math.Max(0.01m, currentPrice - result.StopLoss);
        result.Target1 = Math.Round(currentPrice + (rules.Target1R * risk), 2);
        result.Target2 = Math.Round(currentPrice + (rules.Target2R * risk), 2);
        decimal reward = result.Target1 - currentPrice;
        result.RiskRewardRatio = risk > 0m ? Math.Round(reward / risk, 2) : 0m;
        bool rrPassed = result.RiskRewardRatio >= rules.MinRiskReward;

        if (rrPassed)
        {
            score += rules.RiskRewardPoints;
            passedRules.Add($"RISK_REWARD (+{rules.RiskRewardPoints} pts): Valid R:R Ratio (1:{result.RiskRewardRatio:F1} >= 1:{rules.MinRiskReward:0.0})");
        }
        else
        {
            failedRules.Add($"RISK_REWARD (0/{rules.RiskRewardPoints} pts): Low R:R Ratio (1:{result.RiskRewardRatio:F1})");
        }
        AddFactor("RISK_REWARD", "Risk:reward", rules.RiskRewardPoints, "15m");

        // --------------------------------------------------------------------
        // STAGE C: MARKET CONTEXT RISK ADJUSTMENT + SIGNAL DECISION THRESHOLDS
        // --------------------------------------------------------------------
        // Soft mode only (RequireNiftyMarketFilter = false; in mandatory mode a failed
        // NIFTY filter already returned REJECT above): a weak market lowers confidence
        // instead of blocking, so a strong setup can still clear the BUY threshold.
        if (!niftyPassed)
        {
            score -= marketContextScorePenalty;
            result.MarketPenaltyApplied = marketContextScorePenalty;
        }

        result.Score = Math.Min(100, Math.Max(0, score));
        result.ConfidencePct = result.Score;
        result.PassedRules = passedRules;
        result.FailedRules = failedRules;

        if (result.Score >= buyScoreThreshold)
        {
            result.Decision = "BUY";
            result.IsBuySignal = true;
            string marketNote = niftyPassed ? string.Empty : " [Note: taken against a weak broader market - reduced size applied]";
            result.Reason = $"BUY Signal Confirmed (Score: {result.Score}/100). Passed Stock-Level Hard Filters & {passedRules.Count} Scoring Rules. Entry: ₹{currentPrice:F2}, SL: ₹{result.StopLoss:F2}, Target 1: ₹{result.Target1:F2} (1:{result.RiskRewardRatio:F1} R:R).{marketNote}";
        }
        else if (result.Score >= watchScoreThreshold)
        {
            result.Decision = "WATCH";
            result.IsBuySignal = false;
            result.Reason = $"WATCHLIST Candidate (Score: {result.Score}/100). Passed Hard Filters, but pending breakout / volume momentum.";
        }
        else
        {
            result.Decision = "NO SIGNAL";
            result.IsBuySignal = false;
            result.Reason = $"NO SIGNAL (Score: {result.Score}/100 < {watchScoreThreshold} threshold). Failed factors: {string.Join("; ", failedRules.Take(3))}";
        }

        // Calculate Position Sizing (1% Portfolio Risk Rule assuming ₹1,000,000 capital = ₹10,000 risk)
        decimal accountCapital = 1000000m;
        decimal maxRiskPerTrade = accountCapital * 0.01m; // ₹10,000 risk
        result.CalculatedRiskAmount = Math.Round(risk, 2);
        result.RecommendedQty = risk > 0m ? (int)Math.Floor(maxRiskPerTrade / risk) : 0;

        // Defensive position sizing: reduce size (not block) when market context is weak
        if (!niftyPassed && result.RecommendedQty > 0)
        {
            result.RecommendedQty = Math.Max(1, (int)Math.Floor(result.RecommendedQty * marketContextPositionSizeFactor));
        }

        // Build UI Checklist
        result.Checklist = BuildChecklist(
            requireNifty, niftyPassed, emaTrendPassed, adxPassed, breakoutGroupPassed, volSpikePassed, rsPassed,
            mtfPassed, rsiPassed, macdPassed, candlePassed, rrPassed,
            currentPrice, curEma20_1d, curEma50_1d, volMult, curAdx_1d, curRsi, result.RiskRewardRatio, rules
        );

        return result;
    }

    /// <summary>
    /// Overload for backward compatibility with existing calls missing 15m candles.
    /// </summary>
    public static SwingEvaluationResult Evaluate(
        StockMaster stock,
        List<MarketCandle> stockCandles1d,
        List<MarketCandle> stockCandles60m,
        List<MarketCandle> niftyCandles1d,
        QuantEdge.Domain.Entities.SwingStrategySettings? settings = null)
    {
        return Evaluate(stock, stockCandles1d, null!, stockCandles60m, niftyCandles1d, settings);
    }

    /// <summary>
    /// NIFTY 50 market filter: Close > SMA50 AND EMA20 > EMA50 on daily candles.
    /// Missing/insufficient data (&lt; 50 candles) fails closed when the filter is mandatory
    /// (never buy blind) and passes in legacy soft mode.
    /// Shared by the engine and the auto-trade workers' once-per-scan market gate.
    /// </summary>
    public static bool IsNiftyMarketFilterPassed(List<MarketCandle>? niftyCandles, QuantEdge.Domain.Entities.SwingStrategySettings? settings = null)
    {
        settings ??= QuantEdge.Domain.Entities.SwingStrategySettings.Default;
        var rules = settings.Rules;
        if (niftyCandles == null || niftyCandles.Count < rules.MinNiftyCandles) return !settings.RequireNiftyMarketFilter;

        var closes = niftyCandles.Select(c => c.Close).ToList();
        var sma50 = IndicatorCalculator.CalculateSma(closes, rules.NiftySmaPeriod);
        var ema20 = IndicatorCalculator.CalculateEma(closes, rules.NiftyEmaFast);
        var ema50 = IndicatorCalculator.CalculateEma(closes, rules.NiftyEmaSlow);

        int idx = niftyCandles.Count - 1;
        bool isAboveSma50 = closes[idx] > sma50[idx];
        bool isEmaBullish = ema20[idx] > ema50[idx];

        return isAboveSma50 && isEmaBullish;
    }

    private static ConditionChecklistDto BuildChecklist(
        bool requireNifty, bool niftyPassed, bool emaTrendPassed, bool adxPassed, bool breakoutPassed,
        bool volSpikePassed, bool rsPassed, bool mtfPassed, bool rsiPassed, bool macdPassed,
        bool candlePassed, bool rrPassed,
        decimal price, decimal ema20, decimal ema50, decimal volMult, decimal adx, decimal rsi, decimal rr, SwingRuleParameters rules)
    {
        var niftyItem = requireNifty
            ? new ConditionItemDto("HARD_NIFTY_MARKET_FILTER", "1. [HARD FILTER] Nifty Market Filter", $"Nifty 50 Close > {rules.NiftySmaPeriod} DMA & EMA{rules.NiftyEmaFast} > EMA{rules.NiftyEmaSlow} (mandatory - no stock is bought when this fails)",
                niftyPassed ? "Passed (Market Uptrend)" : "Failed (Market Weak - No New Entries)", $"Close > SMA{rules.NiftySmaPeriod} & EMA{rules.NiftyEmaFast} > EMA{rules.NiftyEmaSlow}", niftyPassed)
            : new ConditionItemDto("MARKET_CONTEXT_FILTER", "1. [MARKET CONTEXT - RISK FACTOR] Nifty Market Filter", $"Nifty 50 Close > {rules.NiftySmaPeriod} DMA & EMA{rules.NiftyEmaFast} > EMA{rules.NiftyEmaSlow} (does not block trade; applies score/size penalty when failed)",
                niftyPassed ? "Passed (Market Uptrend)" : "Failed (Defensive Mode - Risk Adjustment Applied)", $"Close > SMA{rules.NiftySmaPeriod} & EMA{rules.NiftyEmaFast} > EMA{rules.NiftyEmaSlow}", niftyPassed);

        var conditions = new List<ConditionItemDto>
        {
            niftyItem,

            new("HARD_EMA_TREND", "2. [HARD FILTER] EMA Trend Alignment", $"Daily Close > EMA{rules.TrendEmaFast} > EMA{rules.TrendEmaSlow} with rising slopes",
                emaTrendPassed ? $"Passed (Close ₹{price:F1} > EMA{rules.TrendEmaFast} ₹{ema20:F1} > EMA{rules.TrendEmaSlow} ₹{ema50:F1})" : $"Close ₹{price:F1}, EMA{rules.TrendEmaFast} ₹{ema20:F1}",
                $"Close > EMA{rules.TrendEmaFast} > EMA{rules.TrendEmaSlow}", emaTrendPassed),

            new("HARD_ADX_STRENGTH", "3. [HARD FILTER] ADX Trend Strength", $"Daily ADX ({rules.AdxPeriod}) >= {rules.AdxMin:0.0} (Filters out choppy markets)",
                $"{adx:F1} ({(adx >= rules.AdxMin ? "Passed" : "Weak/Choppy")})", $"ADX >= {rules.AdxMin:0.0}", adxPassed),

            new("BREAKOUT_GROUP", $"4. Breakout Group ({rules.BreakoutPoints} Pts)", "15m Close > Previous Day High / Consolidation / 52W High",
                breakoutPassed ? "Passed (Breakout Confirmed)" : "Inside Range", "Breakout Confirmed", breakoutPassed),

            new("VOL_CONFIRMATION", $"5. Volume Confirmation ({rules.VolumePoints} Pts)", $"15m Volume >= {rules.VolumeGoodMultiple:0.0#}x the usual volume for this time of day (same slot on earlier days) & > Prev Vol",
                volSpikePassed ? $"Passed ({volMult:F1}x Avg Vol)" : $"{volMult:F1}x Avg Vol", $">= {rules.VolumeGoodMultiple:0.0#}x Avg Vol", volSpikePassed),

            new("RELATIVE_STRENGTH", $"6. Relative Strength vs Nifty ({rules.RelativeStrengthPoints} Pts)", rules.RelativeStrengthBars == 20 ? "Stock 1M / 3M Return > Nifty 50 Return" : $"Stock {rules.RelativeStrengthBars}-day Return > Nifty 50 Return",
                rsPassed ? "Passed (Outperforming Nifty)" : "Underperforming", "Stock Return > Nifty Return", rsPassed),

            new("MULTITIMEFRAME", $"7. Multi-Timeframe Confirmation ({rules.Mtf60mPoints} Pts)", $"60m Close > 60m EMA{rules.Mtf60mEmaPeriod} & 60m RSI >= {rules.Mtf60mRsiMinBuy:0.##}",
                mtfPassed ? "Passed (60m Hourly Bullish)" : "60m Bearish", $"60m Close > EMA{rules.Mtf60mEmaPeriod}", mtfPassed),

            new("RSI_MOMENTUM", $"8. RSI Momentum Zone ({rules.RsiPoints} Pts)", $"15m RSI ({rules.RsiPeriod}) between {rules.RsiBuyOkLow:0.##} and {rules.RsiBuyOkHigh:0.##} (Best: {rules.RsiBuyBestLow:0.##}-{rules.RsiBuyBestHigh:0.##})",
                $"{rsi:F1}", $"{rules.RsiBuyOkLow:0.0} - {rules.RsiBuyOkHigh:0.0} Zone", rsiPassed),

            new("MACD_BULLISH", $"9. MACD Bullish Signal ({rules.MacdPoints} Pts)", "15m MACD Line > Signal Line",
                macdPassed ? "Passed (MACD > Signal)" : "MACD Bearish", "MACD > Signal Line", macdPassed),

            new("BULLISH_CANDLE", $"10. Bullish Candle Pattern ({rules.CandlePoints} Pts)", "15m Bullish Engulfing, Marubozu, or Close near High",
                candlePassed ? "Passed (Bullish Candle)" : "Weak Candle", "Bullish Candle Pattern", candlePassed),

            new("RISK_REWARD", $"11. Risk/Reward Ratio ({rules.RiskRewardPoints} Pts)", $"Suggested Target 1 vs Stop Loss Ratio >= 1:{rules.MinRiskReward:0.0}",
                rrPassed ? $"Passed (1:{rr:F1} R:R)" : $"Failed (1:{rr:F1} R:R)", $"Risk Reward Ratio >= 1:{rules.MinRiskReward:0.0}", rrPassed)
        };

        int metCount = conditions.Count(c => c.IsMet);
        return new ConditionChecklistDto(metCount, conditions.Count, conditions);
    }

    private static ConditionChecklistDto BuildEmptyChecklist(string reason)
    {
        var conditions = new List<ConditionItemDto>
        {
            new("DATA_CHECK", "Data Integrity", reason, "Failed", "Sufficient Candles", false)
        };
        return new ConditionChecklistDto(0, 1, conditions);
    }

    /// <summary>Daily bars needed before EMA200 is trusted (200 + warm-up).</summary>
    public const int MinBarsForEma200 = 220;

    /// <summary>The candles without the last one when that one hasn't closed yet (start + interval is in the future).</summary>
    public static List<MarketCandle> ClosedCandlesOnly(List<MarketCandle>? candles, TimeSpan interval, DateTime nowUtc)
    {
        if (candles == null || candles.Count == 0) return candles ?? new List<MarketCandle>();
        var last = candles[^1];
        DateTime lastStartUtc = last.CandleTime.Kind == DateTimeKind.Local ? last.CandleTime.ToUniversalTime()
            : DateTime.SpecifyKind(last.CandleTime, DateTimeKind.Utc);
        return lastStartUtc + interval > nowUtc ? candles.Take(candles.Count - 1).ToList() : candles;
    }

    /// <summary>
    /// Average volume of the bars at the same IST time of day as candles[index] on earlier days (intraday only);
    /// null when there are fewer than 3 such bars or the candles are daily.
    /// </summary>
    public static decimal? SameSlotAverageVolume(IReadOnlyList<MarketCandle> candles, int index)
    {
        if (index < 1 || candles[index].Timeframe == "1d") return null;
        TimeSpan slot = IstTimeOfDay(candles[index].CandleTime);
        DateTime day = DayChangeCalculator.IstDate(candles[index].CandleTime);
        var sameSlot = new List<long>();
        for (int i = index - 1; i >= 0 && sameSlot.Count < 20; i--)
        {
            if (IstTimeOfDay(candles[i].CandleTime) == slot && DayChangeCalculator.IstDate(candles[i].CandleTime) < day)
                sameSlot.Add(candles[i].Volume);
        }
        return sameSlot.Count >= 3 ? (decimal)sameSlot.Average(v => (double)v) : null;
    }

    /// <summary>NIFTY's close on the stock candle's IST date, or the latest one before it.</summary>
    public static decimal? NiftyCloseOnOrBefore(IReadOnlyList<MarketCandle> nifty, DateTime candleTime)
    {
        DateTime date = DayChangeCalculator.IstDate(candleTime);
        for (int i = nifty.Count - 1; i >= 0; i--)
        {
            if (DayChangeCalculator.IstDate(nifty[i].CandleTime) <= date) return nifty[i].Close;
        }
        return null;
    }

    private static TimeSpan IstTimeOfDay(DateTime time)
    {
        var utc = time.Kind == DateTimeKind.Local ? time.ToUniversalTime() : DateTime.SpecifyKind(time, DateTimeKind.Utc);
        return TimeZoneInfo.ConvertTimeFromUtc(utc, Helpers.TimeZoneHelper.IndianTimeZone).TimeOfDay;
    }
}
