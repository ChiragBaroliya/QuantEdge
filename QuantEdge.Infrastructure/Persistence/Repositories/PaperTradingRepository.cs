using System;
using System.Collections.Generic;
using System.Data;
using System.Threading.Tasks;
using Dapper;
using QuantEdge.Domain.Entities;

namespace QuantEdge.Infrastructure.Persistence.Repositories;

public class PaperTradingRepository : IPaperTradingRepository
{
    private readonly IDbConnectionFactory _connectionFactory;

    public PaperTradingRepository(IDbConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
    }

    public async Task<PaperAccount?> GetAccountAsync(string userId = "default_user")
    {
        using var connection = _connectionFactory.CreateConnection();
        string sql = @"
            SELECT 
                id AS Id,
                user_id AS UserId,
                account_name AS AccountName,
                initial_balance AS InitialBalance,
                current_balance AS CurrentBalance,
                used_margin AS UsedMargin,
                realized_pnl AS RealizedPnl,
                is_auto_trade_enabled AS IsAutoTradeEnabled,
                trading_mode AS TradingMode,
                auto_trade_timeframe AS AutoTradeTimeframe,
                auto_trade_min_signal_strength AS AutoTradeMinSignalStrength,
                auto_trade_quantity AS AutoTradeQuantity,
                auto_trade_stop_loss_percent AS AutoTradeStopLossPercent,
                auto_trade_take_profit_percent AS AutoTradeTakeProfitPercent,
                max_open_positions AS MaxOpenPositions,
                daily_max_loss_limit AS DailyMaxLossLimit,
                created_at AS CreatedAt,
                updated_at AS UpdatedAt
            FROM paper_accounts
            WHERE user_id = @userId
            LIMIT 1;";

        return await connection.QueryFirstOrDefaultAsync<PaperAccount>(sql, new { userId });
    }

    public async Task<PaperAccount> CreateAccountAsync(string userId = "default_user", string accountName = "Virtual Trading Account", decimal initialBalance = 100000m)
    {
        using var connection = _connectionFactory.CreateConnection();
        string sql = @"
            INSERT INTO paper_accounts (user_id, account_name, initial_balance, current_balance, used_margin, realized_pnl)
            VALUES (@userId, @accountName, @initialBalance, @initialBalance, 0.00, 0.00)
            RETURNING 
                id AS Id,
                user_id AS UserId,
                account_name AS AccountName,
                initial_balance AS InitialBalance,
                current_balance AS CurrentBalance,
                used_margin AS UsedMargin,
                realized_pnl AS RealizedPnl,
                created_at AS CreatedAt,
                updated_at AS UpdatedAt;";

        return await connection.QuerySingleAsync<PaperAccount>(sql, new { userId, accountName, initialBalance });
    }

    public async Task UpdateAccountBalanceAndMarginAsync(int accountId, decimal newBalance, decimal usedMargin, decimal realizedPnl)
    {
        using var connection = _connectionFactory.CreateConnection();
        string sql = @"
            UPDATE paper_accounts
            SET current_balance = @newBalance,
                used_margin = @usedMargin,
                realized_pnl = @realizedPnl,
                updated_at = NOW()
            WHERE id = @accountId;";

        await connection.ExecuteAsync(sql, new { accountId, newBalance, usedMargin, realizedPnl });
    }

