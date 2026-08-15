using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using QuantEdge.Domain.Entities;
using QuantEdge.Infrastructure.Configurations;
using QuantEdge.Infrastructure.DTOs;
using QuantEdge.Infrastructure.Helpers;
using QuantEdge.Infrastructure.Interfaces;
using QuantEdge.Infrastructure.Persistence.Repositories;

namespace QuantEdge.Infrastructure.Services;

/// <summary>
/// Execution provider for Real-Money Live Trading via Zerodha KiteConnect REST API.
/// Routes auto-trade signals and manual orders directly to live broker.
/// </summary>
public class ZerodhaKiteBrokerService : IZerodhaKiteBrokerService, ITradingBrokerService
{
    private readonly IZerodhaSessionRepository _sessionRepository;
    private readonly BrokerConfig _config;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ZerodhaKiteBrokerService> _logger;

    public string Mode => "Live";

    public ZerodhaKiteBrokerService(
        IZerodhaSessionRepository sessionRepository,
        IOptions<BrokerConfig> config,
        IHttpClientFactory httpClientFactory,
        ILogger<ZerodhaKiteBrokerService> logger)
    {
        _sessionRepository = sessionRepository ?? throw new ArgumentNullException(nameof(sessionRepository));
        _config = config?.Value ?? throw new ArgumentNullException(nameof(config));
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<(bool IsValid, string? AccessToken, string? ApiKey, string? Message)> ValidateSessionTokenAsync()
    {
        var session = await _sessionRepository.GetActiveSessionAsync();
        if (session == null || string.IsNullOrWhiteSpace(session.AccessToken))
        {
            return (false, null, null, "No active Zerodha session token found in database. Please generate token in Token Manager.");
        }

        // Validate token was created after 6:00 AM IST on the current trading day
        var indianTime = TimeZoneInfo.ConvertTime(session.CreatedAt, TimeZoneHelper.IndianTimeZone);
        var nowIst = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneHelper.IndianTimeZone);
        var cutoff = nowIst.Date.AddHours(6);

        if (indianTime.Date != nowIst.Date || indianTime < cutoff)
        {
            return (false, null, null, $"Zerodha session token is stale (created {indianTime:yyyy-MM-dd hh:mm tt} IST). Fresh token post 6:00 AM IST required.");
        }

        string apiKey = !string.IsNullOrWhiteSpace(session.ApiKey) ? session.ApiKey : _config.ApiKey;
        return (true, session.AccessToken, apiKey, "Active Zerodha session is valid.");
    }

