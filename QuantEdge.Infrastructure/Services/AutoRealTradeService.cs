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

    // Safety-net defaults for optional risk controls. Stop Loss and the Daily Loss Circuit
    // Breaker are treated as effectively mandatory for real-money positions: if a user has left
    // them unset, these fallbacks apply instead of running the position with no downside cap.
    private const decimal DefaultStopLossPctFallback = 3.00m;
    private const decimal DefaultDailyLossLimitFactor = 0.10m; // 10% of AvailableCapital
    private const int DefaultMaxConcurrentRealPositions = 10;

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

    public async Task<bool> EvaluateAndExecuteRealBuyAsync(string symbol, decimal entryPrice, int metConditionsCount, int userId = 1, bool isBuySignal = false,
        decimal? engineStopLoss = null, decimal? engineTarget = null)
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

        // 6. Daily Loss Circuit Breaker Check (mandatory - falls back to a conservative default
        // percentage of available capital if the user hasn't configured an explicit limit).
        decimal effectiveDailyLossLimit = settings.MaxDailyLossLimit.HasValue && settings.MaxDailyLossLimit.Value > 0
            ? Math.Abs(settings.MaxDailyLossLimit.Value)
            : Math.Max(1m, settings.AvailableCapital * DefaultDailyLossLimitFactor);

        decimal todayRealizedPnl = await _repository.GetTodayRealizedPnlAsync(userId);
        var openPositions = (await _repository.GetOpenPositionsAsync(userId)).ToList();
        decimal totalUnrealized = openPositions.Sum(p => p.UnrealizedPnl);
        decimal totalLoss = todayRealizedPnl + totalUnrealized;

        if (totalLoss <= -effectiveDailyLossLimit)
        {
            await LogAuditAsync(symbol, "CIRCUIT_BREAKER", entryPrice, 0,
                $"Daily loss limit ₹{effectiveDailyLossLimit:N2} breached (Total Loss: ₹{totalLoss:N2}). Pausing live bot.", userId);
            await ToggleRealTradeAsync(false, userId);
            return false;
        }

        // 6b. Portfolio-Level Exposure Cap - bounds the number of simultaneous open real
        // positions across all symbols, independent of the per-symbol duplicate check below.
        if (openPositions.Count >= DefaultMaxConcurrentRealPositions)
        {
            await LogAuditAsync(symbol, "REAL_SIGNAL_SKIPPED", entryPrice, 0,
                $"Portfolio exposure cap reached ({openPositions.Count}/{DefaultMaxConcurrentRealPositions} concurrent open positions)", userId);
            return false;
        }

        // 7. Duplicate Open Position Check
        var existingOpenPos = await _repository.GetOpenPositionBySymbolAsync(userId, symbol);
        if (existingOpenPos != null)
        {
            await LogAuditAsync(symbol, "REAL_SIGNAL_SKIPPED", entryPrice, 0,
                $"Symbol already has an OPEN real position (Position #{existingOpenPos.Id})", userId);
            return false;
        }

        // 7b. Duplicate Pending Order Check - a previous BUY for this symbol may still be resting,
        // unfilled, at the broker (no position exists for it yet since it hasn't confirmed COMPLETE).
        // Without this, the next scan pass would fire a second BUY for the same symbol before the
        // first one resolves. ReconcilePendingRealOrdersAsync will finalize the earlier one.
        var existingPendingBuy = await _repository.GetOpenBrokerOrderAsync(userId, symbol, TradeSide.BUY);
        if (existingPendingBuy != null)
        {
            _logger.LogInformation("Skipping BUY for {Symbol} (User {UserId}): order #{OrderId} is still OPEN at the broker awaiting fill.",
                symbol, userId, existingPendingBuy.BrokerOrderId);
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

        // 9. Target & Stop Loss - prefer the SwingDecisionEngine's own ATR-based levels (the
        // same numbers that earned the signal its Risk/Reward score) over a flat percentage of
        // entry price, since a flat % is disconnected from the volatility context that actually
        // qualified the trade. Falls back to the user's flat-% settings (mandatory-with-fallback,
        // see below) for entry paths that don't go through the scoring engine, e.g. manual buys.
        decimal takeProfit;
        if (engineTarget.HasValue && engineTarget.Value > entryPrice)
        {
            takeProfit = Math.Round(engineTarget.Value, 2);
        }
        else
        {
            decimal tpPct = Math.Abs(settings.ProfitTargetPct) / 100m;
            takeProfit = Math.Round(entryPrice * (1m + tpPct), 2);
        }

        decimal stopLoss;
        if (engineStopLoss.HasValue && engineStopLoss.Value > 0 && engineStopLoss.Value < entryPrice)
        {
            stopLoss = Math.Round(engineStopLoss.Value, 2);
        }
        else
        {
            decimal effectiveStopLossPct = settings.StopLossPct.HasValue && settings.StopLossPct.Value > 0
                ? Math.Abs(settings.StopLossPct.Value)
                : DefaultStopLossPctFallback;
            stopLoss = Math.Round(entryPrice * (1m - effectiveStopLossPct / 100m), 2);
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

            string brokerOrderId = brokerResult.BrokerOrderId ?? $"KITE-{DateTime.UtcNow.Ticks}";

            // Placing the order only means Kite accepted it for the exchange — it does NOT mean it has
            // traded. Confirm the real fill status before ever recording/announcing "FILLED".
            var statusCheck = await _brokerService.GetOrderStatusAsync(brokerOrderId, userId);
            bool confirmedComplete = statusCheck.Success && string.Equals(statusCheck.BrokerStatus, "COMPLETE", StringComparison.OrdinalIgnoreCase);
            bool confirmedRejected = statusCheck.Success &&
                (string.Equals(statusCheck.BrokerStatus, "REJECTED", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(statusCheck.BrokerStatus, "CANCELLED", StringComparison.OrdinalIgnoreCase));

            if (confirmedRejected)
            {
                await _repository.CreateOrderAsync(new RealOrder
                {
                    UserId = userId,
                    BrokerOrderId = brokerOrderId,
                    Symbol = symbol,
                    Side = TradeSide.BUY,
                    Quantity = quantity,
                    OrderType = PaperOrderType.Market,
                    Price = entryPrice,
                    StopLoss = stopLoss,
                    TakeProfit = takeProfit,
                    Status = PaperOrderStatus.Rejected,
                    FilledPrice = 0m,
                    RejectionReason = statusCheck.Message,
                    Remarks = $"[LIVE REAL MONEY] Zerodha Order #{brokerOrderId} (Met {metConditionsCount}/11)"
                });

                await LogAuditAsync(symbol, "ORDER_REJECTED", entryPrice, quantity,
                    $"Zerodha BUY Order {statusCheck.BrokerStatus}: {statusCheck.Message} (Order #{brokerOrderId})", userId);
                return false;
            }

            if (!confirmedComplete)
            {
                // Order accepted by the broker but still resting (OPEN / TRIGGER PENDING), or the status
                // check itself couldn't confirm a fill. Record it as Open with no position created yet —
                // ReconcilePendingRealOrdersAsync opens the position once the broker confirms the real fill.
                await _repository.CreateOrderAsync(new RealOrder
                {
                    UserId = userId,
                    BrokerOrderId = brokerOrderId,
                    Symbol = symbol,
                    Side = TradeSide.BUY,
                    Quantity = quantity,
                    OrderType = PaperOrderType.Market,
                    Price = entryPrice,
                    StopLoss = stopLoss,
                    TakeProfit = takeProfit,
                    Status = PaperOrderStatus.Open,
                    FilledPrice = 0m,
                    Remarks = $"[LIVE REAL MONEY] Zerodha Order #{brokerOrderId} (Met {metConditionsCount}/11)"
                });

                await LogAuditAsync(symbol, "BUY_ORDER_OPEN", entryPrice, quantity,
                    $"🕓 BUY order placed for {symbol} — Status: OPEN, awaiting execution (Order #{brokerOrderId})", userId);

                if (_hubContext != null)
                {
                    await _hubContext.Clients.All.SendAsync("ReceiveRealTradeAlert", new
                    {
                        symbol,
                        side = "BUY_OPEN",
                        quantity,
                        price = entryPrice,
                        brokerOrderId,
                        userId,
                        message = $"🕓 BUY order for {symbol} is OPEN at the broker (not yet filled) — Order #{brokerOrderId}"
                    });
                }

                await BroadcastDashboardUpdateAsync(userId);
                return true;
            }

            decimal executedPrice = statusCheck.AveragePrice > 0m ? statusCheck.AveragePrice : (brokerResult.ExecutedPrice > 0m ? brokerResult.ExecutedPrice : entryPrice);

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
            string slText = $"₹{stopLoss:F2}";
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

    public async Task<(bool Success, string Message)> EnableHoldingMonitoringAsync(string symbol, int quantity, decimal averagePrice, decimal targetPrice, int userId = 1)
    {
        symbol = symbol.ToUpper().Trim();

        if (quantity <= 0 || averagePrice <= 0 || targetPrice <= 0)
        {
            return (false, "Quantity, average price, and target price must all be greater than zero.");
        }

        if (targetPrice <= averagePrice)
        {
            return (false, $"Target price (₹{targetPrice:F2}) must be above the average buy price (₹{averagePrice:F2}).");
        }

        var existingOpenPos = await _repository.GetOpenPositionBySymbolAsync(userId, symbol);
        if (existingOpenPos != null)
        {
            return (false, $"{symbol} already has an OPEN monitored position (Position #{existingOpenPos.Id}).");
        }

        var settings = await GetSettingsAsync(userId);
        decimal effectiveStopLossPct = settings.StopLossPct.HasValue && settings.StopLossPct.Value > 0
            ? Math.Abs(settings.StopLossPct.Value)
            : DefaultStopLossPctFallback;
        decimal stopLoss = Math.Round(averagePrice * (1m - effectiveStopLossPct / 100m), 2);

        var newPosition = await _repository.UpsertPositionAsync(new RealPosition
        {
            UserId = userId,
            Symbol = symbol,
            Side = TradeSide.BUY,
            Quantity = quantity,
            AverageEntryPrice = averagePrice,
            CurrentPrice = averagePrice,
            UnrealizedPnl = 0m,
            StopLoss = stopLoss,
            TakeProfit = targetPrice,
            TrailingStopLoss = null,
            Status = PositionStatus.OPEN,
            TradeType = TradeType.Auto,
            RealizedPnl = 0m
        });

        _realTradeCache?.AddOrUpdatePosition(newPosition);

        await LogAuditAsync(symbol, "HOLDING_MONITOR_ENABLED", targetPrice, quantity,
            $"📦 Zerodha Holding enrolled for auto-sell monitoring (Qty: {quantity}, Avg: ₹{averagePrice:F2}, Target: ₹{targetPrice:F2})", userId);

        if (_hubContext != null)
        {
            await _hubContext.Clients.Group($"user-{userId}").SendAsync("ReceiveHoldingMonitorUpdate", new
            {
                symbol,
                quantity,
                averagePrice,
                targetPrice,
                positionId = newPosition.Id,
                userId,
                message = $"📦 {symbol} is now being auto-monitored for a sell at ₹{targetPrice:N2}."
            });
        }

        await BroadcastDashboardUpdateAsync(userId);
        return (true, $"{symbol} is now being monitored. It will be sold automatically once it reaches ₹{targetPrice:F2}.");
    }

    public async Task<(bool Success, string Message)> ManualSellAsync(string symbol, int quantity, decimal currentPrice, string? product, decimal? entryPriceHint, string reason, int userId = 1)
    {
        symbol = symbol.ToUpper().Trim();
        if (string.IsNullOrWhiteSpace(symbol) || quantity <= 0 || currentPrice <= 0m)
        {
            return (false, "A valid symbol, quantity, and current price are required.");
        }

        var tokenCheck = await _brokerService.ValidateSessionTokenAsync(userId);
        if (!tokenCheck.IsValid)
        {
            return (false, tokenCheck.Message ?? "Zerodha session is not active.");
        }

        // If the bot is already tracking this symbol as an open position, route through the existing
        // square-off pipeline so P&L, position closing, and trade history stay fully consistent.
        var trackedPosition = await _repository.GetOpenPositionBySymbolAsync(userId, symbol);
        if (trackedPosition != null)
        {
            bool closed = await SquareOffSinglePositionAsync(trackedPosition.Id, reason, userId);
            return (closed, closed
                ? $"SELL order submitted for {symbol}."
                : $"SELL order for {symbol} was not placed — it may already have an order resting OPEN at the broker. Check the Real Orders Book.");
        }

        // Otherwise this is a plain Zerodha Holding/Position the bot isn't tracking - sell it directly.
        var existingPendingOrder = await _repository.GetOpenBrokerOrderAsync(userId, symbol, TradeSide.SELL);
        if (existingPendingOrder != null)
        {
            return (false, $"A SELL order for {symbol} is already OPEN at the broker (Order #{existingPendingOrder.BrokerOrderId}), awaiting fill.");
        }

        string cleanProduct = string.IsNullOrWhiteSpace(product) ? "CNC" : product.ToUpper().Trim();

        try
        {
            var brokerResult = await _brokerService.PlaceLiveOrderAsync(symbol, TradeSide.SELL, quantity, PaperOrderType.Market, currentPrice, cleanProduct, userId);

            if (!brokerResult.Success)
            {
                // Placement itself was rejected by Zerodha - no real broker order was ever created, so
                // there is no broker order ID to record (and none should be fabricated: a made-up
                // "KITE-SELL-..." string invites clicking Resync on it later, which just fails with
                // "Invalid order_id" since Zerodha never issued one). Surface the real reason instead.
                await _repository.CreateOrderAsync(new RealOrder
                {
                    UserId = userId,
                    Symbol = symbol,
                    Side = TradeSide.SELL,
                    Quantity = quantity,
                    OrderType = PaperOrderType.Market,
                    Price = currentPrice,
                    Status = PaperOrderStatus.Rejected,
                    FilledPrice = 0m,
                    RejectionReason = brokerResult.Message,
                    TradeType = TradeType.Manual,
                    Remarks = $"Manual SELL ({reason}) Rejected by Zerodha: {brokerResult.Message}"
                });

                await LogAuditAsync(symbol, "SELL_FAILED", currentPrice, quantity, $"Manual Sell Failed: {brokerResult.Message}", userId);
                return (false, brokerResult.Message ?? "Zerodha rejected the sell order.");
            }

            string brokerOrderId = brokerResult.BrokerOrderId ?? $"KITE-SELL-{DateTime.UtcNow.Ticks}";

            // Placing the order only means Kite accepted it for the exchange - confirm the real fill
            // status before ever recording/announcing "FILLED".
            var statusCheck = await _brokerService.GetOrderStatusAsync(brokerOrderId, userId);
            bool confirmedComplete = statusCheck.Success && string.Equals(statusCheck.BrokerStatus, "COMPLETE", StringComparison.OrdinalIgnoreCase);
            bool confirmedRejected = statusCheck.Success &&
                (string.Equals(statusCheck.BrokerStatus, "REJECTED", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(statusCheck.BrokerStatus, "CANCELLED", StringComparison.OrdinalIgnoreCase));

            if (confirmedRejected)
            {
                await _repository.CreateOrderAsync(new RealOrder
                {
                    UserId = userId,
                    BrokerOrderId = brokerOrderId,
                    Symbol = symbol,
                    Side = TradeSide.SELL,
                    Quantity = quantity,
                    OrderType = PaperOrderType.Market,
                    Price = currentPrice,
                    Status = PaperOrderStatus.Rejected,
                    FilledPrice = 0m,
                    RejectionReason = statusCheck.Message,
                    TradeType = TradeType.Manual,
                    Remarks = $"Manual SELL ({reason}) - Broker ID: {brokerOrderId}"
                });

                await LogAuditAsync(symbol, "SELL_FAILED", currentPrice, quantity,
                    $"Manual Sell Order {statusCheck.BrokerStatus}: {statusCheck.Message} (Order #{brokerOrderId})", userId);
                return (false, $"Sell order {statusCheck.BrokerStatus}: {statusCheck.Message}");
            }

            if (!confirmedComplete)
            {
                // Order accepted by the broker but still resting (OPEN / TRIGGER PENDING). Recorded as
                // Open - ReconcilePendingRealOrdersAsync finalizes it once the broker confirms the fill.
                await _repository.CreateOrderAsync(new RealOrder
                {
                    UserId = userId,
                    BrokerOrderId = brokerOrderId,
                    Symbol = symbol,
                    Side = TradeSide.SELL,
                    Quantity = quantity,
                    OrderType = PaperOrderType.Market,
                    Price = currentPrice,
                    Status = PaperOrderStatus.Open,
                    FilledPrice = 0m,
                    TradeType = TradeType.Manual,
                    Remarks = $"Manual SELL ({reason}) - Broker ID: {brokerOrderId}"
                });

                await LogAuditAsync(symbol, "SELL_ORDER_OPEN", currentPrice, quantity,
                    $"🕓 Manual SELL order placed for {symbol} — Status: OPEN, awaiting execution (Order #{brokerOrderId})", userId);

                if (_hubContext != null)
                {
                    await _hubContext.Clients.All.SendAsync("ReceiveRealTradeAlert", new
                    {
                        symbol,
                        side = "SELL_OPEN",
                        quantity,
                        price = currentPrice,
                        brokerOrderId,
                        userId,
                        message = $"🕓 Manual SELL order for {symbol} is OPEN at the broker (not yet filled) — Order #{brokerOrderId}"
                    });
                }

                await BroadcastDashboardUpdateAsync(userId);
                return (true, $"SELL order placed for {symbol} — currently OPEN at the broker, awaiting execution.");
            }

            decimal executedPrice = statusCheck.AveragePrice > 0m ? statusCheck.AveragePrice : (brokerResult.ExecutedPrice > 0m ? brokerResult.ExecutedPrice : currentPrice);
            decimal? realizedPnl = entryPriceHint.HasValue && entryPriceHint.Value > 0m ? (executedPrice - entryPriceHint.Value) * quantity : (decimal?)null;

            var order = await _repository.CreateOrderAsync(new RealOrder
            {
                UserId = userId,
                BrokerOrderId = brokerOrderId,
                Symbol = symbol,
                Side = TradeSide.SELL,
                Quantity = quantity,
                OrderType = PaperOrderType.Market,
                Price = executedPrice,
                Status = PaperOrderStatus.Filled,
                FilledPrice = executedPrice,
                FilledAt = DateTime.UtcNow,
                TradeType = TradeType.Manual,
                Remarks = $"Manual SELL ({reason}) - Broker ID: {brokerOrderId}"
            });

            await _repository.RecordTradeHistoryAsync(new RealTradeHistory
            {
                UserId = userId,
                OrderId = order.Id,
                BrokerOrderId = brokerOrderId,
                Symbol = symbol,
                Side = TradeSide.SELL,
                Quantity = quantity,
                EntryPrice = entryPriceHint ?? 0m,
                ExecutedPrice = executedPrice,
                RealizedPnl = realizedPnl ?? 0m,
                TradeType = TradeType.Manual,
                ExitReason = reason,
                Remarks = realizedPnl.HasValue
                    ? $"Manual SELL: {reason} | Realized P&L: ₹{realizedPnl.Value:F2}"
                    : $"Manual SELL: {reason}"
            });

            string pnlText = realizedPnl.HasValue ? $" | P&L: {(realizedPnl.Value >= 0 ? "+" : "")}₹{realizedPnl.Value:N2}" : "";
            await LogAuditAsync(symbol, "REAL_SELL", executedPrice, quantity,
                $"⚡ Manual Live SELL ({reason}) @ ₹{executedPrice:F2}{pnlText} (Order #{brokerOrderId})", userId);

            if (_hubContext != null)
            {
                await _hubContext.Clients.All.SendAsync("ReceiveRealTradeAlert", new
                {
                    symbol,
                    side = "SELL",
                    quantity,
                    price = executedPrice,
                    realizedPnl,
                    reason,
                    brokerOrderId,
                    userId,
                    message = $"⚡ MANUAL REAL SELL: {symbol} @ ₹{executedPrice:N2}{pnlText}"
                });
            }

            await BroadcastDashboardUpdateAsync(userId);
            return (true, $"SELL order filled for {symbol} @ ₹{executedPrice:F2}.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Manual sell failed for {Symbol} (User {UserId})", symbol, userId);
            await LogAuditAsync(symbol, "SYSTEM_ERROR", currentPrice, quantity, $"Manual SELL execution error: {ex.Message}", userId);
            return (false, $"Manual sell failed: {ex.Message}");
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

    private enum RealSellOutcome { Failed, Filled, OrderOpenPending, AlreadyPending }

    private async Task<bool> ExecuteRealSellOrderAsync(RealPosition position, decimal currentLtp, string exitReason, int userId)
        => (await ExecuteRealSellOrderCoreAsync(position, currentLtp, exitReason, userId)) is RealSellOutcome.Filled or RealSellOutcome.OrderOpenPending;

    private async Task<RealSellOutcome> ExecuteRealSellOrderCoreAsync(RealPosition position, decimal currentLtp, string exitReason, int userId)
    {
        var settings = await GetSettingsAsync(userId);
        try
        {
            // A previous exit attempt for this position may still be resting, unfilled, at the broker
            // (e.g. a limit sell whose price hasn't been touched yet). Placing another one here would
            // double-sell the same shares once both eventually fill, so skip until it resolves —
            // the reconciliation pass (ReconcilePendingRealOrdersAsync) will pick it up and either
            // finalize it as Filled or clear it as Cancelled/Rejected.
            var existingPendingOrder = await _repository.GetOpenBrokerOrderAsync(userId, position.Symbol, TradeSide.SELL);
            if (existingPendingOrder != null)
            {
                _logger.LogInformation("Skipping SELL for {Symbol} (User {UserId}): order #{OrderId} is still OPEN at the broker awaiting fill.",
                    position.Symbol, userId, existingPendingOrder.BrokerOrderId);
                return RealSellOutcome.AlreadyPending;
            }

            _logger.LogInformation("[REAL MONEY SELL TRIGGERED - User {UserId}] Position #{Id} {Symbol} Qty:{Qty} @ {Ltp}. Reason: {Reason}",
                userId, position.Id, position.Symbol, position.Quantity, currentLtp, exitReason);

            // Execute Real Market Sell via Zerodha Kite API
            var brokerResult = await _brokerService.SquareOffLivePositionAsync(
                position.Symbol,
                position.Quantity,
                position.Side,
                currentLtp,
                settings.ProductType,
                userId);

            if (!brokerResult.Success)
            {
                // Placement itself was rejected by Zerodha - no real broker order was ever created, so
                // there is no broker order ID to record (and none should be fabricated: a made-up
                // "KITE-SELL-..." string invites clicking Resync on it later, which just fails with
                // "Invalid order_id" since Zerodha never issued one). Surface the real reason instead.
                decimal rejectedPrice = brokerResult.ExecutedPrice > 0m ? brokerResult.ExecutedPrice : currentLtp;

                await _repository.CreateOrderAsync(new RealOrder
                {
                    UserId = userId,
                    Symbol = position.Symbol,
                    Side = TradeSide.SELL,
                    Quantity = position.Quantity,
                    OrderType = PaperOrderType.Market,
                    Price = rejectedPrice,
                    Status = PaperOrderStatus.Rejected,
                    FilledPrice = 0m,
                    RejectionReason = brokerResult.Message,
                    Remarks = $"Real SELL ({exitReason}) Rejected by Zerodha: {brokerResult.Message}"
                });

                bool isTpinError = brokerResult.Message != null &&
                    (brokerResult.Message.Contains("e-DIS", StringComparison.OrdinalIgnoreCase) ||
                     brokerResult.Message.Contains("TPIN", StringComparison.OrdinalIgnoreCase) ||
                     brokerResult.Message.Contains("authorization", StringComparison.OrdinalIgnoreCase));

                string actionType = isTpinError ? "SELL_REJECTED_EDIS_REQUIRED" : "SELL_FAILED";

                await LogAuditAsync(position.Symbol, actionType, rejectedPrice, position.Quantity,
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
                return RealSellOutcome.Failed;
            }

            string brokerOrderId = brokerResult.BrokerOrderId ?? $"KITE-SELL-{DateTime.UtcNow.Ticks}";

            // Placing the order only means Kite accepted it for the exchange — it does NOT mean it has
            // traded. Confirm the real fill status before ever recording/announcing "FILLED".
            var statusCheck = await _brokerService.GetOrderStatusAsync(brokerOrderId, userId);
            bool confirmedComplete = statusCheck.Success && string.Equals(statusCheck.BrokerStatus, "COMPLETE", StringComparison.OrdinalIgnoreCase);
            bool confirmedRejected = statusCheck.Success &&
                (string.Equals(statusCheck.BrokerStatus, "REJECTED", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(statusCheck.BrokerStatus, "CANCELLED", StringComparison.OrdinalIgnoreCase));

            if (confirmedRejected)
            {
                await _repository.CreateOrderAsync(new RealOrder
                {
                    UserId = userId,
                    BrokerOrderId = brokerOrderId,
                    Symbol = position.Symbol,
                    Side = TradeSide.SELL,
                    Quantity = position.Quantity,
                    OrderType = PaperOrderType.Market,
                    Price = currentLtp,
                    Status = PaperOrderStatus.Rejected,
                    FilledPrice = 0m,
                    RejectionReason = statusCheck.Message,
                    Remarks = $"Real SELL ({exitReason}) - Broker ID: {brokerOrderId}"
                });

                await LogAuditAsync(position.Symbol, "SELL_FAILED", currentLtp, position.Quantity,
                    $"Zerodha Sell Order {statusCheck.BrokerStatus}: {statusCheck.Message} (Order #{brokerOrderId})", userId);

                return RealSellOutcome.Failed;
            }

            if (!confirmedComplete)
            {
                // Order accepted by the broker but still resting (OPEN / TRIGGER PENDING), or the status
                // check itself couldn't confirm a fill. Record it as Open and leave the position open —
                // ReconcilePendingRealOrdersAsync finalizes it once the broker confirms the real outcome.
                await _repository.CreateOrderAsync(new RealOrder
                {
                    UserId = userId,
                    BrokerOrderId = brokerOrderId,
                    Symbol = position.Symbol,
                    Side = TradeSide.SELL,
                    Quantity = position.Quantity,
                    OrderType = PaperOrderType.Market,
                    Price = currentLtp,
                    Status = PaperOrderStatus.Open,
                    FilledPrice = 0m,
                    Remarks = $"Real SELL ({exitReason}) - Broker ID: {brokerOrderId}"
                });

                await LogAuditAsync(position.Symbol, "SELL_ORDER_OPEN", currentLtp, position.Quantity,
                    $"🕓 SELL order placed for {position.Symbol} ({exitReason}) — Status: OPEN, awaiting execution (Order #{brokerOrderId})", userId);

                if (_hubContext != null)
                {
                    await _hubContext.Clients.All.SendAsync("ReceiveRealTradeAlert", new
                    {
                        symbol = position.Symbol,
                        side = "SELL_OPEN",
                        quantity = position.Quantity,
                        price = currentLtp,
                        exitReason,
                        brokerOrderId,
                        userId,
                        message = $"🕓 SELL order for {position.Symbol} is OPEN at the broker (not yet filled) — Order #{brokerOrderId}"
                    });
                }

                await BroadcastDashboardUpdateAsync(userId);
                return RealSellOutcome.OrderOpenPending;
            }

            decimal executedPrice = statusCheck.AveragePrice > 0m ? statusCheck.AveragePrice : (brokerResult.ExecutedPrice > 0m ? brokerResult.ExecutedPrice : currentLtp);
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
                Status = PaperOrderStatus.Filled,
                FilledPrice = executedPrice,
                FilledAt = DateTime.UtcNow,
                Remarks = $"Real SELL ({exitReason}) - Broker ID: {brokerOrderId}"
            });

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

                await _hubContext.Clients.Group($"user-{userId}").SendAsync("ReceiveHoldingSoldEvent", new
                {
                    symbol = position.Symbol,
                    quantity = position.Quantity,
                    price = executedPrice,
                    realizedPnl,
                    exitReason,
                    brokerOrderId,
                    userId
                });
            }

            await BroadcastDashboardUpdateAsync(userId);
            return RealSellOutcome.Filled;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to execute real sell order for {Symbol} (User {UserId})", position.Symbol, userId);
            await LogAuditAsync(position.Symbol, "SYSTEM_ERROR", currentLtp, position.Quantity, $"Real SELL execution error: {ex.Message}", userId);
            return RealSellOutcome.Failed;
        }
    }

    public async Task ReconcilePendingRealOrdersAsync()
    {
        var pendingOrders = (await _repository.GetAllPendingBrokerOrdersAsync()).ToList();
        if (!pendingOrders.Any()) return;

        foreach (var order in pendingOrders)
        {
            if (string.IsNullOrWhiteSpace(order.BrokerOrderId)) continue;

            try
            {
                var statusCheck = await _brokerService.GetOrderStatusAsync(order.BrokerOrderId, order.UserId);
                if (!statusCheck.Success || string.IsNullOrWhiteSpace(statusCheck.BrokerStatus))
                {
                    continue; // Broker/session unavailable this cycle - retry next pass, order stays Open.
                }

                if (string.Equals(statusCheck.BrokerStatus, "COMPLETE", StringComparison.OrdinalIgnoreCase))
                {
                    decimal executedPrice = statusCheck.AveragePrice > 0m ? statusCheck.AveragePrice : order.Price;
                    await FinalizeFilledOrderAsync(order, executedPrice);
                }
                else if (string.Equals(statusCheck.BrokerStatus, "REJECTED", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(statusCheck.BrokerStatus, "CANCELLED", StringComparison.OrdinalIgnoreCase))
                {
                    await FinalizeRejectedOrderAsync(order, statusCheck.BrokerStatus, statusCheck.Message);
                }
                // Otherwise still OPEN/TRIGGER PENDING at the broker - leave as-is, retry next cycle.
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error reconciling pending real order #{OrderId} ({Symbol}, User {UserId})", order.Id, order.Symbol, order.UserId);
            }
        }
    }

    /// <summary>
    /// Force re-verifies one order's status directly against Zerodha and corrects our records if they've
    /// drifted from the broker's truth - including an order that was already recorded Filled/Rejected here
    /// but the broker still shows resting (e.g. one placed before the broker-confirmed-fill logic existed).
    /// Unlike <see cref="ReconcilePendingRealOrdersAsync"/> (which only scans Open orders every cycle),
    /// this can be triggered on demand for any single order from the Real Orders Book.
    /// </summary>
    public async Task<(bool Success, string Message)> ResyncOrderStatusAsync(int orderId, int userId = 1)
    {
        var order = await _repository.GetOrderByIdAsync(orderId);
        if (order == null) return (false, "Order not found.");
        if (order.UserId != userId) return (false, "This order does not belong to the current user.");

        var (success, _, message) = await ResyncSingleOrderAsync(order);
        return (success, message);
    }

    /// <summary>
    /// Resyncs every recent order for a user against Zerodha in one pass - the same broker-truth check
    /// <see cref="ResyncOrderStatusAsync"/> does for one order, applied to the whole Real Orders Book at
    /// once. This is what "Sync Now" runs so it actually re-verifies order status with the broker instead
    /// of just re-reading whatever QuantEdge's own records currently say.
    /// </summary>
    public async Task<(bool Success, string Message)> ResyncRecentOrdersAsync(int userId = 1)
    {
        var orders = (await _repository.GetRecentOrdersAsync(userId, 20))
            .Where(o => !string.IsNullOrWhiteSpace(o.BrokerOrderId))
            .ToList();

        if (!orders.Any())
        {
            return (true, "No recent orders with a broker order ID to resync.");
        }

        int correctedCount = 0;
        int failedCount = 0;

        foreach (var order in orders)
        {
            try
            {
                var (success, corrected, _) = await ResyncSingleOrderAsync(order);
                if (!success) failedCount++;
                else if (corrected) correctedCount++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error resyncing order #{OrderId} ({Symbol}) during bulk resync for User {UserId}", order.Id, order.Symbol, userId);
                failedCount++;
            }
        }

        string summary = correctedCount > 0
            ? $"Resynced {orders.Count} order(s) with Zerodha — {correctedCount} corrected to match the broker's real status."
            : $"Resynced {orders.Count} order(s) with Zerodha — all already matched the broker's real status.";
        if (failedCount > 0)
        {
            summary += $" ({failedCount} could not be verified right now — check Zerodha session/connectivity.)";
        }

        return (true, summary);
    }

    /// <summary>
    /// Force re-verifies one order's status directly against Zerodha and corrects our records if they've
    /// drifted from the broker's truth (e.g. an order recorded Filled/Rejected here that Zerodha still
    /// shows resting OPEN). Shared by the single-order Resync action and the bulk "Sync Now" resync.
    /// </summary>
    private async Task<(bool Success, bool Corrected, string Message)> ResyncSingleOrderAsync(RealOrder order)
    {
        if (string.IsNullOrWhiteSpace(order.BrokerOrderId))
        {
            return (false, false, $"{order.Symbol} order has no broker order ID to verify.");
        }

        var statusCheck = await _brokerService.GetOrderStatusAsync(order.BrokerOrderId, order.UserId);
        if (!statusCheck.Success || string.IsNullOrWhiteSpace(statusCheck.BrokerStatus))
        {
            return (false, false, statusCheck.Message ?? $"Could not verify {order.Symbol} order #{order.BrokerOrderId} with the broker right now.");
        }

        bool brokerComplete = string.Equals(statusCheck.BrokerStatus, "COMPLETE", StringComparison.OrdinalIgnoreCase);
        bool brokerRejectedOrCancelled = string.Equals(statusCheck.BrokerStatus, "REJECTED", StringComparison.OrdinalIgnoreCase) ||
                                         string.Equals(statusCheck.BrokerStatus, "CANCELLED", StringComparison.OrdinalIgnoreCase);

        if (brokerComplete)
        {
            if (order.Status == PaperOrderStatus.Filled)
            {
                return (true, false, $"Already in sync: {order.Symbol} order #{order.BrokerOrderId} is FILLED at the broker.");
            }

            decimal executedPrice = statusCheck.AveragePrice > 0m ? statusCheck.AveragePrice : order.Price;
            await FinalizeFilledOrderAsync(order, executedPrice);
            return (true, true, $"Corrected: {order.Symbol} order #{order.BrokerOrderId} is FILLED @ ₹{executedPrice:F2} at the broker. Records updated.");
        }

        if (brokerRejectedOrCancelled)
        {
            if (order.Status == PaperOrderStatus.Rejected)
            {
                return (true, false, $"Already in sync: {order.Symbol} order #{order.BrokerOrderId} is {statusCheck.BrokerStatus} at the broker.");
            }

            await FinalizeRejectedOrderAsync(order, statusCheck.BrokerStatus, statusCheck.Message);
            return (true, true, $"Corrected: {order.Symbol} order #{order.BrokerOrderId} is {statusCheck.BrokerStatus} at the broker. Records updated.");
        }

        // Broker still shows it resting (OPEN / TRIGGER PENDING).
        if (order.Status == PaperOrderStatus.Open)
        {
            return (true, false, $"Already in sync: {order.Symbol} order #{order.BrokerOrderId} is still OPEN at the broker, awaiting fill.");
        }

        // Was previously recorded Filled/Rejected here but the broker actually still shows it resting.
        // Correct the status back to Open so the periodic reconciler picks it up going forward. We
        // deliberately do NOT try to guess-reopen/re-close a position from this historical drift - if a
        // SELL had closed a bot position (or a BUY had opened one) based on the incorrect fill, that's
        // flagged here for manual review instead of auto-corrected.
        await _repository.UpdateOrderStatusAsync(order.Id, PaperOrderStatus.Open, 0m, order.BrokerOrderId);

        string positionNote = order.Side == TradeSide.SELL
            ? " This order previously showed as FILLED and may have closed a bot position that Zerodha never actually confirmed sold — check Zerodha Holdings and re-enable monitoring via 'Set Target' if the shares are still held."
            : " This order previously showed as FILLED and may have opened a bot position for shares that were never actually bought — check Bot Positions.";

        await LogAuditAsync(order.Symbol, order.Side == TradeSide.SELL ? "SELL_ORDER_OPEN" : "BUY_ORDER_OPEN", order.Price, order.Quantity,
            $"🔄 Status corrected: Order #{order.BrokerOrderId} for {order.Symbol} was recorded as {order.Status} but Zerodha confirms it is still OPEN (unfilled).{positionNote}", order.UserId);

        await BroadcastDashboardUpdateAsync(order.UserId);
        return (true, true, $"Corrected: {order.Symbol} order #{order.BrokerOrderId} is actually still OPEN at the broker (was recorded {order.Status}).{positionNote}");
    }

    private async Task FinalizeFilledOrderAsync(RealOrder order, decimal executedPrice)
    {
        await _repository.UpdateOrderStatusAsync(order.Id, PaperOrderStatus.Filled, executedPrice, order.BrokerOrderId);

        if (order.Side == TradeSide.SELL)
        {
            var position = await _repository.GetOpenPositionBySymbolAsync(order.UserId, order.Symbol);
            if (position != null)
            {
                decimal realizedPnl = (executedPrice - position.AverageEntryPrice) * position.Quantity;
                string exitReason = order.Remarks ?? "Exit";

                await _repository.ClosePositionAsync(position.Id, executedPrice, realizedPnl, exitReason);
                _realTradeCache?.RemovePosition(position.Id);

                await _repository.RecordTradeHistoryAsync(new RealTradeHistory
                {
                    UserId = order.UserId,
                    OrderId = order.Id,
                    BrokerOrderId = order.BrokerOrderId,
                    Symbol = order.Symbol,
                    Side = TradeSide.SELL,
                    Quantity = order.Quantity,
                    EntryPrice = position.AverageEntryPrice,
                    ExecutedPrice = executedPrice,
                    RealizedPnl = realizedPnl,
                    TradeType = TradeType.Auto,
                    ExitReason = exitReason,
                    Remarks = $"Real SELL: {exitReason} | Realized P&L: ₹{realizedPnl:F2}"
                });

                string pnlSign = realizedPnl >= 0 ? "+" : "";
                await LogAuditAsync(order.Symbol, "REAL_SELL", executedPrice, order.Quantity,
                    $"⚡ Live SELL confirmed FILLED @ ₹{executedPrice:F2} | P&L: {pnlSign}₹{realizedPnl:N2} (Order #{order.BrokerOrderId})", order.UserId);

                if (_hubContext != null)
                {
                    await _hubContext.Clients.All.SendAsync("ReceiveRealTradeAlert", new
                    {
                        symbol = order.Symbol,
                        side = "SELL",
                        quantity = order.Quantity,
                        price = executedPrice,
                        realizedPnl,
                        exitReason,
                        brokerOrderId = order.BrokerOrderId,
                        userId = order.UserId,
                        message = $"⚡ LIVE REAL SELL: {order.Symbol} confirmed FILLED @ ₹{executedPrice:N2} | P&L: {pnlSign}₹{realizedPnl:N2}"
                    });

                    await _hubContext.Clients.Group($"user-{order.UserId}").SendAsync("ReceiveHoldingSoldEvent", new
                    {
                        symbol = order.Symbol,
                        quantity = order.Quantity,
                        price = executedPrice,
                        realizedPnl,
                        exitReason,
                        brokerOrderId = order.BrokerOrderId,
                        userId = order.UserId
                    });
                }

                await BroadcastDashboardUpdateAsync(order.UserId);
            }
        }
        else if (order.Side == TradeSide.BUY)
        {
            // No position exists yet for a pending BUY - create it now that the broker has
            // confirmed the real fill, using the SL/TP calculated at signal time and stored
            // on the order. TrailingStopLoss starts null and self-populates on the next
            // position-monitor cycle, same as any other freshly opened position.
            var newPosition = await _repository.UpsertPositionAsync(new RealPosition
            {
                UserId = order.UserId,
                Symbol = order.Symbol,
                Side = TradeSide.BUY,
                Quantity = order.Quantity,
                AverageEntryPrice = executedPrice,
                CurrentPrice = executedPrice,
                UnrealizedPnl = 0m,
                StopLoss = order.StopLoss,
                TakeProfit = order.TakeProfit,
                TrailingStopLoss = null,
                Status = PositionStatus.OPEN,
                TradeType = TradeType.Auto,
                RealizedPnl = 0m
            });

            _realTradeCache?.AddOrUpdatePosition(newPosition);

            await _repository.RecordTradeHistoryAsync(new RealTradeHistory
            {
                UserId = order.UserId,
                OrderId = order.Id,
                BrokerOrderId = order.BrokerOrderId,
                Symbol = order.Symbol,
                Side = TradeSide.BUY,
                Quantity = order.Quantity,
                EntryPrice = executedPrice,
                ExecutedPrice = executedPrice,
                RealizedPnl = 0m,
                TradeType = TradeType.Auto,
                Remarks = $"Real BUY confirmed FILLED @ ₹{executedPrice:F2} (Broker ID: {order.BrokerOrderId})"
            });

            string todayKey = $"realtrade:today_count:{order.UserId}:{DateTime.UtcNow:yyyyMMdd}";
            await _cacheService.RemoveAsync(todayKey);

            await LogAuditAsync(order.Symbol, "REAL_BUY", executedPrice, order.Quantity,
                $"⚡ Live BUY confirmed FILLED @ ₹{executedPrice:F2} (Qty: {order.Quantity}, Order #{order.BrokerOrderId})", order.UserId);

            if (_hubContext != null)
            {
                await _hubContext.Clients.All.SendAsync("ReceiveRealTradeAlert", new
                {
                    symbol = order.Symbol,
                    side = "BUY",
                    quantity = order.Quantity,
                    price = executedPrice,
                    target = order.TakeProfit,
                    stopLoss = order.StopLoss,
                    brokerOrderId = order.BrokerOrderId,
                    userId = order.UserId,
                    message = $"⚡ LIVE REAL BUY: {order.Quantity} shares of {order.Symbol} confirmed FILLED @ ₹{executedPrice:N2}"
                });
            }

            await BroadcastDashboardUpdateAsync(order.UserId);
        }
    }

    private async Task FinalizeRejectedOrderAsync(RealOrder order, string? brokerStatus, string? message)
    {
        await _repository.UpdateOrderStatusAsync(order.Id, PaperOrderStatus.Rejected, 0m, order.BrokerOrderId, message);

        string actionType = order.Side == TradeSide.SELL ? "SELL_FAILED" : "ORDER_REJECTED";
        string followUp = order.Side == TradeSide.SELL ? "Position remains open for retry." : "No position was opened.";
        await LogAuditAsync(order.Symbol, actionType, order.Price, order.Quantity,
            $"Zerodha order #{order.BrokerOrderId} for {order.Symbol} ended as {brokerStatus}: {message}. {followUp}", order.UserId);
    }

    public async Task<int> SquareOffAllPositionsAsync(string reason = "Emergency Panic Kill Switch Triggered", int userId = 1)
    {
        _logger.LogWarning("EMERGENCY KILL SWITCH TRIGGERED for {UserId}. Reason: {Reason}", userId, reason);

        // Instantly turn OFF Real Auto Trading
        await _repository.ToggleRealTradeAsync(userId, false);
        string cacheKey = $"realtrade:settings:{userId}";
        await _cacheService.RemoveAsync(cacheKey);

        var openPositions = (await _repository.GetOpenPositionsAsync(userId)).ToList();
        int filledCount = 0;
        int pendingCount = 0;

        foreach (var pos in openPositions)
        {
            decimal exitPrice = pos.CurrentPrice > 0m ? pos.CurrentPrice : pos.AverageEntryPrice;
            var outcome = await ExecuteRealSellOrderCoreAsync(pos, exitPrice, reason, userId);
            if (outcome == RealSellOutcome.Filled) filledCount++;
            else if (outcome == RealSellOutcome.OrderOpenPending) pendingCount++;
        }

        string userTag = await GetUserTagAsync(userId);
        string summary = pendingCount > 0
            ? $"🚨 EMERGENCY KILL SWITCH EXECUTED: Bot Stopped, {filledCount} position(s) squared off, {pendingCount} SELL order(s) placed and still OPEN at the broker (awaiting fill)."
            : $"🚨 EMERGENCY KILL SWITCH EXECUTED: Bot Stopped, {filledCount} live positions squared off.";
        await LogAuditAsync(userTag, "KILL_SWITCH_ACTIVE", null, filledCount + pendingCount, summary, userId);

        await BroadcastDashboardUpdateAsync(userId);
        return filledCount;
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
