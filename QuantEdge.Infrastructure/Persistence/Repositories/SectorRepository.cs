using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;

namespace QuantEdge.Infrastructure.Persistence.Repositories;

/// <summary>
/// Sectors and their linked stocks. Backed by sectors / stock_sectors (schema.sql, seeded by
/// stock_sector_data.sql); reads via fn_get_sector_stocks / fn_get_sector_list (functions.sql),
/// writes via fn_upsert_sector / sp_set_stock_sectors (stored_procedures.sql).
/// </summary>
public class SectorRepository : ISectorRepository
{
    private readonly IDbConnectionFactory _connectionFactory;

    public SectorRepository(IDbConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
    }

    public async Task<IReadOnlyList<SectorStockRow>> GetSectorStocksAsync()
    {
        using var connection = _connectionFactory.CreateConnection();
        string sql = "SELECT * FROM fn_get_sector_stocks();";

        var rows = await connection.QueryAsync<SectorStockRow>(sql);
        return rows.ToList();
    }

    public async Task<IReadOnlyList<SectorListItem>> GetSectorListAsync()
    {
        using var connection = _connectionFactory.CreateConnection();
        string sql = "SELECT * FROM fn_get_sector_list();";

        var rows = await connection.QueryAsync<SectorListItem>(sql);
        return rows.ToList();
    }

    public async Task<SectorListItem> UpsertSectorAsync(string name)
    {
        using var connection = _connectionFactory.CreateConnection();
        string sql = "SELECT * FROM fn_upsert_sector(@name);";

        return await connection.QuerySingleAsync<SectorListItem>(sql, new { name });
    }

    public async Task SetStockSectorsAsync(int stockId, IEnumerable<int> sectorIds)
    {
        using var connection = _connectionFactory.CreateConnection();
        string sql = "CALL sp_set_stock_sectors(@stockId, @sectorIds);";

        await connection.ExecuteAsync(sql, new { stockId, sectorIds = sectorIds.Distinct().ToArray() });
    }
}
