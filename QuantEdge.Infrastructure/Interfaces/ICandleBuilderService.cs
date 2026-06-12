using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using QuantEdge.Infrastructure.DTOs;

namespace QuantEdge.Infrastructure.Interfaces;

/// <summary>
/// Service responsible for aggregating raw real-time ticks into OHLCV candlesticks across multiple timeframes.
/// </summary>
public interface ICandleBuilderService
{
    /// <summary>
    /// Event triggered when a candlestick is completed and closed.
    /// </summary>
    event Func<CandleDto, Task>? OnCandleClosed;

    /// <summary>
    /// Event triggered when an active candlestick receives a tick update.
    /// </summary>
    event Func<CandleDto, Task>? OnCandleUpdated;

    /// <summary>
    /// Ingests a new market tick and aggregates it into current active candles.
    /// </summary>
    Task ProcessTickAsync(TickDataDto tick);

    /// <summary>
    /// Retrieves the in-memory history of closed candles for a given asset and interval.
    /// </summary>
    IReadOnlyList<CandleDto> GetHistory(string symbol, TimeSpan interval);
}
