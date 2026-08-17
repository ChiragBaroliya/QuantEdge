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
    private readonly IRealTradeCacheService? _cacheService;
    private readonly BrokerConfig _config;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ZerodhaKiteBrokerService> _logger;

    public string Mode => "Live";

    public ZerodhaKiteBrokerService(
        IZerodhaSessionRepository sessionRepository,
        IOptions<BrokerConfig> config,
        IHttpClientFactory httpClientFactory,
        ILogger<ZerodhaKiteBrokerService> logger,
        IRealTradeCacheService? cacheService = null)
    {
        _sessionRepository = sessionRepository ?? throw new ArgumentNullException(nameof(sessionRepository));
        _config = config?.Value ?? throw new ArgumentNullException(nameof(config));
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _cacheService = cacheService;
    }

    public async Task<(bool IsValid, string? AccessToken, string? ApiKey, string? Message)> ValidateSessionTokenAsync(int userId = 1)
    {
        // 1. Try RAM Cache first
        var session = _cacheService?.GetUserSession(userId);
        if (session == null)
        {
            session = await _sessionRepository.GetActiveSessionAsync(userId);
            if (session != null && _cacheService != null)
            {
                _cacheService.SetUserSession(session);
            }
        }

        if (session == null || string.IsNullOrWhiteSpace(session.AccessToken))
        {
            return (false, null, null, $"No active Zerodha session token found for user {userId}. Please click 'Connect Zerodha' on Auto Real Trade page.");
        }

        // Validate token was created after 6:00 AM IST on the current trading day
        var indianTime = TimeZoneInfo.ConvertTime(session.CreatedAt, TimeZoneHelper.IndianTimeZone);
        var nowIst = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneHelper.IndianTimeZone);
        var cutoff = nowIst.Date.AddHours(6);

        if (indianTime.Date != nowIst.Date || indianTime < cutoff)
        {
            return (false, null, null, $"Zerodha session token for user {userId} is stale (created {indianTime:yyyy-MM-dd hh:mm tt} IST). Fresh token post 6:00 AM IST required.");
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
        var tokenValidation = await ValidateSessionTokenAsync(userId);
        if (!tokenValidation.IsValid)
        {
            _logger.LogWarning("PlaceLiveOrderAsync rejected for User {UserId}: {Reason}", userId, tokenValidation.Message);
            return (false, null, 0m, tokenValidation.Message);
        }

        string transactionType = side == TradeSide.BUY ? "BUY" : "SELL";
        string kiteOrderType = orderType == PaperOrderType.Limit ? "LIMIT" : "MARKET";
        string cleanSymbol = symbol.ToUpper().Trim();
        string kiteProduct = string.IsNullOrWhiteSpace(product) ? "CNC" : product.ToUpper().Trim();

        _logger.LogInformation("[REAL MONEY LIVE ORDER - User {UserId}] Placing KiteConnect order: {Symbol} {Side} Qty:{Qty} Product:{Product} @ ₹{Price:F2}",
            userId, cleanSymbol, transactionType, quantity, kiteProduct, price);

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

            _logger.LogInformation("Zerodha Order Response for User {UserId}: Code {Status}, Body: {Body}", userId, response.StatusCode, responseJson);

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

                // Check for SEBI CDSL e-DIS / TPIN requirement
                if (errorMsg.Contains("e-DIS", StringComparison.OrdinalIgnoreCase) ||
                    errorMsg.Contains("TPIN", StringComparison.OrdinalIgnoreCase) ||
                    errorMsg.Contains("authorization", StringComparison.OrdinalIgnoreCase))
                {
                    errorMsg = $"CDSL e-DIS / TPIN authorization required in Zerodha for selling {cleanSymbol}. Please authorize in Zerodha Kite holdings.";
                }

                _logger.LogError("Zerodha Order Placement Failed for User {UserId}: {ErrorMsg}", userId, errorMsg);
                return (false, null, 0m, $"Zerodha Error: {errorMsg}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "HTTP Exception placing live order with Zerodha for User {UserId} on {Symbol}", userId, symbol);
            return (false, null, 0m, $"Network/API Exception: {ex.Message}");
        }
    }

    public async Task<(bool Success, string? Message)> CancelLiveOrderAsync(string brokerOrderId, int userId = 1)
    {
        var tokenValidation = await ValidateSessionTokenAsync(userId);
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

            _logger.LogInformation("Zerodha Cancel Order Response #{OrderId} for User {UserId}: Code {Status}, Body: {Body}", brokerOrderId, userId, response.StatusCode, responseJson);
            return (response.IsSuccessStatusCode, response.IsSuccessStatusCode ? "Order cancelled" : responseJson);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error cancelling live order #{OrderId} for User {UserId}", brokerOrderId, userId);
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
        TradeSide exitSide = positionSide == TradeSide.BUY ? TradeSide.SELL : TradeSide.BUY;
        return await PlaceLiveOrderAsync(symbol, exitSide, quantity, PaperOrderType.Market, 0m, product, userId);
    }

    public async Task<(bool Success, decimal AvailableCash, decimal UsedMargin, string? Message)> GetEquityMarginsAsync(int userId = 1)
    {
        var tokenValidation = await ValidateSessionTokenAsync(userId);
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
            _logger.LogWarning(ex, "Error fetching live equity margins from Zerodha for User {UserId}", userId);
            return (false, 0m, 0m, ex.Message);
        }
    }

    public async Task<(bool Success, ZerodhaPositionsDto? Positions, string? Message)> GetLivePositionsAsync(int userId = 1)
    {
        var tokenValidation = await ValidateSessionTokenAsync(userId);
        if (!tokenValidation.IsValid)
        {
            return (false, null, tokenValidation.Message);
        }

        try
        {
            var client = _httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.Clear();
            client.DefaultRequestHeaders.Add("X-Kite-Version", "3");
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("token", $"{tokenValidation.ApiKey}:{tokenValidation.AccessToken}");

            var response = await client.GetAsync("https://api.kite.trade/portfolio/positions");
            var responseJson = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Failed to fetch positions from Zerodha for User {UserId}: Code {Status}", userId, response.StatusCode);
                return (false, null, $"Zerodha Error: {response.StatusCode}");
            }

            using var doc = JsonDocument.Parse(responseJson);
            if (doc.RootElement.TryGetProperty("data", out var dataElem))
            {
                var result = new ZerodhaPositionsDto();

                if (dataElem.TryGetProperty("net", out var netElem) && netElem.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in netElem.EnumerateArray())
                    {
                        var pos = ParsePositionItem(item);
                        if (pos != null)
                        {
                            result.Net.Add(pos);
                        }
                    }
                }

                if (dataElem.TryGetProperty("day", out var dayElem) && dayElem.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in dayElem.EnumerateArray())
                    {
                        var pos = ParsePositionItem(item);
                        if (pos != null)
                        {
                            result.Day.Add(pos);
                        }
                    }
                }

                result.TotalM2M = result.Net.Sum(p => p.M2m);
                result.TotalRealizedPnl = result.Net.Sum(p => p.Realised);
                result.TotalUnrealizedPnl = result.Net.Sum(p => p.Unrealised);

                return (true, result, "Positions fetched successfully.");
            }

            return (false, null, "Could not find 'data' in Zerodha positions response.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception fetching live positions from Zerodha for User {UserId}", userId);
            return (false, null, ex.Message);
        }
    }

    public async Task<(bool Success, List<ZerodhaHoldingDto>? Holdings, string? Message)> GetLiveHoldingsAsync(int userId = 1)
    {
        var tokenValidation = await ValidateSessionTokenAsync(userId);
        if (!tokenValidation.IsValid)
        {
            return (false, null, tokenValidation.Message);
        }

        try
        {
            var client = _httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.Clear();
            client.DefaultRequestHeaders.Add("X-Kite-Version", "3");
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("token", $"{tokenValidation.ApiKey}:{tokenValidation.AccessToken}");

            var response = await client.GetAsync("https://api.kite.trade/portfolio/holdings");
            var responseJson = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Failed to fetch holdings from Zerodha for User {UserId}: Code {Status}", userId, response.StatusCode);
                return (false, null, $"Zerodha Error: {response.StatusCode}");
            }

            using var doc = JsonDocument.Parse(responseJson);
            if (doc.RootElement.TryGetProperty("data", out var dataElem) && dataElem.ValueKind == JsonValueKind.Array)
            {
                var holdingsList = new List<ZerodhaHoldingDto>();
                foreach (var item in dataElem.EnumerateArray())
                {
                    var h = new ZerodhaHoldingDto
                    {
                        TradingSymbol = GetJsonString(item, "tradingsymbol"),
                        Exchange = GetJsonString(item, "exchange", "NSE"),
                        Isin = GetJsonString(item, "isin"),
                        Quantity = GetJsonInt(item, "quantity"),
                        T1Quantity = GetJsonInt(item, "t1_quantity"),
                        RealisedQuantity = GetJsonInt(item, "realised_quantity"),
                        AveragePrice = GetJsonDecimal(item, "average_price"),
                        LastPrice = GetJsonDecimal(item, "last_price"),
                        ClosePrice = GetJsonDecimal(item, "close_price"),
                        Pnl = GetJsonDecimal(item, "pnl"),
                        DayChange = GetJsonDecimal(item, "day_change"),
                        DayChangePercentage = GetJsonDecimal(item, "day_change_percentage"),
                        Value = GetJsonDecimal(item, "value")
                    };

                    if (h.Value == 0m && h.Quantity > 0 && h.LastPrice > 0)
                    {
                        h.Value = h.Quantity * h.LastPrice;
                    }

                    holdingsList.Add(h);
                }

                return (true, holdingsList, "Holdings fetched successfully.");
            }

            return (false, null, "Could not find 'data' in Zerodha holdings response.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception fetching live holdings from Zerodha for User {UserId}", userId);
            return (false, null, ex.Message);
        }
    }

    private static ZerodhaPositionItemDto? ParsePositionItem(JsonElement item)
    {
        try
        {
            var symbol = GetJsonString(item, "tradingsymbol");
            if (string.IsNullOrWhiteSpace(symbol)) return null;

            return new ZerodhaPositionItemDto
            {
                TradingSymbol = symbol,
                Exchange = GetJsonString(item, "exchange", "NSE"),
                Product = GetJsonString(item, "product", "CNC"),
                Quantity = GetJsonInt(item, "quantity"),
                BuyQuantity = GetJsonInt(item, "buy_quantity"),
                SellQuantity = GetJsonInt(item, "sell_quantity"),
                BuyPrice = GetJsonDecimal(item, "buy_price"),
                SellPrice = GetJsonDecimal(item, "sell_price"),
                BuyValue = GetJsonDecimal(item, "buy_value"),
                SellValue = GetJsonDecimal(item, "sell_value"),
                LastPrice = GetJsonDecimal(item, "last_price"),
                ClosePrice = GetJsonDecimal(item, "close_price"),
                Pnl = GetJsonDecimal(item, "pnl"),
                M2m = GetJsonDecimal(item, "m2m"),
                Realised = GetJsonDecimal(item, "realised"),
                Unrealised = GetJsonDecimal(item, "unrealised"),
                Value = GetJsonDecimal(item, "value"),
                Multiplier = GetJsonDecimal(item, "multiplier", 1m)
            };
        }
        catch
        {
            return null;
        }
    }

    private static string GetJsonString(JsonElement elem, string propName, string defaultValue = "")
    {
        if (elem.TryGetProperty(propName, out var prop) && prop.ValueKind == JsonValueKind.String)
        {
            return prop.GetString() ?? defaultValue;
        }
        return defaultValue;
    }

    private static int GetJsonInt(JsonElement elem, string propName, int defaultValue = 0)
    {
        if (elem.TryGetProperty(propName, out var prop))
        {
            if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out var val)) return val;
            if (prop.ValueKind == JsonValueKind.String && int.TryParse(prop.GetString(), out var parsed)) return parsed;
        }
        return defaultValue;
    }

    private static decimal GetJsonDecimal(JsonElement elem, string propName, decimal defaultValue = 0m)
    {
        if (elem.TryGetProperty(propName, out var prop))
        {
            if (prop.ValueKind == JsonValueKind.Number && prop.TryGetDecimal(out var val)) return val;
            if (prop.ValueKind == JsonValueKind.String && decimal.TryParse(prop.GetString(), out var parsed)) return parsed;
        }
        return defaultValue;
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
