using System.Collections.Generic;
using System.Threading.Tasks;
using QuantEdge.Domain.Entities;

namespace QuantEdge.Infrastructure.Interfaces;

public interface IZerodhaKiteBrokerService
{
    string Mode { get; }

    /// <summary>
    /// Validates if the active Zerodha session token in the database is valid for today (post 6 AM IST).
    /// </summary>
    Task<(bool IsValid, string? AccessToken, string? ApiKey, string? Message)> ValidateSessionTokenAsync();

    /// <summary>
    /// Places a real-money live order via Zerodha Kite Connect REST API (POST /orders/regular).
    /// </summary>
    Task<(bool Success, string? BrokerOrderId, decimal ExecutedPrice, string? Message)> PlaceLiveOrderAsync(
        string symbol, 
        TradeSide side, 
        int quantity, 
        PaperOrderType orderType, 
        decimal price, 
        string product = "CNC", 
        int userId = 1);

    /// <summary>
    /// Cancels an open or pending order with Zerodha broker (DELETE /orders/regular/{order_id}).
    /// </summary>
    Task<(bool Success, string? Message)> CancelLiveOrderAsync(string brokerOrderId, int userId = 1);

    /// <summary>
    /// Squares off an open position with Zerodha broker by placing an opposing market order.
    /// </summary>
    Task<(bool Success, string? BrokerOrderId, decimal ExecutedPrice, string? Message)> SquareOffLivePositionAsync(
        string symbol, 
        int quantity, 
        TradeSide positionSide, 
        string product = "CNC", 
        int userId = 1);

    /// <summary>
    /// Retrieves live available and used equity margins directly from Zerodha Kite Connect (GET /user/margins/equity).
    /// </summary>
    Task<(bool Success, decimal AvailableCash, decimal UsedMargin, string? Message)> GetEquityMarginsAsync(int userId = 1);
}
