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

    /// <summary>
    /// Updates the timeframe-specific history stored field for a stock master record.
    /// </summary>
    Task UpdateHistoryStoredAsync(int id, string timeframe, int? status);

    /// <summary>
    /// Retrieves overall data coverage summary statistics.
    /// </summary>
    Task<QuantEdge.Infrastructure.DTOs.CoverageSummaryDto> GetCoverageSummaryAsync();

    /// <summary>
    /// Retrieves paginated stock coverage data matching search and filter criteria.
    /// </summary>
    Task<QuantEdge.Infrastructure.DTOs.PaginatedCoverageResult> GetPaginatedCoverageAsync(string? search, string? statusFilter, string? historyFilter, int pageNumber, int pageSize);

    /// <summary>
    /// Updates a stock's active status and timeframe history stored flags.
    /// </summary>
    Task UpdateStockCoverageFlagsAsync(QuantEdge.Infrastructure.DTOs.UpdateStockCoverageRequest request);
}

