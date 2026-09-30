using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using QuantEdge.Domain.Entities;
using QuantEdge.Infrastructure.DTOs;

namespace QuantEdge.Infrastructure.Persistence.Repositories;

/// <summary>
/// Manual Paper Trading settings and execution logs. Backed by manual_paper_trade_settings and
/// manual_paper_trade_execution_logs (schema.sql) via the fn_*manual_paper_trade* functions (functions.sql).
/// </summary>
public class ManualPaperTradeRepository : IManualPaperTradeRepository
{
    private readonly IDbConnectionFactory _connectionFactory;

    public ManualPaperTradeRepository(IDbConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
    }

    public async Task<ManualPaperTradeSettings> GetSettingsAsync(int userId = 1)
    {
        using var connection = _connectionFactory.CreateConnection();
        string sql = "SELECT * FROM fn_get_manual_paper_trade_settings(@userId);";

        var settings = await connection.QueryFirstOrDefaultAsync<ManualPaperTradeSettings>(sql, new { userId });
        if (settings == null)
        {
            settings = new ManualPaperTradeSettings { UserId = userId };
            return await UpsertSettingsAsync(settings);
        }
        return settings;
    }

    public async Task<ManualPaperTradeSettings> UpsertSettingsAsync(ManualPaperTradeSettings settings)
    {
        using var connection = _connectionFactory.CreateConnection();
        string sql = @"
            SELECT * FROM fn_upsert_manual_paper_trade_settings(
                @UserId,
                @IsManualTradeEnabled,
                @AvailableCapital,
                @ProfitTargetPct,
                @StopLossPct,
                @TrailingSlPct,
                @MaxDurationDays,
                @MaxTradesPerDay,
                @FixedAmountPerTrade,
                @TradingWindowStart,
                @TradingWindowEnd,
                @EntryDelayMinutes,
                @MaxDailyLossLimit,
                @ExitMode,
                @CloseCheckTime,
                @StopLossAtrMult,
                @TrailAtrMult,
                @TargetAtrMult
            );";

        return await connection.QuerySingleAsync<ManualPaperTradeSettings>(sql, settings);
    }

    public async Task ToggleManualTradeAsync(int userId, bool enabled)
    {
        using var connection = _connectionFactory.CreateConnection();
        string sql = "SELECT fn_toggle_manual_paper_trade(@userId, @enabled);";

        await connection.ExecuteAsync(sql, new { userId, enabled });
    }

    public async Task<int> GetTodayManualTradeCountAsync(int userId = 1)
    {
        using var connection = _connectionFactory.CreateConnection();
        DateTime todayStartUtc = DateTime.UtcNow.Date;

        string sql = "SELECT fn_get_today_manual_paper_trade_count(@userId, @todayStartUtc);";

        return await connection.ExecuteScalarAsync<int>(sql, new { userId, todayStartUtc });
    }

    public async Task LogExecutionAsync(ManualPaperTradeExecutionLog log)
    {
        using var connection = _connectionFactory.CreateConnection();
        string sql = "SELECT fn_log_manual_paper_trade_execution(@UserId, @Symbol, @ActionType, @Price, @Quantity, @Reason);";

        await connection.ExecuteAsync(sql, log);
    }

    public async Task<IEnumerable<ManualPaperTradeExecutionLog>> GetTodayLogsAsync(int userId = 1, int limit = 50)
    {
        using var connection = _connectionFactory.CreateConnection();
        DateTime todayStartUtc = DateTime.UtcNow.Date;

        string sql = "SELECT * FROM fn_get_today_manual_paper_trade_logs(@userId, @todayStartUtc, @limit);";

        return await connection.QueryAsync<ManualPaperTradeExecutionLog>(sql, new { userId, todayStartUtc, limit });
    }

    public async Task<ManualPaperDashboardDto> GetDashboardAsync(int accountId, DateTime todayStartUtc)
    {
        using var connection = _connectionFactory.CreateConnection();
        string sql = "SELECT * FROM fn_get_manual_paper_dashboard(@accountId, @todayStartUtc);";

        return await connection.QueryFirstOrDefaultAsync<ManualPaperDashboardDto>(sql, new { accountId, todayStartUtc })
            ?? new ManualPaperDashboardDto();
    }

    public async Task<IEnumerable<PaperPosition>> GetOpenPositionsAsync(int accountId)
    {
        using var connection = _connectionFactory.CreateConnection();
        string sql = "SELECT * FROM fn_get_manual_paper_open_positions(@accountId);";

        return await connection.QueryAsync<PaperPosition>(sql, new { accountId });
    }

    public async Task<(IEnumerable<PaperOrder> Items, int TotalCount)> GetOrdersPagedAsync(int accountId, ManualPaperOrderFilterDto filter)
    {
        using var connection = _connectionFactory.CreateConnection();

        int page = filter.Page < 1 ? 1 : filter.Page;
        int pageSize = filter.PageSize <= 0 ? 10 : filter.PageSize;

        string sql = @"
            SELECT * FROM fn_get_manual_paper_orders_paged(
                @AccountId, @Symbol, @Side, @Status, @FromDate, @ToDate, @PageSize, @Offset
            );";

        var rows = (await connection.QueryAsync<PaperOrderPagedRaw>(sql, new
        {
            AccountId = accountId,
            Symbol = string.IsNullOrWhiteSpace(filter.Symbol) ? null : filter.Symbol.Trim(),
            Side = filter.Side.HasValue ? (int?)filter.Side.Value : null,
            Status = filter.Status.HasValue ? (int?)filter.Status.Value : null,
            filter.FromDate,
            filter.ToDate,
            PageSize = pageSize,
            Offset = (page - 1) * pageSize
        })).ToList();

        return rows.Count == 0 ? (Enumerable.Empty<PaperOrder>(), 0) : (rows, (int)rows[0].TotalCount);
    }

    public async Task<(IEnumerable<PaperTradeHistory> Items, int TotalCount)> GetTradeHistoryPagedAsync(int accountId, PaperTradeHistoryFilterDto filter)
    {
        using var connection = _connectionFactory.CreateConnection();

        int page = filter.Page < 1 ? 1 : filter.Page;
        int pageSize = filter.PageSize <= 0 ? 10 : filter.PageSize;

        string sql = @"
            SELECT * FROM fn_get_manual_paper_trade_history_paged(
                @AccountId, @Symbol, @Side, @FromDate, @ToDate, @PageSize, @Offset
            );";

        var rows = (await connection.QueryAsync<PaperTradeHistoryPagedRaw>(sql, new
        {
            AccountId = accountId,
            Symbol = string.IsNullOrWhiteSpace(filter.Symbol) ? null : filter.Symbol.Trim(),
            Side = filter.Side.HasValue ? (int?)filter.Side.Value : null,
            filter.FromDate,
            filter.ToDate,
            PageSize = pageSize,
            Offset = (page - 1) * pageSize
        })).ToList();

        return rows.Count == 0 ? (Enumerable.Empty<PaperTradeHistory>(), 0) : (rows, (int)rows[0].TotalCount);
    }

    private class PaperOrderPagedRaw : PaperOrder
    {
        public long TotalCount { get; set; }
    }

    private class PaperTradeHistoryPagedRaw : PaperTradeHistory
    {
        public long TotalCount { get; set; }
    }
}
