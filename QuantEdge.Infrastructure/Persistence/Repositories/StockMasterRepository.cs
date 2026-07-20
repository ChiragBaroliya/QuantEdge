using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using QuantEdge.Domain.Entities;
using QuantEdge.Infrastructure.DTOs;

namespace QuantEdge.Infrastructure.Persistence.Repositories;

/// <summary>
/// High-performance Dapper repository implementation for querying stock master records
/// using PostgreSQL stored functions.
/// </summary>
public class StockMasterRepository : IStockMasterRepository
{
    private readonly IDbConnectionFactory _connectionFactory;

    static StockMasterRepository()
    {
        DefaultTypeMap.MatchNamesWithUnderscores = true;
    }

    public StockMasterRepository(IDbConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
    }

    /// <summary>
    /// Retrieves all active stock symbols and instrument tokens using sp_get_active_stocks.
    /// </summary>
    public async Task<IEnumerable<StockMaster>> GetActiveStocksAsync()
    {
        using var connection = _connectionFactory.CreateConnection();
        return await connection.QueryAsync<StockMaster>(
            "SELECT * FROM sp_get_active_stocks();"
        );
    }

    /// <summary>
    /// Retrieves a specific stock master record by symbol using sp_get_stock_by_symbol.
    /// </summary>
    public async Task<StockMaster?> GetBySymbolAsync(string symbol)
    {
        using var connection = _connectionFactory.CreateConnection();
        return await connection.QueryFirstOrDefaultAsync<StockMaster>(
            "SELECT * FROM sp_get_stock_by_symbol(@p_symbol);",
            new { p_symbol = symbol }
        );
    }

    /// <summary>
    /// Retrieves all stock master records (active and inactive).
    /// </summary>
    public async Task<IEnumerable<StockMaster>> GetAllAsync()
    {
        using var connection = _connectionFactory.CreateConnection();
        return await connection.QueryAsync<StockMaster>(
            "SELECT * FROM stock_master ORDER BY symbol;"
        );
    }

    /// <summary>
    /// Updates the timeframe-specific history stored field for a stock master record.
    /// </summary>
    public async Task UpdateHistoryStoredAsync(int id, string timeframe, int? status)
    {
        using var connection = _connectionFactory.CreateConnection();
        string column = timeframe.ToLower() switch
        {
            "1m" => "is_histry_stored_1m",
            "5m" => "is_histry_stored_5m",
            "15m" => "is_histry_stored_15m",
            "60m" => "is_histry_stored_60m",
            "1d" => "is_histry_stored_1d",
            _ => throw new ArgumentException($"Invalid timeframe: {timeframe}")
        };
        await connection.ExecuteAsync(
            $"UPDATE stock_master SET {column} = @Status WHERE id = @Id;",
            new { Id = id, Status = status }
        );
    }

    /// <summary>
    /// Retrieves overall data coverage summary statistics via sp_get_data_coverage_summary.
    /// </summary>
    public async Task<CoverageSummaryDto> GetCoverageSummaryAsync()
    {
        using var connection = _connectionFactory.CreateConnection();
        var result = await connection.QueryFirstOrDefaultAsync<CoverageSummaryDto>(
            "SELECT * FROM sp_get_data_coverage_summary();"
        );
        return result ?? new CoverageSummaryDto();
    }

    /// <summary>
    /// Retrieves paginated stock coverage data using sp_get_paginated_stock_coverage.
    /// </summary>
    public async Task<PaginatedCoverageResult> GetPaginatedCoverageAsync(string? search, string? statusFilter, string? historyFilter, int pageNumber, int pageSize)
    {
        using var connection = _connectionFactory.CreateConnection();
        var items = (await connection.QueryAsync<StockCoverageDto>(
            "SELECT * FROM sp_get_paginated_stock_coverage(@p_search, @p_status_filter, @p_history_filter, @p_page_number, @p_page_size);",
            new {
                p_search = string.IsNullOrWhiteSpace(search) ? null : search.Trim(),
                p_status_filter = string.IsNullOrWhiteSpace(statusFilter) ? null : statusFilter.Trim(),
                p_history_filter = string.IsNullOrWhiteSpace(historyFilter) ? null : historyFilter.Trim(),
                p_page_number = pageNumber < 1 ? 1 : pageNumber,
                p_page_size = pageSize < 1 ? 25 : pageSize
            }
        )).ToList();

        int totalCount = items.FirstOrDefault()?.TotalRecords ?? 0;

        return new PaginatedCoverageResult
        {
            Items = items,
            TotalCount = totalCount,
            PageNumber = pageNumber,
            PageSize = pageSize
        };
    }

    /// <summary>
    /// Updates a stock's active status and timeframe history stored flags using sp_update_stock_coverage_flags.
    /// </summary>
    public async Task UpdateStockCoverageFlagsAsync(UpdateStockCoverageRequest request)
    {
        if (request == null) throw new ArgumentNullException(nameof(request));

        using var connection = _connectionFactory.CreateConnection();
        await connection.ExecuteAsync(
            "SELECT sp_update_stock_coverage_flags(@p_id, @p_is_active, @p_histry_1m, @p_histry_5m, @p_histry_15m, @p_histry_60m, @p_histry_1d);",
            new {
                p_id = request.Id,
                p_is_active = request.IsActive,
                p_histry_1m = request.IsHistryStored1m,
                p_histry_5m = request.IsHistryStored5m,
                p_histry_15m = request.IsHistryStored15m,
                p_histry_60m = request.IsHistryStored60m,
                p_histry_1d = request.IsHistryStored1d
            }
        );
    }

    /// <summary>
    /// Deletes a stock master record and its associated market candles using sp_delete_stock_master.
    /// </summary>
    public async Task DeleteStockAsync(int id)
    {
        using var connection = _connectionFactory.CreateConnection();
        await connection.ExecuteAsync(
            "SELECT sp_delete_stock_master(@p_id);",
            new { p_id = id }
        );
    }

    /// <summary>
    /// Deletes multiple stock master records and associated market candles using sp_bulk_delete_stock_master.
    /// </summary>
    public async Task BulkDeleteStocksAsync(IEnumerable<int> ids)
    {
        if (ids == null || !ids.Any()) return;

        using var connection = _connectionFactory.CreateConnection();
        await connection.ExecuteAsync(
            "SELECT sp_bulk_delete_stock_master(@p_ids);",
            new { p_ids = ids.ToArray() }
        );
    }
}



