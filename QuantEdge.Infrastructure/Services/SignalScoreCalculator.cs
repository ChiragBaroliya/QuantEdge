using System;

namespace QuantEdge.Infrastructure.Services;

/// <summary>
/// Service responsible for calculating weighted scores for buy and sell trading signals
/// based on technical indicator states.
/// </summary>
public class SignalScoreCalculator
{
    // Indicator Weight Constants
    private const int WeightEmaTrend = 20;
    private const int WeightRsi = 15;
    private const int WeightMacd = 20;
    private const int WeightVwap = 15;
    private const int WeightVolume = 30;

    /// <summary>
    /// Calculates individual Buy and Sell scores (out of 100) based on input metrics.
    /// </summary>
    public (int BuyScore, int SellScore) CalculateScores(
        decimal latestPrice,
        decimal latestOpen,
        long latestVolume,
        decimal ema20,
        decimal ema50,
        decimal rsi,
        decimal vwap,
        decimal latestHist,
        double avgVolume20,
        decimal prevPrice,
        decimal prevOpen,
        decimal prevEma20,
        decimal prevEma50,
        decimal prevRsi,
        decimal prevVwap,
        decimal prevHist)
    {
        int buyScore = 0;
        int sellScore = 0;

        // 1. EMA Trend (Weight 20)
        // Buy: EMA20 > EMA50 + widening gap
        decimal latestGap = ema20 - ema50;
        decimal prevGap = prevEma20 - prevEma50;
        bool isBuyEma = (ema20 > ema50) && (latestGap > prevGap);

        // Sell: EMA20 < EMA50 OR sudden slope reversal (EMA20 slope turns negative)
        bool isSellEma = (ema20 < ema50) || (ema20 < prevEma20);

        if (isBuyEma)
        {
            buyScore += WeightEmaTrend;
        }
        if (isSellEma)
        {
            sellScore += WeightEmaTrend;
        }

        // 2. RSI (Weight 15)
        // Buy: 50–65 (rising)
        bool isBuyRsi = (rsi >= 50 && rsi <= 65) && (rsi > prevRsi);

        // Sell: >70 (overbought reversal) OR <45 (momentum failing)
        bool isSellRsi = (rsi > 70) || (rsi < 45);

        if (isBuyRsi)
        {
            buyScore += WeightRsi;
        }
        if (isSellRsi)
        {
            sellScore += WeightRsi;
        }

        // 3. MACD Cross (Weight 20)
        // Buy: Histogram turning positive + crossover
        bool isBuyMacd = (latestHist > 0) && (prevHist <= 0);

        // Sell: Histogram declining from peak (divergence)
        bool isSellMacd = (latestHist < prevHist);

        if (isBuyMacd)
        {
            buyScore += WeightMacd;
        }
        if (isSellMacd)
        {
            sellScore += WeightMacd;
        }

        // 4. VWAP (Weight 15)
        // Buy: Price > VWAP + holding above it
        bool isBuyVwap = (latestPrice > vwap) && (prevPrice > prevVwap);

        // Sell: Price rejects VWAP from above (breaks below vwap)
        bool isSellVwap = (latestPrice < vwap);

        if (isBuyVwap)
        {
            buyScore += WeightVwap;
        }
        if (isSellVwap)
        {
            sellScore += WeightVwap;
        }

        // 5. Volume Spike (Weight 30)
        // Buy: >1.5x avg on green candles
        bool isBuyVolume = (avgVolume20 > 0) && ((double)latestVolume > avgVolume20 * 1.5) && (latestPrice > latestOpen);

        // Sell: >2x avg on red candles
        bool isSellVolume = (avgVolume20 > 0) && ((double)latestVolume > avgVolume20 * 2.0) && (latestPrice < latestOpen);

        if (isBuyVolume)
        {
            buyScore += WeightVolume;
        }
        if (isSellVolume)
        {
            sellScore += WeightVolume;
        }

        return (buyScore, sellScore);
    }

    /// <summary>
    /// Evaluates scoring thresholds and returns the strength category.
    /// </summary>
    public string DetermineStrength(int score)
    {
        if (score >= 90)
        {
            return "Very Strong";
        }
        if (score >= 70)
        {
            return "Strong";
        }
        if (score >= 50)
        {
            return "Weak";
        }
        return "None";
    }
}
