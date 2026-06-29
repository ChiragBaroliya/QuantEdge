using System;
using System.Collections.Generic;
using System.Linq;

namespace QuantEdge.Infrastructure.Services;

/// <summary>
/// Technical analysis helper class providing pure mathematical indicator calculation formulas.
/// </summary>
public static class IndicatorCalculator
{
    /// <summary>
    /// Computes Exponential Moving Average (EMA) for a list of values.
    /// </summary>
    public static List<decimal> CalculateEma(List<decimal> prices, int period)
    {
        var ema = new List<decimal>();
        if (prices.Count == 0) return ema;

        if (prices.Count < period)
        {
            // Fallback: partial simple moving averages if we don't have enough periods
            decimal accum = 0;
            for (int i = 0; i < prices.Count; i++)
            {
                accum += prices[i];
                ema.Add(accum / (i + 1));
            }
            return ema;
        }

        decimal multiplier = 2.0m / (period + 1);

        // Seed: Simple Moving Average for the first 'period' elements
        decimal sum = 0;
        for (int i = 0; i < period; i++)
        {
            sum += prices[i];
            ema.Add(sum / (i + 1));
        }

        // Overwrite the seeding point with the clean SMA
        ema[period - 1] = sum / period;

        // Apply EMA formula sequentially
        for (int i = period; i < prices.Count; i++)
        {
            decimal value = (prices[i] * multiplier) + (ema[i - 1] * (1 - multiplier));
            ema.Add(value);
        }

        return ema;
    }

    /// <summary>
    /// Computes Relative Strength Index (RSI) using Wilder's Smoothing Technique.
    /// </summary>
    public static List<decimal> CalculateRsi(List<decimal> prices, int period = 14)
    {
        var rsi = new List<decimal>();
        if (prices.Count == 0) return rsi;

        // Default neutral RSI to 50 for initial indexes
        for (int i = 0; i < prices.Count; i++)
        {
            rsi.Add(50m);
        }

        if (prices.Count < period + 1)
        {
            return rsi;
        }

        decimal avgGain = 0;
        decimal avgLoss = 0;

        // First period Gain/Loss calculation
        for (int i = 1; i <= period; i++)
        {
            decimal change = prices[i] - prices[i - 1];
            if (change > 0)
            {
                avgGain += change;
            }
            else
            {
                avgLoss += -change;
            }
        }

        avgGain /= period;
        avgLoss /= period;

        rsi[period] = avgLoss == 0 ? 100m : 100m - (100m / (1m + (avgGain / avgLoss)));

        // Subsequent smoothing loop
        for (int i = period + 1; i < prices.Count; i++)
        {
            decimal change = prices[i] - prices[i - 1];
            decimal gain = change > 0 ? change : 0m;
            decimal loss = change < 0 ? -change : 0m;

            avgGain = ((avgGain * (period - 1)) + gain) / period;
            avgLoss = ((avgLoss * (period - 1)) + loss) / period;

            rsi[i] = avgLoss == 0 ? 100m : 100m - (100m / (1m + (avgGain / avgLoss)));
        }

        return rsi;
    }

    /// <summary>
    /// Computes MACD and MACD Signal Line.
    /// </summary>
    public static (List<decimal> Macd, List<decimal> Signal) CalculateMacd(List<decimal> prices)
    {
        var macd = new List<decimal>();
        var signal = new List<decimal>();

        if (prices.Count == 0) return (macd, signal);

        var ema12 = CalculateEma(prices, 12);
        var ema26 = CalculateEma(prices, 26);

        for (int i = 0; i < prices.Count; i++)
        {
            macd.Add(ema12[i] - ema26[i]);
        }

        signal = CalculateEma(macd, 9);

        return (macd, signal);
    }

    /// <summary>
    /// Computes Simple Moving Average (SMA) for a list of values.
    /// </summary>
    public static List<decimal> CalculateSma(List<decimal> values, int period)
    {
        var sma = new List<decimal>();
        if (values.Count == 0) return sma;

        decimal sum = 0;
        for (int i = 0; i < values.Count; i++)
        {
            sum += values[i];
            if (i >= period)
            {
                sum -= values[i - period];
                sma.Add(sum / period);
            }
            else
            {
                sma.Add(sum / (i + 1));
            }
        }
        return sma;
    }

    /// <summary>
    /// Computes rolling maximum value (52-week High / 250 trading days).
    /// </summary>
    public static List<decimal> Calculate52WeekHigh(List<decimal> highs, int period = 250)
    {
        var highs52W = new List<decimal>();
        if (highs.Count == 0) return highs52W;

        for (int i = 0; i < highs.Count; i++)
        {
            int start = Math.Max(0, i - period + 1);
            decimal max = highs[start];
            for (int j = start + 1; j <= i; j++)
            {
                if (highs[j] > max) max = highs[j];
            }
            highs52W.Add(max);
        }
        return highs52W;
    }

