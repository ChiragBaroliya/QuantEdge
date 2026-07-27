using System.Collections.Generic;
using System.Threading.Tasks;
using QuantEdge.Domain.Entities;

namespace QuantEdge.Infrastructure.Interfaces;

/// <summary>
/// High-performance in-memory caching service for market candles and indicators per symbol & timeframe.
/// Supports Indian Market trading hours limits (e.g. 400 candles for 1m, 80 candles for 5m).
/// </summary>
public interface IMarketDataCacheService
{
    /// <summary>
    /// Retrieves recent candles from RAM cache. If missing, fetches from database repository once to warm up the cache.
    /// </summary>
    Task<List<MarketCandle>> GetRecentCandlesAsync(string symbol, string timeframe, int limit = 200);

    /// <summary>
    /// Adds or updates a single candle in the in-memory cache for its symbol and timeframe.
    /// </summary>
    void AddOrUpdateCandle(MarketCandle candle);

    /// <summary>
    /// Bulk updates the in-memory cache for multiple candles.
    /// </summary>
    void AddOrUpdateCandleBatch(IEnumerable<MarketCandle> candles);

    /// <summary>
    /// Retrieves recent calculated indicators from RAM cache. If missing, fetches from database.
    /// </summary>
    Task<List<MarketIndicator>> GetRecentIndicatorsAsync(string symbol, string timeframe, int limit = 1);

    /// <summary>
    /// Retrieves the latest calculated indicator from RAM cache for a symbol and timeframe.
    /// </summary>
    Task<MarketIndicator?> GetLatestIndicatorAsync(string symbol, string timeframe);

    /// <summary>
    /// Adds or updates a single MarketIndicator in the in-memory cache.
    /// </summary>
    void AddOrUpdateIndicator(MarketIndicator indicator);

    /// <summary>
    /// Clears the in-memory cache for a specific symbol or all symbols.
    /// </summary>
    void ClearCache(string? symbol = null, string? timeframe = null);
}