    public async Task<PaperOrder> CreateOrderAsync(PaperOrder order)
    {
        using var connection = _connectionFactory.CreateConnection();
        string sql = @"
            INSERT INTO paper_orders (
                account_id, symbol, order_type, side, quantity, price, 
                trigger_price, stop_loss, take_profit, status, filled_price, filled_at, trade_type, created_at, remarks
            )
            VALUES (
                @AccountId, @Symbol, @OrderType, @Side, @Quantity, @Price,
                @TriggerPrice, @StopLoss, @TakeProfit, @Status, @FilledPrice, @FilledAt, @TradeType, NOW(), @Remarks
            )
            RETURNING 
                id AS Id,
                account_id AS AccountId,
                symbol AS Symbol,
                order_type AS OrderType,
                side AS Side,
                quantity AS Quantity,
                price AS Price,
                trigger_price AS TriggerPrice,
                stop_loss AS StopLoss,
                take_profit AS TakeProfit,
                status AS Status,
                filled_price AS FilledPrice,
                filled_at AS FilledAt,
                trade_type AS TradeType,
                created_at AS CreatedAt,
                remarks AS Remarks;";

        return await connection.QuerySingleAsync<PaperOrder>(sql, new
        {
            order.AccountId,
            order.Symbol,
            OrderType = (int)order.OrderType,
            Side = (int)order.Side,
            order.Quantity,
            order.Price,
            order.TriggerPrice,
            order.StopLoss,
            order.TakeProfit,
            Status = (int)order.Status,
            order.FilledPrice,
            order.FilledAt,
            TradeType = (int)order.TradeType,
            order.Remarks
        });
    }

    public async Task<PaperOrder?> GetOrderByIdAsync(int orderId)
    {
        using var connection = _connectionFactory.CreateConnection();
        string sql = @"
            SELECT 
                id AS Id,
                account_id AS AccountId,
                symbol AS Symbol,
                order_type AS OrderType,
                side AS Side,
                quantity AS Quantity,
                price AS Price,
                trigger_price AS TriggerPrice,
                stop_loss AS StopLoss,
                take_profit AS TakeProfit,
                status AS Status,
                filled_price AS FilledPrice,
                filled_at AS FilledAt,
                trade_type AS TradeType,
                created_at AS CreatedAt,
                remarks AS Remarks
            FROM paper_orders
            WHERE id = @orderId;";

        return await connection.QueryFirstOrDefaultAsync<PaperOrder>(sql, new { orderId });
    }

    public async Task UpdateOrderStatusAsync(int orderId, PaperOrderStatus status, decimal? filledPrice = null, string? remarks = null)
    {
        using var connection = _connectionFactory.CreateConnection();
        string sql = @"
            UPDATE paper_orders
            SET status = @status,
                filled_price = COALESCE(@filledPrice, filled_price),
                filled_at = CASE WHEN @status = 1 THEN NOW() ELSE filled_at END,
                remarks = COALESCE(@remarks, remarks)
            WHERE id = @orderId;";

        await connection.ExecuteAsync(sql, new { orderId, status = (int)status, filledPrice, remarks });
    }

    public async Task<IEnumerable<PaperOrder>> GetOrdersAsync(int accountId, bool activeOnly = false)
    {
        using var connection = _connectionFactory.CreateConnection();
        string sql = @"
            SELECT 
                Id,
                AccountId,
                Symbol,
                OrderType,
                Side,
                Quantity,
                Price,
                TriggerPrice,
                StopLoss,
                TakeProfit,
                Status,
                FilledPrice,
                FilledAt,
                TradeType,
                CreatedAt,
                Remarks
            FROM fn_get_paper_orders(@accountId, @activeOnly);";

        return await connection.QueryAsync<PaperOrder>(sql, new { accountId, activeOnly });
    }

    public async Task<PaperPosition?> GetOpenPositionBySymbolAsync(int accountId, string symbol)
    {
        using var connection = _connectionFactory.CreateConnection();
        string sql = @"
            SELECT 
                id AS Id,
                account_id AS AccountId,
                symbol AS Symbol,
                side AS Side,
                quantity AS Quantity,
                average_entry_price AS AverageEntryPrice,
                current_price AS CurrentPrice,
                unrealized_pnl AS UnrealizedPnl,
                stop_loss AS StopLoss,
                take_profit AS TakeProfit,
                status AS Status,
                trade_type AS TradeType,
                exit_reason AS ExitReason,
                opened_at AS OpenedAt,
                closed_at AS ClosedAt,
                realized_pnl AS RealizedPnl
            FROM paper_positions
            WHERE account_id = @accountId AND symbol = @symbol AND status = 0
            LIMIT 1;";

        return await connection.QueryFirstOrDefaultAsync<PaperPosition>(sql, new { accountId, symbol });
    }

