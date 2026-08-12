using System.Collections.Generic;
using System.Threading.Tasks;
using QuantEdge.Domain.Entities;

namespace QuantEdge.Infrastructure.Persistence.Repositories;

public interface IPaperTradingRepository
{
    Task<PaperAccount?> GetAccountAsync(string userId = "default_user");
    Task<PaperAccount> CreateAccountAsync(string userId = "default_user", string accountName = "Virtual Trading Account", decimal initialBalance = 100000m);
    Task UpdateAccountBalanceAndMarginAsync(int accountId, decimal newBalance, decimal usedMargin, decimal realizedPnl);
    
    Task<PaperOrder> CreateOrderAsync(PaperOrder order);
    Task<PaperOrder?> GetOrderByIdAsync(int orderId);
    Task UpdateOrderStatusAsync(int orderId, PaperOrderStatus status, decimal? filledPrice = null, string? remarks = null);
    Task<IEnumerable<PaperOrder>> GetOrdersAsync(int accountId, bool activeOnly = false);
    
    Task<PaperPosition?> GetOpenPositionBySymbolAsync(int accountId, string symbol);
    Task<PaperPosition> UpsertPositionAsync(PaperPosition position);
    Task<bool> ClosePositionAsync(int positionId, decimal exitPrice, decimal realizedPnl, string? exitReason = null);

    Task<IEnumerable<PaperPosition>> GetPositionsAsync(int accountId, bool openOnly = true);
    
    Task RecordTradeHistoryAsync(PaperTradeHistory history);
    Task<IEnumerable<PaperTradeHistory>> GetTradeHistoryAsync(int accountId, int limit = 50);
    Task<(IEnumerable<PaperTradeHistory> Items, int TotalCount)> GetTradeHistoryPagedAsync(int accountId, QuantEdge.Infrastructure.DTOs.PaperTradeHistoryFilterDto filter);
    
    Task UpdateAutoTradeSettingsAsync(int accountId, QuantEdge.Infrastructure.DTOs.AutoTradeSettingsDto settings);
    Task ResetAccountAsync(int accountId, decimal defaultBalance = 100000m);
}
