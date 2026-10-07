using System;

namespace QuantEdge.Infrastructure.DTOs;

/// <summary>
/// Immutable Data Transfer Object representing a single price tick or trade event.
/// PrevClose / DayOpen / DayHigh / DayLow come from the Kite quote packet's OHLC block (ohlc.close is the
/// previous session's close); 0 when the feed does not supply them.
/// </summary>
public record TickDataDto(
    string Symbol,
    decimal LTP,
    long Volume,
    DateTime Timestamp,
    decimal PrevClose = 0m,
    decimal DayOpen = 0m,
    decimal DayHigh = 0m,
    decimal DayLow = 0m
);