    public async Task<PaperPosition> UpsertPositionAsync(PaperPosition position)
    {
        using var connection = _connectionFactory.CreateConnection();
        if (position.Id == 0)
        {
            string sqlInsert = @"
                INSERT INTO paper_positions (
                    account_id, symbol, side, quantity, average_entry_price, current_price,
                    unrealized_pnl, stop_loss, take_profit, status, trade_type, exit_reason, opened_at, realized_pnl
                )
                VALUES (
                    @AccountId, @Symbol, @Side, @Quantity, @AverageEntryPrice, @CurrentPrice,
                    @UnrealizedPnl, @StopLoss, @TakeProfit, @Status, @TradeType, @ExitReason, NOW(), @RealizedPnl
                )
                RETURNING 
                    id AS Id, account_id AS AccountId, symbol AS Symbol, side AS Side,
                    quantity AS Quantity, average_entry_price AS AverageEntryPrice, current_price AS CurrentPrice,
                    unrealized_pnl AS UnrealizedPnl, stop_loss AS StopLoss, take_profit AS TakeProfit,
                    status AS Status, trade_type AS TradeType, exit_reason AS ExitReason, opened_at AS OpenedAt, closed_at AS ClosedAt, realized_pnl AS RealizedPnl;";

            return await connection.QuerySingleAsync<PaperPosition>(sqlInsert, new
            {
                position.AccountId,
                position.Symbol,
                Side = (int)position.Side,
                position.Quantity,
                position.AverageEntryPrice,
                position.CurrentPrice,
                position.UnrealizedPnl,
                position.StopLoss,
                position.TakeProfit,
                Status = (int)position.Status,
                TradeType = (int)position.TradeType,
                position.ExitReason,
                position.RealizedPnl
            });
        }
        else
        {
            string sqlUpdate = @"
                UPDATE paper_positions
                SET quantity = @Quantity,
                    average_entry_price = @AverageEntryPrice,
                    current_price = @CurrentPrice,
                    unrealized_pnl = @UnrealizedPnl,
                    stop_loss = @StopLoss,
                    take_profit = @TakeProfit,
                    status = @Status,
                    exit_reason = COALESCE(@ExitReason, exit_reason),
                    realized_pnl = @RealizedPnl
                WHERE id = @Id
                RETURNING 
                    id AS Id, account_id AS AccountId, symbol AS Symbol, side AS Side,
                    quantity AS Quantity, average_entry_price AS AverageEntryPrice, current_price AS CurrentPrice,
                    unrealized_pnl AS UnrealizedPnl, stop_loss AS StopLoss, take_profit AS TakeProfit,
                    status AS Status, trade_type AS TradeType, exit_reason AS ExitReason, opened_at AS OpenedAt, closed_at AS ClosedAt, realized_pnl AS RealizedPnl;";

            return await connection.QuerySingleAsync<PaperPosition>(sqlUpdate, new
            {
                position.Id,
                position.Quantity,
                position.AverageEntryPrice,
                position.CurrentPrice,
                position.UnrealizedPnl,
                position.StopLoss,
                position.TakeProfit,
                Status = (int)position.Status,
                position.ExitReason,
                position.RealizedPnl
            });
        }
    }

    public async Task ClosePositionAsync(int positionId, decimal exitPrice, decimal realizedPnl, string? exitReason = null)
    {
        using var connection = _connectionFactory.CreateConnection();
        string sql = @"
            UPDATE paper_positions
            SET status = 1,
                current_price = @exitPrice,
                unrealized_pnl = 0,
                realized_pnl = @realizedPnl,
                exit_reason = COALESCE(@exitReason, exit_reason),
                closed_at = NOW()
            WHERE id = @positionId;";

        await connection.ExecuteAsync(sql, new { positionId, exitPrice, realizedPnl, exitReason });
    }

