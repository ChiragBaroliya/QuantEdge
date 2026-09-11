using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using QuantEdge.Domain.Entities;

namespace QuantEdge.Infrastructure.Persistence.Repositories;

public interface IRealTradingRepository
{
    // Settings
    Task<RealTradeSettings> GetSettingsAsync(int userId = 1);
    Task<IEnumerable<RealTradeSettings>> GetActiveSettingsAsync();
    Task<RealTradeSettings> UpsertSettingsAsync(RealTradeSettings settings);
    Task ToggleRealTradeAsync(int userId, bool enabled);

    // Orders
    Task<RealOrder> CreateOrderAsync(RealOrder order);
    Task<RealOrder?> GetOrderByIdAsync(int orderId);
    Task<RealOrder?> GetOrderByBrokerOrderIdAsync(string brokerOrderId);
    Task UpdateOrderStatusAsync(int orderId, PaperOrderStatus status, decimal filledPrice, string? brokerOrderId = null, string? rejectionReason = null);
    Task<IEnumerable<RealOrder>> GetRecentOrdersAsync(int userId = 1, int limit = 50);

    /// <summary>
    /// Returns the most recent still-open (broker-accepted but unfilled) order for a symbol/side, if any.
    /// Used to avoid placing a duplicate exit order while a previous one is still resting at the broker.
    /// </summary>
    Task<RealOrder?> GetOpenBrokerOrderAsync(int userId, string symbol, TradeSide side);

    /// <summary>
    /// Returns every order (across all users) still awaiting broker fill confirmation, for periodic reconciliation.
    /// </summary>
    Task<IEnumerable<RealOrder>> GetAllPendingBrokerOrdersAsync();

    // Positions
    Task<RealPosition> UpsertPositionAsync(RealPosition position);
    Task<RealPosition?> GetOpenPositionByIdAsync(int positionId);
    Task<RealPosition?> GetOpenPositionBySymbolAsync(int userId, string symbol);
    Task<IEnumerable<RealPosition>> GetOpenPositionsAsync(int userId = 1);
    Task<IEnumerable<RealPosition>> GetAllOpenPositionsAsync();
    Task ClosePositionAsync(int positionId, decimal exitPrice, decimal realizedPnl, string exitReason);
    Task UpdateTrailingStopLossAsync(int positionId, decimal newTrailingStopLoss);

    // Trade History & Logs
    Task<RealTradeHistory> RecordTradeHistoryAsync(RealTradeHistory history);
    Task<IEnumerable<RealTradeHistory>> GetTradeHistoryAsync(int userId = 1, int limit = 100);
    Task<int> GetTodayRealTradeCountAsync(int userId = 1);
    Task<decimal> GetTodayRealizedPnlAsync(int userId = 1);
    Task LogExecutionAsync(RealTradeExecutionLog log);
    Task<IEnumerable<RealTradeExecutionLog>> GetTodayLogsAsync(int userId = 1, int limit = 50);
}
