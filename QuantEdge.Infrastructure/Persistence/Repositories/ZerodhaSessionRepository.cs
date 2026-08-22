using System;
using System.Collections.Generic;
using System.Data;
using System.Threading.Tasks;
using Dapper;
using QuantEdge.Infrastructure.Models;
using QuantEdge.Infrastructure.Interfaces;

namespace QuantEdge.Infrastructure.Persistence.Repositories;

/// <summary>
/// Dapper-based repository for Zerodha session token management.
/// Supports multi-user session lookups and in-memory cache.
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

    private static bool _schemaInitialized = false;
    private static readonly object _initLock = new object();

    private async Task EnsureSchemaAsync(IDbConnection conn)
    {
        if (_schemaInitialized) return;

        try
        {
            await conn.ExecuteAsync(@"
                ALTER TABLE zerodha_sessions ADD COLUMN IF NOT EXISTS user_id INT DEFAULT 1;
                ALTER TABLE zerodha_sessions ADD COLUMN IF NOT EXISTS client_id VARCHAR(50);
                ALTER TABLE zerodha_sessions ADD COLUMN IF NOT EXISTS user_name VARCHAR(100);
                ALTER TABLE zerodha_sessions ADD COLUMN IF NOT EXISTS user_email VARCHAR(100);
                ALTER TABLE zerodha_sessions ADD COLUMN IF NOT EXISTS is_ddpi_enabled BOOLEAN DEFAULT FALSE;
                ALTER TABLE zerodha_sessions ADD COLUMN IF NOT EXISTS api_secret VARCHAR(100);
            ");
            _schemaInitialized = true;
        }
        catch
        {
            // Ignore if permissions or already running
        }
    }

    /// <inheritdoc />
    public async Task<string?> ActivateTokenIfValidAsync(string apiKey, int userId = 1)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentNullException(nameof(apiKey));

        using var conn = _connectionFactory.CreateConnection();
        await EnsureSchemaAsync(conn);

        // Call stored function sp_activate_zerodha_token
        string sql = "SELECT sp_activate_zerodha_token(@apiKey, @userId);";
        var activatedToken = await conn.ExecuteScalarAsync<string?>(sql, new { apiKey, userId });

        if (_cacheService != null)
        {
            await _cacheService.RemoveAsync($"zerodha_active_session_{userId}");
            await _cacheService.RemoveAsync("zerodha_active_session");
        }

        return activatedToken;
    }

    /// <inheritdoc />
    public async Task<ZerodhaSession?> GetActiveSessionAsync(int userId = 1)
    {
        string cacheKey = $"zerodha_active_session_{userId}";
        if (_cacheService != null)
        {
            var cached = await _cacheService.GetAsync<ZerodhaSession>(cacheKey);
            if (cached != null) return cached;
        }

        using var conn = _connectionFactory.CreateConnection();
        await EnsureSchemaAsync(conn);

        // Call stored function fn_get_active_zerodha_session
        string sql = "SELECT * FROM fn_get_active_zerodha_session(@userId);";
        var session = await conn.QueryFirstOrDefaultAsync<ZerodhaSession>(sql, new { userId });

        if (_cacheService != null && session != null)
        {
            await _cacheService.SetAsync(cacheKey, session, TimeSpan.FromMinutes(10));
        }

        return session;
    }

    /// <inheritdoc />
    public async Task<IEnumerable<ZerodhaSession>> GetAllActiveSessionsAsync()
    {
        using var conn = _connectionFactory.CreateConnection();
        await EnsureSchemaAsync(conn);

        // Call stored function fn_get_all_active_zerodha_sessions
        string sql = "SELECT * FROM fn_get_all_active_zerodha_sessions();";
        return await conn.QueryAsync<ZerodhaSession>(sql);
    }

    /// <inheritdoc />
    public async Task UpsertSessionAsync(int userId, string apiKey, string? apiSecret, string accessToken, string? clientId = null, string? userName = null, string? userEmail = null)
    {
        using var conn = _connectionFactory.CreateConnection();
        await EnsureSchemaAsync(conn);

        // Call stored procedure sp_upsert_user_zerodha_session
        string sql = @"
            CALL sp_upsert_user_zerodha_session(
                @userId, 
                @clientId, 
                @userName, 
                @userEmail, 
                @apiKey, 
                @apiSecret, 
                @accessToken
            );";
        await conn.ExecuteAsync(sql, new { userId, clientId, userName, userEmail, apiKey, apiSecret, accessToken });

        if (_cacheService != null)
        {
            await _cacheService.RemoveAsync($"zerodha_active_session_{userId}");
            await _cacheService.RemoveAsync("zerodha_active_session");
        }
    }

    /// <inheritdoc />
    public async Task UpdateDdpiStatusAsync(int userId, bool isDdpiEnabled)
    {
        using var conn = _connectionFactory.CreateConnection();
        await EnsureSchemaAsync(conn);

        string sql = "CALL sp_update_user_ddpi_status(@userId, @isDdpiEnabled);";
        await conn.ExecuteAsync(sql, new { userId, isDdpiEnabled });

        if (_cacheService != null)
        {
            await _cacheService.RemoveAsync($"zerodha_active_session_{userId}");
            await _cacheService.RemoveAsync("zerodha_active_session");
        }
    }
}

