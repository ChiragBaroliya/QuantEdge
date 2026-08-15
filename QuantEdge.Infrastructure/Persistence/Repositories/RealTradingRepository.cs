using System;
using System.Collections.Generic;
using System.Data;
using System.Threading.Tasks;
using Dapper;
using QuantEdge.Domain.Entities;

namespace QuantEdge.Infrastructure.Persistence.Repositories;

public class RealTradingRepository : IRealTradingRepository
{
    private readonly IDbConnectionFactory _connectionFactory;

    public RealTradingRepository(IDbConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
    }

    public async Task<RealTradeSettings> GetSettingsAsync(int userId = 1)
    {
        using var connection = _connectionFactory.CreateConnection();
        string sql = "SELECT * FROM fn_get_real_trade_settings(@userId);";

        var settings = await connection.QueryFirstOrDefaultAsync<RealTradeSettings>(sql, new { userId });
        if (settings == null)
        {
            settings = new RealTradeSettings { UserId = userId };
            return await UpsertSettingsAsync(settings);
        }
        return settings;
    }

    public async Task<IEnumerable<RealTradeSettings>> GetActiveSettingsAsync()
    {
        using var connection = _connectionFactory.CreateConnection();
        string sql = "SELECT * FROM fn_get_active_real_trade_settings();";

        return await connection.QueryAsync<RealTradeSettings>(sql);
    }

    public async Task<RealTradeSettings> UpsertSettingsAsync(RealTradeSettings settings)
    {
        using var connection = _connectionFactory.CreateConnection();
        string sql = @"
            SELECT * FROM fn_upsert_real_trade_settings(
                @UserId,
                @IsRealTradeEnabled,
                @AvailableCapital,
                @ProfitTargetPct,
                @StopLossPct,
                @TrailingSlEnabled,
                @TrailingSlPct,
                @MaxDurationDays,
                @MaxTradesPerDay,
                @FixedAmountPerTrade,
                @MaxDailyLossLimit,
                @ProductType,
                @MinConditionsMatch,
                @TradingWindowStart,
                @TradingWindowEnd
            );";

        return await connection.QuerySingleAsync<RealTradeSettings>(sql, settings);
    }

    public async Task ToggleRealTradeAsync(int userId, bool enabled)
    {
        using var connection = _connectionFactory.CreateConnection();
        string sql = "SELECT fn_toggle_real_trade(@userId, @enabled);";

        await connection.ExecuteAsync(sql, new { userId, enabled });
    }

    public async Task<RealOrder> CreateOrderAsync(RealOrder order)
    {
        using var connection = _connectionFactory.CreateConnection();
        string sql = @"
            SELECT * FROM fn_create_real_order(
                @UserId,
                @BrokerOrderId,
                @Symbol,
                @Side,
                @Quantity,
                @OrderType,
                @Price,
                @StopLoss,
                @TakeProfit,
                @Status,
                @FilledPrice,
                @FilledAt,
                @RejectionReason,
                @TradeType,
                @Remarks
            );";

        return await connection.QuerySingleAsync<RealOrder>(sql, order);
    }

    public async Task<RealOrder?> GetOrderByIdAsync(int orderId)
    {
        using var connection = _connectionFactory.CreateConnection();
        string sql = "SELECT * FROM fn_get_real_order_by_id(@orderId);";

        return await connection.QueryFirstOrDefaultAsync<RealOrder>(sql, new { orderId });
    }

    public async Task<RealOrder?> GetOrderByBrokerOrderIdAsync(string brokerOrderId)
    {
        using var connection = _connectionFactory.CreateConnection();
        string sql = "SELECT * FROM fn_get_real_order_by_broker_id(@brokerOrderId);";

        return await connection.QueryFirstOrDefaultAsync<RealOrder>(sql, new { brokerOrderId });
    }

    public async Task UpdateOrderStatusAsync(int orderId, PaperOrderStatus status, decimal filledPrice, string? brokerOrderId = null, string? rejectionReason = null)
    {
        using var connection = _connectionFactory.CreateConnection();
        string sql = "CALL sp_update_real_order_status(@orderId, @status, @filledPrice, @brokerOrderId, @rejectionReason);";

        await connection.ExecuteAsync(sql, new { orderId, status = (int)status, filledPrice, brokerOrderId, rejectionReason });
    }

    public async Task<IEnumerable<RealOrder>> GetRecentOrdersAsync(int userId = 1, int limit = 50)
    {
        using var connection = _connectionFactory.CreateConnection();
        string sql = "SELECT * FROM fn_get_recent_real_orders(@userId, @limit);";

        return await connection.QueryAsync<RealOrder>(sql, new { userId, limit });
    }