    /// <summary>
    /// Computes Average True Range (ATR) using Wilder's Smoothing.
    /// </summary>
    public static List<decimal> CalculateAtr(List<decimal> highs, List<decimal> lows, List<decimal> closes, int period = 14)
    {
        var atrList = new List<decimal>();
        int count = highs.Count;
        if (count == 0) return atrList;

        for (int i = 0; i < count; i++) atrList.Add(0m);

        if (count < period) return atrList;

        var tr = new decimal[count];
        tr[0] = highs[0] - lows[0];
        for (int i = 1; i < count; i++)
        {
            decimal tr1 = highs[i] - lows[i];
            decimal tr2 = Math.Abs(highs[i] - closes[i - 1]);
            decimal tr3 = Math.Abs(lows[i] - closes[i - 1]);
            tr[i] = Math.Max(tr1, Math.Max(tr2, tr3));
        }

        // Seed with SMA of TR
        decimal sumTR = 0;
        for (int i = 0; i < period; i++)
        {
            sumTR += tr[i];
            atrList[i] = sumTR / (i + 1);
        }

        atrList[period - 1] = sumTR / period;

        for (int i = period; i < count; i++)
        {
            atrList[i] = ((atrList[i - 1] * (period - 1)) + tr[i]) / period;
        }

        return atrList;
    }

    /// <summary>
    /// Computes Average Directional Index (ADX) using Wilder's Smoothing.
    /// </summary>
    public static List<decimal> CalculateAdx(List<decimal> highs, List<decimal> lows, List<decimal> closes, int period = 14)
    {
        var adxList = new List<decimal>();
        int count = highs.Count;
        if (count == 0) return adxList;

        for (int i = 0; i < count; i++) adxList.Add(0m);

        if (count < period * 2) return adxList;

        var tr = new decimal[count];
        var plusDM = new decimal[count];
        var minusDM = new decimal[count];

        for (int i = 1; i < count; i++)
        {
            decimal upMove = highs[i] - highs[i - 1];
            decimal downMove = lows[i - 1] - lows[i];

            plusDM[i] = (upMove > downMove && upMove > 0) ? upMove : 0m;
            minusDM[i] = (downMove > upMove && downMove > 0) ? downMove : 0m;

            decimal tr1 = highs[i] - lows[i];
            decimal tr2 = Math.Abs(highs[i] - closes[i - 1]);
            decimal tr3 = Math.Abs(lows[i] - closes[i - 1]);
            tr[i] = Math.Max(tr1, Math.Max(tr2, tr3));
        }
        tr[0] = highs[0] - lows[0];

        var smoothedTR = new decimal[count];
        var smoothedPlusDM = new decimal[count];
        var smoothedMinusDM = new decimal[count];

        decimal sumTR = 0, sumPlusDM = 0, sumMinusDM = 0;
        for (int i = 0; i < period; i++)
        {
            sumTR += tr[i];
            sumPlusDM += plusDM[i];
            sumMinusDM += minusDM[i];
            smoothedTR[i] = sumTR;
            smoothedPlusDM[i] = sumPlusDM;
            smoothedMinusDM[i] = sumMinusDM;
        }

        smoothedTR[period - 1] = sumTR;
        smoothedPlusDM[period - 1] = sumPlusDM;
        smoothedMinusDM[period - 1] = sumMinusDM;

        for (int i = period; i < count; i++)
        {
            smoothedTR[i] = smoothedTR[i - 1] - (smoothedTR[i - 1] / period) + tr[i];
            smoothedPlusDM[i] = smoothedPlusDM[i - 1] - (smoothedPlusDM[i - 1] / period) + plusDM[i];
            smoothedMinusDM[i] = smoothedMinusDM[i - 1] - (smoothedMinusDM[i - 1] / period) + minusDM[i];
        }

        var dx = new decimal[count];
        for (int i = 0; i < count; i++)
        {
            decimal trVal = smoothedTR[i];
            if (trVal == 0m)
            {
                dx[i] = 0m;
                continue;
            }

            decimal plusDI = 100m * smoothedPlusDM[i] / trVal;
            decimal minusDI = 100m * smoothedMinusDM[i] / trVal;
            decimal sumDI = plusDI + minusDI;
            dx[i] = sumDI == 0m ? 0m : 100m * Math.Abs(plusDI - minusDI) / sumDI;
        }

        decimal sumDX = 0;
        for (int i = 0; i < period * 2 - 1; i++)
        {
            if (i >= period - 1) sumDX += dx[i];
            adxList[i] = 15m;
        }

        decimal initialAdx = sumDX / period;
        adxList[period * 2 - 2] = initialAdx;

        for (int i = period * 2 - 1; i < count; i++)
        {
            adxList[i] = ((adxList[i - 1] * (period - 1)) + dx[i]) / period;
        }

        return adxList;
    }
}
