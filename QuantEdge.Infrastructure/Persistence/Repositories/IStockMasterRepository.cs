using System.Collections.Generic;
using System.Threading.Tasks;
using QuantEdge.Domain.Entities;

namespace QuantEdge.Infrastructure.Persistence.Repositories;

/// <summary>
/// Repository interface for querying stock master mappings from the database.
/// </summary>
public interface IStockMasterRepository
{
    /// <summary>
    /// Retrieves all active stock symbols and instrument tokens.
    /// </summary>
    Task<IEnumerable<StockMaster>> GetActiveStocksAsync();

    /// <summary>
    /// Retrieves a specific stock master record by symbol.
    /// </summary>
    Task<StockMaster?> GetBySymbolAsync(string symbol);

    /// <summary>
    /// Retrieves all stock master records (active and inactive).
    /// </summary>
    Task<IEnumerable<StockMaster>> GetAllAsync();
}
