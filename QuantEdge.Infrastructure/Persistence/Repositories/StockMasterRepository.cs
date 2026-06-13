using System;
using System.Collections.Generic;
using System.Data;
using System.Threading.Tasks;
using Dapper;
using QuantEdge.Domain.Entities;

namespace QuantEdge.Infrastructure.Persistence.Repositories;

/// <summary>
/// High-performance Dapper repository implementation for querying stock master records
/// using PostgreSQL stored functions.
/// </summary>
public class StockMasterRepository : IStockMasterRepository
{
    private readonly IDbConnectionFactory _connectionFactory;

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
}
