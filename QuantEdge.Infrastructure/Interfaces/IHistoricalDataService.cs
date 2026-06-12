using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using QuantEdge.Domain.Entities;

namespace QuantEdge.Infrastructure.Interfaces;

/// <summary>
/// Service interface for fetching and syncing historical candlestick data from market data brokers.
/// </summary>
public interface IHistoricalDataService
{
    /// <summary>
    /// Fetches historical candles from the broker and saves them to the database.
    /// </summary>
    Task<IEnumerable<MarketCandle>> FetchHistoricalCandlesAsync(
        string symbol, 
        string timeframe, 
        DateTime fromTime, 
        DateTime toTime, 
        CancellationToken cancellationToken);

    /// <summary>
    /// Detects missing data gaps in the database and fetches/backfills them from the broker.
    /// </summary>
    Task SyncGapsAsync(string symbol, string timeframe, CancellationToken cancellationToken);
}
