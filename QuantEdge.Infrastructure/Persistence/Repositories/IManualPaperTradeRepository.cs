using System.Collections.Generic;
using System.Threading.Tasks;
using QuantEdge.Domain.Entities;
using QuantEdge.Infrastructure.DTOs;

namespace QuantEdge.Infrastructure.Persistence.Repositories;

/// <summary>
/// Manual Paper Trading data - settings, execution logs and its own orders / positions / trade history
/// (manual_paper_* tables). Nothing else in the system (paper matching engine, Auto Paper, Auto Real) reads or
/// writes these tables; the only Worker job is ManualShortSquareOffWorker (buys back open shorts at day end).
/// </summary>
public interface IManualPaperTradeRepository
{
    Task<ManualPaperTradeSettings> GetSettingsAsync(int userId = 1);
    Task<ManualPaperTradeSettings> UpsertSettingsAsync(ManualPaperTradeSettings settings);
    Task ToggleManualTradeAsync(int userId, bool enabled);
    Task<int> GetTodayManualTradeCountAsync(int userId = 1);
    Task LogExecutionAsync(ManualPaperTradeExecutionLog log);
    Task<IEnumerable<ManualPaperTradeExecutionLog>> GetTodayLogsAsync(int userId = 1, int limit = 50);

    /// <summary>Manual entry - BUY (long) or SELL (short) - order + OPEN position + history (fn_open_manual_paper_position).
    /// Returns the new position id, or null when an OPEN position already exists for the symbol.</summary>
    Task<int?> CreateEntryAsync(int userId, TradeSide side, string symbol, int quantity, decimal price, decimal stopLoss,
        decimal trailingSlPct, decimal takeProfit, string orderRemarks, string historyRemarks, bool autoExit = false);

    /// <summary>Pending limit entry (fn_place_manual_paper_limit_order). Returns the order id, or null when an OPEN
    /// position or a Pending order already exists for the symbol.</summary>
    Task<int?> PlaceLimitOrderAsync(int userId, TradeSide side, string symbol, int quantity, decimal limitPrice,
        decimal stopLossPct, decimal trailingSlPct, decimal estStopLoss, decimal estTakeProfit, string remarks, bool autoExit = false);

    /// <summary>Every user's OPEN Auto Exit positions with their trail state (fn_get_manual_paper_auto_exit_positions).</summary>
    Task<IEnumerable<ManualPaperAutoExitPositionDto>> GetAutoExitPositionsAsync();

    /// <summary>Stored 1-minute candles of a symbol with fromUtc &lt;= start &lt;= toUtc, oldest first (fn_get_manual_paper_candles).</summary>
    Task<IEnumerable<ManualPaperCandleDto>> GetCandlesAsync(string symbol, DateTime fromUtc, DateTime toUtc);

    /// <summary>Saves an auto-exit position's trail state (fn_update_manual_paper_position_trail); skipped when the
    /// position closed or its Trailing SL % changed since trailingSlPct was read.</summary>
    Task<bool> UpdatePositionTrailAsync(int positionId, decimal? trailingSlPct, decimal? bestPrice, decimal? trailingStopLoss, DateTime checkedUntilUtc);

    /// <summary>Pending limit orders with their first price-crossing stored candle - one user's, or every user's
    /// when userId is null (fn_get_manual_paper_pending_orders).</summary>
    Task<IEnumerable<ManualPaperPendingOrderDto>> GetPendingOrdersAsync(int? userId = null);

    /// <summary>Fills a Pending limit order into an OPEN position (fn_fill_manual_paper_limit_order). Returns the
    /// position id, or null when it is no longer Pending or the symbol already has an OPEN position.</summary>
    Task<int?> FillLimitOrderAsync(int orderId, decimal fillPrice, decimal stopLoss, decimal trailingSlPct, decimal takeProfit,
        string historyRemarks);

    /// <summary>Cancels / expires one Pending order (fn_cancel_manual_paper_order). True when it was Pending.</summary>
    Task<bool> CancelOrderAsync(int userId, int orderId, string reason);

    /// <summary>Every user's OPEN short positions (AccountId = user id) - fn_get_manual_paper_open_shorts. Used by the
    /// short auto square-off job.</summary>
    Task<IEnumerable<PaperPosition>> GetAllOpenShortPositionsAsync();

    /// <summary>Close (position closed + exit order + history): SELL for a long, BUY to cover for a short -
    /// fn_close_manual_paper_position.</summary>
    Task<ManualPaperCloseResult> ClosePositionAsync(int userId, int positionId, decimal exitPrice, string exitReason);

    /// <summary>Edit an OPEN position's SL / Trailing SL % / Target and Auto Exit (null = unchanged) -
    /// fn_update_manual_paper_position_levels.</summary>
    Task<bool> UpdatePositionLevelsAsync(int userId, int positionId, decimal stopLoss, decimal trailingSlPct, decimal takeProfit,
        bool? autoExit = null);

    /// <summary>OPEN manual positions - fn_get_manual_paper_open_positions.</summary>
    Task<IEnumerable<ManualPaperPosition>> GetOpenPositionsAsync(int userId);

    /// <summary>Dashboard figures - fn_get_manual_paper_dashboard.</summary>
    Task<ManualPaperDashboardDto> GetDashboardAsync(int userId, System.DateTime todayStartUtc);

    /// <summary>Paged manual orders - fn_get_manual_paper_orders_paged.</summary>
    Task<(IEnumerable<PaperOrder> Items, int TotalCount)> GetOrdersPagedAsync(int userId, ManualPaperOrderFilterDto filter);

    /// <summary>Paged manual trade history - fn_get_manual_paper_trade_history_paged.</summary>
    Task<(IEnumerable<PaperTradeHistory> Items, int TotalCount)> GetTradeHistoryPagedAsync(int userId, PaperTradeHistoryFilterDto filter);

    /// <summary>Latest stored 1-minute close per symbol - fn_get_manual_paper_latest_prices (no Zerodha call).</summary>
    Task<IEnumerable<ManualPaperPriceDto>> GetLatestPricesAsync(IEnumerable<string> symbols);

    /// <summary>Reset Capital - deletes the user's manual orders, positions, history and logs.</summary>
    Task ResetAsync(int userId);
}
