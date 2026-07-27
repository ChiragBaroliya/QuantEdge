using System;
using System.Collections.Generic;
using System.Linq;
using QuantEdge.Domain.Entities;
using QuantEdge.Infrastructure.DTOs;

namespace QuantEdge.Infrastructure.Services;

public class SwingEvaluationResult
{
    public int Score { get; set; }
    public string Decision { get; set; } = "HOLD";
    public decimal ConfidencePct { get; set; }
    public bool IsMarketFilterPassed { get; set; }
    public bool IsBuySignal { get; set; }
    public bool IsSellSignal { get; set; }
    public decimal EntryPrice { get; set; }
    public decimal StopLoss { get; set; }
    public decimal Target1 { get; set; }
    public decimal Target2 { get; set; }
    public decimal RiskRewardRatio { get; set; }
    public string Reason { get; set; } = string.Empty;
    public List<string> PassedRules { get; set; } = new();
    public List<string> FailedRules { get; set; } = new();
    public ConditionChecklistDto Checklist { get; set; } = null!;
    public string Sector { get; set; } = string.Empty;
}

public static class SwingDecisionEngine
{
    public static SwingEvaluationResult Evaluate(
        StockMaster stock,
        List<MarketCandle> stockCandles1d,
        List<MarketCandle> stockCandles60m,
        List<MarketCandle> niftyCandles1d)
    {
        var result = new SwingEvaluationResult();
        string symbol = stock?.Symbol ?? "UNKNOWN";
        result.Sector = GetSectorForSymbol(symbol);

        if (stockCandles1d == null || stockCandles1d.Count < 50)
        {
            result.Decision = "HOLD";
            result.Reason = "Insufficient daily candle history (minimum 50 required).";
            result.Checklist = BuildEmptyChecklist("Insufficient daily data");
            return result;
        }

        // Align daily arrays
        var closes = stockCandles1d.Select(c => c.Close).ToList();
        var highs = stockCandles1d.Select(c => c.High).ToList();
        var lows = stockCandles1d.Select(c => c.Low).ToList();
        var opens = stockCandles1d.Select(c => c.Open).ToList();
        var volumes = stockCandles1d.Select(c => c.Volume).ToList();

        int idx = closes.Count - 1;
        decimal price = closes[idx];
        decimal open = opens[idx];
        decimal high = highs[idx];
        decimal low = lows[idx];
        long vol = volumes[idx];

        result.EntryPrice = price;

        // Calculate Technical Indicators
        var ema20 = IndicatorCalculator.CalculateEma(closes, 20);
        var ema50 = IndicatorCalculator.CalculateEma(closes, 50);
        var ema200 = IndicatorCalculator.CalculateEma(closes, 200);
        var rsi14 = IndicatorCalculator.CalculateRsi(closes, 14);
        var (macd, macdSignal) = IndicatorCalculator.CalculateMacd(closes);
        var adx14 = IndicatorCalculator.CalculateAdx(highs, lows, closes, 14);
        var atr14 = IndicatorCalculator.CalculateAtr(highs, lows, closes, 14);
        var high52W = IndicatorCalculator.Calculate52WeekHigh(highs, Math.Min(250, highs.Count));

        decimal curEma20 = ema20[idx];
        decimal curEma50 = ema50[idx];
        decimal curEma200 = ema200[idx];
        decimal curRsi = rsi14[idx];
        decimal curMacd = macd[idx];
        decimal curMacdSignal = macdSignal[idx];
        decimal curAdx = adx14[idx];
        decimal curAtr = Math.Max(0.1m, atr14[idx]);
        decimal cur52wHigh = high52W[idx];

        // 20-day Average Volume
        var prev20Vol = volumes.Skip(Math.Max(0, idx - 20)).Take(Math.Min(20, idx)).ToList();
        decimal avgVol20 = prev20Vol.Any() ? (decimal)prev20Vol.Average(v => (double)v) : 0m;
        decimal volMult = avgVol20 > 0m ? Math.Round(vol / avgVol20, 2) : 0m;

        // ATR Stop Loss & Target Calculations (Rules 11 & 12)
        result.StopLoss = Math.Round(Math.Max(0.01m, price - (1.0m * curAtr)), 2);
        decimal risk = Math.Max(0.01m, price - result.StopLoss);
        result.Target1 = Math.Round(price + (2.0m * curAtr), 2);
        result.Target2 = Math.Round(price + (3.0m * curAtr), 2);
        decimal reward = result.Target1 - price;
        result.RiskRewardRatio = Math.Round(reward / risk, 2);

        // Nifty Market Filter
        bool niftyPassed = EvaluateNiftyMarketFilter(niftyCandles1d);
        result.IsMarketFilterPassed = niftyPassed;

        // Scoring Accumulators (Total 100 max)
        int score = 0;
        var passedRules = new List<string>();
        var failedRules = new List<string>();

        // 1. Market Filter (5 pts)
        if (niftyPassed)
        {
            score += 5;
            passedRules.Add("Nifty Market Filter (Passed)");
        }
        else
        {
            failedRules.Add("Nifty Market Filter (Market in Downtrend/Correction)");
        }

        // 2. EMA Trend Rule (Max 20 pts) (Rule 2)
        // Check base conditions: Close > EMA20, EMA20 > EMA50, rising slopes, EMA200 not falling sharply
        bool ema20Rising = idx >= 2 && ema20[idx] > ema20[idx - 2];
        bool ema50Rising = idx >= 2 && ema50[idx] > ema50[idx - 2];
        bool ema200NotFallingSharply = idx >= 5 && (ema200[idx] >= ema200[idx - 5] * 0.995m);
        bool baseEmaTrend = price > curEma20 && curEma20 > curEma50 && ema20Rising && ema50Rising && ema200NotFallingSharply;
        bool perfectEmaAlignment = baseEmaTrend && curEma50 > curEma200;

        if (perfectEmaAlignment)
        {
            score += 20;
            passedRules.Add("EMA Trend: Perfect Bullish Alignment (Close > EMA20 > EMA50 > EMA200)");
        }
        else if (baseEmaTrend)
        {
            score += 15;
            passedRules.Add("EMA Trend: Strong Base Uptrend (Close > EMA20 > EMA50, Rising Slopes)");
        }
        else
        {
            failedRules.Add("EMA Trend: Price below EMA20/50 or flat/declining slopes");
        }

        // 3. Breakout Rules (Max 20 pts) (Rules 7 & 8)
        // Rule 7: Previous Day High / Swing High Breakout (10 pts)
        decimal prevDayHigh = idx >= 1 ? highs[idx - 1] : high;
        decimal swingHigh = idx >= 15 ? highs.Skip(Math.Max(0, idx - 15)).Take(15).Max() : high;
        bool isPrevHighBreakout = price > prevDayHigh || price >= swingHigh;

        if (isPrevHighBreakout)
        {
            score += 10;
            passedRules.Add($"Previous High Breakout (Close ₹{price:F2} > Prev High ₹{prevDayHigh:F2})");
        }
        else
        {
            failedRules.Add("Previous High Breakout (Inside range)");
        }

        // Rule 8: 10-20 Day Consolidation Breakout (10 pts)
        bool isConsolidationBreakout = false;
        if (idx >= 15)
        {
            var consHighs = highs.Skip(Math.Max(0, idx - 15)).Take(14).ToList();
            var consLows = lows.Skip(Math.Max(0, idx - 15)).Take(14).ToList();
            if (consHighs.Any() && consLows.Any())
            {
                decimal cMax = consHighs.Max();
                decimal cMin = consLows.Min();
                decimal consRangePct = cMin > 0m ? (cMax - cMin) / cMin * 100m : 99m;
                if (consRangePct <= 10.0m && price > cMax)
                {
                    isConsolidationBreakout = true;
                }
            }
        }

        if (isConsolidationBreakout)
        {
            score += 10;
            passedRules.Add("Consolidation Breakout (Broken out after 10-20 session tight consolidation)");
        }

        // 4. Volume Rule (Max 15 pts) (Rule 4)
        bool volSpike15x = volMult >= 1.5m;
        bool volGreaterPrev = idx >= 1 && vol > volumes[idx - 1];
        bool volExpansion2Sessions = idx >= 2 && vol > volumes[idx - 1] && volumes[idx - 1] > volumes[idx - 2];

        int volScore = 0;
        if (volSpike15x) volScore += 7;
        if (volGreaterPrev) volScore += 4;
        if (volExpansion2Sessions) volScore += 4;

        score += volScore;
        if (volScore >= 11)
        {
            passedRules.Add($"Volume Expansion ({volMult:F1}x Avg, Volume increasing 2 sessions)");
        }
        else if (volScore >= 7)
        {
            passedRules.Add($"Volume Spike ({volMult:F1}x Avg Volume)");
        }
        else
        {
            failedRules.Add($"Volume Expansion ({volMult:F1}x Avg Vol, missing volume surge)");
        }

        // 5. ADX Trend Strength Rule (Max 10 pts) (Rule 1)
        if (curAdx >= 25m)
        {
            score += 10;
            passedRules.Add($"ADX Trend Strength: Strong Trend ({curAdx:F1} >= 25)");
        }
        else if (curAdx >= 20m)
        {
            score += 7;
            passedRules.Add($"ADX Trend Strength: Good Trend ({curAdx:F1} in 20-24)");
        }
        else if (curAdx >= 18m)
        {
            score += 4;
            passedRules.Add($"ADX Trend Strength: Moderate Trend ({curAdx:F1} in 18-19)");
        }
        else
        {
            failedRules.Add($"ADX Trend Strength: Weak Trend ({curAdx:F1} < 18)");
        }

        // 6. RSI Rule (Max 10 pts) (Rule 3)
        if (curRsi >= 55m && curRsi <= 70m)
        {
            score += 10;
            passedRules.Add($"RSI Momentum: Best Zone ({curRsi:F1} in 55-70)");
        }
        else if (curRsi >= 50m && curRsi < 55m)
        {
            score += 7;
            passedRules.Add($"RSI Momentum: Moderate Zone ({curRsi:F1} in 50-55)");
        }
        else if (curRsi > 70m && curRsi <= 75m)
        {
            score += 5;
            passedRules.Add($"RSI Momentum: Acceptable ({curRsi:F1} in 70-75)");
        }
        else
        {
            failedRules.Add($"RSI Zone: Outside sweet spot ({curRsi:F1})");
        }

        // 7. MACD Confirmation Rule (Max 10 pts)
        bool macdAboveSignal = curMacd > curMacdSignal;
        bool macdCrossedToday = idx >= 1 && macdAboveSignal && macd[idx - 1] <= macdSignal[idx - 1];

        if (macdAboveSignal && macdCrossedToday)
        {
            score += 10;
            passedRules.Add($"MACD: Fresh Bullish Crossover (MACD {curMacd:F2} > Signal {curMacdSignal:F2})");
        }
        else if (macdAboveSignal)
        {
            score += 7;
            passedRules.Add($"MACD: Bullish Alignment (MACD {curMacd:F2} > Signal {curMacdSignal:F2})");
        }
        else
        {
            failedRules.Add("MACD: Bearish or Below Signal Line");
        }

        // 8. 52-Week High Rule (Max 5 pts) (Rule 5)
        bool isNew52WBreakout = price >= cur52wHigh;
        bool isWithin5Pct = price >= 0.95m * cur52wHigh;
        bool isWithin10Pct = price >= 0.90m * cur52wHigh;

        if (isNew52WBreakout)
        {
            score += 5;
            passedRules.Add("52-Week High: New 52W High Breakout!");
        }
        else if (isWithin5Pct)
        {
            score += 3;
            passedRules.Add("52-Week High: Within 5% of 52W High");
        }
        else if (isWithin10Pct)
        {
            score += 1;
            passedRules.Add("52-Week High: Within 10% of 52W High");
        }
        else
        {
            failedRules.Add("52-Week High: Below 10% threshold");
        }

        // 9. Relative Strength vs Nifty (Max 5 pts) (Rules 9 & 10)
        bool rs1m = false;
        bool rs3m = false;

        if (idx >= 20 && niftyCandles1d != null && niftyCandles1d.Count >= 21)
        {
            int nIdx = niftyCandles1d.Count - 1;
            decimal stockRet1M = (price - closes[idx - 20]) / closes[idx - 20];
            decimal niftyRet1M = (niftyCandles1d[nIdx].Close - niftyCandles1d[nIdx - 20].Close) / niftyCandles1d[nIdx - 20].Close;
            rs1m = stockRet1M > niftyRet1M;

            if (idx >= 60 && niftyCandles1d.Count >= 61)
            {
                decimal stockRet3M = (price - closes[idx - 60]) / closes[idx - 60];
                decimal niftyRet3M = (niftyCandles1d[nIdx].Close - niftyCandles1d[nIdx - 60].Close) / niftyCandles1d[nIdx - 60].Close;
                rs3m = stockRet3M > niftyRet3M;
            }
        }

        if (rs1m && rs3m)
        {
            score += 5;
            passedRules.Add("Relative Strength: Outperforming Nifty 50 on both 1M and 3M");
        }
        else if (rs1m || rs3m)
        {
            score += 3;
            passedRules.Add("Relative Strength: Outperforming Nifty 50");
        }
        else
        {
            failedRules.Add("Relative Strength: Underperforming Nifty 50 benchmark");
        }

        // Bullish Candle Rule Check (Rule 6)
        bool isBullishEngulfing = idx >= 1 && price > open && opens[idx - 1] > closes[idx - 1] && open <= closes[idx - 1] && price >= opens[idx - 1];
        bool isMarubozu = (high - low) > 0m && ((price - open) / (high - low)) >= 0.80m;
        bool isStrongBreakoutCandle = (price - open) >= (1.5m * curAtr) || (open > 0m && (price - open) / open >= 0.025m);
        bool isCloseNearHigh = (high - low) > 0m && ((price - low) / (high - low)) >= 0.75m;
        bool isCloseAbovePrevHigh = idx >= 1 && price > highs[idx - 1];

        bool isStrongBullishCandle = isBullishEngulfing || isMarubozu || isStrongBreakoutCandle || isCloseNearHigh || isCloseAbovePrevHigh;

        // Multi-Timeframe Confirmation (Rule 14)
        bool hourly60mPassed = true;
        string mtfReason = "";
        if (stockCandles60m != null && stockCandles60m.Count >= 20)
        {
            var closes60m = stockCandles60m.Select(c => c.Close).ToList();
            var ema20_60m = IndicatorCalculator.CalculateEma(closes60m, 20);
            var rsi_60m = IndicatorCalculator.CalculateRsi(closes60m, 14);

            int idx60m = stockCandles60m.Count - 1;
            decimal close60m = closes60m[idx60m];
            decimal ema20_60mVal = ema20_60m[idx60m];
            decimal rsi60mVal = rsi_60m[idx60m];

            hourly60mPassed = close60m > ema20_60mVal && rsi60mVal >= 40m;
            if (!hourly60mPassed)
            {
                mtfReason = $"1-Hour timeframe is bearish (60m Close ₹{close60m:F2} < EMA20 ₹{ema20_60mVal:F2}, RSI {rsi60mVal:F1}).";
                failedRules.Add("Multi-Timeframe: 60m Trend is Bearish");
            }
            else
            {
                passedRules.Add("Multi-Timeframe: 60m Hourly Trend is Bullish");
            }
        }

        // Decision Categorization (Rule 13)
        result.Score = Math.Min(100, Math.Max(0, score));
        result.ConfidencePct = result.Score;
        result.PassedRules = passedRules;
        result.FailedRules = failedRules;

        if (result.Score >= 90)
        {
            result.Decision = "STRONG BUY";
        }
        else if (result.Score >= 75)
        {
            result.Decision = "BUY";
        }
        else if (result.Score >= 60)
        {
            result.Decision = "HOLD";
        }
        else
        {
            result.Decision = "AVOID";
        }

        // Signal Generation Validation (Rules 12 & 14)
        bool rrPassed = result.RiskRewardRatio >= 2.0m;
        if (!rrPassed)
        {
            failedRules.Add($"Risk Reward Filter (< 1:2 ratio: {result.RiskRewardRatio:F2})");
        }
        else
        {
            passedRules.Add($"Risk Reward Filter (1:{result.RiskRewardRatio:F1} >= 1:2)");
        }

        if ((result.Decision == "BUY" || result.Decision == "STRONG BUY"))
        {
            if (!niftyPassed || !hourly60mPassed || !rrPassed || !isStrongBullishCandle)
            {
                result.Decision = "HOLD"; // Downgrade to Watchlist
                result.IsBuySignal = false;
                result.Reason = $"Downgraded to HOLD / Watchlist (Score: {result.Score}/100). ";
                if (!niftyPassed) result.Reason += "Market Filter Failed. ";
                if (!hourly60mPassed) result.Reason += mtfReason + " ";
                if (!rrPassed) result.Reason += $"Risk/Reward ratio insufficient ({result.RiskRewardRatio:F2}). ";
                if (!isStrongBullishCandle) result.Reason += "Candle lacks strong bullish confirmation. ";
            }
            else
            {
                result.IsBuySignal = true;
                result.Reason = $"{result.Decision} (Score: {result.Score}/100). Passed {passedRules.Count} key criteria. Stop Loss: ₹{result.StopLoss:F2}, Target 1: ₹{result.Target1:F2} (1:{result.RiskRewardRatio:F1} R:R).";
            }
        }
        else
        {
            result.IsBuySignal = false;
            result.Reason = $"{result.Decision} (Score: {result.Score}/100). Failed factors: {string.Join("; ", failedRules.Take(4))}";
        }

        // Build Condition Checklist DTO for UI
        result.Checklist = BuildChecklist(
            niftyPassed, baseEmaTrend, isPrevHighBreakout, isConsolidationBreakout,
            volSpike15x, volGreaterPrev, curAdx, curRsi, curMacd > curMacdSignal,
            isWithin10Pct, isStrongBullishCandle, rs1m || rs3m, hourly60mPassed, rrPassed,
            price, curEma20, curEma50, curEma200, volMult, curAdx, curRsi, result.RiskRewardRatio
        );

        return result;
    }

