using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using QuantEdge.Domain.Entities;
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
    private readonly ISwingStrategySettingsRepository? _strategySettingsRepository;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ZerodhaKiteBrokerService> _logger;

    public string Mode => "Live";

    public ZerodhaKiteBrokerService(
        IZerodhaSessionRepository sessionRepository,
        IHttpClientFactory httpClientFactory,
        ILogger<ZerodhaKiteBrokerService> logger,
        IRealTradeCacheService? cacheService = null,
        ISwingStrategySettingsRepository? strategySettingsRepository = null)
    {
        _sessionRepository = sessionRepository ?? throw new ArgumentNullException(nameof(sessionRepository));
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _cacheService = cacheService;
        _strategySettingsRepository = strategySettingsRepository;
    }

    public async Task<(bool IsValid, string? AccessToken, string? ApiKey, string? Message)> ValidateSessionTokenAsync(int userId = 1)
    {
        // 1. Resolve from the DB-backed session store first. Token Manager invalidates this
        // repository's own (short-TTL) cache on every login, so a freshly generated token is
        // picked up immediately. The RAM warmup cache is only a fallback for DB outages, since
        // it is populated once at pre-market warmup and would otherwise mask a same-day re-login.
        var session = await _sessionRepository.GetActiveSessionAsync(userId);
        if (session == null)
        {
            session = _cacheService?.GetUserSession(userId);
        }
        else
        {
            _cacheService?.SetUserSession(session);
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
            return (false, null, null, $"Zerodha session token for user {userId} is stale (created {indianTime:yyyy-MM-dd hh:mm tt} IST, checked at {nowIst:yyyy-MM-dd hh:mm tt} IST, cutoff {cutoff:yyyy-MM-dd hh:mm tt} IST). Fresh token post 6:00 AM IST required.");
        }

        return (true, session.AccessToken, session.ApiKey, "Active Zerodha session is valid.");
    }

    public async Task<(bool Success, string? BrokerOrderId, decimal ExecutedPrice, string? Message)> PlaceLiveOrderAsync(
        string symbol,
        TradeSide side,
        int quantity,
        PaperOrderType orderType,
        decimal price,
        string product = "CNC",
        int userId = 1,
        decimal? protectionBufferPctOverride = null)
    {
        var tokenValidation = await ValidateSessionTokenAsync(userId);
        if (!tokenValidation.IsValid)
        {
            _logger.LogWarning("PlaceLiveOrderAsync rejected for User {UserId}: {Reason}", userId, tokenValidation.Message);
            return (false, null, 0m, tokenValidation.Message);
        }

        string transactionType = side == TradeSide.BUY ? "BUY" : "SELL";
        string cleanSymbol = symbol.ToUpper().Trim();
        string kiteProduct = string.IsNullOrWhiteSpace(product) ? "CNC" : product.ToUpper().Trim();

        // Kite Connect rejects plain MARKET orders on the "regular" variety via API ("Market orders
        // without market protection are not allowed via API. Please set market protection or use a
        // Limit order."). Zerodha's own suggested fix is used here: submit a LIMIT order with a small
        // protection band around the reference price, in the direction that still fills immediately
        // for a normal, liquid NSE equity move, instead of a bare MARKET order. Buffer is configurable
        // via Strategy Settings (default 0.5%) so it can be tuned without a redeploy, or widened
        // per-order via protectionBufferPctOverride (e.g. a gap-through exit needs more room to fill).
        decimal marketProtectionBufferPct = 0.005m;
        if (protectionBufferPctOverride.HasValue)
        {
            marketProtectionBufferPct = protectionBufferPctOverride.Value;
        }
        else if (_strategySettingsRepository != null)
        {
            var strategySettings = await _strategySettingsRepository.GetSettingsAsync();
            marketProtectionBufferPct = strategySettings.MarketProtectionBufferPct;
        }
        string kiteOrderType = "LIMIT";
        decimal orderPrice = price;
        if (orderType != PaperOrderType.Limit)
        {
            orderPrice = side == TradeSide.BUY
                ? price * (1m + marketProtectionBufferPct)
                : price * (1m - marketProtectionBufferPct);
        }

        // NSE equities are rejected if the price is not a multiple of the script's tick size
        // ("Tick size for this script is 0.05..."). 0.05 covers the vast majority of NSE
        // equities; rounding here (rather than plain 2-decimal rounding) keeps the market
        // protection band and any upstream price from landing on an invalid tick.
        const decimal tickSize = 0.05m;
        orderPrice = Math.Round(Math.Round(orderPrice / tickSize, MidpointRounding.AwayFromZero) * tickSize, 2);

        if (orderPrice <= 0m)
        {
            _logger.LogError("PlaceLiveOrderAsync rejected for User {UserId}: no valid reference price supplied for {Symbol} (received ₹{Price:F2}). A LIMIT order cannot be submitted without a price.",
                userId, symbol, price);
            return (false, null, 0m, $"No valid reference price available for {symbol}; cannot submit order.");
        }

        _logger.LogInformation("[REAL MONEY LIVE ORDER - User {UserId}] Placing KiteConnect order: {Symbol} {Side} Qty:{Qty} Product:{Product} @ ₹{Price:F2}",
            userId, cleanSymbol, transactionType, quantity, kiteProduct, orderPrice);

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

            if (orderPrice > 0m)
            {
                formData.Add("price", orderPrice.ToString("F2"));
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

    public async Task<(bool Success, string? BrokerStatus, decimal AveragePrice, int FilledQuantity, string? Message)> GetOrderStatusAsync(string brokerOrderId, int userId = 1)
    {
        var tokenValidation = await ValidateSessionTokenAsync(userId);
        if (!tokenValidation.IsValid)
        {
            return (false, null, 0m, 0, tokenValidation.Message);
        }

        try
        {
            var client = _httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.Clear();
            client.DefaultRequestHeaders.Add("X-Kite-Version", "3");
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("token", $"{tokenValidation.ApiKey}:{tokenValidation.AccessToken}");

            var response = await client.GetAsync($"https://api.kite.trade/orders/{brokerOrderId}");
            var responseJson = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Zerodha GetOrderStatus failed for Order #{OrderId}, User {UserId}: Code {Status}, Body: {Body}",
                    brokerOrderId, userId, response.StatusCode, responseJson);

                string errorMsg = "Broker error";
                try
                {
                    using var errDoc = JsonDocument.Parse(responseJson);
                    if (errDoc.RootElement.TryGetProperty("message", out var errMsgElem))
                    {
                        errorMsg = errMsgElem.GetString() ?? errorMsg;
                    }
                }
                catch { }

                return (false, null, 0m, 0, $"Broker error fetching order status: {errorMsg}");
            }

            using var doc = JsonDocument.Parse(responseJson);
            if (!doc.RootElement.TryGetProperty("data", out var dataElem) || dataElem.ValueKind != JsonValueKind.Array || dataElem.GetArrayLength() == 0)
            {
                return (false, null, 0m, 0, "No order history returned by broker for this order.");
            }

            // Kite returns the full status history for the order; the last entry is the current state.
            var latest = dataElem[dataElem.GetArrayLength() - 1];
            string? status = latest.TryGetProperty("status", out var statusElem) ? statusElem.GetString() : null;
            decimal averagePrice = latest.TryGetProperty("average_price", out var avgElem) && avgElem.TryGetDecimal(out var avg) ? avg : 0m;
            int filledQuantity = latest.TryGetProperty("filled_quantity", out var qtyElem) && qtyElem.TryGetInt32(out var qty) ? qty : 0;
            string? statusMessage = latest.TryGetProperty("status_message", out var msgElem) ? msgElem.GetString() : null;

            return (true, status, averagePrice, filledQuantity, statusMessage ?? status);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching order status for #{OrderId}, User {UserId}", brokerOrderId, userId);
            return (false, null, 0m, 0, $"Network/API Exception: {ex.Message}");
        }
    }

    public async Task<(bool Success, string? BrokerOrderId, decimal ExecutedPrice, string? Message)> SquareOffLivePositionAsync(
        string symbol,
        int quantity,
        TradeSide positionSide,
        decimal currentPrice,
        string product = "CNC",
        int userId = 1,
        decimal? protectionBufferPctOverride = null)
    {
        TradeSide exitSide = positionSide == TradeSide.BUY ? TradeSide.SELL : TradeSide.BUY;
        return await PlaceLiveOrderAsync(symbol, exitSide, quantity, PaperOrderType.Market, currentPrice, product, userId, protectionBufferPctOverride);
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

    public async Task<(bool Success, Dictionary<string, decimal>? Ltps, string? Message)> GetLtpQuotesAsync(
        IEnumerable<(string Symbol, string Exchange)> instruments, int userId = 1)
    {
        var instrumentList = instruments
            .Where(i => !string.IsNullOrWhiteSpace(i.Symbol))
            .Select(i => (Symbol: i.Symbol.Trim().ToUpper(), Exchange: string.IsNullOrWhiteSpace(i.Exchange) ? "NSE" : i.Exchange.Trim().ToUpper()))
            .Distinct()
            .ToList();

        if (instrumentList.Count == 0)
        {
            return (true, new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase), "No instruments requested.");
        }

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

            string query = string.Join("&", instrumentList.Select(i => $"i={Uri.EscapeDataString($"{i.Exchange}:{i.Symbol}")}"));
            var response = await client.GetAsync($"https://api.kite.trade/quote/ltp?{query}");
            var responseJson = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Failed to fetch LTP quotes from Zerodha for User {UserId}: Code {Status}", userId, response.StatusCode);
                return (false, null, $"Zerodha Error: {response.StatusCode}");
            }

            using var doc = JsonDocument.Parse(responseJson);
            var result = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);

            if (doc.RootElement.TryGetProperty("data", out var dataElem))
            {
                foreach (var prop in dataElem.EnumerateObject())
                {
                    // Key is "EXCHANGE:SYMBOL" (e.g. "NSE:INFY")
                    var symbol = prop.Name.Contains(':') ? prop.Name.Split(':')[1] : prop.Name;
                    result[symbol] = GetJsonDecimal(prop.Value, "last_price");
                }
            }

            return (true, result, "LTP quotes fetched successfully.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Exception fetching live LTP quotes from Zerodha for User {UserId}", userId);
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
