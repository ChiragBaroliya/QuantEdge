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
        string sql = @"
            SELECT 
                id AS Id,
                user_id AS UserId,
                is_auto_trade_enabled AS IsAutoTradeEnabled,
                available_capital AS AvailableCapital,
                profit_target_pct AS ProfitTargetPct,
                stop_loss_pct AS StopLossPct,
                max_duration_days AS MaxDurationDays,
                max_trades_per_day AS MaxTradesPerDay,
                fixed_amount_per_trade AS FixedAmountPerTrade,
                min_conditions_match AS MinConditionsMatch,
                trading_window_start AS TradingWindowStart,
                trading_window_end AS TradingWindowEnd,
                updated_at AS UpdatedAt
            FROM auto_trade_settings
            WHERE user_id = @userId;";

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
        string sql = @"
            SELECT 
                id AS Id,
                user_id AS UserId,
                is_auto_trade_enabled AS IsAutoTradeEnabled,
                available_capital AS AvailableCapital,
                profit_target_pct AS ProfitTargetPct,
                stop_loss_pct AS StopLossPct,
                max_duration_days AS MaxDurationDays,
                max_trades_per_day AS MaxTradesPerDay,
                fixed_amount_per_trade AS FixedAmountPerTrade,
                min_conditions_match AS MinConditionsMatch,
                trading_window_start AS TradingWindowStart,
                trading_window_end AS TradingWindowEnd,
                updated_at AS UpdatedAt
            FROM auto_trade_settings
            WHERE is_auto_trade_enabled = TRUE;";

        return await connection.QueryAsync<AutoTradeSettings>(sql);
    }

    public async Task<AutoTradeSettings> UpsertSettingsAsync(AutoTradeSettings settings)

    {
        using var connection = _connectionFactory.CreateConnection();
        string sql = @"
            INSERT INTO auto_trade_settings (
                user_id, is_auto_trade_enabled, available_capital, profit_target_pct, stop_loss_pct,
                max_duration_days, max_trades_per_day, fixed_amount_per_trade, min_conditions_match,
                trading_window_start, trading_window_end, updated_at
            )
            VALUES (
                @UserId, @IsAutoTradeEnabled, @AvailableCapital, @ProfitTargetPct, @StopLossPct,
                @MaxDurationDays, @MaxTradesPerDay, @FixedAmountPerTrade, @MinConditionsMatch,
                @TradingWindowStart, @TradingWindowEnd, NOW()
            )
            ON CONFLICT (user_id) DO UPDATE
            SET is_auto_trade_enabled = EXCLUDED.is_auto_trade_enabled,
                available_capital = EXCLUDED.available_capital,
                profit_target_pct = EXCLUDED.profit_target_pct,
                stop_loss_pct = EXCLUDED.stop_loss_pct,
                max_duration_days = EXCLUDED.max_duration_days,
                max_trades_per_day = EXCLUDED.max_trades_per_day,
                fixed_amount_per_trade = EXCLUDED.fixed_amount_per_trade,
                min_conditions_match = EXCLUDED.min_conditions_match,
                trading_window_start = EXCLUDED.trading_window_start,
                trading_window_end = EXCLUDED.trading_window_end,
                updated_at = NOW()
            RETURNING 
                id AS Id,
                user_id AS UserId,
                is_auto_trade_enabled AS IsAutoTradeEnabled,
                available_capital AS AvailableCapital,
                profit_target_pct AS ProfitTargetPct,
                stop_loss_pct AS StopLossPct,
                max_duration_days AS MaxDurationDays,
                max_trades_per_day AS MaxTradesPerDay,
                fixed_amount_per_trade AS FixedAmountPerTrade,
                min_conditions_match AS MinConditionsMatch,
                trading_window_start AS TradingWindowStart,
                trading_window_end AS TradingWindowEnd,
                updated_at AS UpdatedAt;";

        return await connection.QuerySingleAsync<AutoTradeSettings>(sql, settings);
    }

    public async Task ToggleAutoTradeAsync(string userId, bool enabled)
    {
        using var connection = _connectionFactory.CreateConnection();
        string sql = @"
            UPDATE auto_trade_settings
            SET is_auto_trade_enabled = @enabled,
                updated_at = NOW()
            WHERE user_id = @userId;";

        await connection.ExecuteAsync(sql, new { userId, enabled });
    }

    public async Task<int> GetTodayAutoTradeCountAsync(string userId = "default_user")
    {
        using var connection = _connectionFactory.CreateConnection();
        // Today's trading window start is 09:15 AM today IST (or UTC equivalent)
        DateTime todayStartUtc = DateTime.UtcNow.Date; 

        string sql = @"
            SELECT COUNT(*) 
            FROM auto_trade_execution_logs 
            WHERE user_id = @userId 
              AND action_type = 'AUTO_BUY' 
              AND executed_at >= @todayStartUtc;";

        return await connection.ExecuteScalarAsync<int>(sql, new { userId, todayStartUtc });
    }

    public async Task LogExecutionAsync(AutoTradeExecutionLog log)
    {
        using var connection = _connectionFactory.CreateConnection();
        string sql = @"
            INSERT INTO auto_trade_execution_logs (user_id, symbol, action_type, price, quantity, reason, executed_at)
            VALUES (@UserId, @Symbol, @ActionType, @Price, @Quantity, @Reason, NOW());";

        await connection.ExecuteAsync(sql, log);
    }

    public async Task<IEnumerable<AutoTradeExecutionLog>> GetTodayLogsAsync(string userId = "default_user", int limit = 50)
    {
        using var connection = _connectionFactory.CreateConnection();
        DateTime todayStartUtc = DateTime.UtcNow.Date;

        string sql = @"
            SELECT 
                id AS Id,
                user_id AS UserId,
                symbol AS Symbol,
                action_type AS ActionType,
                price AS Price,
                quantity AS Quantity,
                reason AS Reason,
                executed_at AS ExecutedAt
            FROM auto_trade_execution_logs
            WHERE user_id = @userId AND executed_at >= @todayStartUtc
            ORDER BY executed_at DESC
            LIMIT @limit;";

        return await connection.QueryAsync<AutoTradeExecutionLog>(sql, new { userId, todayStartUtc, limit });
    }
}
