using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using QuantEdge.Domain.Entities;
using QuantEdge.Infrastructure.DTOs;
using QuantEdge.Infrastructure.Interfaces;
using QuantEdge.Infrastructure.Persistence.Repositories;

namespace QuantEdge.Infrastructure.Services;

/// <summary>
/// Core implementation of ISignalEngineService. Coordinates technical indicator loading, 
/// scoring, strength categorization, and database persistence.
/// </summary>
public class SignalEngineService : ISignalEngineService
{
    private readonly IMarketCandleRepository _candleRepository;
    private readonly IMarketIndicatorRepository _indicatorRepository;
    private readonly ITradingSignalRepository _tradingSignalRepository;
    private readonly SignalScoreCalculator _scoreCalculator;
    private readonly ILogger<SignalEngineService> _logger;

    public SignalEngineService(
        IMarketCandleRepository candleRepository,
        IMarketIndicatorRepository indicatorRepository,
        ITradingSignalRepository tradingSignalRepository,
        SignalScoreCalculator scoreCalculator,
        ILogger<SignalEngineService> logger)
    {
        _candleRepository = candleRepository ?? throw new ArgumentNullException(nameof(candleRepository));
        _indicatorRepository = indicatorRepository ?? throw new ArgumentNullException(nameof(indicatorRepository));
        _tradingSignalRepository = tradingSignalRepository ?? throw new ArgumentNullException(nameof(tradingSignalRepository));
        _scoreCalculator = scoreCalculator ?? throw new ArgumentNullException(nameof(scoreCalculator));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<SignalEvaluationResult> EvaluateSignalAsync(string symbol, string timeframe, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Initiating signal evaluation engine for {Symbol} ({Timeframe})...", symbol, timeframe);

        // 1. Fetch latest candles to calculate volume spikes and get current price
        var candlesList = (await _candleRepository.GetHistoryAsync(symbol, timeframe, limit: 21)).ToList();
        if (candlesList.Count < 2)
        {
            _logger.LogWarning("Insufficient candle data to evaluate symbol {Symbol}. Minimum required: 2 candles.", symbol);
            return CreateHoldResult(symbol, timeframe, "Insufficient candle data (minimum 2 candles required).");
        }

        var latestCandle = candlesList[0];
        var prevCandle = candlesList[1];
        decimal latestPrice = latestCandle.Close;
        decimal latestOpen = latestCandle.Open;
        long latestVolume = latestCandle.Volume;
        decimal prevPrice = prevCandle.Close;
        decimal prevOpen = prevCandle.Open;

        // Calculate Average Volume of the previous 20 candles (if available, otherwise all available previous)
        var previousCandles = candlesList.Skip(1).Take(20).ToList();
        double avgVolume20 = previousCandles.Any() ? previousCandles.Average(c => (double)c.Volume) : 0;

        // 2. Fetch latest indicators to calculate crossovers and load current values
        var indicatorsList = (await _indicatorRepository.GetHistoryAsync(symbol, timeframe, limit: 2)).ToList();
        if (indicatorsList.Count < 2)
        {
            _logger.LogWarning("Insufficient indicator data to evaluate symbol {Symbol}. Minimum required: 2 indicator records.", symbol);
            return CreateHoldResult(symbol, timeframe, "Insufficient technical indicator history in database.");
        }

        var latestInd = indicatorsList[0];
        var prevInd = indicatorsList[1];

        decimal rsi = latestInd.RSI;
        decimal prevRsi = prevInd.RSI;
        decimal vwap = latestInd.VWAP;
        decimal prevVwap = prevInd.VWAP;
        decimal ema20 = latestInd.EMA20;
        decimal prevEma20 = prevInd.EMA20;
        decimal ema50 = latestInd.EMA50;
        decimal prevEma50 = prevInd.EMA50;

        decimal latestHist = latestInd.MACD - latestInd.SignalLine;
        decimal prevHist = prevInd.MACD - prevInd.SignalLine;

        // Calculate MACD Crossover (for backward compatibility / record mapping)
        bool isMacdCross = false;

        // Bullish Cross: Previous MACD <= Previous Signal AND Latest MACD > Latest Signal, confirmed by rising histogram
        if (prevInd.MACD <= prevInd.SignalLine && latestInd.MACD > latestInd.SignalLine && latestHist > prevHist)
        {
            isMacdCross = true;
        }
        // Bearish Cross: Previous MACD >= Previous Signal AND Latest MACD < Latest Signal, confirmed by falling histogram
        else if (prevInd.MACD >= prevInd.SignalLine && latestInd.MACD < latestInd.SignalLine && latestHist < prevHist)
        {
            isMacdCross = true;
        }

        // 3. Compute weighted Buy and Sell scores
        var (buyScore, sellScore) = _scoreCalculator.CalculateScores(
            latestPrice, latestOpen, latestVolume, ema20, ema50, rsi, vwap, latestHist, avgVolume20,
            prevPrice, prevOpen, prevEma20, prevEma50, prevRsi, prevVwap, prevHist
        );

        // 4. Select the dominant signal
        string signalType = "HOLD";
        int finalScore = Math.Max(buyScore, sellScore);

        if (buyScore >= 50 && buyScore >= sellScore)
        {
            signalType = "BUY";
            finalScore = buyScore;
        }
        else if (sellScore >= 50 && sellScore > buyScore)
        {
            signalType = "SELL";
            finalScore = sellScore;
        }

        string strength = signalType == "HOLD" ? "None" : _scoreCalculator.DetermineStrength(finalScore);
        string reason = BuildReasonString(
            signalType, finalScore, strength, buyScore, sellScore, latestPrice, prevPrice, latestOpen, latestVolume, avgVolume20,
            vwap, prevVwap, rsi, prevRsi, ema20, prevEma20, ema50, prevEma50, latestHist, prevHist
        );

        // 5. Persist signal output to database if a valid BUY/SELL is triggered
        if (signalType != "HOLD")
        {
            var deterministicId = GenerateDeterministicIntId(symbol, signalType, latestCandle.CandleTime);

            var tradingSignal = new TradingSignal
            {
                Id = deterministicId,
                Symbol = symbol.ToUpper(),
                SignalType = signalType,
                SignalStrength = (decimal)finalScore,
                EntryPrice = latestPrice,
                Reason = reason,
                CandleTime = latestCandle.CandleTime.ToUniversalTime(),
                CreatedAt = DateTime.UtcNow
            };

            try
            {
                await _tradingSignalRepository.InsertAsync(tradingSignal);
                _logger.LogInformation("Successfully saved dynamic {Type} signal to PostgreSQL for {Symbol}.", signalType, symbol);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to persist trading signal output for {Symbol}.", symbol);
            }
        }

        return new SignalEvaluationResult(
            Symbol: symbol.ToUpper(),
            Timeframe: timeframe,
            SignalType: signalType,
            Score: finalScore,
            Strength: strength,
            Reason: reason,
            LatestPrice: latestPrice,
            VWAP: vwap,
            RSI: rsi,
            EMA20: ema20,
            EMA50: ema50,
            MACD: latestInd.MACD,
            MACDSignal: latestInd.SignalLine,
            MACDCross: isMacdCross,
            LatestVolume: latestVolume,
            AvgVolume20: avgVolume20,
            VolumeSpike: (avgVolume20 > 0 && (double)latestVolume > (avgVolume20 * 1.5)),
            EvaluatedAt: DateTime.UtcNow
        );
    }

    private static string BuildReasonString(
        string signalType, 
        int score, 
        string strength,
        int buyScore,
        int sellScore,
        decimal price, 
        decimal prevPrice,
        decimal open,
        long volume,
        double avgVolume,
        decimal vwap, 
        decimal prevVwap,
        decimal rsi, 
        decimal prevRsi,
        decimal ema20, 
        decimal prevEma20,
        decimal ema50, 
        decimal prevEma50,
        decimal latestHist,
        decimal prevHist)
    {
        var elements = new List<string>();
        decimal latestGap = ema20 - ema50;
        decimal prevGap = prevEma20 - prevEma50;

        if (signalType == "HOLD")
        {
            var dominantSide = buyScore >= sellScore ? "BUY" : "SELL";
            var maxScore = Math.Max(buyScore, sellScore);
            
            if (dominantSide == "BUY")
            {
                if ((ema20 > ema50) && (latestGap > prevGap)) 
                    elements.Add("EMA Trend widening");
                if (price > vwap && prevPrice > prevVwap) 
                    elements.Add("Price above VWAP");
                if (rsi >= 50 && rsi <= 65 && rsi > prevRsi) 
                    elements.Add("Rising RSI in momentum zone");
                if (latestHist > 0 && prevHist <= 0) 
                    elements.Add("MACD Crossover");
                if (avgVolume > 0 && (double)volume > avgVolume * 1.5 && price > open) 
                    elements.Add("Bullish Volume Spike");
            }
            else
            {
                if (ema20 < ema50) 
                    elements.Add("EMA Bearish Trend");
                else if (ema20 < prevEma20) 
                    elements.Add("EMA20 Slope Reversal");
                    
                if (price < vwap) 
                    elements.Add("Price below VWAP");
                    
                if (rsi > 70) 
                    elements.Add("RSI Overbought");
                else if (rsi < 45) 
                    elements.Add("RSI Momentum Failure");
                    
                if (latestHist < prevHist) 
                    elements.Add("MACD declining");
                    
                if (avgVolume > 0 && (double)volume > avgVolume * 2.0 && price < open) 
                    elements.Add("Bearish Volume Spike");
            }
            
            string factorsText = elements.Any() ? $" Factors: {string.Join(", ", elements)}." : " No indicator weights met.";
            return $"HOLD. Dominant {dominantSide} score {maxScore}/100.{factorsText} Need at least 50 score to trigger signal.";
        }

        if (signalType == "BUY")
        {
            if ((ema20 > ema50) && (latestGap > prevGap)) 
                elements.Add("EMA Trend Accelerating (EMA20 > EMA50 with widening gap)");
            if (price > vwap && prevPrice > prevVwap) 
                elements.Add("Price holding above VWAP");
            if (rsi >= 50 && rsi <= 65 && rsi > prevRsi) 
                elements.Add($"Rising RSI in momentum zone ({rsi:F1})");
            if (latestHist > 0 && prevHist <= 0) 
                elements.Add("MACD Bullish Crossover (histogram turned positive)");
            if (avgVolume > 0 && (double)volume > avgVolume * 1.5 && price > open) 
                elements.Add("Bullish Volume Spike (>1.5x avg on green candle)");
        }
        else if (signalType == "SELL")
        {
            if (ema20 < ema50) 
                elements.Add("EMA Bearish Trend (EMA20 < EMA50)");
            else if (ema20 < prevEma20) 
                elements.Add("EMA20 Slope Reversal (momentum turning down)");
                
            if (price < vwap) 
                elements.Add("Price rejected below VWAP");
                
            if (rsi > 70) 
                elements.Add($"RSI Overbought Reversal ({rsi:F1} > 70)");
            else if (rsi < 45) 
                elements.Add($"RSI Momentum Failure ({rsi:F1} < 45)");
                
            if (latestHist < prevHist) 
                elements.Add("MACD Histogram declining from peak");
                
            if (avgVolume > 0 && (double)volume > avgVolume * 2.0 && price < open) 
                elements.Add("Bearish Volume Spike (>2x avg on red candle)");
        }

        return $"{strength} {signalType} signal generated with score {score}/100. Factors: {string.Join(", ", elements)}.";
    }

    private static SignalEvaluationResult CreateHoldResult(string symbol, string timeframe, string reason)
    {
        return new SignalEvaluationResult(
            Symbol: symbol.ToUpper(),
            Timeframe: timeframe,
            SignalType: "HOLD",
            Score: 0,
            Strength: "None",
            Reason: reason,
            LatestPrice: 0,
            VWAP: 0,
            RSI: 0,
            EMA20: 0,
            EMA50: 0,
            MACD: 0,
            MACDSignal: 0,
            MACDCross: false,
            LatestVolume: 0,
            AvgVolume20: 0,
            VolumeSpike: false,
            EvaluatedAt: DateTime.UtcNow
        );
    }

    private static int GenerateDeterministicIntId(string symbol, string signalType, DateTime candleTime)
    {
        string input = $"{symbol}_{signalType}_{candleTime:yyyyMMddHHmmss}";
        uint hash = 2166136261;
        foreach (char c in input)
        {
            hash = (hash ^ c) * 16777619;
        }
        return (int)hash;
    }
}
