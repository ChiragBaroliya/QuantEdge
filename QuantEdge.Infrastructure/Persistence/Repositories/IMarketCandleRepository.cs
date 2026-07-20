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
    /// Retrieves historical candles using a Stored Procedure, returning auto-mapped entities.
    /// </summary>
    Task<IEnumerable<MarketCandle>> GetHistoryAsync(string symbol, string timeframe, int limit);

    /// <summary>
    /// Deletes all history for today for a specific symbol and timeframe.
    /// </summary>
    Task DeleteTodayHistoryAsync(string symbol, string timeframe);
}
