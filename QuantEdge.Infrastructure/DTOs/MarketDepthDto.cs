using System;
using System.Collections.Generic;
using QuantEdge.Infrastructure.Models;

namespace QuantEdge.Infrastructure.DTOs;

/// <summary>
/// Immutable Data Transfer Object representing the current bid-ask order book structure.
/// </summary>
public record MarketDepthDto(
    string Symbol,
    DateTime Timestamp,
    IReadOnlyList<DepthLevel> Bids,
    IReadOnlyList<DepthLevel> Asks
);