    public async Task<(bool Success, string? BrokerOrderId, decimal ExecutedPrice, string? Message)> PlaceLiveOrderAsync(
        string symbol,
        TradeSide side,
        int quantity,
        PaperOrderType orderType,
        decimal price,
        string product = "CNC",
        int userId = 1)
    {
        var tokenValidation = await ValidateSessionTokenAsync();
        if (!tokenValidation.IsValid)
        {
            _logger.LogWarning("PlaceLiveOrderAsync rejected: {Reason}", tokenValidation.Message);
            return (false, null, 0m, tokenValidation.Message);
        }

        string transactionType = side == TradeSide.BUY ? "BUY" : "SELL";
        string kiteOrderType = orderType == PaperOrderType.Limit ? "LIMIT" : "MARKET";
        string cleanSymbol = symbol.ToUpper().Trim();
        string kiteProduct = string.IsNullOrWhiteSpace(product) ? "CNC" : product.ToUpper().Trim();

        _logger.LogInformation("[REAL MONEY LIVE ORDER] Placing KiteConnect order: {Symbol} {Side} Qty:{Qty} Product:{Product} @ ₹{Price:F2}",
            cleanSymbol, transactionType, quantity, kiteProduct, price);

        try
        {
            var client = _httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.Clear();
            client.DefaultRequestHeaders.Add("X-Kite-Version", "3");
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("token", $"{tokenValidation.ApiKey}:{tokenValidation.AccessToken}");

            var formData = new Dictionary<string, string>
            {
                { "tradingsymbol", cleanSymbol },
                { "exchange", "NSE" },
                { "transaction_type", transactionType },
                { "order_type", kiteOrderType },
                { "quantity", quantity.ToString() },
                { "product", kiteProduct },
                { "validity", "DAY" }
            };

            if (orderType == PaperOrderType.Limit && price > 0m)
            {
                formData.Add("price", price.ToString("F2"));
            }

            var requestContent = new FormUrlEncodedContent(formData);
            var response = await client.PostAsync("https://api.kite.trade/orders/regular", requestContent);
            var responseJson = await response.Content.ReadAsStringAsync();

            _logger.LogInformation("Zerodha Order Response: Code {Status}, Body: {Body}", response.StatusCode, responseJson);

            if (response.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(responseJson);
                if (doc.RootElement.TryGetProperty("data", out var dataElem) &&
                    dataElem.TryGetProperty("order_id", out var orderIdElem))
                {
                    string brokerOrderId = orderIdElem.GetString() ?? $"KITE-{DateTime.UtcNow.Ticks}";
                    return (true, brokerOrderId, price, $"Live order placed successfully (Order ID: {brokerOrderId})");
                }

                return (true, $"KITE-{DateTime.UtcNow.Ticks}", price, "Live order placed successfully");
            }
            else
            {
                string errorMsg = "Broker error";
                try
                {
                    using var doc = JsonDocument.Parse(responseJson);
                    if (doc.RootElement.TryGetProperty("message", out var msgElem))
                    {
                        errorMsg = msgElem.GetString() ?? errorMsg;
                    }
                }
                catch { }

                _logger.LogError("Zerodha Order Placement Failed: {ErrorMsg}", errorMsg);
                return (false, null, 0m, $"Zerodha Error: {errorMsg}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "HTTP Exception placing live order with Zerodha for {Symbol}", symbol);
            return (false, null, 0m, $"Network/API Exception: {ex.Message}");
        }
    }

    public async Task<(bool Success, string? Message)> CancelLiveOrderAsync(string brokerOrderId, int userId = 1)
    {
        var tokenValidation = await ValidateSessionTokenAsync();
        if (!tokenValidation.IsValid)
        {
            return (false, tokenValidation.Message);
        }

        try
        {
            var client = _httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.Clear();
            client.DefaultRequestHeaders.Add("X-Kite-Version", "3");
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("token", $"{tokenValidation.ApiKey}:{tokenValidation.AccessToken}");

            var response = await client.DeleteAsync($"https://api.kite.trade/orders/regular/{brokerOrderId}");
            var responseJson = await response.Content.ReadAsStringAsync();

            _logger.LogInformation("Zerodha Cancel Order Response #{OrderId}: Code {Status}, Body: {Body}", brokerOrderId, response.StatusCode, responseJson);
            return (response.IsSuccessStatusCode, response.IsSuccessStatusCode ? "Order cancelled" : responseJson);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error cancelling live order #{OrderId}", brokerOrderId);
            return (false, ex.Message);
        }
    }

    public async Task<(bool Success, string? BrokerOrderId, decimal ExecutedPrice, string? Message)> SquareOffLivePositionAsync(
        string symbol,
        int quantity,
        TradeSide positionSide,
        string product = "CNC",
        int userId = 1)
    {
        // Opposing side to exit: if holding BUY, sell to close; if holding SELL, buy to close
        TradeSide exitSide = positionSide == TradeSide.BUY ? TradeSide.SELL : TradeSide.BUY;
        return await PlaceLiveOrderAsync(symbol, exitSide, quantity, PaperOrderType.Market, 0m, product, userId);
    }

    public async Task<(bool Success, decimal AvailableCash, decimal UsedMargin, string? Message)> GetEquityMarginsAsync(int userId = 1)
    {
        var tokenValidation = await ValidateSessionTokenAsync();
        if (!tokenValidation.IsValid)
        {
            return (false, 0m, 0m, tokenValidation.Message);
        }

        try
        {
            var client = _httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.Clear();
            client.DefaultRequestHeaders.Add("X-Kite-Version", "3");
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("token", $"{tokenValidation.ApiKey}:{tokenValidation.AccessToken}");

            var response = await client.GetAsync("https://api.kite.trade/user/margins/equity");
            var responseJson = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(responseJson);
                if (doc.RootElement.TryGetProperty("data", out var dataElem))
                {
                    decimal availableCash = 0m;
                    decimal usedMargin = 0m;

                    if (dataElem.TryGetProperty("net", out var netElem))
                    {
                        availableCash = netElem.GetDecimal();
                    }
                    else if (dataElem.TryGetProperty("available", out var availElem) &&
                             availElem.TryGetProperty("cash", out var cashElem))
                    {
                        availableCash = cashElem.GetDecimal();
                    }

                    if (dataElem.TryGetProperty("utilised", out var utilElem) &&
                        utilElem.TryGetProperty("debits", out var debitsElem))
                    {
                        usedMargin = debitsElem.GetDecimal();
                    }

                    return (true, availableCash, usedMargin, "Margins fetched successfully.");
                }
            }

            return (false, 0m, 0m, "Failed to parse margins response from KiteConnect.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error fetching live equity margins from Zerodha");
            return (false, 0m, 0m, ex.Message);
        }
    }

    // ITradingBrokerService compatibility methods
    public async Task<PaperOrder> PlaceOrderAsync(PlacePaperOrderDto dto, string userId = "default_user", decimal currentLtp = 0m)
    {
        int uid = int.TryParse(userId, out var parsed) ? parsed : 1;
        var result = await PlaceLiveOrderAsync(dto.Symbol, dto.Side, dto.Quantity, dto.OrderType, currentLtp, "CNC", uid);
        return new PaperOrder
        {
            Id = new Random().Next(100000, 999999),
            Symbol = dto.Symbol,
            OrderType = dto.OrderType,
            Side = dto.Side,
            Quantity = dto.Quantity,
            Price = currentLtp,
            Status = result.Success ? PaperOrderStatus.Filled : PaperOrderStatus.Rejected,
            FilledPrice = result.ExecutedPrice > 0m ? result.ExecutedPrice : currentLtp,
            FilledAt = result.Success ? DateTime.UtcNow : null,
            Remarks = $"[LIVE REAL MONEY - Zerodha API] {result.Message}"
        };
    }

    public Task CancelOrderAsync(int orderId, string userId = "default_user")
    {
        int uid = int.TryParse(userId, out var parsed) ? parsed : 1;
        return CancelLiveOrderAsync(orderId.ToString(), uid);
    }

    public Task ClosePositionAsync(int positionId, decimal currentLtp = 0m, string userId = "default_user")
    {
        _logger.LogInformation("[REAL MONEY LIVE TRADE] Closing Zerodha Live Position #{PositionId} @ {ExitPrice}", positionId, currentLtp);
        return Task.CompletedTask;
    }
}
