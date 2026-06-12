using System;

namespace QuantEdge.Infrastructure.DTOs;

/// <summary>
/// Immutable DTO record containing the detailed evaluation outputs of the BUY/SELL Signal Engine.
/// </summary>
public record SignalEvaluationResult(
    string Symbol,
    string Timeframe,
    string SignalType, // BUY, SELL, HOLD
    int Score,
    string Strength, // Weak, Strong, Very Strong, None
    string Reason,
    decimal LatestPrice,
    decimal VWAP,
    decimal RSI,
    decimal EMA20,
    decimal EMA50,
    decimal MACD,
    decimal MACDSignal,
    bool MACDCross,
    long LatestVolume,
    double AvgVolume20,
    bool VolumeSpike,
    DateTime EvaluatedAt
);
