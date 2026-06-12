using System;

namespace QuantEdge.Infrastructure.DTOs;

/// <summary>
/// Immutable Data Transfer Object representing an OHLCV candlestick for a specific interval.
/// </summary>
public record CandleDto(
    string Symbol,
    TimeSpan Interval,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    long Volume,
    DateTime Timestamp,
    bool IsClosed
);