    private static bool EvaluateNiftyMarketFilter(List<MarketCandle> niftyCandles)
    {
        if (niftyCandles == null || niftyCandles.Count < 50) return true; // Default fallback

        var closes = niftyCandles.Select(c => c.Close).ToList();
        var sma50 = IndicatorCalculator.CalculateSma(closes, 50);
        var ema20 = IndicatorCalculator.CalculateEma(closes, 20);
        var ema50 = IndicatorCalculator.CalculateEma(closes, 50);

        int idx = niftyCandles.Count - 1;
        bool isAboveSma50 = closes[idx] > sma50[idx];
        bool isEmaBullish = ema20[idx] > ema50[idx];

        return isAboveSma50 && isEmaBullish;
    }

    private static ConditionChecklistDto BuildChecklist(
        bool niftyPassed, bool emaTrendPassed, bool prevHighPassed, bool consBreakoutPassed,
        bool volSpikePassed, bool volGreaterPrevPassed, decimal adxVal, decimal rsiVal, bool macdPassed,
        bool high52wPassed, bool candlePassed, bool rsPassed, bool mtfPassed, bool rrPassed,
        decimal price, decimal ema20, decimal ema50, decimal ema200, decimal volMult, decimal adx, decimal rsi, decimal rr)
    {
        var conditions = new List<ConditionItemDto>
        {
            new("MARKET_FILTER", "1. Nifty Market Filter", "Nifty 50 Close > 50 DMA & EMA20 > EMA50",
                niftyPassed ? "Passed (Market Uptrend)" : "Failed (Defensive Mode)", "Close > SMA50 & EMA20 > EMA50", niftyPassed),

            new("EMA_TREND", "2. EMA Trend Alignment", "Close > EMA20 > EMA50 with rising slopes & stable EMA200",
                emaTrendPassed ? $"Passed (Close ₹{price:F1} > EMA20 ₹{ema20:F1} > EMA50 ₹{ema50:F1})" : $"Close ₹{price:F1}, EMA20 ₹{ema20:F1}, EMA50 ₹{ema50:F1}",
                "Close > EMA20 > EMA50", emaTrendPassed),

            new("PREV_HIGH_BREAKOUT", "3. Previous High Breakout", "Current Close > Previous Day High or Swing High",
                prevHighPassed ? "Passed (Breakout Confirmed)" : "Failed (Inside Range)", "Close > Prev High / Swing High", prevHighPassed),

            new("CONSOLIDATION_BREAKOUT", "4. Consolidation Breakout", "Breakout after 10-20 trading days narrow price range",
                consBreakoutPassed ? "Passed (10-20 Day Range Breakout)" : "No Consolidation Breakout", "Breakout > 10-20D Range", consBreakoutPassed),

            new("VOL_CONFIRMATION", "5. Volume Confirmation", "Volume >= 1.5x 20-day Avg & > Prev Day Volume",
                volSpikePassed ? $"Passed ({volMult:F1}x Avg Volume)" : $"{volMult:F1}x Avg Volume", ">= 1.5x Avg Volume", volSpikePassed),

            new("ADX_STRENGTH", "6. ADX Trend Strength", "ADX (14) >= 20 (Strong: >=25, Good: 20-24)",
                $"{adxVal:F1} ({GetAdxLabel(adxVal)})", "ADX >= 20.0", adxVal >= 20m),

            new("RSI_MOMENTUM", "7. RSI Momentum Zone", "RSI (14) between 50 and 75 (Best: 55-70)",
                $"{rsiVal:F1}", "50.0 - 70.0 Zone", rsiVal >= 50m && rsiVal <= 75m),

            new("MACD_BULLISH", "8. MACD Bullish Signal", "MACD Line above Signal Line",
                macdPassed ? "Passed (MACD > Signal)" : "Failed", "MACD > Signal Line", macdPassed),

            new("NEAR_52W", "9. 52-Week High Proximity", "Close Price within 10% of 52-Week High or New High",
                high52wPassed ? "Passed (Within 10% / New High)" : "Below 10%", "Close >= 90% 52W High", high52wPassed),

            new("BULLISH_CANDLE", "10. Bullish Candle Pattern", "Bullish Engulfing, Marubozu, Breakout candle, or Close near High",
                candlePassed ? "Passed (Strong Bullish Pattern)" : "Failed (Weak Candle)", "Strong Bullish Confirmation", candlePassed),

            new("RELATIVE_STRENGTH", "11. Relative Strength vs Nifty", "Outperforming Nifty 50 Index over 1M or 3M",
                rsPassed ? "Passed (Outperforming Nifty)" : "Failed", "1M/3M Stock Ret > Nifty Ret", rsPassed),

            new("MULTITIMEFRAME", "12. Multi-Timeframe Confirmation", "1-Hour (60m) Close > EMA20 & RSI >= 40",
                mtfPassed ? "Passed (Daily & 60m Bullish)" : "Failed (60m Bearish)", "60m Close > EMA20", mtfPassed),

            new("RISK_REWARD", "13. Risk/Reward Ratio", "Suggested Target 1 vs Stop Loss Ratio >= 1:2.0",
                rrPassed ? $"Passed (1:{rr:F1} R:R)" : $"Failed (1:{rr:F1} R:R)", "Risk Reward Ratio >= 1:2", rrPassed)
        };

        int metCount = conditions.Count(c => c.IsMet);
        return new ConditionChecklistDto(metCount, conditions.Count, conditions);
    }

    private static string GetAdxLabel(decimal adx) => adx switch
    {
        >= 25m => "Strong Trend",
        >= 20m => "Good Trend",
        >= 18m => "Moderate Trend",
        _ => "Weak Trend"
    };

    private static ConditionChecklistDto BuildEmptyChecklist(string reason)
    {
        var conditions = new List<ConditionItemDto>
        {
            new("DATA_CHECK", "Data Integrity", reason, "Failed", "Sufficient Candles", false)
        };
        return new ConditionChecklistDto(0, 1, conditions);
    }

    private static string GetSectorForSymbol(string symbol) => symbol.ToUpperInvariant() switch
    {
        "INFY" or "TCS" => "IT",
        "HDFCBANK" or "ICICIBANK" or "AXISBANK" or "SBIN" => "Banking & Financials",
        "RELIANCE" => "Energy & Oil",
        "TATAMOTORS" => "Auto",
        "LT" => "Capital Goods & Infra",
        "ITC" => "FMCG",
        "NIFTYBEES" or "NIFTY 50" => "Index",
        _ => "General Equities"
    };
}
