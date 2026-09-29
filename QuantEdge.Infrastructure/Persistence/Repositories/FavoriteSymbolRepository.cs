using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;

namespace QuantEdge.Infrastructure.Persistence.Repositories;

/// <summary>
/// Per-user favorite symbols. Backed by user_favorite_symbols (schema.sql):
/// reads via fn_get_user_favorite_symbols (functions.sql), writes via
/// sp_add_user_favorite_symbol / sp_remove_user_favorite_symbol (stored_procedures.sql).
/// </summary>
public class FavoriteSymbolRepository : IFavoriteSymbolRepository
{
    private readonly IDbConnectionFactory _connectionFactory;

    public FavoriteSymbolRepository(IDbConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
    }

    public async Task<IReadOnlyList<string>> GetFavoritesAsync(int userId)
    {
        using var connection = _connectionFactory.CreateConnection();
        string sql = "SELECT * FROM fn_get_user_favorite_symbols(@userId);";

        var symbols = await connection.QueryAsync<string>(sql, new { userId });
        return symbols.ToList();
    }

    public async Task AddFavoriteAsync(int userId, string symbol)
    {
        using var connection = _connectionFactory.CreateConnection();
        string sql = "CALL sp_add_user_favorite_symbol(@userId, @symbol);";

        await connection.ExecuteAsync(sql, new { userId, symbol });
    }

    public async Task RemoveFavoriteAsync(int userId, string symbol)
    {
        using var connection = _connectionFactory.CreateConnection();
        string sql = "CALL sp_remove_user_favorite_symbol(@userId, @symbol);";

        await connection.ExecuteAsync(sql, new { userId, symbol });
    }
}
