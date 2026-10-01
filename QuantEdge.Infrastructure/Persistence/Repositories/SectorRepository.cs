using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;

namespace QuantEdge.Infrastructure.Persistence.Repositories;

/// <summary>
/// Sectors and their linked stocks. Backed by sectors / stock_sectors (schema.sql, seeded by
/// stock_sector_data.sql); reads via fn_get_sector_stocks (functions.sql).
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
}
