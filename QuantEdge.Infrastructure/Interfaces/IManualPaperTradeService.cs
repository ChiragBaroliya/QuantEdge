using System.Collections.Generic;
using System.Threading.Tasks;
using QuantEdge.Domain.Entities;
using QuantEdge.Infrastructure.DTOs;

namespace QuantEdge.Infrastructure.Interfaces;

/// <summary>
/// Manual Paper Trading - the paper counterpart of Manual Real Trade. Fully separate from
/// <see cref="IAutoTradeService"/>: its own settings (manual_paper_trade_settings) and execution logs
/// (manual_paper_trade_execution_logs), so Auto Paper Trading behaviour is not affected.
/// </summary>
public interface IManualPaperTradeService
{
    Task<ManualPaperTradeSettings> GetSettingsAsync(int userId = 1);
    Task<ManualPaperTradeSettings> UpdateSettingsAsync(ManualPaperTradeSettingsUpdateDto updateDto, int userId = 1);
    Task ToggleManualTradeAsync(bool enabled, int userId = 1);
    Task<int> GetTodayTradeCountAsync(int userId = 1);
    Task<IEnumerable<ManualPaperTradeExecutionLog>> GetTodayLogsAsync(int userId = 1, int limit = 50);

    /// <summary>Manual Trading page stat cards - Manual paper figures with live unrealized P&amp;L.</summary>
    Task<ManualPaperDashboardDto> GetDashboardAsync(int userId = 1);

    /// <summary>Manual Trading page - OPEN Manual paper positions with live LTP / unrealized P&amp;L.</summary>
    Task<IEnumerable<PaperPosition>> GetOpenPositionsAsync();

    /// <summary>Manual Trading page - paged, filtered Manual paper orders.</summary>
    Task<PagedResultDto<PaperOrder>> GetOrdersPagedAsync(ManualPaperOrderFilterDto filter);

    /// <summary>Manual Trading page - paged, filtered Manual paper trade execution history.</summary>
    Task<PagedResultDto<PaperTradeHistory>> GetTradeHistoryPagedAsync(PaperTradeHistoryFilterDto filter);

    /// <summary>
    /// Places a manual paper BUY after the same entry gates as Manual Real Trade, with user Quantity and
    /// mandatory trade-wise Stop Loss % / Trailing Stop Loss %. Returns whether it executed and why not.
    /// </summary>
    Task<(bool Success, string Message)> ExecuteManualBuyAsync(string symbol, decimal entryPrice, int quantity,
        decimal stopLossPct, decimal trailingSlPct, int userId = 1);

    /// <summary>
    /// Close button on the Manual Trading page - sells an OPEN manual position at the latest price.
    /// This is the only way a manual paper position is sold (no background exit job).
    /// </summary>
    Task<(bool Success, string Message)> ClosePositionAsync(int positionId, int userId = 1);
}
