using System.Collections.Generic;
using System.Threading.Tasks;
using QuantEdge.Domain.Entities;

namespace QuantEdge.Infrastructure.Persistence.Repositories;

/// <summary>
/// Repository interface for saving and querying calculated market indicators.
/// </summary>
public interface IMarketIndicatorRepository
{
    /// <summary>
    /// Inserts calculated indicators using a Stored Procedure.
    /// </summary>
    Task InsertAsync(MarketIndicator indicator);

    /// <summary>
    /// Retrieves historical indicators using a Stored Procedure, returning auto-mapped entities.
    /// </summary>
    Task<IEnumerable<MarketIndicator>> GetHistoryAsync(string symbol, string timeframe, int limit);

    /// <summary>
    /// Deletes today's calculated indicators for a specific symbol and timeframe.
    /// </summary>
    Task DeleteTodayIndicatorsAsync(string symbol, string timeframe);
}
