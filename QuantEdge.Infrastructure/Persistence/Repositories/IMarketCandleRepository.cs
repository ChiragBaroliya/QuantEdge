using System.Collections.Generic;
using System.Threading.Tasks;
using QuantEdge.Domain.Entities;

namespace QuantEdge.Infrastructure.Persistence.Repositories;

/// <summary>
/// Repository interface for saving and querying market candle bars.
/// </summary>
public interface IMarketCandleRepository
{
    /// <summary>
    /// Inserts a new market candle using a Stored Procedure.
    /// </summary>
    Task InsertAsync(MarketCandle candle);

    /// <summary>
    /// Efficiently batch inserts multiple market candles using a single database connection.
    /// </summary>
    Task InsertBatchAsync(IEnumerable<MarketCandle> candles);

    /// <summary>
    /// Retrieves historical candles using a Stored Procedure or direct SQL query, returning auto-mapped entities.
    /// </summary>
    Task<IEnumerable<MarketCandle>> GetHistoryAsync(string symbol, string timeframe, int? limit = null, System.DateTime? beforeTime = null);

    /// <summary>
    /// Deletes all history for today for a specific symbol and timeframe.
    /// </summary>
    Task DeleteTodayHistoryAsync(string symbol, string timeframe);

    /// <summary>
    /// Deletes history within a specific date range for a symbol (or all symbols if symbol is null/empty).
    /// </summary>
    Task DeleteHistoryRangeAsync(string? symbol, string timeframe, System.DateTime fromDate, System.DateTime toDate);

    /// <summary>
    /// Purges candles and indicators matching created_at date across all timeframes and updates stock_master flags.
    /// </summary>
    Task<(long deletedCandles, long deletedIndicators, int affectedStocks)> PurgeHistoryByDateAsync(System.DateTime targetDate, string? symbol);
}