    public async Task<RealPosition> UpsertPositionAsync(RealPosition position)
    {
        using var connection = _connectionFactory.CreateConnection();
        string sql = @"
            SELECT * FROM fn_upsert_real_position(
                @UserId,
                @Symbol,
                @Side,
                @Quantity,
                @AverageEntryPrice,
                @CurrentPrice,
                @UnrealizedPnl,
                @StopLoss,
                @TakeProfit,
                @TrailingStopLoss,
                @Status,
                @TradeType,
                @ExitReason,
                @RealizedPnl
            );";

        return await connection.QuerySingleAsync<RealPosition>(sql, position);
    }

    public async Task<RealPosition?> GetOpenPositionByIdAsync(int positionId)
    {
        using var connection = _connectionFactory.CreateConnection();
        string sql = "SELECT * FROM fn_get_open_real_position_by_id(@positionId);";

        return await connection.QueryFirstOrDefaultAsync<RealPosition>(sql, new { positionId });
    }

    public async Task<RealPosition?> GetOpenPositionBySymbolAsync(int userId, string symbol)
    {
        using var connection = _connectionFactory.CreateConnection();
        string sql = "SELECT * FROM fn_get_open_real_position_by_symbol(@userId, @symbol);";

        return await connection.QueryFirstOrDefaultAsync<RealPosition>(sql, new { userId, symbol });
    }

    public async Task<IEnumerable<RealPosition>> GetOpenPositionsAsync(int userId = 1)
    {
        using var connection = _connectionFactory.CreateConnection();
        string sql = "SELECT * FROM fn_get_open_real_positions(@userId);";

        return await connection.QueryAsync<RealPosition>(sql, new { userId });
    }

    public async Task ClosePositionAsync(int positionId, decimal exitPrice, decimal realizedPnl, string exitReason)
    {
        using var connection = _connectionFactory.CreateConnection();
        string sql = "CALL sp_close_real_position(@positionId, @exitPrice, @realizedPnl, @exitReason);";

        await connection.ExecuteAsync(sql, new { positionId, exitPrice, realizedPnl, exitReason });
    }

    public async Task UpdateTrailingStopLossAsync(int positionId, decimal newTrailingStopLoss)
    {
        using var connection = _connectionFactory.CreateConnection();
        string sql = "CALL sp_update_real_trailing_sl(@positionId, @newTrailingStopLoss);";

        await connection.ExecuteAsync(sql, new { positionId, newTrailingStopLoss });
    }

    public async Task<RealTradeHistory> RecordTradeHistoryAsync(RealTradeHistory history)
    {
        using var connection = _connectionFactory.CreateConnection();
        string sql = @"
            SELECT * FROM fn_record_real_trade_history(
                @UserId,
                @OrderId,
                @BrokerOrderId,
                @Symbol,
                @Side,
                @Quantity,
                @EntryPrice,
                @ExecutedPrice,
                @RealizedPnl,
                @TradeType,
                @ExitReason,
                @Remarks
            );";

        return await connection.QuerySingleAsync<RealTradeHistory>(sql, history);
    }

    public async Task<IEnumerable<RealTradeHistory>> GetTradeHistoryAsync(int userId = 1, int limit = 100)
    {
        using var connection = _connectionFactory.CreateConnection();
        string sql = "SELECT * FROM fn_get_real_trade_history(@userId, @limit);";

        return await connection.QueryAsync<RealTradeHistory>(sql, new { userId, limit });
    }

    public async Task<int> GetTodayRealTradeCountAsync(int userId = 1)
    {
        using var connection = _connectionFactory.CreateConnection();
        DateTime todayStartUtc = DateTime.UtcNow.Date;

        string sql = "SELECT fn_get_today_real_trade_count(@userId, @todayStartUtc);";

        return await connection.ExecuteScalarAsync<int>(sql, new { userId, todayStartUtc });
    }

    public async Task<decimal> GetTodayRealizedPnlAsync(int userId = 1)
    {
        using var connection = _connectionFactory.CreateConnection();
        DateTime todayStartUtc = DateTime.UtcNow.Date;

        string sql = "SELECT fn_get_today_realized_pnl(@userId, @todayStartUtc);";

        return await connection.ExecuteScalarAsync<decimal>(sql, new { userId, todayStartUtc });
    }

    public async Task LogExecutionAsync(RealTradeExecutionLog log)
    {
        using var connection = _connectionFactory.CreateConnection();
        string sql = "CALL sp_log_real_trade_execution(@UserId, @Symbol, @ActionType, @Price, @Quantity, @Reason);";

        await connection.ExecuteAsync(sql, log);
    }

    public async Task<IEnumerable<RealTradeExecutionLog>> GetTodayLogsAsync(int userId = 1, int limit = 50)
    {
        using var connection = _connectionFactory.CreateConnection();
        DateTime todayStartUtc = DateTime.UtcNow.Date;

        string sql = "SELECT * FROM fn_get_today_real_trade_logs(@userId, @todayStartUtc, @limit);";

        return await connection.QueryAsync<RealTradeExecutionLog>(sql, new { userId, todayStartUtc, limit });
    }
}
