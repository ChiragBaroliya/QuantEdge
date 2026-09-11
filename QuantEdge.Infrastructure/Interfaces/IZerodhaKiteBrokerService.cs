using System.Collections.Generic;
using System.Threading.Tasks;
using QuantEdge.Domain.Entities;
using QuantEdge.Infrastructure.DTOs;

namespace QuantEdge.Infrastructure.Interfaces;

public interface IZerodhaKiteBrokerService
{
    string Mode { get; }

    /// <summary>
    /// Validates if the active Zerodha session token for a specific user is valid for today (post 6 AM IST).
    /// </summary>
    Task<(bool IsValid, string? AccessToken, string? ApiKey, string? Message)> ValidateSessionTokenAsync(int userId = 1);

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
    /// Confirms the broker-side execution status of a previously placed order (GET /orders/{order_id}).
    /// Placing an order only means Kite *accepted* it for the exchange — this call is required to know
    /// whether it has actually traded (COMPLETE), is still resting (OPEN/TRIGGER PENDING), or was
    /// CANCELLED/REJECTED. Never assume "placed" means "filled".
    /// </summary>
    Task<(bool Success, string? BrokerStatus, decimal AveragePrice, int FilledQuantity, string? Message)> GetOrderStatusAsync(string brokerOrderId, int userId = 1);

    /// <summary>
    /// Squares off an open position with Zerodha broker by placing an opposing market-protected order.
    /// </summary>
    Task<(bool Success, string? BrokerOrderId, decimal ExecutedPrice, string? Message)> SquareOffLivePositionAsync(
        string symbol,
        int quantity,
        TradeSide positionSide,
        decimal currentPrice,
        string product = "CNC",
        int userId = 1);

    /// <summary>
    /// Retrieves live available and used equity margins directly from Zerodha Kite Connect (GET /user/margins/equity).
    /// </summary>
    Task<(bool Success, decimal AvailableCash, decimal UsedMargin, string? Message)> GetEquityMarginsAsync(int userId = 1);

    /// <summary>
    /// Retrieves live day & net open positions and real-time P&L directly from Zerodha (GET /portfolio/positions).
    /// </summary>
    Task<(bool Success, ZerodhaPositionsDto? Positions, string? Message)> GetLivePositionsAsync(int userId = 1);

    /// <summary>
    /// Retrieves active demat equity holdings and long-term P&L directly from Zerodha (GET /portfolio/holdings).
    /// </summary>
    Task<(bool Success, List<ZerodhaHoldingDto>? Holdings, string? Message)> GetLiveHoldingsAsync(int userId = 1);

    /// <summary>
    /// Retrieves live last-traded price for a batch of instruments directly from Zerodha (GET /quote/ltp).
    /// Returned dictionary is keyed by trading symbol (case-insensitive).
    /// </summary>
    Task<(bool Success, Dictionary<string, decimal>? Ltps, string? Message)> GetLtpQuotesAsync(
        IEnumerable<(string Symbol, string Exchange)> instruments, int userId = 1);
}
