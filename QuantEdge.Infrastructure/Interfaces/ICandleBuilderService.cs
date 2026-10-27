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
    /// Closes (and raises <see cref="OnCandleClosed"/> for) every active candle whose period has ended by
    /// <paramref name="nowUtc"/>. A period never runs past the 15:30 IST session close, so the day's last
    /// intraday bars and the daily candle close at the session end instead of waiting for the next
    /// trading day's first tick (which, for a Friday or a pre-holiday day, may never reach this process).
    /// </summary>
    Task CloseCompletedCandlesAsync(DateTime nowUtc);

    /// <summary>
    /// Retrieves the in-memory history of closed candles for a given asset and interval.
    /// </summary>
    IReadOnlyList<CandleDto> GetHistory(string symbol, TimeSpan interval);
}
