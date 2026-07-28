using System.Collections.Generic;
using System.Threading.Tasks;
using QuantEdge.Domain.Entities;
using QuantEdge.Infrastructure.DTOs;

namespace QuantEdge.Infrastructure.Interfaces;

public interface IPaperTradingService
{
    Task<PaperPortfolioDto> GetPortfolioAsync(string userId = "default_user");
    Task<PaperOrder> PlaceOrderAsync(PlacePaperOrderDto dto, string userId = "default_user", decimal currentLtp = 0m);
    Task CancelOrderAsync(int orderId, string userId = "default_user");
    Task ClosePositionAsync(int positionId, decimal currentLtp = 0m, string userId = "default_user");
    Task ResetAccountAsync(string userId = "default_user");
    Task ProcessTickForPaperMatchingAsync(string symbol, decimal ltp);
    Task ProcessSignalForAutoTradeAsync(TradingSignal signal);
    Task SetAutoTradeStatusAsync(bool enabled);
    bool GetAutoTradeStatus();
    Task<IEnumerable<PaperPosition>> GetOpenPositionsAsync(string userId = "default_user");
    Task<IEnumerable<PaperOrder>> GetOrdersAsync(string userId = "default_user", bool activeOnly = false);
    Task<IEnumerable<PaperTradeHistory>> GetTradeHistoryAsync(string userId = "default_user", int limit = 50);
}