    public async Task<IEnumerable<PaperPosition>> GetPositionsAsync(int accountId, bool openOnly = true)
    {
        using var connection = _connectionFactory.CreateConnection();
        string sql = @"
            SELECT 
                id AS Id,
                account_id AS AccountId,
                symbol AS Symbol,
                side AS Side,
                quantity AS Quantity,
                average_entry_price AS AverageEntryPrice,
                current_price AS CurrentPrice,
                unrealized_pnl AS UnrealizedPnl,
                stop_loss AS StopLoss,
                take_profit AS TakeProfit,
                status AS Status,
                trade_type AS TradeType,
                exit_reason AS ExitReason,
                opened_at AS OpenedAt,
                closed_at AS ClosedAt,
                realized_pnl AS RealizedPnl
            FROM paper_positions
            WHERE account_id = @accountId
              AND (@openOnly = FALSE OR status = 0)
            ORDER BY opened_at DESC;";

        return await connection.QueryAsync<PaperPosition>(sql, new { accountId, openOnly });
    }

    public async Task RecordTradeHistoryAsync(PaperTradeHistory history)
    {
        using var connection = _connectionFactory.CreateConnection();
        string sql = @"
            INSERT INTO paper_trade_history (account_id, order_id, symbol, side, quantity, executed_price, realized_pnl, trade_type, exit_reason, executed_at, remarks)
            VALUES (@AccountId, @OrderId, @Symbol, @Side, @Quantity, @ExecutedPrice, @RealizedPnl, @TradeType, @ExitReason, NOW(), @Remarks);";

        await connection.ExecuteAsync(sql, new
        {
            history.AccountId,
            history.OrderId,
            history.Symbol,
            Side = (int)history.Side,
            history.Quantity,
            history.ExecutedPrice,
            history.RealizedPnl,
            TradeType = (int)history.TradeType,
            history.ExitReason,
            history.Remarks
        });
    }

    public async Task<IEnumerable<PaperTradeHistory>> GetTradeHistoryAsync(int accountId, int limit = 50)
    {
        using var connection = _connectionFactory.CreateConnection();
        string sql = @"
            SELECT 
                id AS Id,
                account_id AS AccountId,
                order_id AS OrderId,
                symbol AS Symbol,
                side AS Side,
                quantity AS Quantity,
                executed_price AS ExecutedPrice,
                realized_pnl AS RealizedPnl,
                trade_type AS TradeType,
                exit_reason AS ExitReason,
                executed_at AS ExecutedAt,
                remarks AS Remarks
            FROM paper_trade_history
            WHERE account_id = @accountId
            ORDER BY executed_at DESC
            LIMIT @limit;";

        return await connection.QueryAsync<PaperTradeHistory>(sql, new { accountId, limit });
    }

