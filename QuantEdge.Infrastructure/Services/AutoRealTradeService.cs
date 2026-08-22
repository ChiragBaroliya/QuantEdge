using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using QuantEdge.Domain.Entities;
using QuantEdge.Infrastructure.DTOs;
using QuantEdge.Infrastructure.Helpers;
using QuantEdge.Infrastructure.Hubs;
using QuantEdge.Infrastructure.Interfaces;
using QuantEdge.Infrastructure.Persistence.Repositories;

using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;

namespace QuantEdge.Infrastructure.Services;

public class AutoRealTradeService : IAutoRealTradeService
{
    private readonly IRealTradingRepository _repository;
    private readonly IZerodhaKiteBrokerService _brokerService;
    private readonly IZerodhaSessionRepository _sessionRepository;
    private readonly IIndianHolidayRepository _holidayRepository;
    private readonly IMarketHoursService _marketHoursService;
    private readonly ICacheService _cacheService;
    private readonly IRealTradeCacheService? _realTradeCache;
    private readonly IHubContext<MarketDataHub>? _hubContext;
    private readonly IServiceScopeFactory? _scopeFactory;
    private readonly ILogger<AutoRealTradeService> _logger;
    private readonly ConcurrentDictionary<int, string> _userNameCache = new();

    public AutoRealTradeService(
        IRealTradingRepository repository,
        IZerodhaKiteBrokerService brokerService,
        IZerodhaSessionRepository sessionRepository,
        IIndianHolidayRepository holidayRepository,
        IMarketHoursService marketHoursService,
        ICacheService cacheService,
        ILogger<AutoRealTradeService> logger,
        IRealTradeCacheService? realTradeCache = null,
        IHubContext<MarketDataHub>? hubContext = null,
        IServiceScopeFactory? scopeFactory = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _brokerService = brokerService ?? throw new ArgumentNullException(nameof(brokerService));
        _sessionRepository = sessionRepository ?? throw new ArgumentNullException(nameof(sessionRepository));
        _holidayRepository = holidayRepository ?? throw new ArgumentNullException(nameof(holidayRepository));
        _marketHoursService = marketHoursService ?? throw new ArgumentNullException(nameof(marketHoursService));
        _cacheService = cacheService ?? throw new ArgumentNullException(nameof(cacheService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _realTradeCache = realTradeCache;
        _hubContext = hubContext;
        _scopeFactory = scopeFactory;
    }

    public async Task<RealTradeSettings> GetSettingsAsync(int userId = 1)
    {
        var ramSettings = _realTradeCache?.GetUserSettings(userId);
        if (ramSettings != null) return ramSettings;

        string cacheKey = $"realtrade:settings:{userId}";
        var cached = await _cacheService.GetAsync<RealTradeSettings>(cacheKey);
        if (cached != null) return cached;

        var settings = await _repository.GetSettingsAsync(userId);
        await _cacheService.SetAsync(cacheKey, settings, TimeSpan.FromMinutes(15));
        _realTradeCache?.SetUserSettings(settings);
        return settings;
    }

    public async Task<RealTradeSettings> UpdateSettingsAsync(RealTradeSettingsUpdateDto updateDto, int userId = 1)
    {
        // 1. Check if Market is currently Open (09:15 AM - 03:30 PM IST)
        if (await _marketHoursService.IsWithinMarketHoursAsync())
        {
            throw new InvalidOperationException("⚙️ Auto Real Trade settings are LOCKED during active market hours (09:15 AM - 03:30 PM IST). You can adjust and save settings after 03:30 PM.");
        }

        var existing = await _repository.GetSettingsAsync(userId);
        if (existing == null)
        {
            existing = new RealTradeSettings { UserId = userId };
        }

        existing.IsRealTradeEnabled = updateDto.IsRealTradeEnabled;
        existing.AvailableCapital = updateDto.AvailableCapital;
        existing.ProfitTargetPct = updateDto.ProfitTargetPct;
        existing.StopLossPct = updateDto.StopLossPct; // Optional
        existing.TrailingSlEnabled = updateDto.TrailingSlEnabled; // Optional
        existing.TrailingSlPct = updateDto.TrailingSlPct; // Optional
        existing.MaxDurationDays = updateDto.MaxDurationDays;
        existing.MaxTradesPerDay = updateDto.MaxTradesPerDay;
        existing.FixedAmountPerTrade = updateDto.FixedAmountPerTrade;
        existing.MaxDailyLossLimit = updateDto.MaxDailyLossLimit; // Optional
        existing.ProductType = string.IsNullOrWhiteSpace(updateDto.ProductType) ? "CNC" : updateDto.ProductType.ToUpper();
        existing.MinConditionsMatch = updateDto.MinConditionsMatch;
        existing.TradingWindowStart = updateDto.TradingWindowStart ?? "09:15";
        existing.TradingWindowEnd = updateDto.TradingWindowEnd ?? "15:30";

        var updated = await _repository.UpsertSettingsAsync(existing);

        string cacheKey = $"realtrade:settings:{userId}";
        await _cacheService.RemoveAsync(cacheKey);
        await _cacheService.SetAsync(cacheKey, updated, TimeSpan.FromMinutes(15));
        _realTradeCache?.SetUserSettings(updated);

        await BroadcastDashboardUpdateAsync(userId);
        return updated;
    }

    public async Task ToggleRealTradeAsync(bool enabled, int userId = 1)
    {
        string userTag = await GetUserTagAsync(userId);
        if (enabled)
        {
            // Verify Zerodha Token before enabling Live Auto Trading
            var tokenCheck = await _brokerService.ValidateSessionTokenAsync(userId);
            if (!tokenCheck.IsValid)
            {
                _logger.LogWarning("Cannot turn ON Real Auto Trade for User {UserId}: {Reason}", userId, tokenCheck.Message);
                await LogAuditAsync(userTag, "LIVE_ENABLE_FAILED", null, null,
                    $"Failed to enable Real Auto Trade: {tokenCheck.Message}", userId);
                throw new InvalidOperationException($"⚠️ Zerodha Account Not Connected: {tokenCheck.Message}");
            }
        }

        await _repository.ToggleRealTradeAsync(userId, enabled);

        var settings = await GetSettingsAsync(userId);
        settings.IsRealTradeEnabled = enabled;
        _realTradeCache?.SetUserSettings(settings);

        string cacheKey = $"realtrade:settings:{userId}";
        await _cacheService.RemoveAsync(cacheKey);

        await LogAuditAsync(userTag, enabled ? "REAL_TRADE_ENABLED" : "REAL_TRADE_DISABLED", null, null,
            enabled ? "⚡ REAL MONEY Auto Trading Master Switch turned ON" : "Real Auto Trading Master Switch turned OFF", userId);

        await BroadcastDashboardUpdateAsync(userId);
    }

    public async Task<int> GetTodayRealTradeCountAsync(int userId = 1)
    {
        string todayKey = $"realtrade:today_count:{userId}:{DateTime.UtcNow:yyyyMMdd}";
        var cachedCount = await _cacheService.GetAsync<int?>(todayKey);
        if (cachedCount.HasValue) return cachedCount.Value;

        int count = await _repository.GetTodayRealTradeCountAsync(userId);
        await _cacheService.SetAsync(todayKey, (int?)count, TimeSpan.FromMinutes(5));
        return count;
    }

    public async Task<RealTradeDashboardDto> GetDashboardDataAsync(int userId = 1)
    {
        var settings = await GetSettingsAsync(userId);
        var positions = (await _repository.GetOpenPositionsAsync(userId)).ToList();
        var recentOrders = await _repository.GetRecentOrdersAsync(userId, 20);
        var todayLogs = await GetTodayLogsAsync(userId, 50);
        int todayCount = await GetTodayRealTradeCountAsync(userId);

        decimal unrealizedPnl = positions.Sum(p => p.UnrealizedPnl);
        decimal todayRealizedPnl = await _repository.GetTodayRealizedPnlAsync(userId);

        decimal todayTradeAmount = recentOrders
            .Where(o => o.Side == TradeSide.BUY && o.Status == PaperOrderStatus.Filled && o.CreatedAt >= DateTime.UtcNow.Date)
            .Sum(o => o.Quantity * o.FilledPrice);

        if (todayTradeAmount == 0 && todayCount > 0)
        {
            todayTradeAmount = todayCount * settings.FixedAmountPerTrade;
        }

        // Live Margin, Positions & Portfolio P&L from Broker
        decimal availableMargin = settings.AvailableCapital;
        decimal usedMargin = 0m;
        var tokenValidation = await _brokerService.ValidateSessionTokenAsync(userId);
        string tokenCreatedIst = "N/A";
        string tokenExpiresIst = "N/A";
        string apiKey = string.Empty;
        string clientId = string.Empty;
        string accountHolderName = string.Empty;
        bool isDdpiEnabled = false;
        ZerodhaPositionsDto? brokerPositions = null;
        List<ZerodhaHoldingDto>? brokerHoldings = null;
        decimal zerodhaM2m = 0m;
        decimal zerodhaRealizedPnl = 0m;
        decimal zerodhaUnrealizedPnl = 0m;

        var activeSession = await _sessionRepository.GetActiveSessionAsync(userId);
        if (activeSession != null)
        {
            var istTime = TimeZoneInfo.ConvertTime(activeSession.CreatedAt, TimeZoneHelper.IndianTimeZone);
            tokenCreatedIst = istTime.ToString("hh:mm tt, dd MMM");
            tokenExpiresIst = istTime.Date.AddDays(1).AddHours(6).ToString("hh:mm tt, dd MMM");
            apiKey = activeSession.ApiKey;
            clientId = activeSession.ClientId ?? string.Empty;
            accountHolderName = activeSession.UserName ?? string.Empty;
            isDdpiEnabled = activeSession.IsDdpiEnabled;
        }

        if (tokenValidation.IsValid)
        {
            var marginRes = await _brokerService.GetEquityMarginsAsync(userId);
            if (marginRes.Success)
            {
                availableMargin = marginRes.AvailableCash;
                usedMargin = marginRes.UsedMargin;
            }

            var posRes = await _brokerService.GetLivePositionsAsync(userId);
            if (posRes.Success && posRes.Positions != null)
            {
                brokerPositions = posRes.Positions;
                zerodhaM2m = posRes.Positions.TotalM2M;
                zerodhaRealizedPnl = posRes.Positions.TotalRealizedPnl;
                zerodhaUnrealizedPnl = posRes.Positions.TotalUnrealizedPnl;
            }

            var holdRes = await _brokerService.GetLiveHoldingsAsync(userId);
            if (holdRes.Success && holdRes.Holdings != null)
            {
                brokerHoldings = holdRes.Holdings;
            }
        }

        var nextRunInfo = AutoTradeService.Calculate15MinNextRunInfo();

        string systemStatus = "IDLE";
        if (!tokenValidation.IsValid)
        {
            systemStatus = "TOKEN_EXPIRED";
        }
        else if (settings.IsRealTradeEnabled)
        {
            systemStatus = "LIVE_ACTIVE";
        }
        else
        {
            systemStatus = "PAUSED";
        }

        return new RealTradeDashboardDto
        {
            Settings = settings,
            TodayTradeCount = todayCount,
            TodayTradeAmount = todayTradeAmount,
            ActivePositionsCount = positions.Count,
            TotalUnrealizedPnl = unrealizedPnl,
            TotalRealizedPnlToday = todayRealizedPnl,
            AvailableBrokerMargin = availableMargin,
            UsedBrokerMargin = usedMargin,
            IsBrokerTokenActive = tokenValidation.IsValid,
            IsDdpiEnabled = isDdpiEnabled,
            ClientId = clientId,
            AccountHolderName = accountHolderName,
            ApiKey = apiKey,
            BrokerTokenCreatedIst = tokenCreatedIst,
            BrokerTokenExpiresIst = tokenExpiresIst,
            TpinGuidanceRequired = !isDdpiEnabled,
            IsWebSocketConnected = true,
            IsRestPollingFallback = false,
            SystemStatus = systemStatus,
            OpenPositions = positions,
            RecentOrders = recentOrders,
            TodayLogs = todayLogs,
            NextRunTime = nextRunInfo.NextRunTime,
            NextRunSeconds = nextRunInfo.NextRunSeconds,
            NextRunFormatted = nextRunInfo.FormattedText,
            IsMarketOpen = nextRunInfo.IsMarketOpen,
            BrokerPositions = brokerPositions,
            BrokerHoldings = brokerHoldings,
            ZerodhaTotalM2M = zerodhaM2m,
            ZerodhaRealizedPnl = zerodhaRealizedPnl,
            ZerodhaUnrealizedPnl = zerodhaUnrealizedPnl
        };
    }

    public async Task LogAuditAsync(string symbol, string actionType, decimal? price, int? quantity, string? reason, int userId = 1)
    {
        var log = new RealTradeExecutionLog
        {
            UserId = userId,
            Symbol = symbol.ToUpper().Trim(),
            ActionType = actionType,
            Price = price,
            Quantity = quantity,
            Reason = reason,
            ExecutedAt = DateTime.UtcNow
        };
        await _repository.LogExecutionAsync(log);

        if (_hubContext != null)
        {
            await _hubContext.Clients.All.SendAsync("ReceiveRealTradeLogEvent", log);
        }
    }

    public async Task<IEnumerable<RealTradeExecutionLog>> GetTodayLogsAsync(int userId = 1, int limit = 50)
    {
        return await _repository.GetTodayLogsAsync(userId, limit);
    }

    public async Task<bool> EvaluateAndExecuteRealBuyAsync(string symbol, decimal entryPrice, int metConditionsCount, int userId = 1, bool isBuySignal = false)
    {
        symbol = symbol.ToUpper().Trim();
        var settings = await GetSettingsAsync(userId);

        // 1. Master Switch Validation
        if (!settings.IsRealTradeEnabled)
        {
            return false;
        }

        // 2. Token Active & Health Check
        var tokenCheck = await _brokerService.ValidateSessionTokenAsync(userId);
        if (!tokenCheck.IsValid)
        {
            await LogAuditAsync(symbol, "REAL_SIGNAL_SKIPPED", entryPrice, 0, $"Zerodha Token Invalid: {tokenCheck.Message}", userId);
            return false;
        }

        // 3. Market Hours & Trading Window Check
        if (!await _marketHoursService.IsWithinMarketHoursAsync())
        {
            await LogAuditAsync(symbol, "REAL_SIGNAL_SKIPPED", entryPrice, 0, "Outside Market Hours or Holiday", userId);
            return false;
        }

        if (!IsWithinTradingWindow(settings.TradingWindowStart, settings.TradingWindowEnd))
        {
            await LogAuditAsync(symbol, "REAL_SIGNAL_SKIPPED", entryPrice, 0,
                $"Outside trading window ({settings.TradingWindowStart} - {settings.TradingWindowEnd})", userId);
            return false;
        }

        // 4. Condition Match Check
        if (!isBuySignal && metConditionsCount < settings.MinConditionsMatch)
        {
            await LogAuditAsync(symbol, "REAL_SIGNAL_SKIPPED", entryPrice, 0,
                $"Condition score {metConditionsCount}/11 below required {settings.MinConditionsMatch}/11", userId);
            return false;
        }

        // 5. Daily Trade Limit Check
        int todayCount = await GetTodayRealTradeCountAsync(userId);
        if (todayCount >= settings.MaxTradesPerDay)
        {
            await LogAuditAsync(symbol, "REAL_SIGNAL_SKIPPED", entryPrice, 0,
                $"Daily limit of {settings.MaxTradesPerDay} real trades reached ({todayCount}/{settings.MaxTradesPerDay})", userId);
            return false;
        }

        // 6. Optional Daily Loss Circuit Breaker Check
        if (settings.MaxDailyLossLimit.HasValue && settings.MaxDailyLossLimit.Value > 0)
        {
            decimal todayRealizedPnl = await _repository.GetTodayRealizedPnlAsync(userId);
            var openPositions = await _repository.GetOpenPositionsAsync(userId);
            decimal totalUnrealized = openPositions.Sum(p => p.UnrealizedPnl);
            decimal totalLoss = todayRealizedPnl + totalUnrealized;

            if (totalLoss <= -Math.Abs(settings.MaxDailyLossLimit.Value))
            {
                await LogAuditAsync(symbol, "CIRCUIT_BREAKER", entryPrice, 0,
                    $"Daily loss limit ₹{settings.MaxDailyLossLimit.Value:N2} breached (Total Loss: ₹{totalLoss:N2}). Pausing live bot.", userId);
                await ToggleRealTradeAsync(false, userId);
                return false;
            }
        }

        // 7. Duplicate Open Position Check
        var existingOpenPos = await _repository.GetOpenPositionBySymbolAsync(userId, symbol);
        if (existingOpenPos != null)
        {
            await LogAuditAsync(symbol, "REAL_SIGNAL_SKIPPED", entryPrice, 0,
                $"Symbol already has an OPEN real position (Position #{existingOpenPos.Id})", userId);
            return false;
        }

        // 8. Capital & Margin Validation
        var marginResult = await _brokerService.GetEquityMarginsAsync(userId);
        decimal availableMargin = marginResult.Success ? marginResult.AvailableCash : settings.AvailableCapital;

        if (availableMargin < settings.FixedAmountPerTrade)
        {
            await LogAuditAsync(symbol, "REAL_SIGNAL_SKIPPED", entryPrice, 0,
                $"Insufficient Broker Capital (₹{availableMargin:N2} < Trade Amount ₹{settings.FixedAmountPerTrade:N2})", userId);
            return false;
        }

        int quantity = (int)Math.Floor(settings.FixedAmountPerTrade / entryPrice);
        if (quantity < 1)
        {
            await LogAuditAsync(symbol, "REAL_SIGNAL_SKIPPED", entryPrice, 0,
                $"Calculated quantity 0 for entry price ₹{entryPrice:N2}", userId);
            return false;
        }

        // 9. Target & Optional Stop Loss & Trailing SL Setup
        decimal tpPct = Math.Abs(settings.ProfitTargetPct) / 100m;
        decimal takeProfit = Math.Round(entryPrice * (1m + tpPct), 2);

        decimal? stopLoss = null;
        if (settings.StopLossPct.HasValue && settings.StopLossPct.Value > 0)
        {
            stopLoss = Math.Round(entryPrice * (1m - Math.Abs(settings.StopLossPct.Value) / 100m), 2);
        }

        decimal? trailingSl = null;
        if (settings.TrailingSlEnabled && settings.TrailingSlPct.HasValue && settings.TrailingSlPct.Value > 0)
        {
            trailingSl = Math.Round(entryPrice * (1m - Math.Abs(settings.TrailingSlPct.Value) / 100m), 2);
        }

        try
        {
            // Execute Real Buy Order via Zerodha Kite API
            var brokerResult = await _brokerService.PlaceLiveOrderAsync(
                symbol,
                TradeSide.BUY,
                quantity,
                PaperOrderType.Market,
                entryPrice,
                settings.ProductType,
                userId);

            if (!brokerResult.Success)
            {
                await _repository.CreateOrderAsync(new RealOrder
                {
                    UserId = userId,
                    Symbol = symbol,
                    Side = TradeSide.BUY,
                    Quantity = quantity,
                    OrderType = PaperOrderType.Market,
                    Price = entryPrice,
                    StopLoss = stopLoss,
                    TakeProfit = takeProfit,
                    Status = PaperOrderStatus.Rejected,
                    RejectionReason = brokerResult.Message,
                    Remarks = $"Real BUY Rejected by Zerodha: {brokerResult.Message}"
                });

                await LogAuditAsync(symbol, "ORDER_REJECTED", entryPrice, quantity,
                    $"Zerodha Order Placement Failed: {brokerResult.Message}", userId);
                return false;
            }

            decimal executedPrice = brokerResult.ExecutedPrice > 0m ? brokerResult.ExecutedPrice : entryPrice;
            string brokerOrderId = brokerResult.BrokerOrderId ?? $"KITE-{DateTime.UtcNow.Ticks}";

            // Insert Real Order
            var order = await _repository.CreateOrderAsync(new RealOrder
            {
                UserId = userId,
                BrokerOrderId = brokerOrderId,
                Symbol = symbol,
                Side = TradeSide.BUY,
                Quantity = quantity,
                OrderType = PaperOrderType.Market,
                Price = executedPrice,
                StopLoss = stopLoss,
                TakeProfit = takeProfit,
                Status = PaperOrderStatus.Filled,
                FilledPrice = executedPrice,
                FilledAt = DateTime.UtcNow,
                Remarks = $"[LIVE REAL MONEY] Zerodha Order #{brokerOrderId} (Met {metConditionsCount}/11)"
            });

            // Upsert Real Position
            var newPosition = await _repository.UpsertPositionAsync(new RealPosition
            {
                UserId = userId,
                Symbol = symbol,
                Side = TradeSide.BUY,
                Quantity = quantity,
                AverageEntryPrice = executedPrice,
                CurrentPrice = executedPrice,
                UnrealizedPnl = 0m,
                StopLoss = stopLoss,
                TakeProfit = takeProfit,
                TrailingStopLoss = trailingSl,
                Status = PositionStatus.OPEN,
                TradeType = TradeType.Auto,
                RealizedPnl = 0m
            });

            _realTradeCache?.AddOrUpdatePosition(newPosition);

            // Record Real Trade History
            await _repository.RecordTradeHistoryAsync(new RealTradeHistory
            {
                UserId = userId,
                OrderId = order.Id,
                BrokerOrderId = brokerOrderId,
                Symbol = symbol,
                Side = TradeSide.BUY,
                Quantity = quantity,
                EntryPrice = executedPrice,
                ExecutedPrice = executedPrice,
                RealizedPnl = 0m,
                TradeType = TradeType.Auto,
                Remarks = $"Real BUY Executed @ ₹{executedPrice:F2} (Broker ID: {brokerOrderId})"
            });

            // Invalidate count cache
            string todayKey = $"realtrade:today_count:{userId}:{DateTime.UtcNow:yyyyMMdd}";
            await _cacheService.RemoveAsync(todayKey);

            // Log Audit
            string slText = stopLoss.HasValue ? $"₹{stopLoss.Value:F2}" : "Disabled";
            string tslText = trailingSl.HasValue ? $"₹{trailingSl.Value:F2}" : "Disabled";
            await LogAuditAsync(symbol, "REAL_BUY", executedPrice, quantity,
                $"⚡ Live BUY Executed @ ₹{executedPrice:F2} (Qty: {quantity}, Target: ₹{takeProfit:F2}, SL: {slText}, TSL: {tslText}, Order #{brokerOrderId})", userId);

            // Broadcast SignalR Toast
            if (_hubContext != null)
            {
                await _hubContext.Clients.All.SendAsync("ReceiveRealTradeAlert", new
                {
                    symbol,
                    side = "BUY",
                    quantity,
                    price = executedPrice,
                    target = takeProfit,
                    stopLoss,
                    trailingSl,
                    brokerOrderId,
                    userId,
                    message = $"⚡ LIVE REAL BUY: {quantity} shares of {symbol} @ ₹{executedPrice:N2} (Target ₹{takeProfit:N2})"
                });
            }

            await BroadcastDashboardUpdateAsync(userId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to execute real buy order for {Symbol} (User {UserId})", symbol, userId);
            await LogAuditAsync(symbol, "SYSTEM_ERROR", entryPrice, quantity, $"Real BUY execution failed: {ex.Message}", userId);
            return false;
        }
    }

    public async Task<bool> EvaluateAndExecuteRealSellAsync(RealPosition position, decimal currentLtp, int userId = 1)
    {
        if (position == null || position.Status != PositionStatus.OPEN)
            return false;

        if (!await _marketHoursService.IsWithinMarketHoursAsync())
            return false;

        var settings = await GetSettingsAsync(userId);
        if (!IsWithinTradingWindow(settings.TradingWindowStart, settings.TradingWindowEnd))
            return false;

        string exitReason = string.Empty;
        bool shouldExit = false;

        // 1. Target Hit Check
        if (position.TakeProfit.HasValue && currentLtp >= position.TakeProfit.Value)
        {
            shouldExit = true;
            exitReason = "Target Hit";
        }
        // 2. Trailing Stop Loss Hit Check (Optional)
        else if (settings.TrailingSlEnabled && position.TrailingStopLoss.HasValue && currentLtp <= position.TrailingStopLoss.Value)
        {
            shouldExit = true;
            exitReason = $"Trailing SL Hit @ ₹{position.TrailingStopLoss.Value:F2}";
        }
        // 3. Stop Loss Hit Check (Optional)
        else if (position.StopLoss.HasValue && position.StopLoss.Value > 0 && currentLtp <= position.StopLoss.Value)
        {
            shouldExit = true;
            exitReason = "Stop Loss Hit";
        }
        // 4. Max Duration Check
        else if (settings.MaxDurationDays > 0)
        {
            int daysOpen = (DateTime.UtcNow - position.OpenedAt).Days;
            if (daysOpen >= settings.MaxDurationDays)
            {
                shouldExit = true;
                exitReason = $"Max Duration ({settings.MaxDurationDays} Days) Exit";
            }
        }

        if (shouldExit)
        {
            return await ExecuteRealSellOrderAsync(position, currentLtp, exitReason, userId);
        }

        // Dynamic Trailing SL Calculation (if Trailing SL is active and price is higher)
        if (settings.TrailingSlEnabled && settings.TrailingSlPct.HasValue && settings.TrailingSlPct.Value > 0)
        {
            decimal candidateSl = Math.Round(currentLtp * (1m - Math.Abs(settings.TrailingSlPct.Value) / 100m), 2);
            if (!position.TrailingStopLoss.HasValue || candidateSl > position.TrailingStopLoss.Value)
            {
                await _repository.UpdateTrailingStopLossAsync(position.Id, candidateSl);
                position.TrailingStopLoss = candidateSl;
                _realTradeCache?.AddOrUpdatePosition(position);
            }
        }

        return false;
    }

    private async Task<bool> ExecuteRealSellOrderAsync(RealPosition position, decimal currentLtp, string exitReason, int userId)
    {
        var settings = await GetSettingsAsync(userId);
        try
        {
            _logger.LogInformation("[REAL MONEY SELL TRIGGERED - User {UserId}] Position #{Id} {Symbol} Qty:{Qty} @ {Ltp}. Reason: {Reason}",
                userId, position.Id, position.Symbol, position.Quantity, currentLtp, exitReason);

            // Execute Real Market Sell via Zerodha Kite API
            var brokerResult = await _brokerService.SquareOffLivePositionAsync(
                position.Symbol,
                position.Quantity,
                position.Side,
                settings.ProductType,
                userId);

            decimal executedPrice = brokerResult.ExecutedPrice > 0m ? brokerResult.ExecutedPrice : currentLtp;
            string brokerOrderId = brokerResult.BrokerOrderId ?? $"KITE-SELL-{DateTime.UtcNow.Ticks}";

            decimal realizedPnl = (executedPrice - position.AverageEntryPrice) * position.Quantity;

            // Create Sell Order Record
            var sellOrder = await _repository.CreateOrderAsync(new RealOrder
            {
                UserId = userId,
                BrokerOrderId = brokerOrderId,
                Symbol = position.Symbol,
                Side = TradeSide.SELL,
                Quantity = position.Quantity,
                OrderType = PaperOrderType.Market,
                Price = executedPrice,
                Status = brokerResult.Success ? PaperOrderStatus.Filled : PaperOrderStatus.Rejected,
                FilledPrice = executedPrice,
                FilledAt = DateTime.UtcNow,
                RejectionReason = brokerResult.Success ? null : brokerResult.Message,
                Remarks = $"Real SELL ({exitReason}) - Broker ID: {brokerOrderId}"
            });

            if (!brokerResult.Success)
            {
                bool isTpinError = brokerResult.Message != null && 
                    (brokerResult.Message.Contains("e-DIS", StringComparison.OrdinalIgnoreCase) ||
                     brokerResult.Message.Contains("TPIN", StringComparison.OrdinalIgnoreCase) ||
                     brokerResult.Message.Contains("authorization", StringComparison.OrdinalIgnoreCase));

                string actionType = isTpinError ? "SELL_REJECTED_EDIS_REQUIRED" : "SELL_FAILED";

                await LogAuditAsync(position.Symbol, actionType, executedPrice, position.Quantity,
                    $"Zerodha Sell Order Failed: {brokerResult.Message}", userId);

                if (_hubContext != null && isTpinError)
                {
                    await _hubContext.Clients.All.SendAsync("ReceiveRealTradeAlert", new
                    {
                        symbol = position.Symbol,
                        side = "SELL_REJECTED",
                        isTpinError = true,
                        userId,
                        message = $"🚨 CDSL TPIN Required: Sell order for {position.Symbol} failed. Please authorize CDSL TPIN in Zerodha Kite holdings and retry."
                    });
                }
                return false;
            }

            // Close Real Position in DB & RAM
            await _repository.ClosePositionAsync(position.Id, executedPrice, realizedPnl, exitReason);
            _realTradeCache?.RemovePosition(position.Id);

            // Record Trade History
            await _repository.RecordTradeHistoryAsync(new RealTradeHistory
            {
                UserId = userId,
                OrderId = sellOrder.Id,
                BrokerOrderId = brokerOrderId,
                Symbol = position.Symbol,
                Side = TradeSide.SELL,
                Quantity = position.Quantity,
                EntryPrice = position.AverageEntryPrice,
                ExecutedPrice = executedPrice,
                RealizedPnl = realizedPnl,
                TradeType = TradeType.Auto,
                ExitReason = exitReason,
                Remarks = $"Real SELL: {exitReason} | Realized P&L: ₹{realizedPnl:F2}"
            });

            // Log Audit
            string pnlSign = realizedPnl >= 0 ? "+" : "";
            await LogAuditAsync(position.Symbol, "REAL_SELL", executedPrice, position.Quantity,
                $"⚡ Live SELL ({exitReason}) @ ₹{executedPrice:F2} | P&L: {pnlSign}₹{realizedPnl:N2} (Order #{brokerOrderId})", userId);

            // Broadcast SignalR Toast
            if (_hubContext != null)
            {
                await _hubContext.Clients.All.SendAsync("ReceiveRealTradeAlert", new
                {
                    symbol = position.Symbol,
                    side = "SELL",
                    quantity = position.Quantity,
                    price = executedPrice,
                    realizedPnl,
                    exitReason,
                    brokerOrderId,
                    userId,
                    message = $"⚡ LIVE REAL SELL: {position.Symbol} ({exitReason}) @ ₹{executedPrice:N2} | P&L: {pnlSign}₹{realizedPnl:N2}"
                });
            }

            await BroadcastDashboardUpdateAsync(userId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to execute real sell order for {Symbol} (User {UserId})", position.Symbol, userId);
            await LogAuditAsync(position.Symbol, "SYSTEM_ERROR", currentLtp, position.Quantity, $"Real SELL execution error: {ex.Message}", userId);
            return false;
        }
    }

    public async Task<int> SquareOffAllPositionsAsync(string reason = "Emergency Panic Kill Switch Triggered", int userId = 1)
    {
        _logger.LogWarning("EMERGENCY KILL SWITCH TRIGGERED for {UserId}. Reason: {Reason}", userId, reason);

        // Instantly turn OFF Real Auto Trading
        await _repository.ToggleRealTradeAsync(userId, false);
        string cacheKey = $"realtrade:settings:{userId}";
        await _cacheService.RemoveAsync(cacheKey);

        var openPositions = (await _repository.GetOpenPositionsAsync(userId)).ToList();
        int closedCount = 0;

        foreach (var pos in openPositions)
        {
            decimal exitPrice = pos.CurrentPrice > 0m ? pos.CurrentPrice : pos.AverageEntryPrice;
            bool closed = await ExecuteRealSellOrderAsync(pos, exitPrice, reason, userId);
            if (closed) closedCount++;
        }

        string userTag = await GetUserTagAsync(userId);
        await LogAuditAsync(userTag, "KILL_SWITCH_ACTIVE", null, closedCount,
            $"🚨 EMERGENCY KILL SWITCH EXECUTED: Bot Stopped, {closedCount} live positions squared off.", userId);

        await BroadcastDashboardUpdateAsync(userId);
        return closedCount;
    }

    public async Task<bool> SquareOffSinglePositionAsync(int positionId, string reason = "Manual Exit", int userId = 1)
    {
        var position = await _repository.GetOpenPositionByIdAsync(positionId);
        if (position == null) return false;

        decimal exitPrice = position.CurrentPrice > 0m ? position.CurrentPrice : position.AverageEntryPrice;
        return await ExecuteRealSellOrderAsync(position, exitPrice, reason, userId);
    }

    private async Task<string> GetUserTagAsync(int userId)
    {
        if (_userNameCache.TryGetValue(userId, out var cachedName) && !string.IsNullOrWhiteSpace(cachedName))
        {
            return cachedName;
        }

        if (_scopeFactory != null)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var userRepo = scope.ServiceProvider.GetService<IUserRepository>();
                if (userRepo != null)
                {
                    var user = await userRepo.GetByIdAsync(userId);
                    if (user != null)
                    {
                        string name = !string.IsNullOrWhiteSpace(user.Username) 
                            ? user.Username.ToUpper().Trim() 
                            : (!string.IsNullOrWhiteSpace(user.FullName) ? user.FullName.Split(' ')[0].ToUpper().Trim() : $"USER_{userId}");
                        _userNameCache[userId] = name;
                        return name;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not resolve username from DB for User {UserId}.", userId);
            }
        }

        return userId == 1 ? "CHIRAG" : $"USER_{userId}";
    }

    private static bool IsWithinTradingWindow(string startTime, string endTime)
    {
        var nowIst = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneHelper.IndianTimeZone);
        if (nowIst.DayOfWeek == DayOfWeek.Saturday || nowIst.DayOfWeek == DayOfWeek.Sunday)
            return false;

        if (!TimeSpan.TryParse(startTime, out var start)) start = new TimeSpan(9, 15, 0);
        if (!TimeSpan.TryParse(endTime, out var end)) end = new TimeSpan(15, 30, 0);

        var timeOfDay = nowIst.TimeOfDay;
        return timeOfDay >= start && timeOfDay <= end;
    }

    private async Task BroadcastDashboardUpdateAsync(int userId)
    {
        if (_hubContext != null)
        {
            try
            {
                var dashboard = await GetDashboardDataAsync(userId);
                await _hubContext.Clients.All.SendAsync("ReceiveRealTradeDashboardUpdate", dashboard);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to broadcast real trade dashboard SignalR update.");
            }
        }
    }

    public async Task<RealTradeLivePositionsFastDto> GetLivePositionsFastAsync(int userId = 1)
    {
        var tokenValidation = await _brokerService.ValidateSessionTokenAsync(userId);
        var positions = (await _repository.GetOpenPositionsAsync(userId)).ToList();
        decimal unrealizedPnl = positions.Sum(p => p.UnrealizedPnl);
        decimal todayRealizedPnl = await _repository.GetTodayRealizedPnlAsync(userId);

        decimal availableMargin = 0m;
        decimal usedMargin = 0m;
        ZerodhaPositionsDto? brokerPositions = null;
        List<ZerodhaHoldingDto>? brokerHoldings = null;
        decimal zerodhaM2m = 0m;
        decimal zerodhaRealizedPnl = 0m;
        decimal zerodhaUnrealizedPnl = 0m;

        if (tokenValidation.IsValid)
        {
            var marginTask = _brokerService.GetEquityMarginsAsync(userId);
            var posTask = _brokerService.GetLivePositionsAsync(userId);
            var holdTask = _brokerService.GetLiveHoldingsAsync(userId);

            await Task.WhenAll(marginTask, posTask, holdTask);

            var marginRes = await marginTask;
            if (marginRes.Success)
            {
                availableMargin = marginRes.AvailableCash;
                usedMargin = marginRes.UsedMargin;
            }

            var posRes = await posTask;
            if (posRes.Success && posRes.Positions != null)
            {
                brokerPositions = posRes.Positions;
                zerodhaM2m = posRes.Positions.TotalM2M;
                zerodhaRealizedPnl = posRes.Positions.TotalRealizedPnl;
                zerodhaUnrealizedPnl = posRes.Positions.TotalUnrealizedPnl;
            }

            var holdRes = await holdTask;
            if (holdRes.Success && holdRes.Holdings != null)
            {
                brokerHoldings = holdRes.Holdings;
            }
        }

        return new RealTradeLivePositionsFastDto
        {
            Success = true,
            IsBrokerTokenActive = tokenValidation.IsValid,
            AvailableBrokerMargin = availableMargin,
            UsedBrokerMargin = usedMargin,
            ZerodhaTotalM2M = zerodhaM2m,
            ZerodhaRealizedPnl = zerodhaRealizedPnl,
            ZerodhaUnrealizedPnl = zerodhaUnrealizedPnl,
            TotalUnrealizedPnl = unrealizedPnl,
            TotalRealizedPnlToday = todayRealizedPnl,
            BrokerPositions = brokerPositions,
            BrokerHoldings = brokerHoldings,
            OpenPositions = positions
        };
    }
}
