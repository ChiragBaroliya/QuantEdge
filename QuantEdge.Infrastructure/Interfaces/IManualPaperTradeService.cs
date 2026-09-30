using System.Collections.Generic;
using System.Threading.Tasks;
using QuantEdge.Domain.Entities;
using QuantEdge.Infrastructure.DTOs;

namespace QuantEdge.Infrastructure.Interfaces;

/// <summary>
/// Manual Paper Trading - fully manual (Buy / Close / Edit levels from the page, no Worker job). Fully separate from
/// <see cref="IAutoTradeService"/>: its own settings, execution logs, orders, positions and trade history
/// (manual_paper_* tables), so Auto Paper / Auto Real Trading are not affected.
/// </summary>
public interface IManualPaperTradeService
{
    Task<ManualPaperTradeSettings> GetSettingsAsync(int userId = 1);
    Task<ManualPaperTradeSettings> UpdateSettingsAsync(ManualPaperTradeSettingsUpdateDto updateDto, int userId = 1);
    Task ToggleManualTradeAsync(bool enabled, int userId = 1);
    Task<int> GetTodayTradeCountAsync(int userId = 1);
    Task<IEnumerable<ManualPaperTradeExecutionLog>> GetTodayLogsAsync(int userId = 1, int limit = 50);

    /// <summary>Order ticket price - latest stored 1-minute close from Postgres (no Zerodha call); null if none.</summary>
    Task<ManualPaperPriceDto?> GetQuoteAsync(string symbol);

    /// <summary>Manual Trading page stat cards - Manual paper figures with unrealized P&amp;L at stored prices.</summary>
    Task<ManualPaperDashboardDto> GetDashboardAsync(int userId = 1);

    /// <summary>Manual Trading page - OPEN Manual paper positions with live LTP / unrealized P&amp;L.</summary>
    Task<IEnumerable<PaperPosition>> GetOpenPositionsAsync(int userId = 1);

    /// <summary>Manual Trading page - paged, filtered Manual paper orders.</summary>
    Task<PagedResultDto<PaperOrder>> GetOrdersPagedAsync(ManualPaperOrderFilterDto filter, int userId = 1);

    /// <summary>Manual Trading page - paged, filtered Manual paper trade execution history.</summary>
    Task<PagedResultDto<PaperTradeHistory>> GetTradeHistoryPagedAsync(PaperTradeHistoryFilterDto filter, int userId = 1);

    /// <summary>Reset Capital on the Manual Trading page - clears only manual orders, positions, history and logs.</summary>
    Task ResetAsync(int userId = 1);

    /// <summary>
    /// Places a manual paper BUY after the same entry gates as Manual Real Trade, with user Quantity and
    /// mandatory trade-wise Stop Loss % / Trailing Stop Loss %. Returns whether it executed and why not.
    /// </summary>
    Task<(bool Success, string Message)> ExecuteManualBuyAsync(string symbol, decimal entryPrice, int quantity,
        decimal stopLossPct, decimal trailingSlPct, int userId = 1);

    /// <summary>
    /// Edit button on Live Open Positions - changes an OPEN manual position's Stop Loss, Trailing SL % and
    /// Target (reference levels only; the position is still sold only with Close).
    /// </summary>
    Task<(bool Success, string Message)> UpdatePositionLevelsAsync(int positionId, decimal stopLoss, decimal trailingSlPct,
        decimal takeProfit, int userId = 1);

    /// <summary>
    /// Close button on the Manual Trading page - sells an OPEN manual position at the latest price.
    /// This is the only way a manual paper position is sold (no background exit job).
    /// </summary>
    Task<(bool Success, string Message)> ClosePositionAsync(int positionId, int userId = 1);
}
