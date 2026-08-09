using System;
using System.Data;
using System.Threading.Tasks;
using Dapper;
using QuantEdge.Infrastructure.Models;

using QuantEdge.Infrastructure.Interfaces;

namespace QuantEdge.Infrastructure.Persistence.Repositories;

/// <summary>
/// Dapper-based repository for Zerodha session token management.
/// All data access is funnelled through PostgreSQL stored procedures / functions.
/// Uses Memory Cache (TTL: 10 minutes) for fast active token lookups.
/// </summary>
public class ZerodhaSessionRepository : IZerodhaSessionRepository
{
    private readonly IDbConnectionFactory _connectionFactory;
    private readonly ICacheService? _cacheService;

    public ZerodhaSessionRepository(IDbConnectionFactory connectionFactory, ICacheService? cacheService = null)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _cacheService = cacheService;
    }

    /// <inheritdoc />
    public async Task<string?> ActivateTokenIfValidAsync(string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentNullException(nameof(apiKey));

        using var conn = _connectionFactory.CreateConnection();

        var activatedToken = await conn.ExecuteScalarAsync<string?>(
            "SELECT sp_activate_zerodha_token(@p_api_key)",
            new { p_api_key = apiKey }
        );

        if (_cacheService != null)
        {
            await _cacheService.RemoveAsync("zerodha_active_session");
        }

        return activatedToken;
    }

    /// <inheritdoc />
    public async Task<ZerodhaSession?> GetActiveSessionAsync()
    {
        string cacheKey = "zerodha_active_session";
        if (_cacheService != null)
        {
            var cached = await _cacheService.GetAsync<ZerodhaSession>(cacheKey);
            if (cached != null) return cached;
        }

        using var conn = _connectionFactory.CreateConnection();

        var session = await conn.QueryFirstOrDefaultAsync<ZerodhaSession>(
            "SELECT * FROM sp_get_active_zerodha_session()"
        );

        if (_cacheService != null && session != null)
        {
            await _cacheService.SetAsync(cacheKey, session, TimeSpan.FromMinutes(10));
        }

        return session;
    }
}
