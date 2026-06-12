using System;
using System.Data;
using System.Threading.Tasks;
using Dapper;
using QuantEdge.Infrastructure.Models;

namespace QuantEdge.Infrastructure.Persistence.Repositories;

/// <summary>
/// Dapper-based repository for Zerodha session token management.
/// All data access is funnelled through PostgreSQL stored procedures / functions.
/// </summary>
public class ZerodhaSessionRepository : IZerodhaSessionRepository
{
    private readonly IDbConnectionFactory _connectionFactory;

    public ZerodhaSessionRepository(IDbConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
    }

    /// <inheritdoc />
    public async Task<string?> ActivateTokenIfValidAsync(string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentNullException(nameof(apiKey));

        using var conn = _connectionFactory.CreateConnection();

        // sp_activate_zerodha_token is a PostgreSQL FUNCTION (returns SCALAR VARCHAR)
        // Checks created_at >= today's 6 AM IST; if valid, sets is_active = TRUE and returns token.
        var activatedToken = await conn.ExecuteScalarAsync<string?>(
            "SELECT sp_activate_zerodha_token(@p_api_key)",
            new { p_api_key = apiKey }
        );

        return activatedToken;
    }

    /// <inheritdoc />
    public async Task<ZerodhaSession?> GetActiveSessionAsync()
    {
        using var conn = _connectionFactory.CreateConnection();

        // sp_get_active_zerodha_session is a PostgreSQL FUNCTION returning a TABLE row
        var session = await conn.QueryFirstOrDefaultAsync<ZerodhaSession>(
            "SELECT * FROM sp_get_active_zerodha_session()"
        );

        return session;
    }
}
