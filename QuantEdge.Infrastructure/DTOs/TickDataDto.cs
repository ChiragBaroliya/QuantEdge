using System;

namespace QuantEdge.Infrastructure.DTOs;

/// <summary>
/// Immutable Data Transfer Object representing a single price tick or trade event.
/// </summary>
public record TickDataDto(
    string Symbol,
    decimal LTP,
    long Volume,
    DateTime Timestamp
);
