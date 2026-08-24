using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Dapper;
using QuantEdge.Domain.Entities;

namespace QuantEdge.Infrastructure.Persistence.Repositories;

public class AutoTradeRepository : IAutoTradeRepository
{
    private readonly IDbConnectionFactory _connectionFactory;

    public AutoTradeRepository(IDbConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
    }

    public async Task<AutoTradeSettings> GetSettingsAsync(string userId = "default_user")
    {
        using var connection = _connectionFactory.CreateConnection();
        string sql = "SELECT * FROM fn_get_auto_trade_settings(@userId);";

        var settings = await connection.QueryFirstOrDefaultAsync<AutoTradeSettings>(sql, new { userId });
        if (settings == null)
        {
            settings = new AutoTradeSettings { UserId = userId };
            return await UpsertSettingsAsync(settings);
        }
        return settings;
    }

    public async Task<IEnumerable<AutoTradeSettings>> GetActiveSettingsAsync()
    {
        using var connection = _connectionFactory.CreateConnection();
        string sql = "SELECT * FROM fn_get_active_auto_trade_settings();";

        return await connection.QueryAsync<AutoTradeSettings>(sql);
    }

    public async Task<AutoTradeSettings> UpsertSettingsAsync(AutoTradeSettings settings)
    {
        using var connection = _connectionFactory.CreateConnection();
        string sql = @"
            SELECT * FROM fn_upsert_auto_trade_settings(
                @UserId,
                @IsAutoTradeEnabled,
                @AvailableCapital,
                @ProfitTargetPct,
                @StopLossPct,
                @MaxDurationDays,
                @MaxTradesPerDay,
                @FixedAmountPerTrade,
                @MinConditionsMatch,
                @TradingWindowStart,
                @TradingWindowEnd
            );";

        return await connection.QuerySingleAsync<AutoTradeSettings>(sql, settings);
    }

    public async Task ToggleAutoTradeAsync(string userId, bool enabled)
    {
        using var connection = _connectionFactory.CreateConnection();
        string sql = "SELECT fn_toggle_auto_trade(@userId, @enabled);";

        await connection.ExecuteAsync(sql, new { userId, enabled });
    }

    public async Task<int> GetTodayAutoTradeCountAsync(string userId = "default_user")
    {
        using var connection = _connectionFactory.CreateConnection();
        DateTime todayStartUtc = DateTime.UtcNow.Date; 

        string sql = "SELECT fn_get_today_auto_trade_count(@userId, @todayStartUtc);";

        return await connection.ExecuteScalarAsync<int>(sql, new { userId, todayStartUtc });
    }

    public async Task LogExecutionAsync(AutoTradeExecutionLog log)
    {
        using var connection = _connectionFactory.CreateConnection();
        string sql = "SELECT fn_log_auto_trade_execution(@UserId, @Symbol, @ActionType, @Price, @Quantity, @Reason);";

        await connection.ExecuteAsync(sql, log);
    }

    public async Task<IEnumerable<AutoTradeExecutionLog>> GetTodayLogsAsync(string userId = "default_user", int limit = 50)
    {
        using var connection = _connectionFactory.CreateConnection();
        DateTime todayStartUtc = DateTime.UtcNow.Date;

        string sql = "SELECT * FROM fn_get_today_auto_trade_logs(@userId, @todayStartUtc, @limit);";

        return await connection.QueryAsync<AutoTradeExecutionLog>(sql, new { userId, todayStartUtc, limit });
    }

    public async Task ClearLogsAsync(string userId = "default_user")
    {
        using var connection = _connectionFactory.CreateConnection();
        string sql = "DELETE FROM auto_trade_execution_logs WHERE user_id = @userId;";
        await connection.ExecuteAsync(sql, new { userId });
    }
}
