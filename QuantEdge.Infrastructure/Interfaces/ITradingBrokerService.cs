using System.Threading.Tasks;
using QuantEdge.Domain.Entities;
using QuantEdge.Infrastructure.DTOs;

namespace QuantEdge.Infrastructure.Interfaces;

/// <summary>
/// Universal interface for trade execution supporting both Paper Trading simulation
/// and Real-Money Live Trading via Zerodha KiteConnect API.
/// </summary>
public interface ITradingBrokerService
{
    string Mode { get; } // "Paper" or "Live"
    Task<PaperOrder> PlaceOrderAsync(PlacePaperOrderDto dto, string userId = "default_user", decimal currentLtp = 0m);
    Task CancelOrderAsync(int orderId, string userId = "default_user");
    Task ClosePositionAsync(int positionId, decimal currentLtp = 0m, string userId = "default_user");
}
