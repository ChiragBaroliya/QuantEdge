using System.Collections.Generic;
using System.Threading.Tasks;
using QuantEdge.Domain.Entities;
using QuantEdge.Infrastructure.DTOs;

namespace QuantEdge.Infrastructure.Persistence.Repositories;

public interface IManualPaperTradeRepository
{
    Task<ManualPaperTradeSettings> GetSettingsAsync(int userId = 1);
    Task<ManualPaperTradeSettings> UpsertSettingsAsync(ManualPaperTradeSettings settings);
    Task ToggleManualTradeAsync(int userId, bool enabled);
    Task<int> GetTodayManualTradeCountAsync(int userId = 1);
    Task LogExecutionAsync(ManualPaperTradeExecutionLog log);
    Task<IEnumerable<ManualPaperTradeExecutionLog>> GetTodayLogsAsync(int userId = 1, int limit = 50);

    /// <summary>Paged Manual (trade_type = 0) paper orders - fn_get_manual_paper_orders_paged.</summary>
    Task<(IEnumerable<PaperOrder> Items, int TotalCount)> GetOrdersPagedAsync(int accountId, ManualPaperOrderFilterDto filter);

    /// <summary>Manual (trade_type = 0) dashboard figures - fn_get_manual_paper_dashboard.</summary>
    Task<ManualPaperDashboardDto> GetDashboardAsync(int accountId, System.DateTime todayStartUtc);

    /// <summary>OPEN Manual (trade_type = 0) paper positions - fn_get_manual_paper_open_positions.</summary>
    Task<IEnumerable<PaperPosition>> GetOpenPositionsAsync(int accountId);

    /// <summary>Paged Manual (trade_type = 0) paper trade history - fn_get_manual_paper_trade_history_paged.</summary>
    Task<(IEnumerable<PaperTradeHistory> Items, int TotalCount)> GetTradeHistoryPagedAsync(int accountId, PaperTradeHistoryFilterDto filter);
}
