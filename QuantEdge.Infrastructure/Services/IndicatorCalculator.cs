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
}
