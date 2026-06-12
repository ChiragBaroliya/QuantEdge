using System.Collections.Generic;
using System.Threading.Tasks;
using QuantEdge.Domain.Entities;

namespace QuantEdge.Infrastructure.Persistence.Repositories;

/// <summary>
/// Repository interface for saving and querying trade BUY/SELL recommendations.
/// </summary>
public interface ITradingSignalRepository
{
    /// <summary>
    /// Inserts a generated trade signal using a Stored Procedure.
    /// </summary>
    Task InsertAsync(TradingSignal signal);

    /// <summary>
    /// Retrieves recent trading signals using a Stored Procedure, returning auto-mapped entities.
    /// </summary>
    Task<IEnumerable<TradingSignal>> GetRecentSignalsAsync(int limit);
}