    public async Task<(IEnumerable<PaperTradeHistory> Items, int TotalCount)> GetTradeHistoryPagedAsync(int accountId, QuantEdge.Infrastructure.DTOs.PaperTradeHistoryFilterDto filter)
    {
        using var connection = _connectionFactory.CreateConnection();
        
        int page = filter.Page < 1 ? 1 : filter.Page;
        int pageSize = filter.PageSize <= 0 ? 10 : filter.PageSize;
        int offset = (page - 1) * pageSize;

        string sql = @"
            SELECT 
                Id,
                AccountId,
                OrderId,
                Symbol,
                Side,
                Quantity,
                ExecutedPrice,
                RealizedPnl,
                TradeType,
                ExitReason,
                ExecutedAt,
                Remarks,
                TotalCount
            FROM fn_get_paper_trade_history_paged(
                @AccountId,
                @Symbol,
                @Side,
                @FromDate,
                @ToDate,
                @PageSize,
                @Offset
            );";

        var param = new
        {
            AccountId = accountId,
            Symbol = string.IsNullOrWhiteSpace(filter.Symbol) ? null : filter.Symbol.Trim(),
            Side = filter.Side.HasValue ? (int?)filter.Side.Value : null,
            FromDate = filter.FromDate,
            ToDate = filter.ToDate,
            PageSize = pageSize,
            Offset = offset
        };

        var rawResult = (await connection.QueryAsync<PaperTradeHistoryPagedRaw>(sql, param)).ToList();

        if (!rawResult.Any())
        {
            return (Enumerable.Empty<PaperTradeHistory>(), 0);
        }

        int totalCount = rawResult.First().TotalCount;
        var items = rawResult.Select(r => new PaperTradeHistory
        {
            Id = r.Id,
            AccountId = r.AccountId,
            OrderId = r.OrderId,
            Symbol = r.Symbol,
            Side = r.Side,
            Quantity = r.Quantity,
            ExecutedPrice = r.ExecutedPrice,
            RealizedPnl = r.RealizedPnl,
            TradeType = r.TradeType,
            ExitReason = r.ExitReason,
            ExecutedAt = r.ExecutedAt,
            Remarks = r.Remarks
        });

        return (items, totalCount);
    }

    private class PaperTradeHistoryPagedRaw : PaperTradeHistory
    {
        public int TotalCount { get; set; }
    }

    public async Task ResetAccountAsync(int accountId, decimal defaultBalance = 100000m)
    {
        using var connection = _connectionFactory.CreateConnection();
        connection.Open();
        using var transaction = connection.BeginTransaction();


        try
        {
            // 1. Clear open positions and active orders
            await connection.ExecuteAsync("DELETE FROM paper_positions WHERE account_id = @accountId;", new { accountId }, transaction);
            await connection.ExecuteAsync("DELETE FROM paper_orders WHERE account_id = @accountId;", new { accountId }, transaction);
            await connection.ExecuteAsync("DELETE FROM paper_trade_history WHERE account_id = @accountId;", new { accountId }, transaction);

            // 2. Reset balance
            string resetAccountSql = @"
                UPDATE paper_accounts
                SET current_balance = @defaultBalance,
                    initial_balance = @defaultBalance,
                    used_margin = 0.00,
                    realized_pnl = 0.00,
                    updated_at = NOW()
                WHERE id = @accountId;";

            await connection.ExecuteAsync(resetAccountSql, new { accountId, defaultBalance }, transaction);
            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    public async Task UpdateAutoTradeSettingsAsync(int accountId, QuantEdge.Infrastructure.DTOs.AutoTradeSettingsDto settings)
    {
        using var connection = _connectionFactory.CreateConnection();
        string sql = @"
            UPDATE paper_accounts
            SET is_auto_trade_enabled = @IsAutoTradeEnabled,
                trading_mode = @TradingMode,
                auto_trade_timeframe = @AutoTradeTimeframe,
                auto_trade_min_signal_strength = @AutoTradeMinSignalStrength,
                auto_trade_quantity = @AutoTradeQuantity,
                auto_trade_stop_loss_percent = @AutoTradeStopLossPercent,
                auto_trade_take_profit_percent = @AutoTradeTakeProfitPercent,
                max_open_positions = @MaxOpenPositions,
                daily_max_loss_limit = @DailyMaxLossLimit,
                updated_at = NOW()
            WHERE id = @accountId;";

        await connection.ExecuteAsync(sql, new {
            accountId,
            settings.IsAutoTradeEnabled,
            settings.TradingMode,
            settings.AutoTradeTimeframe,
            settings.AutoTradeMinSignalStrength,
            settings.AutoTradeQuantity,
            settings.AutoTradeStopLossPercent,
            settings.AutoTradeTakeProfitPercent,
            settings.MaxOpenPositions,
            settings.DailyMaxLossLimit
        });
    }
}
