using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using QuantEdge.Domain.Entities;
using QuantEdge.Infrastructure.Constants;
using QuantEdge.Infrastructure.DTOs;
using QuantEdge.Infrastructure.Helpers;
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
    private readonly IHubBroadcastService? _hubBroadcast;
    private readonly IServiceScopeFactory? _scopeFactory;
    private readonly IWebSocketMarketDataService? _webSocketService;
    private readonly ILogger<AutoRealTradeService> _logger;
    private readonly ConcurrentDictionary<int, string> _userNameCache = new();

    // Buy/sell rules (entry gates, SL/Target/Trailing SL levels, exit policy) live in SwingTradeRules,
    // shared with Auto Paper Trading - this service only adds broker execution around them.

    // Gap-open handling for overnight CNC positions: a stop "hit" where the price has already moved
    // well past the trigger level (SwingTradeRules flags it as a gap) needs a wider exit price band
    // to have a realistic chance of filling immediately.
    public const decimal GapExitProtectionBufferPct = 0.02m;

    public AutoRealTradeService(
        IRealTradingRepository repository,
        IZerodhaKiteBrokerService brokerService,
        IZerodhaSessionRepository sessionRepository,
        IIndianHolidayRepository holidayRepository,
        IMarketHoursService marketHoursService,
        ICacheService cacheService,
        ILogger<AutoRealTradeService> logger,
        IRealTradeCacheService? realTradeCache = null,
        IHubBroadcastService? hubBroadcast = null,
        IServiceScopeFactory? scopeFactory = null,
        IWebSocketMarketDataService? webSocketService = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _brokerService = brokerService ?? throw new ArgumentNullException(nameof(brokerService));
        _sessionRepository = sessionRepository ?? throw new ArgumentNullException(nameof(sessionRepository));
        _holidayRepository = holidayRepository ?? throw new ArgumentNullException(nameof(holidayRepository));
        _marketHoursService = marketHoursService ?? throw new ArgumentNullException(nameof(marketHoursService));
        _cacheService = cacheService ?? throw new ArgumentNullException(nameof(cacheService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _realTradeCache = realTradeCache;
        _hubBroadcast = hubBroadcast;
        _scopeFactory = scopeFactory;
        _webSocketService = webSocketService;
    }

    // Ensures a real position's symbol is receiving live WebSocket ticks the moment it's opened,
    // regardless of whether it's part of the auto-scanner's subscribed universe (e.g. a manually
    // traded or holdings-enrolled symbol). Subscription is idempotent at the WebSocket service level,
    // so this is safe to call even if the symbol is already subscribed. Best-effort: a failure here
    // must not fail the buy - the position-monitor's LTP_UNAVAILABLE skip already covers this gap.
    private async Task EnsureSubscribedForExitMonitoringAsync(string symbol)
    {
        if (_webSocketService == null) return;
        try
        {
            await _webSocketService.SubscribeAsync(symbol, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to subscribe {Symbol} to the WebSocket feed for real-position exit monitoring.", symbol);
        }
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
        // StopLossPct/TrailingSlEnabled/TrailingSlPct are intentionally left untouched here - they are
        // no longer editable from the settings UI (SL/Trailing SL are now configured trade-wise from the
        // Manual Real Trade popup instead of as a global override; existing.StopLossPct/TrailingSlPct still
        // returned by GetSettingsAsync purely as the popup's pre-fill defaults).
        existing.MaxDurationDays = updateDto.MaxDurationDays;
        existing.MaxTradesPerDay = updateDto.MaxTradesPerDay;
        existing.FixedAmountPerTrade = updateDto.FixedAmountPerTrade;
        existing.MaxDailyLossLimit = updateDto.MaxDailyLossLimit; // Optional
        existing.ProductType = string.IsNullOrWhiteSpace(updateDto.ProductType) ? "CNC" : updateDto.ProductType.ToUpper();
        existing.MinConditionsMatch = updateDto.MinConditionsMatch;
        existing.TradingWindowStart = updateDto.TradingWindowStart ?? "09:15";
        existing.TradingWindowEnd = updateDto.TradingWindowEnd ?? "15:30";
        existing.EntryDelayMinutes = updateDto.EntryDelayMinutes;
        existing.ExitMode = string.IsNullOrWhiteSpace(updateDto.ExitMode) ? SwingTradeRules.ExitModeSwingClose : updateDto.ExitMode.ToUpperInvariant();
        existing.CloseCheckTime = string.IsNullOrWhiteSpace(updateDto.CloseCheckTime) ? "15:15" : updateDto.CloseCheckTime;
        existing.StopLossAtrMult = updateDto.StopLossAtrMult;
        existing.TrailAtrMult = updateDto.TrailAtrMult;
        existing.TargetAtrMult = updateDto.TargetAtrMult;
        existing.IsAutoShortEnabled = updateDto.IsAutoShortEnabled;
        existing.ShortEntryCutoff = string.IsNullOrWhiteSpace(updateDto.ShortEntryCutoff) ? "15:00" : updateDto.ShortEntryCutoff;
        existing.ShortSquareOffTime = string.IsNullOrWhiteSpace(updateDto.ShortSquareOffTime) ? "15:15" : updateDto.ShortSquareOffTime;

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

        decimal todayRealizedPnl = await _repository.GetTodayRealizedPnlAsync(userId);

        decimal todayTradeAmount = recentOrders
            // Filled entries: a long BUY, or the SELL that opened an Auto Short (a short's covering BUY is an exit).
            .Where(o => (o.IsShort ? o.Side == TradeSide.SELL : o.Side == TradeSide.BUY) && o.Status == PaperOrderStatus.Filled && o.CreatedAt >= DateTime.UtcNow.Date)
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

        ApplyLivePrices(positions, brokerPositions, brokerHoldings);
        decimal unrealizedPnl = positions.Sum(p => p.UnrealizedPnl);

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
            TotalEstimatedCharges = Math.Round(positions.Sum(p => p.EstimatedCharges), 2),
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

        if (_hubBroadcast != null)
        {
            await _hubBroadcast.BroadcastAllAsync("ReceiveRealTradeLogEvent", log);
        }
    }

    public async Task<IEnumerable<RealTradeExecutionLog>> GetTodayLogsAsync(int userId = 1, int limit = 50)
    {
        return await _repository.GetTodayLogsAsync(userId, limit);
    }

    public async Task<IEnumerable<RealTradeHistory>> GetTradeHistoryAsync(int userId = 1, int limit = 100, DateTime? date = null, string? symbol = null, int? side = null)
    {
        return await _repository.GetTradeHistoryAsync(userId, limit, date, symbol, side);
    }

    // Per-(user, symbol) locks guarding the Manual Real Trade path only, so a double-click on
    // "Place Real Trade" can't race two concurrent requests past the duplicate-position check
    // below before either has inserted a row. Auto-scan is single-threaded per worker cycle
    // already and is unaffected (this lock is only acquired when isManualTrade is true).
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> _manualBuyLocks = new();

    public async Task<bool> EvaluateAndExecuteRealBuyAsync(string symbol, decimal entryPrice, int metConditionsCount, int userId = 1, bool isBuySignal = false,
        decimal? engineStopLoss = null, decimal? engineTarget = null,
        bool isManualTrade = false, int? manualQuantity = null, decimal? manualStopLossPct = null, decimal? manualTrailingSlPct = null,
        decimal? dailyAtr = null)
    {
        if (!isManualTrade)
        {
            return await EvaluateAndExecuteRealBuyCoreAsync(symbol, entryPrice, metConditionsCount, userId, isBuySignal,
                engineStopLoss, engineTarget, dailyAtr, isManualTrade, manualQuantity, manualStopLossPct, manualTrailingSlPct);
        }

        string lockKey = $"{userId}:{symbol.ToUpper().Trim()}";
        var manualLock = _manualBuyLocks.GetOrAdd(lockKey, _ => new SemaphoreSlim(1, 1));
        if (!await manualLock.WaitAsync(TimeSpan.Zero))
        {
            _logger.LogInformation("Rejecting duplicate manual Real Trade request for {Symbol} (User {UserId}) - a previous request is still in flight.", symbol, userId);
            return false;
        }

        try
        {
            return await EvaluateAndExecuteRealBuyCoreAsync(symbol, entryPrice, metConditionsCount, userId, isBuySignal,
                engineStopLoss, engineTarget, dailyAtr, isManualTrade, manualQuantity, manualStopLossPct, manualTrailingSlPct);
        }
        finally
        {
            manualLock.Release();
        }
    }

    private async Task<bool> EvaluateAndExecuteRealBuyCoreAsync(string symbol, decimal entryPrice, int metConditionsCount, int userId, bool isBuySignal,
        decimal? engineStopLoss, decimal? engineTarget, decimal? dailyAtr,
        bool isManualTrade, int? manualQuantity, decimal? manualStopLossPct, decimal? manualTrailingSlPct)
    {
        symbol = symbol.ToUpper().Trim();
        var settings = await GetSettingsAsync(userId);

        // The guards below are mirrored read-only by PreviewBuyGuardsAsync (Signal Dashboard verdict
        // card) - keep both in the same order when a guard is added, removed or changed.

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

        // 3b. Opening Entry Delay - new BUY signals are held back for a configurable number of
        // minutes after the window opens, so the opening auction's gap/volatility can resolve
        // before capital is committed. Deliberately NOT applied to exits (EvaluateAndExecuteRealSellAsync)
        // - an existing position must always be able to stop out immediately, even during this delay.
        if (!IsPastEntryDelay(settings.TradingWindowStart, settings.EntryDelayMinutes))
        {
            await LogAuditAsync(symbol, "REAL_SIGNAL_SKIPPED", entryPrice, 0,
                $"Opening entry delay active - new BUY signals held back for {settings.EntryDelayMinutes} min after {settings.TradingWindowStart}", userId);
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
        decimal effectiveDailyLossLimit = SwingTradeRules.EffectiveDailyLossLimit(settings.MaxDailyLossLimit, settings.AvailableCapital);

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
        if (openPositions.Count >= SwingTradeRules.MaxConcurrentPositions)
        {
            await LogAuditAsync(symbol, "REAL_SIGNAL_SKIPPED", entryPrice, 0,
                $"Portfolio exposure cap reached ({openPositions.Count}/{SwingTradeRules.MaxConcurrentPositions} concurrent open positions)", userId);
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

        // ...and the same for a SELL still resting for this symbol - e.g. an Auto Short entry not filled yet (no position
        // exists for it until it fills). Buying now could leave the account both long and short in one stock.
        var existingPendingSell = await _repository.GetOpenBrokerOrderAsync(userId, symbol, TradeSide.SELL);
        if (existingPendingSell != null)
        {
            _logger.LogInformation("Skipping BUY for {Symbol} (User {UserId}): SELL order #{OrderId} is still OPEN at the broker awaiting fill.",
                symbol, userId, existingPendingSell.BrokerOrderId);
            return false;
        }

        // 8. Capital & Margin Validation - the generic FixedAmountPerTrade check only applies to the
        // auto-sized quantity; a manual trade's own requested spend is validated below instead, once
        // its final quantity and re-checked live entry price are known.
        var marginResult = await _brokerService.GetEquityMarginsAsync(userId);
        decimal availableMargin = marginResult.Success ? marginResult.AvailableCash : settings.AvailableCapital;

        if (!isManualTrade && availableMargin < settings.FixedAmountPerTrade)
        {
            await LogAuditAsync(symbol, "REAL_SIGNAL_SKIPPED", entryPrice, 0,
                $"Insufficient Broker Capital (₹{availableMargin:N2} < Trade Amount ₹{settings.FixedAmountPerTrade:N2})", userId);
            return false;
        }

        // 8b. Live Quote Re-check - entryPrice above is from the 15-minute scan cycle's candle
        // close, which can be stale by the time all the risk gates above finish evaluating.
        // Refresh against a live LTP immediately before sizing/order placement so the trade
        // executes against a current price rather than one that's already moved. Engine-supplied
        // ATR-based SL/Target are shifted by the same delta to preserve their original risk
        // distance instead of silently changing the R:R the signal was scored on.
        decimal preLiveEntryPrice = entryPrice;
        var ltpResult = await _brokerService.GetLtpQuotesAsync(new[] { (symbol, "NSE") }, userId);
        if (ltpResult.Success && ltpResult.Ltps != null && ltpResult.Ltps.TryGetValue(symbol, out var liveLtp) && liveLtp > 0m)
        {
            // Guard against a signal that's already moved away from what qualified it (dropped, or
            // run up so far that buying now would chase a local high) - shared with Auto Paper Trading.
            string? driftReason = SwingTradeRules.CheckSignalDrift(preLiveEntryPrice, liveLtp);
            if (driftReason != null)
            {
                await LogAuditAsync(symbol, "REAL_SIGNAL_SKIPPED", preLiveEntryPrice, 0, driftReason, userId);
                return false;
            }

            decimal priceDelta = liveLtp - preLiveEntryPrice;
            if (engineStopLoss.HasValue) engineStopLoss = engineStopLoss.Value + priceDelta;
            if (engineTarget.HasValue) engineTarget = engineTarget.Value + priceDelta;
            entryPrice = liveLtp;
        }
        // If the live quote fetch fails (network/token hiccup), proceed with the original scan-time
        // entryPrice rather than blocking a trade that already passed every risk gate over a
        // secondary, best-effort check.

        int quantity;
        if (isManualTrade)
        {
            // Quantity is user-chosen from the Manual Real Trade popup - no longer auto-sized.
            if (!manualQuantity.HasValue || manualQuantity.Value < 1)
            {
                await LogAuditAsync(symbol, "REAL_SIGNAL_SKIPPED", entryPrice, 0,
                    "Manual trade rejected: Quantity must be a positive whole number.", userId);
                return false;
            }
            quantity = manualQuantity.Value;

            decimal requiredCapital = quantity * entryPrice;
            if (availableMargin < requiredCapital)
            {
                await LogAuditAsync(symbol, "REAL_SIGNAL_SKIPPED", entryPrice, 0,
                    $"Insufficient Broker Capital for requested quantity (₹{availableMargin:N2} < ₹{requiredCapital:N2} for {quantity} @ ₹{entryPrice:N2})", userId);
                return false;
            }
        }
        else
        {
            quantity = (int)Math.Floor(settings.FixedAmountPerTrade / entryPrice);
            if (quantity < 1)
            {
                await LogAuditAsync(symbol, "REAL_SIGNAL_SKIPPED", entryPrice, 0,
                    $"Calculated quantity 0 for entry price ₹{entryPrice:N2}", userId);
                return false;
            }
        }

        // Manual trades carry their own trade-wise SL%/TSL% from the popup - both are mandatory
        // (never optional/blank), so a human is present to correct the input rather than the bot
        // silently falling back and running an unprotected position.
        if (isManualTrade)
        {
            if (!manualStopLossPct.HasValue || manualStopLossPct.Value <= 0)
            {
                await LogAuditAsync(symbol, "REAL_SIGNAL_SKIPPED", entryPrice, 0,
                    "Manual trade rejected: Stop Loss % must be greater than zero.", userId);
                return false;
            }
            if (!manualTrailingSlPct.HasValue || manualTrailingSlPct.Value <= 0)
            {
                await LogAuditAsync(symbol, "REAL_SIGNAL_SKIPPED", entryPrice, 0,
                    "Manual trade rejected: Trailing Stop Loss % must be greater than zero.", userId);
                return false;
            }
        }

        // 9. Target, Stop Loss & initial Trailing SL - shared SwingTradeRules levels (daily-ATR based in
        // SWING_CLOSE mode, engine levels in INTRADAY mode; a Manual Real Trade's own %s override both).
        var tradeParams = SwingTradeParams.From(settings);
        decimal? effectiveStopLossPct = isManualTrade ? Math.Abs(manualStopLossPct!.Value) : null;
        decimal? effectiveTrailingSlPct = isManualTrade ? Math.Abs(manualTrailingSlPct!.Value) : null;
        var levels = SwingTradeRules.ComputeEntryLevels(entryPrice, dailyAtr, engineStopLoss, engineTarget,
            effectiveStopLossPct, effectiveTrailingSlPct, tradeParams);
        decimal takeProfit = levels.TakeProfit;
        decimal stopLoss = levels.StopLoss;
        decimal? trailingSl = levels.TrailingStopLoss;

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
                    TradeType = isManualTrade ? TradeType.Manual : TradeType.Auto,
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
            // Settle now only on an exact, broker-priced full fill. Anything else - still resting, COMPLETE
            // without an average price yet, or partly filled - is recorded Open and settled by the reconcile
            // pass from the broker's own filled_quantity / average_price (same status call, no extra API use).
            var fill = ResolveFill(statusCheck, quantity);
            bool confirmedRejected = fill.Outcome == RealFillOutcome.Rejected;
            bool confirmedComplete = fill.Outcome == RealFillOutcome.Filled && fill.Quantity == quantity;

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
                    TradeType = isManualTrade ? TradeType.Manual : TradeType.Auto,
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
                var openBuyOrder = await _repository.CreateOrderAsync(new RealOrder
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
                    TradeType = isManualTrade ? TradeType.Manual : TradeType.Auto,
                    Remarks = $"[LIVE REAL MONEY] Zerodha Order #{brokerOrderId} (Met {metConditionsCount}/11)"
                });

                await TryRecordFillDetailsAsync(openBuyOrder.Id, entryPrice, null);

                await LogAuditAsync(symbol, "BUY_ORDER_OPEN", entryPrice, quantity,
                    $"🕓 BUY order placed for {symbol} — Status: OPEN, awaiting execution (Order #{brokerOrderId})", userId);

                if (_hubBroadcast != null)
                {
                    await _hubBroadcast.BroadcastAllAsync("ReceiveRealTradeAlert", new
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

            // The broker's average price - never the quote or the order's own price (RealFillResolver).
            decimal executedPrice = fill.Price;

            // Re-anchor SL/Target/Trailing-SL to the actual broker-confirmed fill price rather than the
            // pre-fill quote used to size the order (slippage on a market order). Engine levels keep
            // their original distance from entry.
            if (executedPrice != entryPrice)
            {
                decimal fillDelta = executedPrice - entryPrice;
                levels = SwingTradeRules.ComputeEntryLevels(executedPrice, dailyAtr,
                    engineStopLoss.HasValue ? engineStopLoss.Value + fillDelta : null,
                    engineTarget.HasValue ? engineTarget.Value + fillDelta : null,
                    effectiveStopLossPct, effectiveTrailingSlPct, tradeParams);
                takeProfit = levels.TakeProfit;
                stopLoss = levels.StopLoss;
                trailingSl = levels.TrailingStopLoss;
            }

            TradeType tradeType = isManualTrade ? TradeType.Manual : TradeType.Auto;

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
                TradeType = tradeType,
                Remarks = isManualTrade
                    ? $"[LIVE REAL MONEY - MANUAL] Zerodha Order #{brokerOrderId} (SL {effectiveStopLossPct:F2}% / TSL {effectiveTrailingSlPct:F2}%)"
                    : $"[LIVE REAL MONEY] Zerodha Order #{brokerOrderId} (Met {metConditionsCount}/11)"
            });
            await TryRecordFillDetailsAsync(order.Id, entryPrice, fill.Quantity);

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
                StopLossPct = effectiveStopLossPct,
                TrailingSlPct = effectiveTrailingSlPct,
                Status = PositionStatus.OPEN,
                TradeType = tradeType,
                RealizedPnl = 0m
            });

            _realTradeCache?.AddOrUpdatePosition(newPosition);
            await EnsureSubscribedForExitMonitoringAsync(symbol);

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
                TradeType = tradeType,
                IsExit = false,
                Remarks = $"Real BUY Executed @ ₹{executedPrice:F2} (Broker ID: {brokerOrderId})"
            });

            // Invalidate count cache
            string todayKey = $"realtrade:today_count:{userId}:{DateTime.UtcNow:yyyyMMdd}";
            await _cacheService.RemoveAsync(todayKey);

            // Log Audit
            string slText = $"₹{stopLoss:F2}";
            string tslText = trailingSl.HasValue ? $"₹{trailingSl.Value:F2}" : "activates after +1 ATR";
            await LogAuditAsync(symbol, "REAL_BUY", executedPrice, quantity,
                $"⚡ Live BUY Executed @ ₹{executedPrice:F2} (Qty: {quantity}, Target: ₹{takeProfit:F2}, SL: {slText}, TSL: {tslText}, Order #{brokerOrderId})", userId);

            // Broadcast SignalR Toast
            if (_hubBroadcast != null)
            {
                await _hubBroadcast.BroadcastAllAsync("ReceiveRealTradeAlert", new
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

    // ------------------------------------------------------------------------------------------
    // Auto Short Selling (real money) - intraday only, always product MIS
    // ------------------------------------------------------------------------------------------

    // Zerodha only allows a cash-segment short as an intraday (MIS) position, whatever ProductType the longs use.
    public const string ShortProductType = "MIS";

    // Per-(user, symbol) guard against two short entries racing in this process (scan + a retry). Cross-process
    // duplicates are blocked by the open-position / pending-order checks below.
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> _shortEntryLocks = new();

    public async Task<bool> EvaluateAndExecuteRealShortAsync(string symbol, decimal entryPrice, int metConditionsCount, int userId = 1,
        bool isSellSignal = false, decimal? engineStopLoss = null, decimal? engineTarget = null, decimal? dailyAtr = null)
    {
        var entryLock = _shortEntryLocks.GetOrAdd($"{userId}:{symbol.ToUpper().Trim()}", _ => new SemaphoreSlim(1, 1));
        if (!await entryLock.WaitAsync(TimeSpan.Zero))
        {
            _logger.LogInformation("Rejecting duplicate Auto Short request for {Symbol} (User {UserId}) - a previous request is still in flight.", symbol, userId);
            return false;
        }

        try
        {
            return await EvaluateAndExecuteRealShortCoreAsync(symbol, entryPrice, metConditionsCount, userId, isSellSignal,
                engineStopLoss, engineTarget, dailyAtr);
        }
        finally
        {
            entryLock.Release();
        }
    }

    // Same guards, in the same order and with the same reason texts, as EvaluateAndExecuteRealBuyCoreAsync - plus the
    // Auto Short switch (OFF by default), the short entry cut-off and a pending-order check on both sides.
    private async Task<bool> EvaluateAndExecuteRealShortCoreAsync(string symbol, decimal entryPrice, int metConditionsCount, int userId,
        bool isSellSignal, decimal? engineStopLoss, decimal? engineTarget, decimal? dailyAtr)
    {
        symbol = symbol.ToUpper().Trim();
        var settings = await GetSettingsAsync(userId);

        // 1. Master Switch + Auto Short switch - both must be ON for a real short.
        if (!settings.IsRealTradeEnabled || !settings.IsAutoShortEnabled)
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

        // 3b. Opening Entry Delay
        if (!IsPastEntryDelay(settings.TradingWindowStart, settings.EntryDelayMinutes))
        {
            await LogAuditAsync(symbol, "REAL_SIGNAL_SKIPPED", entryPrice, 0,
                $"Opening entry delay active - new SHORT signals held back for {settings.EntryDelayMinutes} min after {settings.TradingWindowStart}", userId);
            return false;
        }

        // 3c. Short entry cut-off - intraday only, so every short has time to be bought back before the square-off.
        if (!SwingTradeRules.IsBeforeShortEntryCutoff(settings.ShortEntryCutoff, SwingTradeRules.NowIst()))
        {
            await LogAuditAsync(symbol, "REAL_SIGNAL_SKIPPED", entryPrice, 0,
                $"Short selling closed for today - no new shorts at/after {settings.ShortEntryCutoff} IST (open shorts are squared off at {settings.ShortSquareOffTime} IST)", userId);
            return false;
        }

        // 4. Condition Match Check
        if (!isSellSignal && metConditionsCount < settings.MinConditionsMatch)
        {
            await LogAuditAsync(symbol, "REAL_SIGNAL_SKIPPED", entryPrice, 0,
                $"Condition score {metConditionsCount}/11 below required {settings.MinConditionsMatch}/11", userId);
            return false;
        }

        // 5. Daily Trade Limit Check (longs and shorts share it)
        int todayCount = await GetTodayRealTradeCountAsync(userId);
        if (todayCount >= settings.MaxTradesPerDay)
        {
            await LogAuditAsync(symbol, "REAL_SIGNAL_SKIPPED", entryPrice, 0,
                $"Daily limit of {settings.MaxTradesPerDay} real trades reached ({todayCount}/{settings.MaxTradesPerDay})", userId);
            return false;
        }

        // 6. Daily Loss Circuit Breaker
        decimal effectiveDailyLossLimit = SwingTradeRules.EffectiveDailyLossLimit(settings.MaxDailyLossLimit, settings.AvailableCapital);
        decimal todayRealizedPnl = await _repository.GetTodayRealizedPnlAsync(userId);
        var openPositions = (await _repository.GetOpenPositionsAsync(userId)).ToList();
        decimal totalLoss = todayRealizedPnl + openPositions.Sum(p => p.UnrealizedPnl);
        if (totalLoss <= -effectiveDailyLossLimit)
        {
            await LogAuditAsync(symbol, "CIRCUIT_BREAKER", entryPrice, 0,
                $"Daily loss limit ₹{effectiveDailyLossLimit:N2} breached (Total Loss: ₹{totalLoss:N2}). Pausing live bot.", userId);
            await ToggleRealTradeAsync(false, userId);
            return false;
        }

        // 6b. Portfolio-Level Exposure Cap (longs and shorts together)
        if (openPositions.Count >= SwingTradeRules.MaxConcurrentPositions)
        {
            await LogAuditAsync(symbol, "REAL_SIGNAL_SKIPPED", entryPrice, 0,
                $"Portfolio exposure cap reached ({openPositions.Count}/{SwingTradeRules.MaxConcurrentPositions} concurrent open positions)", userId);
            return false;
        }

        // 7. Duplicate Open Position Check - any open position in the symbol, long or short.
        var existingOpenPos = await _repository.GetOpenPositionBySymbolAsync(userId, symbol);
        if (existingOpenPos != null)
        {
            await LogAuditAsync(symbol, "REAL_SIGNAL_SKIPPED", entryPrice, 0,
                $"Symbol already has an OPEN real position (Position #{existingOpenPos.Id})", userId);
            return false;
        }

        // 7b. Duplicate Pending Order Check - any order for the symbol still resting at the broker, BUY or SELL.
        var pendingOrder = await _repository.GetOpenBrokerOrderAsync(userId, symbol, TradeSide.SELL)
            ?? await _repository.GetOpenBrokerOrderAsync(userId, symbol, TradeSide.BUY);
        if (pendingOrder != null)
        {
            _logger.LogInformation("Skipping SHORT for {Symbol} (User {UserId}): order #{OrderId} is still OPEN at the broker awaiting fill.",
                symbol, userId, pendingOrder.BrokerOrderId);
            return false;
        }

        // 8. Capital & Margin Validation - the full trade amount must be free (MIS needs less, so this is conservative).
        var marginResult = await _brokerService.GetEquityMarginsAsync(userId);
        decimal availableMargin = marginResult.Success ? marginResult.AvailableCash : settings.AvailableCapital;
        if (availableMargin < settings.FixedAmountPerTrade)
        {
            await LogAuditAsync(symbol, "REAL_SIGNAL_SKIPPED", entryPrice, 0,
                $"Insufficient Broker Capital (₹{availableMargin:N2} < Trade Amount ₹{settings.FixedAmountPerTrade:N2})", userId);
            return false;
        }

        // 8b. Live Quote Re-check - short drift guard (bounced up = stale, already fallen = too extended to chase).
        decimal preLiveEntryPrice = entryPrice;
        var ltpResult = await _brokerService.GetLtpQuotesAsync(new[] { (symbol, "NSE") }, userId);
        if (ltpResult.Success && ltpResult.Ltps != null && ltpResult.Ltps.TryGetValue(symbol, out var liveLtp) && liveLtp > 0m)
        {
            string? driftReason = SwingTradeRules.CheckShortSignalDrift(preLiveEntryPrice, liveLtp);
            if (driftReason != null)
            {
                await LogAuditAsync(symbol, "REAL_SIGNAL_SKIPPED", preLiveEntryPrice, 0, driftReason, userId);
                return false;
            }

            decimal priceDelta = liveLtp - preLiveEntryPrice;
            if (engineStopLoss.HasValue) engineStopLoss = engineStopLoss.Value + priceDelta;
            if (engineTarget.HasValue) engineTarget = engineTarget.Value + priceDelta;
            entryPrice = liveLtp;
        }

        int quantity = (int)Math.Floor(settings.FixedAmountPerTrade / entryPrice);
        if (quantity < 1)
        {
            await LogAuditAsync(symbol, "REAL_SIGNAL_SKIPPED", entryPrice, 0,
                $"Calculated quantity 0 for entry price ₹{entryPrice:N2}", userId);
            return false;
        }

        // 9. Mirrored levels: Stop Loss above the entry, Target below.
        var tradeParams = SwingTradeParams.From(settings);
        var levels = SwingTradeRules.ComputeShortEntryLevels(entryPrice, dailyAtr, engineStopLoss, engineTarget, tradeParams);
        decimal takeProfit = levels.TakeProfit;
        decimal stopLoss = levels.StopLoss;
        decimal? trailingSl = levels.TrailingStopLoss;

        try
        {
            var brokerResult = await _brokerService.PlaceLiveOrderAsync(symbol, TradeSide.SELL, quantity, PaperOrderType.Market,
                entryPrice, ShortProductType, userId);

            if (!brokerResult.Success)
            {
                await _repository.CreateOrderAsync(new RealOrder
                {
                    UserId = userId,
                    Symbol = symbol,
                    Side = TradeSide.SELL,
                    Quantity = quantity,
                    OrderType = PaperOrderType.Market,
                    Price = entryPrice,
                    StopLoss = stopLoss,
                    TakeProfit = takeProfit,
                    Status = PaperOrderStatus.Rejected,
                    RejectionReason = brokerResult.Message,
                    TradeType = TradeType.Auto,
                    IsShort = true,
                    Remarks = $"Real SHORT SELL Rejected by Zerodha: {brokerResult.Message}"
                });

                await LogAuditAsync(symbol, "ORDER_REJECTED", entryPrice, quantity,
                    $"Zerodha Short Order Placement Failed: {brokerResult.Message}", userId);
                return false;
            }

            string brokerOrderId = brokerResult.BrokerOrderId ?? $"KITE-{DateTime.UtcNow.Ticks}";

            // Accepted is not traded - confirm the fill with the broker before recording a position.
            var statusCheck = await _brokerService.GetOrderStatusAsync(brokerOrderId, userId);
            var fill = ResolveFill(statusCheck, quantity);

            if (fill.Outcome == RealFillOutcome.Rejected)
            {
                await _repository.CreateOrderAsync(new RealOrder
                {
                    UserId = userId,
                    BrokerOrderId = brokerOrderId,
                    Symbol = symbol,
                    Side = TradeSide.SELL,
                    Quantity = quantity,
                    OrderType = PaperOrderType.Market,
                    Price = entryPrice,
                    StopLoss = stopLoss,
                    TakeProfit = takeProfit,
                    Status = PaperOrderStatus.Rejected,
                    FilledPrice = 0m,
                    RejectionReason = statusCheck.Message,
                    TradeType = TradeType.Auto,
                    IsShort = true,
                    Remarks = $"[LIVE REAL MONEY] Zerodha SHORT Order #{brokerOrderId} (Met {metConditionsCount}/11)"
                });

                await LogAuditAsync(symbol, "ORDER_REJECTED", entryPrice, quantity,
                    $"Zerodha SHORT Order {statusCheck.BrokerStatus}: {statusCheck.Message} (Order #{brokerOrderId})", userId);
                return false;
            }

            if (!(fill.Outcome == RealFillOutcome.Filled && fill.Quantity == quantity))
            {
                // Resting or not yet confirmed - recorded Open; ReconcilePendingRealOrdersAsync opens the short on the fill.
                var openShortOrder = await _repository.CreateOrderAsync(new RealOrder
                {
                    UserId = userId,
                    BrokerOrderId = brokerOrderId,
                    Symbol = symbol,
                    Side = TradeSide.SELL,
                    Quantity = quantity,
                    OrderType = PaperOrderType.Market,
                    Price = entryPrice,
                    StopLoss = stopLoss,
                    TakeProfit = takeProfit,
                    Status = PaperOrderStatus.Open,
                    FilledPrice = 0m,
                    TradeType = TradeType.Auto,
                    IsShort = true,
                    Remarks = $"[LIVE REAL MONEY] Zerodha SHORT Order #{brokerOrderId} (Met {metConditionsCount}/11)"
                });

                await TryRecordFillDetailsAsync(openShortOrder.Id, entryPrice, null);
                await LogAuditAsync(symbol, "SHORT_ORDER_OPEN", entryPrice, quantity,
                    $"🕓 SHORT SELL order placed for {symbol} — Status: OPEN, awaiting execution (Order #{brokerOrderId})", userId);
                await BroadcastDashboardUpdateAsync(userId);
                return true;
            }

            decimal executedPrice = fill.Price;
            if (executedPrice != entryPrice)
            {
                decimal fillDelta = executedPrice - entryPrice;
                levels = SwingTradeRules.ComputeShortEntryLevels(executedPrice, dailyAtr,
                    engineStopLoss.HasValue ? engineStopLoss.Value + fillDelta : null,
                    engineTarget.HasValue ? engineTarget.Value + fillDelta : null, tradeParams);
                takeProfit = levels.TakeProfit;
                stopLoss = levels.StopLoss;
                trailingSl = levels.TrailingStopLoss;
            }

            var order = await _repository.CreateOrderAsync(new RealOrder
            {
                UserId = userId,
                BrokerOrderId = brokerOrderId,
                Symbol = symbol,
                Side = TradeSide.SELL,
                Quantity = quantity,
                OrderType = PaperOrderType.Market,
                Price = executedPrice,
                StopLoss = stopLoss,
                TakeProfit = takeProfit,
                Status = PaperOrderStatus.Filled,
                FilledPrice = executedPrice,
                FilledAt = DateTime.UtcNow,
                TradeType = TradeType.Auto,
                IsShort = true,
                Remarks = $"[LIVE REAL MONEY] Zerodha SHORT Order #{brokerOrderId} (Met {metConditionsCount}/11)"
            });
            await TryRecordFillDetailsAsync(order.Id, entryPrice, fill.Quantity);

            var newPosition = await _repository.UpsertPositionAsync(new RealPosition
            {
                UserId = userId,
                Symbol = symbol,
                Side = TradeSide.SELL,
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
            await EnsureSubscribedForExitMonitoringAsync(symbol);

            await _repository.RecordTradeHistoryAsync(new RealTradeHistory
            {
                UserId = userId,
                OrderId = order.Id,
                BrokerOrderId = brokerOrderId,
                Symbol = symbol,
                Side = TradeSide.SELL,
                Quantity = quantity,
                EntryPrice = executedPrice,
                ExecutedPrice = executedPrice,
                RealizedPnl = 0m,
                TradeType = TradeType.Auto,
                IsExit = false,
                Remarks = $"Real SHORT SELL Executed @ ₹{executedPrice:F2} (Broker ID: {brokerOrderId})"
            });

            await _cacheService.RemoveAsync($"realtrade:today_count:{userId}:{DateTime.UtcNow:yyyyMMdd}");

            string tslText = trailingSl.HasValue ? $"₹{trailingSl.Value:F2}" : "activates after -1 ATR";
            await LogAuditAsync(symbol, "REAL_SHORT", executedPrice, quantity,
                $"⚡ Live SHORT SELL Executed @ ₹{executedPrice:F2} (Qty: {quantity}, MIS, Target: ₹{takeProfit:F2}, SL: ₹{stopLoss:F2}, TSL: {tslText}, Order #{brokerOrderId}) - auto square-off at {settings.ShortSquareOffTime} IST", userId);

            if (_hubBroadcast != null)
            {
                await _hubBroadcast.BroadcastAllAsync("ReceiveRealTradeAlert", new
                {
                    symbol,
                    side = "SELL",
                    quantity,
                    price = executedPrice,
                    target = takeProfit,
                    stopLoss,
                    trailingSl,
                    brokerOrderId,
                    userId,
                    message = $"⚡ LIVE REAL SHORT: {quantity} shares of {symbol} @ ₹{executedPrice:N2} (Target ₹{takeProfit:N2})"
                });
            }

            await BroadcastDashboardUpdateAsync(userId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to execute real short order for {Symbol} (User {UserId})", symbol, userId);
            await LogAuditAsync(symbol, "SYSTEM_ERROR", entryPrice, quantity, $"Real SHORT execution failed: {ex.Message}", userId);
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

        // Holdings enrollment has no ATR of its own, so SwingTradeRules falls back to its fixed default
        // Stop Loss %; the user-supplied target replaces the computed one.
        var settings = await GetSettingsAsync(userId);
        var levels = SwingTradeRules.ComputeEntryLevels(averagePrice, null, null, null, null, null, SwingTradeParams.From(settings));
        decimal stopLoss = levels.StopLoss;
        decimal? trailingSl = levels.TrailingStopLoss;

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
            TrailingStopLoss = trailingSl,
            Status = PositionStatus.OPEN,
            TradeType = TradeType.Auto,
            RealizedPnl = 0m
        });

        _realTradeCache?.AddOrUpdatePosition(newPosition);
        await EnsureSubscribedForExitMonitoringAsync(symbol);

        await LogAuditAsync(symbol, "HOLDING_MONITOR_ENABLED", targetPrice, quantity,
            $"📦 Zerodha Holding enrolled for auto-sell monitoring (Qty: {quantity}, Avg: ₹{averagePrice:F2}, Target: ₹{targetPrice:F2})", userId);

        if (_hubBroadcast != null)
        {
            await _hubBroadcast.BroadcastGroupAsync($"user-{userId}", "ReceiveHoldingMonitorUpdate", new
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
            // Side-aware: a tracked short is bought back (covered), not sold again.
            string exitLabel = trackedPosition.Side == TradeSide.SELL ? "BUY TO COVER" : "SELL";
            bool closed = await SquareOffSinglePositionAsync(trackedPosition.Id, reason, userId);
            return (closed, closed
                ? $"{exitLabel} order submitted for {symbol}."
                : $"{exitLabel} order for {symbol} was not placed — it may already have an order resting OPEN at the broker. Check the Real Orders Book.");
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
            // status before ever recording/announcing "FILLED". Only an exact, broker-priced full fill is
            // settled here; partial / unpriced fills are recorded Open and settled by the reconcile pass.
            var statusCheck = await _brokerService.GetOrderStatusAsync(brokerOrderId, userId);
            var fill = ResolveFill(statusCheck, quantity);
            bool confirmedComplete = fill.Outcome == RealFillOutcome.Filled && fill.Quantity == quantity;
            bool confirmedRejected = fill.Outcome == RealFillOutcome.Rejected;

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
                var openManualSell = await _repository.CreateOrderAsync(new RealOrder
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

                await TryRecordFillDetailsAsync(openManualSell.Id, currentPrice, null);

                await LogAuditAsync(symbol, "SELL_ORDER_OPEN", currentPrice, quantity,
                    $"🕓 Manual SELL order placed for {symbol} — Status: OPEN, awaiting execution (Order #{brokerOrderId})", userId);

                if (_hubBroadcast != null)
                {
                    await _hubBroadcast.BroadcastAllAsync("ReceiveRealTradeAlert", new
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

            decimal executedPrice = fill.Price;
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
            await TryRecordFillDetailsAsync(order.Id, currentPrice, fill.Quantity);

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

            if (_hubBroadcast != null)
            {
                await _hubBroadcast.BroadcastAllAsync("ReceiveRealTradeAlert", new
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

        // An open short is exited by buying it back - its own intraday policy, see EvaluateAndExecuteRealCoverAsync.
        if (position.Side == TradeSide.SELL)
            return await EvaluateAndExecuteRealCoverAsync(position, currentLtp, userId);

        var settings = await GetSettingsAsync(userId);
        var nowIst = SwingTradeRules.NowIst();
        if (!SwingTradeRules.IsWithinTradingWindow(settings.TradingWindowStart, settings.TradingWindowEnd, nowIst))
            return false;

        // Max Days Hold is counted in NSE trading days (weekends/holidays excluded), not calendar days.
        int tradingDaysOpen = settings.MaxDurationDays > 0
            ? await _marketHoursService.CountTradingDaysElapsedAsync(position.OpenedAt, DateTime.UtcNow)
            : 0;

        var decision = SwingTradeRules.EvaluateExit(ExitPositionView.From(position), currentLtp, nowIst,
            tradingDaysOpen, settings.MaxDurationDays, SwingTradeParams.From(settings));

        if (decision.ShouldExit)
        {
            return await ExecuteRealSellOrderAsync(position, currentLtp, decision.Reason, userId,
                decision.IsGap ? GapExitProtectionBufferPct : null);
        }

        if (decision.NewTrailingStopLoss.HasValue)
        {
            bool activated = !position.TrailingStopLoss.HasValue || position.TrailingStopLoss.Value < position.AverageEntryPrice;
            await _repository.UpdateTrailingStopLossAsync(position.Id, decision.NewTrailingStopLoss.Value);
            position.TrailingStopLoss = decision.NewTrailingStopLoss.Value;
            _realTradeCache?.AddOrUpdatePosition(position);

            if (activated && SwingTradeParams.From(settings).IsSwingClose)
            {
                await LogAuditAsync(position.Symbol, "TRAILING_SL_ACTIVATED", currentLtp, position.Quantity,
                    $"Trailing SL activated @ ₹{decision.NewTrailingStopLoss.Value:F2} (entry ₹{position.AverageEntryPrice:F2}) - now checked on closing basis", userId);
            }
        }

        return false;
    }

    // Positions already reported as a stale short (see EvaluateAndExecuteRealCoverAsync) - one alert per position, not
    // one per 20-second monitor cycle.
    private static readonly ConcurrentDictionary<int, byte> _staleShortsReported = new();

    // Exit for an open real SHORT: SwingTradeRules.EvaluateShortExit (auto square-off, then live target / stop /
    // trailing SL). Not gated by the user's trading window (only market hours) - a short must always be closable today.
    private async Task<bool> EvaluateAndExecuteRealCoverAsync(RealPosition position, decimal currentLtp, int userId)
    {
        var settings = await GetSettingsAsync(userId);
        var nowIst = SwingTradeRules.NowIst();

        // A short still recorded open from an earlier day was not bought back by QuantEdge - Zerodha squares off open MIS
        // positions itself at the close, so the broker most likely holds nothing now. Buying "back" would then OPEN a new
        // long, so no order is placed: it is flagged once for a manual check (Reconciliation page / Zerodha positions).
        if (SwingTradeRules.ToIst(position.OpenedAt).Date < nowIst.Date)
        {
            if (_staleShortsReported.TryAdd(position.Id, 0))
            {
                _logger.LogWarning("Stale real short #{PositionId} ({Symbol}, User {UserId}) is still open from {OpenedAt:dd-MMM} - not covering automatically.",
                    position.Id, position.Symbol, userId, SwingTradeRules.ToIst(position.OpenedAt));
                await LogAuditAsync(position.Symbol, "SHORT_STALE", currentLtp, position.Quantity,
                    $"⚠️ Short position #{position.Id} is still recorded OPEN from {SwingTradeRules.ToIst(position.OpenedAt):dd-MMM} (intraday shorts are squared off the same day). No order placed - check Zerodha positions and close it in QuantEdge manually.", userId);
            }
            return false;
        }

        var tradeParams = SwingTradeParams.From(settings);
        var decision = SwingTradeRules.EvaluateShortExit(ExitPositionView.From(position), currentLtp, nowIst,
            settings.ShortSquareOffTime, tradeParams);

        if (decision.ShouldExit)
        {
            return await ExecuteRealSellOrderAsync(position, currentLtp, decision.Reason, userId,
                decision.IsGap ? GapExitProtectionBufferPct : null);
        }

        if (decision.NewTrailingStopLoss.HasValue)
        {
            bool activated = !position.TrailingStopLoss.HasValue || position.TrailingStopLoss.Value > position.AverageEntryPrice;
            await _repository.UpdateTrailingStopLossAsync(position.Id, decision.NewTrailingStopLoss.Value);
            position.TrailingStopLoss = decision.NewTrailingStopLoss.Value;
            _realTradeCache?.AddOrUpdatePosition(position);

            if (activated && tradeParams.IsSwingClose)
            {
                await LogAuditAsync(position.Symbol, "TRAILING_SL_ACTIVATED", currentLtp, position.Quantity,
                    $"Short trailing SL activated @ ₹{decision.NewTrailingStopLoss.Value:F2} (entry ₹{position.AverageEntryPrice:F2})", userId);
            }
        }

        return false;
    }

    private enum RealSellOutcome { Failed, Filled, OrderOpenPending, AlreadyPending }

    private async Task<bool> ExecuteRealSellOrderAsync(RealPosition position, decimal currentLtp, string exitReason, int userId, decimal? protectionBufferPctOverride = null)
        => (await ExecuteRealSellOrderCoreAsync(position, currentLtp, exitReason, userId, protectionBufferPctOverride)) is RealSellOutcome.Filled or RealSellOutcome.OrderOpenPending;

    private async Task<RealSellOutcome> ExecuteRealSellOrderCoreAsync(RealPosition position, decimal currentLtp, string exitReason, int userId, decimal? protectionBufferPctOverride = null)
    {
        var settings = await GetSettingsAsync(userId);

        // A long is exited with a SELL; a short (Auto Short, always MIS) is exited by BUYING it back. Every order below
        // is recorded on the exit side and flagged IsShort for a short, so reconciliation closes the right position.
        bool isShort = position.Side == TradeSide.SELL;
        TradeSide exitSide = isShort ? TradeSide.BUY : TradeSide.SELL;
        string exitLabel = isShort ? "BUY TO COVER" : "SELL";
        string exitProduct = isShort ? ShortProductType : settings.ProductType;
        try
        {
            // A previous exit attempt for this position may still be resting, unfilled, at the broker
            // (e.g. a limit sell whose price hasn't been touched yet). Placing another one here would
            // double-sell the same shares once both eventually fill, so skip until it resolves —
            // the reconciliation pass (ReconcilePendingRealOrdersAsync) will pick it up and either
            // finalize it as Filled or clear it as Cancelled/Rejected.
            var existingPendingOrder = await _repository.GetOpenBrokerOrderAsync(userId, position.Symbol, exitSide);
            if (existingPendingOrder != null)
            {
                _logger.LogInformation("Skipping {ExitLabel} for {Symbol} (User {UserId}): order #{OrderId} is still OPEN at the broker awaiting fill.",
                    exitLabel, position.Symbol, userId, existingPendingOrder.BrokerOrderId);
                return RealSellOutcome.AlreadyPending;
            }

            _logger.LogInformation("[REAL MONEY {ExitLabel} TRIGGERED - User {UserId}] Position #{Id} {Symbol} Qty:{Qty} @ {Ltp}. Reason: {Reason}",
                exitLabel, userId, position.Id, position.Symbol, position.Quantity, currentLtp, exitReason);

            // Execute Real Market exit via Zerodha Kite API (SquareOffLivePositionAsync places the opposite side of
            // the position). A wider protection band is used when this exit was flagged as a gap-through (see
            // EvaluateAndExecuteRealSellAsync) to maximize the odds of an immediate fill during a fast-moving/gapped market.
            var brokerResult = await _brokerService.SquareOffLivePositionAsync(
                position.Symbol,
                position.Quantity,
                position.Side,
                currentLtp,
                exitProduct,
                userId,
                protectionBufferPctOverride);

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
                    Side = exitSide,
                    Quantity = position.Quantity,
                    OrderType = PaperOrderType.Market,
                    Price = rejectedPrice,
                    Status = PaperOrderStatus.Rejected,
                    FilledPrice = 0m,
                    RejectionReason = brokerResult.Message,
                    IsShort = isShort,
                    Remarks = $"Real {exitLabel} ({exitReason}) Rejected by Zerodha: {brokerResult.Message}"
                });

                // CDSL TPIN / e-DIS only applies to selling delivery shares, never to buying back a short.
                bool isTpinError = !isShort && brokerResult.Message != null &&
                    (brokerResult.Message.Contains("e-DIS", StringComparison.OrdinalIgnoreCase) ||
                     brokerResult.Message.Contains("TPIN", StringComparison.OrdinalIgnoreCase) ||
                     brokerResult.Message.Contains("authorization", StringComparison.OrdinalIgnoreCase));

                string actionType = isTpinError ? "SELL_REJECTED_EDIS_REQUIRED" : isShort ? "COVER_FAILED" : "SELL_FAILED";

                await LogAuditAsync(position.Symbol, actionType, rejectedPrice, position.Quantity,
                    isShort ? $"Zerodha Buy-to-Cover Order Failed: {brokerResult.Message}" : $"Zerodha Sell Order Failed: {brokerResult.Message}", userId);

                if (_hubBroadcast != null && isTpinError)
                {
                    await _hubBroadcast.BroadcastAllAsync("ReceiveRealTradeAlert", new
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
            // Close the position here only on an exact, broker-priced full fill; a partial or unpriced fill
            // is recorded Open and the reconcile pass closes or shrinks the position from the broker's numbers.
            var fill = ResolveFill(statusCheck, position.Quantity);
            bool confirmedComplete = fill.Outcome == RealFillOutcome.Filled && fill.Quantity == position.Quantity;
            bool confirmedRejected = fill.Outcome == RealFillOutcome.Rejected;

            if (confirmedRejected)
            {
                await _repository.CreateOrderAsync(new RealOrder
                {
                    UserId = userId,
                    BrokerOrderId = brokerOrderId,
                    Symbol = position.Symbol,
                    Side = exitSide,
                    Quantity = position.Quantity,
                    OrderType = PaperOrderType.Market,
                    Price = currentLtp,
                    Status = PaperOrderStatus.Rejected,
                    FilledPrice = 0m,
                    RejectionReason = statusCheck.Message,
                    IsShort = isShort,
                    Remarks = $"Real {exitLabel} ({exitReason}) - Broker ID: {brokerOrderId}"
                });

                await LogAuditAsync(position.Symbol, isShort ? "COVER_FAILED" : "SELL_FAILED", currentLtp, position.Quantity,
                    isShort
                        ? $"Zerodha Buy-to-Cover Order {statusCheck.BrokerStatus}: {statusCheck.Message} (Order #{brokerOrderId})"
                        : $"Zerodha Sell Order {statusCheck.BrokerStatus}: {statusCheck.Message} (Order #{brokerOrderId})", userId);

                return RealSellOutcome.Failed;
            }

            if (!confirmedComplete)
            {
                // Order accepted by the broker but still resting (OPEN / TRIGGER PENDING), or the status
                // check itself couldn't confirm a fill. Record it as Open and leave the position open —
                // ReconcilePendingRealOrdersAsync finalizes it once the broker confirms the real outcome.
                var openSellOrder = await _repository.CreateOrderAsync(new RealOrder
                {
                    UserId = userId,
                    BrokerOrderId = brokerOrderId,
                    Symbol = position.Symbol,
                    Side = exitSide,
                    Quantity = position.Quantity,
                    OrderType = PaperOrderType.Market,
                    Price = currentLtp,
                    Status = PaperOrderStatus.Open,
                    FilledPrice = 0m,
                    IsShort = isShort,
                    Remarks = $"Real {exitLabel} ({exitReason}) - Broker ID: {brokerOrderId}"
                });

                await TryRecordFillDetailsAsync(openSellOrder.Id, currentLtp, null);

                await LogAuditAsync(position.Symbol, isShort ? "COVER_ORDER_OPEN" : "SELL_ORDER_OPEN", currentLtp, position.Quantity,
                    $"🕓 {exitLabel} order placed for {position.Symbol} ({exitReason}) — Status: OPEN, awaiting execution (Order #{brokerOrderId})", userId);

                if (_hubBroadcast != null)
                {
                    await _hubBroadcast.BroadcastAllAsync("ReceiveRealTradeAlert", new
                    {
                        symbol = position.Symbol,
                        side = isShort ? "BUY_OPEN" : "SELL_OPEN",
                        quantity = position.Quantity,
                        price = currentLtp,
                        exitReason,
                        brokerOrderId,
                        userId,
                        message = $"🕓 {exitLabel} order for {position.Symbol} is OPEN at the broker (not yet filled) — Order #{brokerOrderId}"
                    });
                }

                await BroadcastDashboardUpdateAsync(userId);
                return RealSellOutcome.OrderOpenPending;
            }

            decimal executedPrice = fill.Price;
            // A long gains when the price rose; a short gains when it fell.
            decimal realizedPnl = isShort
                ? (position.AverageEntryPrice - executedPrice) * position.Quantity
                : (executedPrice - position.AverageEntryPrice) * position.Quantity;

            // Create Sell (or Buy-to-Cover) Order Record
            var sellOrder = await _repository.CreateOrderAsync(new RealOrder
            {
                UserId = userId,
                BrokerOrderId = brokerOrderId,
                Symbol = position.Symbol,
                Side = exitSide,
                Quantity = position.Quantity,
                OrderType = PaperOrderType.Market,
                Price = executedPrice,
                Status = PaperOrderStatus.Filled,
                FilledPrice = executedPrice,
                FilledAt = DateTime.UtcNow,
                IsShort = isShort,
                Remarks = $"Real {exitLabel} ({exitReason}) - Broker ID: {brokerOrderId}"
            });
            await TryRecordFillDetailsAsync(sellOrder.Id, currentLtp, fill.Quantity);

            // Close Real Position in DB & RAM
            await _repository.ClosePositionAsync(position.Id, executedPrice, realizedPnl, exitReason);
            _realTradeCache?.RemovePosition(position.Id);
            _realTradeCache?.RemoveLiveLtp(position.Symbol);

            // Record Trade History
            await _repository.RecordTradeHistoryAsync(new RealTradeHistory
            {
                UserId = userId,
                OrderId = sellOrder.Id,
                BrokerOrderId = brokerOrderId,
                Symbol = position.Symbol,
                Side = exitSide,
                Quantity = position.Quantity,
                EntryPrice = position.AverageEntryPrice,
                ExecutedPrice = executedPrice,
                RealizedPnl = realizedPnl,
                TradeType = TradeType.Auto,
                ExitReason = exitReason,
                IsExit = true,
                Remarks = $"Real {exitLabel}: {exitReason} | Realized P&L: ₹{realizedPnl:F2}"
            });

            // Log Audit
            string pnlSign = realizedPnl >= 0 ? "+" : "";
            await LogAuditAsync(position.Symbol, isShort ? "REAL_COVER" : "REAL_SELL", executedPrice, position.Quantity,
                $"⚡ Live {exitLabel} ({exitReason}) @ ₹{executedPrice:F2} | P&L: {pnlSign}₹{realizedPnl:N2} (Order #{brokerOrderId})", userId);

            // Broadcast SignalR Toast
            if (_hubBroadcast != null)
            {
                await _hubBroadcast.BroadcastAllAsync("ReceiveRealTradeAlert", new
                {
                    symbol = position.Symbol,
                    side = isShort ? "BUY" : "SELL",
                    quantity = position.Quantity,
                    price = executedPrice,
                    realizedPnl,
                    exitReason,
                    brokerOrderId,
                    userId,
                    message = $"⚡ LIVE REAL {exitLabel}: {position.Symbol} ({exitReason}) @ ₹{executedPrice:N2} | P&L: {pnlSign}₹{realizedPnl:N2}"
                });
            }

            // "Holding sold" only means something for a long (the Holdings panel); a covered short was never a holding.
            if (_hubBroadcast != null && !isShort)
            {
                await _hubBroadcast.BroadcastGroupAsync($"user-{userId}", "ReceiveHoldingSoldEvent", new
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
            _logger.LogError(ex, "Failed to execute real {ExitLabel} order for {Symbol} (User {UserId})", exitLabel, position.Symbol, userId);
            await LogAuditAsync(position.Symbol, "SYSTEM_ERROR", currentLtp, position.Quantity, $"Real {exitLabel} execution error: {ex.Message}", userId);
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

                var fill = ResolveFill(statusCheck, order.Quantity);
                if (fill.Outcome is RealFillOutcome.Filled or RealFillOutcome.PartiallyFilledThenClosed)
                {
                    // Broker's filled quantity and average price - a partly-filled-then-cancelled order still
                    // moved shares, so it opens / shrinks / closes the position for exactly that quantity.
                    await FinalizeFilledOrderAsync(order, fill.Price, fill.Quantity);
                }
                else if (fill.Outcome == RealFillOutcome.Rejected)
                {
                    await FinalizeRejectedOrderAsync(order, statusCheck.BrokerStatus, statusCheck.Message);
                }
                // Pending: still resting, or done but without an average price yet - retry next cycle.
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

        var fill = ResolveFill(statusCheck, order.Quantity);
        bool brokerTerminal = string.Equals(statusCheck.BrokerStatus, "COMPLETE", StringComparison.OrdinalIgnoreCase) ||
                              string.Equals(statusCheck.BrokerStatus, "REJECTED", StringComparison.OrdinalIgnoreCase) ||
                              string.Equals(statusCheck.BrokerStatus, "CANCELLED", StringComparison.OrdinalIgnoreCase);

        if (fill.Outcome is RealFillOutcome.Filled or RealFillOutcome.PartiallyFilledThenClosed)
        {
            string qtyText = fill.Quantity < order.Quantity ? $" ({fill.Quantity} of {order.Quantity} filled)" : string.Empty;
            if (order.Status == PaperOrderStatus.Filled)
            {
                return (true, false, $"Already in sync: {order.Symbol} order #{order.BrokerOrderId} is FILLED at the broker{qtyText}.");
            }

            await FinalizeFilledOrderAsync(order, fill.Price, fill.Quantity);
            return (true, true, $"Corrected: {order.Symbol} order #{order.BrokerOrderId} is FILLED @ ₹{fill.Price:F2} at the broker{qtyText}. Records updated.");
        }

        if (fill.Outcome == RealFillOutcome.Pending && brokerTerminal)
        {
            // Finished at the broker but no average price reported yet - never settle on a guessed price.
            return (true, false, $"{order.Symbol} order #{order.BrokerOrderId} is {statusCheck.BrokerStatus} at the broker; waiting for Zerodha to report the average fill price.");
        }

        if (fill.Outcome == RealFillOutcome.Rejected)
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

        string positionNote = order.IsShort
            ? (IsExitOrder(order)
                ? " This order previously showed as FILLED and may have closed a short that Zerodha never actually confirmed bought back — check Zerodha positions."
                : " This order previously showed as FILLED and may have opened a short that was never actually sold — check Bot Positions.")
            : order.Side == TradeSide.SELL
            ? " This order previously showed as FILLED and may have closed a bot position that Zerodha never actually confirmed sold — check Zerodha Holdings and re-enable monitoring via 'Set Target' if the shares are still held."
            : " This order previously showed as FILLED and may have opened a bot position for shares that were never actually bought — check Bot Positions.";

        await LogAuditAsync(order.Symbol, order.Side == TradeSide.SELL ? "SELL_ORDER_OPEN" : "BUY_ORDER_OPEN", order.Price, order.Quantity,
            $"🔄 Status corrected: Order #{order.BrokerOrderId} for {order.Symbol} was recorded as {order.Status} but Zerodha confirms it is still OPEN (unfilled).{positionNote}", order.UserId);

        await BroadcastDashboardUpdateAsync(order.UserId);
        return (true, true, $"Corrected: {order.Symbol} order #{order.BrokerOrderId} is actually still OPEN at the broker (was recorded {order.Status}).{positionNote}");
    }

    // Does this order close a position? Long flow: a SELL. Auto Short (IsShort): the BUY that covers it - its SELL opens it.
    private static bool IsExitOrder(RealOrder order) =>
        order.IsShort ? order.Side == TradeSide.BUY : order.Side == TradeSide.SELL;

    /// <param name="executedPrice">The broker's average fill price.</param>
    /// <param name="filledQuantity">The broker's filled_quantity - below order.Quantity when the order partly filled and was then cancelled.</param>
    private async Task FinalizeFilledOrderAsync(RealOrder order, decimal executedPrice, int filledQuantity)
    {
        await _repository.UpdateOrderStatusAsync(order.Id, PaperOrderStatus.Filled, executedPrice, order.BrokerOrderId);
        await TryRecordFillDetailsAsync(order.Id, null, filledQuantity);
        bool isPartialOrder = filledQuantity < order.Quantity;

        if (order.IsShort)
        {
            await FinalizeFilledShortOrderAsync(order, executedPrice, filledQuantity, isPartialOrder);
            return;
        }

        if (order.Side == TradeSide.SELL)
        {
            var position = await _repository.GetOpenPositionBySymbolAsync(order.UserId, order.Symbol);
            // A long SELL only ever closes a long - never an open short in the same symbol.
            if (position != null && position.Side == TradeSide.BUY)
            {
                // P&L only on the shares that actually sold. Selling fewer than held shrinks the position,
                // which stays open and monitored for the rest.
                int soldQty = Math.Min(filledQuantity, position.Quantity);
                bool closesPosition = soldQty >= position.Quantity;
                decimal realizedPnl = (executedPrice - position.AverageEntryPrice) * soldQty;
                string exitReason = order.Remarks ?? "Exit";

                if (closesPosition)
                {
                    await _repository.ClosePositionAsync(position.Id, executedPrice, position.RealizedPnl + realizedPnl, exitReason);
                    _realTradeCache?.RemovePosition(position.Id);
                    _realTradeCache?.RemoveLiveLtp(position.Symbol);
                }
                else
                {
                    await _repository.ReducePositionQuantityAsync(position.Id, position.Quantity - soldQty, realizedPnl);
                    position.Quantity -= soldQty;
                    position.RealizedPnl += realizedPnl;
                    _realTradeCache?.AddOrUpdatePosition(position);
                }

                await _repository.RecordTradeHistoryAsync(new RealTradeHistory
                {
                    UserId = order.UserId,
                    OrderId = order.Id,
                    BrokerOrderId = order.BrokerOrderId,
                    Symbol = order.Symbol,
                    Side = TradeSide.SELL,
                    Quantity = soldQty,
                    EntryPrice = position.AverageEntryPrice,
                    ExecutedPrice = executedPrice,
                    RealizedPnl = realizedPnl,
                    TradeType = TradeType.Auto,
                    ExitReason = exitReason,
                    IsExit = true,
                    Remarks = closesPosition
                        ? $"Real SELL: {exitReason} | Realized P&L: ₹{realizedPnl:F2}"
                        : $"Real SELL (partial {soldQty} of {order.Quantity}, {position.Quantity} still held): {exitReason} | Realized P&L: ₹{realizedPnl:F2}"
                });

                string pnlSign = realizedPnl >= 0 ? "+" : "";
                string partialText = closesPosition ? string.Empty : $" — PARTIAL: {soldQty} of {order.Quantity} sold, {position.Quantity} still held and monitored";
                await LogAuditAsync(order.Symbol, "REAL_SELL", executedPrice, soldQty,
                    $"⚡ Live SELL confirmed FILLED @ ₹{executedPrice:F2} | P&L: {pnlSign}₹{realizedPnl:N2} (Order #{order.BrokerOrderId}){partialText}", order.UserId);

                if (_hubBroadcast != null)
                {
                    await _hubBroadcast.BroadcastAllAsync("ReceiveRealTradeAlert", new
                    {
                        symbol = order.Symbol,
                        side = "SELL",
                        quantity = soldQty,
                        price = executedPrice,
                        realizedPnl,
                        exitReason,
                        brokerOrderId = order.BrokerOrderId,
                        userId = order.UserId,
                        message = $"⚡ LIVE REAL SELL: {order.Symbol} confirmed FILLED @ ₹{executedPrice:N2} | P&L: {pnlSign}₹{realizedPnl:N2}{partialText}"
                    });

                    await _hubBroadcast.BroadcastGroupAsync($"user-{order.UserId}", "ReceiveHoldingSoldEvent", new
                    {
                        symbol = order.Symbol,
                        quantity = soldQty,
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
            // on the order. TrailingStopLoss starts null: SwingTradeRules sets it on the next
            // monitor cycle (INTRADAY mode) or once the trade qualifies for trailing (SWING_CLOSE).
            // Note: a Manual Real Trade's per-trade StopLossPct/TrailingSlPct live only on the
            // position row (see EvaluateAndExecuteRealBuyCoreAsync), not on RealOrder, so a manual
            // BUY that rests OPEN instead of filling immediately (rare for a Market order) reconciles
            // here without them - its trailing SL then falls back to the fixed default % rather than
            // the user's originally-chosen %, same as an Auto position.
            // SL / target were computed at signal time from the order price; keep their distance but anchor
            // them to the real fill price, as the immediate-fill path does.
            decimal fillDelta = order.Price > 0m ? executedPrice - order.Price : 0m;
            var newPosition = await _repository.UpsertPositionAsync(new RealPosition
            {
                UserId = order.UserId,
                Symbol = order.Symbol,
                Side = TradeSide.BUY,
                Quantity = filledQuantity,
                AverageEntryPrice = executedPrice,
                CurrentPrice = executedPrice,
                UnrealizedPnl = 0m,
                StopLoss = order.StopLoss.HasValue ? Math.Round(order.StopLoss.Value + fillDelta, 2) : null,
                TakeProfit = order.TakeProfit.HasValue ? Math.Round(order.TakeProfit.Value + fillDelta, 2) : null,
                TrailingStopLoss = null,
                Status = PositionStatus.OPEN,
                TradeType = order.TradeType,
                RealizedPnl = 0m
            });

            _realTradeCache?.AddOrUpdatePosition(newPosition);
            await EnsureSubscribedForExitMonitoringAsync(order.Symbol);

            await _repository.RecordTradeHistoryAsync(new RealTradeHistory
            {
                UserId = order.UserId,
                OrderId = order.Id,
                BrokerOrderId = order.BrokerOrderId,
                Symbol = order.Symbol,
                Side = TradeSide.BUY,
                Quantity = filledQuantity,
                EntryPrice = executedPrice,
                ExecutedPrice = executedPrice,
                RealizedPnl = 0m,
                TradeType = order.TradeType,
                IsExit = false,
                Remarks = isPartialOrder
                    ? $"Real BUY PARTIALLY FILLED {filledQuantity} of {order.Quantity} @ ₹{executedPrice:F2}, rest cancelled (Broker ID: {order.BrokerOrderId})"
                    : $"Real BUY confirmed FILLED @ ₹{executedPrice:F2} (Broker ID: {order.BrokerOrderId})"
            });

            string todayKey = $"realtrade:today_count:{order.UserId}:{DateTime.UtcNow:yyyyMMdd}";
            await _cacheService.RemoveAsync(todayKey);

            string buyPartialText = isPartialOrder ? $" — PARTIAL: {filledQuantity} of {order.Quantity} filled, rest cancelled; position opened for {filledQuantity}" : string.Empty;
            await LogAuditAsync(order.Symbol, "REAL_BUY", executedPrice, filledQuantity,
                $"⚡ Live BUY confirmed FILLED @ ₹{executedPrice:F2} (Qty: {filledQuantity}, Order #{order.BrokerOrderId}){buyPartialText}", order.UserId);

            if (_hubBroadcast != null)
            {
                await _hubBroadcast.BroadcastAllAsync("ReceiveRealTradeAlert", new
                {
                    symbol = order.Symbol,
                    side = "BUY",
                    quantity = filledQuantity,
                    price = executedPrice,
                    target = newPosition.TakeProfit,
                    stopLoss = newPosition.StopLoss,
                    brokerOrderId = order.BrokerOrderId,
                    userId = order.UserId,
                    message = $"⚡ LIVE REAL BUY: {filledQuantity} shares of {order.Symbol} confirmed FILLED @ ₹{executedPrice:N2}{buyPartialText}"
                });
            }

            await BroadcastDashboardUpdateAsync(order.UserId);
        }
    }

    // Best-effort: signal_price / filled_quantity come from schema.sql (Plan P4/P5). A missing column (script not
    // applied yet) must never fail an order that has already reached Zerodha - log and carry on.
    private async Task TryRecordFillDetailsAsync(int orderId, decimal? signalPrice, int? filledQuantity)
    {
        try
        {
            await _repository.RecordOrderFillDetailsAsync(orderId, signalPrice, filledQuantity);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not store signal price / filled quantity for real order #{OrderId} (apply the real_orders ALTERs in schema.sql).", orderId);
        }
    }

    // Reads one broker status into what actually traded. A failed status call counts as Pending (retry next pass).
    private static RealFill ResolveFill((bool Success, string? BrokerStatus, decimal AveragePrice, int FilledQuantity, string? Message) status, int orderedQuantity) =>
        status.Success
            ? RealFillResolver.Resolve(status.BrokerStatus, status.AveragePrice, status.FilledQuantity, orderedQuantity)
            : new RealFill(RealFillOutcome.Pending, 0, 0m);

    // Broker-confirmed fill of an Auto Short order: the entry SELL opens the short position (levels stored on the order,
    // shifted to the fill price); the covering BUY closes it - or shrinks it on a partial fill.
    private async Task FinalizeFilledShortOrderAsync(RealOrder order, decimal executedPrice, int filledQuantity, bool isPartialOrder)
    {
        if (IsExitOrder(order))
        {
            var position = await _repository.GetOpenPositionBySymbolAsync(order.UserId, order.Symbol);
            if (position == null || position.Side != TradeSide.SELL)
            {
                _logger.LogWarning("Cover order #{OrderId} ({Symbol}, User {UserId}) filled but no open SHORT position was found to close.",
                    order.Id, order.Symbol, order.UserId);
                await LogAuditAsync(order.Symbol, "REAL_COVER", executedPrice, filledQuantity,
                    $"⚠️ Buy-to-cover Order #{order.BrokerOrderId} FILLED @ ₹{executedPrice:F2} but no open short was found in QuantEdge - check Zerodha positions.", order.UserId);
                return;
            }

            int coveredQty = Math.Min(filledQuantity, position.Quantity);
            bool closesPosition = coveredQty >= position.Quantity;
            decimal realizedPnl = (position.AverageEntryPrice - executedPrice) * coveredQty;
            string exitReason = order.Remarks ?? "Exit";

            if (closesPosition)
            {
                await _repository.ClosePositionAsync(position.Id, executedPrice, position.RealizedPnl + realizedPnl, exitReason);
                _realTradeCache?.RemovePosition(position.Id);
                _realTradeCache?.RemoveLiveLtp(position.Symbol);
            }
            else
            {
                await _repository.ReducePositionQuantityAsync(position.Id, position.Quantity - coveredQty, realizedPnl);
                position.Quantity -= coveredQty;
                position.RealizedPnl += realizedPnl;
                _realTradeCache?.AddOrUpdatePosition(position);
            }

            await _repository.RecordTradeHistoryAsync(new RealTradeHistory
            {
                UserId = order.UserId,
                OrderId = order.Id,
                BrokerOrderId = order.BrokerOrderId,
                Symbol = order.Symbol,
                Side = TradeSide.BUY,
                Quantity = coveredQty,
                EntryPrice = position.AverageEntryPrice,
                ExecutedPrice = executedPrice,
                RealizedPnl = realizedPnl,
                TradeType = TradeType.Auto,
                ExitReason = exitReason,
                IsExit = true,
                Remarks = closesPosition
                    ? $"Real BUY TO COVER: {exitReason} | Realized P&L: ₹{realizedPnl:F2}"
                    : $"Real BUY TO COVER (partial {coveredQty} of {order.Quantity}, {position.Quantity} still short): {exitReason} | Realized P&L: ₹{realizedPnl:F2}"
            });

            string pnlSign = realizedPnl >= 0 ? "+" : "";
            string partialText = closesPosition ? string.Empty : $" — PARTIAL: {coveredQty} of {order.Quantity} covered, {position.Quantity} still short and monitored";
            await LogAuditAsync(order.Symbol, "REAL_COVER", executedPrice, coveredQty,
                $"⚡ Live BUY TO COVER confirmed FILLED @ ₹{executedPrice:F2} | P&L: {pnlSign}₹{realizedPnl:N2} (Order #{order.BrokerOrderId}){partialText}", order.UserId);

            if (_hubBroadcast != null)
            {
                await _hubBroadcast.BroadcastAllAsync("ReceiveRealTradeAlert", new
                {
                    symbol = order.Symbol,
                    side = "BUY",
                    quantity = coveredQty,
                    price = executedPrice,
                    realizedPnl,
                    exitReason,
                    brokerOrderId = order.BrokerOrderId,
                    userId = order.UserId,
                    message = $"⚡ LIVE REAL COVER: {order.Symbol} confirmed FILLED @ ₹{executedPrice:N2} | P&L: {pnlSign}₹{realizedPnl:N2}{partialText}"
                });
            }

            await BroadcastDashboardUpdateAsync(order.UserId);
            return;
        }

        // Entry SELL of a short.
        decimal fillDelta = order.Price > 0m ? executedPrice - order.Price : 0m;
        var newPosition = await _repository.UpsertPositionAsync(new RealPosition
        {
            UserId = order.UserId,
            Symbol = order.Symbol,
            Side = TradeSide.SELL,
            Quantity = filledQuantity,
            AverageEntryPrice = executedPrice,
            CurrentPrice = executedPrice,
            UnrealizedPnl = 0m,
            StopLoss = order.StopLoss.HasValue ? Math.Round(order.StopLoss.Value + fillDelta, 2) : null,
            TakeProfit = order.TakeProfit.HasValue ? Math.Round(order.TakeProfit.Value + fillDelta, 2) : null,
            TrailingStopLoss = null,
            Status = PositionStatus.OPEN,
            TradeType = order.TradeType,
            RealizedPnl = 0m
        });

        _realTradeCache?.AddOrUpdatePosition(newPosition);
        await EnsureSubscribedForExitMonitoringAsync(order.Symbol);

        await _repository.RecordTradeHistoryAsync(new RealTradeHistory
        {
            UserId = order.UserId,
            OrderId = order.Id,
            BrokerOrderId = order.BrokerOrderId,
            Symbol = order.Symbol,
            Side = TradeSide.SELL,
            Quantity = filledQuantity,
            EntryPrice = executedPrice,
            ExecutedPrice = executedPrice,
            RealizedPnl = 0m,
            TradeType = order.TradeType,
            IsExit = false,
            Remarks = isPartialOrder
                ? $"Real SHORT SELL PARTIALLY FILLED {filledQuantity} of {order.Quantity} @ ₹{executedPrice:F2}, rest cancelled (Broker ID: {order.BrokerOrderId})"
                : $"Real SHORT SELL confirmed FILLED @ ₹{executedPrice:F2} (Broker ID: {order.BrokerOrderId})"
        });

        await _cacheService.RemoveAsync($"realtrade:today_count:{order.UserId}:{DateTime.UtcNow:yyyyMMdd}");

        string shortPartialText = isPartialOrder ? $" — PARTIAL: {filledQuantity} of {order.Quantity} filled, rest cancelled; short opened for {filledQuantity}" : string.Empty;
        await LogAuditAsync(order.Symbol, "REAL_SHORT", executedPrice, filledQuantity,
            $"⚡ Live SHORT SELL confirmed FILLED @ ₹{executedPrice:F2} (Qty: {filledQuantity}, Order #{order.BrokerOrderId}){shortPartialText}", order.UserId);

        if (_hubBroadcast != null)
        {
            await _hubBroadcast.BroadcastAllAsync("ReceiveRealTradeAlert", new
            {
                symbol = order.Symbol,
                side = "SELL",
                quantity = filledQuantity,
                price = executedPrice,
                target = newPosition.TakeProfit,
                stopLoss = newPosition.StopLoss,
                brokerOrderId = order.BrokerOrderId,
                userId = order.UserId,
                message = $"⚡ LIVE REAL SHORT: {filledQuantity} shares of {order.Symbol} confirmed FILLED @ ₹{executedPrice:N2}{shortPartialText}"
            });
        }

        await BroadcastDashboardUpdateAsync(order.UserId);
    }

    private async Task FinalizeRejectedOrderAsync(RealOrder order, string? brokerStatus, string? message)
    {
        await _repository.UpdateOrderStatusAsync(order.Id, PaperOrderStatus.Rejected, 0m, order.BrokerOrderId, message);

        bool isExit = IsExitOrder(order);
        string actionType = isExit ? (order.IsShort ? "COVER_FAILED" : "SELL_FAILED") : "ORDER_REJECTED";
        string followUp = isExit ? "Position remains open for retry." : "No position was opened.";
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
        var livePrices = await GetLiveLtpsAsync(openPositions.Select(p => p.Symbol), userId);
        int filledCount = 0;
        int pendingCount = 0;

        var todayIst = SwingTradeRules.NowIst().Date;
        foreach (var pos in openPositions)
        {
            // A short still open from an earlier day was most likely squared off by Zerodha already - buying it "back"
            // would open a new long, so it is left for a manual check (see EvaluateAndExecuteRealCoverAsync).
            if (pos.Side == TradeSide.SELL && SwingTradeRules.ToIst(pos.OpenedAt).Date < todayIst)
            {
                _logger.LogWarning("Kill switch skipped stale short #{PositionId} ({Symbol}) opened on an earlier day.", pos.Id, pos.Symbol);
                continue;
            }

            decimal exitPrice = GetExitReferencePrice(pos, livePrices);
            // Side-aware: a long is SOLD, an Auto Short is BOUGHT back (MIS).
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

        var livePrices = await GetLiveLtpsAsync(new[] { position.Symbol }, userId);
        decimal exitPrice = GetExitReferencePrice(position, livePrices);
        return await ExecuteRealSellOrderAsync(position, exitPrice, reason, userId);
    }

    // ------------------------------------------------------------------------------------------
    // Live prices for open positions
    // ------------------------------------------------------------------------------------------

    // RealPosition.CurrentPrice/UnrealizedPnl are written once at entry and never persisted again, so
    // every read path overlays the live price here: a fresh WebSocket tick first, else Zerodha's own
    // last_price from the positions/holdings snapshot the caller already fetched (no extra API call).
    private void ApplyLivePrices(IEnumerable<RealPosition> positions, ZerodhaPositionsDto? brokerPositions, List<ZerodhaHoldingDto>? brokerHoldings)
    {
        foreach (var position in positions)
        {
            decimal ltp = 0m;
            string source = "LIVE";
            if (_realTradeCache == null || !_realTradeCache.TryGetFreshLtp(position.Symbol, RealTradeSchedule.LtpFreshnessWindow, out ltp))
            {
                ltp = FindBrokerLastPrice(position.Symbol, brokerPositions, brokerHoldings) ?? 0m;
                source = "BROKER";
            }

            if (ltp <= 0m)
            {
                // No fresh tick and no broker price: CurrentPrice is the value frozen at buy time, so the P&L
                // shown would be a fake ₹0. Flag it so the page shows "—" instead.
                position.PriceSource = "NONE";
                position.PriceAsOfUtc = null;
                continue;
            }
            position.PriceSource = source;
            position.PriceAsOfUtc = DateTime.UtcNow;
            position.CurrentPrice = ltp;
            position.UnrealizedPnl = CalculateUnrealizedPnl(position, ltp);
            // "Net if sold now": round-trip charges at this price (display only; gross stays what exits use).
            position.EstimatedCharges = Math.Round(ChargesCalculator.EstimateOpenPosition(
                position.AverageEntryPrice, ltp, position.Quantity, position.Side == TradeSide.SELL, position.OpenedAt), 2);
        }
    }

    private static decimal CalculateUnrealizedPnl(RealPosition position, decimal ltp) =>
        Math.Round((position.Side == TradeSide.SELL ? position.AverageEntryPrice - ltp : ltp - position.AverageEntryPrice) * position.Quantity, 2);

    private static decimal? FindBrokerLastPrice(string symbol, ZerodhaPositionsDto? brokerPositions, List<ZerodhaHoldingDto>? brokerHoldings)
    {
        var brokerPosition = brokerPositions?.Net?.FirstOrDefault(x =>
            string.Equals(x.TradingSymbol, symbol, StringComparison.OrdinalIgnoreCase) && x.LastPrice > 0m);
        if (brokerPosition != null) return brokerPosition.LastPrice;

        var holding = brokerHoldings?.FirstOrDefault(x =>
            string.Equals(x.TradingSymbol, symbol, StringComparison.OrdinalIgnoreCase) && x.LastPrice > 0m);
        return holding?.LastPrice;
    }

    // Live prices for an exit (or a display) that must not use the frozen RealPosition.CurrentPrice:
    // fresh WebSocket tick first, then one batched Zerodha REST quote for the remaining symbols.
    private async Task<Dictionary<string, (decimal Ltp, string Source)>> GetLiveLtpsAsync(IEnumerable<string> symbols, int userId)
    {
        var result = new Dictionary<string, (decimal Ltp, string Source)>(StringComparer.OrdinalIgnoreCase);
        var stale = new List<string>();

        foreach (var symbol in symbols.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (_realTradeCache != null && _realTradeCache.TryGetFreshLtp(symbol, RealTradeSchedule.LtpFreshnessWindow, out var ltp) && ltp > 0m)
                result[symbol] = (ltp, "WS");
            else
                stale.Add(symbol);
        }

        if (stale.Count == 0) return result;

        try
        {
            var quote = await _brokerService.GetLtpQuotesAsync(stale.Select(s => (s, "NSE")), userId);
            if (quote.Success && quote.Ltps != null)
            {
                foreach (var symbol in stale)
                {
                    if (quote.Ltps.TryGetValue(symbol, out var ltp) && ltp > 0m)
                        result[symbol] = (ltp, "REST");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "REST LTP quote failed for {Symbols} (User {UserId})", string.Join(",", stale), userId);
        }

        return result;
    }

    private static decimal GetExitReferencePrice(RealPosition position, Dictionary<string, (decimal Ltp, string Source)> livePrices) =>
        livePrices.TryGetValue(position.Symbol, out var live) ? live.Ltp
        : position.CurrentPrice > 0m ? position.CurrentPrice
        : position.AverageEntryPrice;

    // ------------------------------------------------------------------------------------------
    // Pre-trade guard preview (Signal Dashboard verdict card)
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// Read-only mirror of the guards in EvaluateAndExecuteRealBuyCoreAsync, in the same order and with
    /// the same reason texts: nothing is logged, no order is placed and the circuit breaker does not
    /// switch the bot off. Guard 12 (live price drift since the scan) is only knowable at order time.
    /// </summary>
    public async Task<BuyGuardPreviewDto> PreviewBuyGuardsAsync(string symbol, decimal entryPrice, int metConditionsCount, bool isBuySignal, int userId = 1)
    {
        symbol = symbol.ToUpper().Trim();
        var settings = await GetSettingsAsync(userId);
        var preview = new BuyGuardPreviewDto
        {
            IsBotEnabled = settings.IsRealTradeEnabled,
            TradeAmount = settings.FixedAmountPerTrade
        };

        BuyGuardPreviewDto Fail(int guard, string reason)
        {
            var g = RealTradeGuards.Get(guard);
            preview.WillBuy = false;
            preview.FailedGuardNumber = g.Number;
            preview.FailedGuardName = g.Name;
            preview.Reason = reason;
            return preview;
        }

        if (!settings.IsRealTradeEnabled)
            return Fail(1, "Auto Real Trade is switched off");

        var tokenCheck = await _brokerService.ValidateSessionTokenAsync(userId);
        if (!tokenCheck.IsValid)
            return Fail(2, $"Zerodha Token Invalid: {tokenCheck.Message}");

        if (!await _marketHoursService.IsWithinMarketHoursAsync())
            return Fail(3, "Outside Market Hours or Holiday");
        if (!IsWithinTradingWindow(settings.TradingWindowStart, settings.TradingWindowEnd))
            return Fail(3, $"Outside trading window ({settings.TradingWindowStart} - {settings.TradingWindowEnd})");

        if (!IsPastEntryDelay(settings.TradingWindowStart, settings.EntryDelayMinutes))
            return Fail(4, $"Opening entry delay active - new BUY signals held back for {settings.EntryDelayMinutes} min after {settings.TradingWindowStart}");

        if (!isBuySignal && metConditionsCount < settings.MinConditionsMatch)
            return Fail(5, $"Condition score {metConditionsCount}/11 below required {settings.MinConditionsMatch}/11");

        int todayCount = await GetTodayRealTradeCountAsync(userId);
        if (todayCount >= settings.MaxTradesPerDay)
            return Fail(6, $"Daily limit of {settings.MaxTradesPerDay} real trades reached ({todayCount}/{settings.MaxTradesPerDay})");

        decimal effectiveDailyLossLimit = SwingTradeRules.EffectiveDailyLossLimit(settings.MaxDailyLossLimit, settings.AvailableCapital);
        decimal todayRealizedPnl = await _repository.GetTodayRealizedPnlAsync(userId);
        var openPositions = (await _repository.GetOpenPositionsAsync(userId)).ToList();
        decimal totalLoss = todayRealizedPnl + openPositions.Sum(p => p.UnrealizedPnl);
        if (totalLoss <= -effectiveDailyLossLimit)
            return Fail(7, $"Daily loss limit ₹{effectiveDailyLossLimit:N2} breached (Total Loss: ₹{totalLoss:N2}) - the bot pauses itself on its next buy attempt");

        if (openPositions.Count >= SwingTradeRules.MaxConcurrentPositions)
            return Fail(8, $"Portfolio exposure cap reached ({openPositions.Count}/{SwingTradeRules.MaxConcurrentPositions} concurrent open positions)");

        var existingOpenPos = await _repository.GetOpenPositionBySymbolAsync(userId, symbol);
        if (existingOpenPos != null)
            return Fail(9, $"Symbol already has an OPEN real position (Position #{existingOpenPos.Id})");

        var existingPendingBuy = await _repository.GetOpenBrokerOrderAsync(userId, symbol, TradeSide.BUY);
        if (existingPendingBuy != null)
            return Fail(10, $"A BUY order (#{existingPendingBuy.BrokerOrderId}) is still waiting at the broker");

        var marginResult = await _brokerService.GetEquityMarginsAsync(userId);
        decimal availableMargin = marginResult.Success ? marginResult.AvailableCash : settings.AvailableCapital;
        preview.AvailableMargin = availableMargin;
        if (availableMargin < settings.FixedAmountPerTrade)
            return Fail(11, $"Insufficient Broker Capital (₹{availableMargin:N2} < Trade Amount ₹{settings.FixedAmountPerTrade:N2})");

        int quantity = entryPrice > 0m ? (int)Math.Floor(settings.FixedAmountPerTrade / entryPrice) : 0;
        preview.Quantity = quantity;
        preview.EstimatedCost = Math.Round(quantity * entryPrice, 2);
        if (quantity < 1)
            return Fail(13, $"Calculated quantity 0 for entry price ₹{entryPrice:N2}");

        preview.WillBuy = true;
        return preview;
    }

    // ------------------------------------------------------------------------------------------
    // Stock journey (Auto Real Trade "Flow" popup)
    // ------------------------------------------------------------------------------------------

    public async Task<SymbolJourneyDto> GetSymbolJourneyAsync(string symbol, int userId = 1)
    {
        symbol = symbol.ToUpper().Trim();
        var settings = await GetSettingsAsync(userId);
        var tradeParams = SwingTradeParams.From(settings);
        var nowIst = SwingTradeRules.NowIst();

        var dto = new SymbolJourneyDto
        {
            Symbol = symbol,
            AsOfUtc = DateTime.UtcNow,
            ExitMode = tradeParams.ExitMode,
            IsSwingClose = tradeParams.IsSwingClose,
            ProductType = settings.ProductType,
            IsMarketOpen = await _marketHoursService.IsWithinMarketHoursAsync(),
            IsClosingWindow = SwingTradeRules.IsInClosingWindow(nowIst, tradeParams),
            CloseCheckTime = SwingTradeRules.GetClosingWindowStart(tradeParams).ToString(@"hh\:mm"),
            MaxDurationDays = settings.MaxDurationDays,
            FixedAmountPerTrade = settings.FixedAmountPerTrade,
            MonitorIntervalSeconds = (int)RealTradeSchedule.MonitorInterval.TotalSeconds
        };

        var position = await _repository.GetOpenPositionBySymbolAsync(userId, symbol);
        var orders = (await _repository.GetRecentOrdersAsync(userId, 100))
            .Where(o => string.Equals(o.Symbol, symbol, StringComparison.OrdinalIgnoreCase))
            .OrderBy(o => o.CreatedAt)
            .ToList();
        var logs = (await GetTodayLogsAsync(userId, 500))
            .Where(l => string.Equals(l.Symbol, symbol, StringComparison.OrdinalIgnoreCase))
            .OrderBy(l => l.ExecutedAt)
            .ToList();
        dto.Orders = orders;
        dto.Logs = logs;

        // The popup refreshes every few seconds, so Zerodha is only called when needed: a fresh
        // WebSocket tick covers a bot-managed position; the account rows are only fetched for a stock
        // the bot doesn't manage; a REST quote is the last resort.
        if (_realTradeCache != null && _realTradeCache.TryGetFreshLtp(symbol, RealTradeSchedule.LtpFreshnessWindow, out var wsLtp))
        {
            dto.Ltp = wsLtp;
            dto.LtpSource = "WS";
        }

        var tokenValidation = await _brokerService.ValidateSessionTokenAsync(userId);
        if (tokenValidation.IsValid && position == null)
        {
            var posTask = _brokerService.GetLivePositionsAsync(userId);
            var holdTask = _brokerService.GetLiveHoldingsAsync(userId);
            await Task.WhenAll(posTask, holdTask);

            var posRes = await posTask;
            dto.BrokerPosition = posRes.Success
                ? posRes.Positions?.Net?.FirstOrDefault(x => string.Equals(x.TradingSymbol, symbol, StringComparison.OrdinalIgnoreCase) && x.Quantity != 0)
                : null;
            var holdRes = await holdTask;
            dto.BrokerHolding = holdRes.Success
                ? holdRes.Holdings?.FirstOrDefault(x => string.Equals(x.TradingSymbol, symbol, StringComparison.OrdinalIgnoreCase) && x.Quantity + x.T1Quantity > 0)
                : null;

            if (!dto.Ltp.HasValue)
            {
                decimal? brokerLtp = dto.BrokerPosition is { LastPrice: > 0m } bp ? bp.LastPrice
                    : dto.BrokerHolding is { LastPrice: > 0m } bh ? bh.LastPrice
                    : null;
                if (brokerLtp.HasValue)
                {
                    dto.Ltp = brokerLtp;
                    dto.LtpSource = "ZERODHA";
                }
            }
        }

        if (!dto.Ltp.HasValue && tokenValidation.IsValid && (position != null || dto.BrokerPosition != null || dto.BrokerHolding != null))
        {
            var live = await GetLiveLtpsAsync(new[] { symbol }, userId);
            if (live.TryGetValue(symbol, out var price))
            {
                dto.Ltp = price.Ltp;
                dto.LtpSource = price.Source;
            }
        }

        if (position != null)
        {
            if (dto.Ltp.HasValue)
            {
                position.CurrentPrice = dto.Ltp.Value;
                position.UnrealizedPnl = CalculateUnrealizedPnl(position, dto.Ltp.Value);
            }
            dto.Position = position;
            dto.IsEntryDay = SwingTradeRules.ToIst(position.OpenedAt).Date == nowIst.Date;
            dto.TradingDaysHeld = await _marketHoursService.CountTradingDaysElapsedAsync(position.OpenedAt, DateTime.UtcNow);

            // The order that opened this position (a BUY, or an Auto Short's SELL); none means it was enrolled from Zerodha Holdings.
            bool isShortPosition = position.Side == TradeSide.SELL;
            dto.EntryOrder = orders
                .Where(o => o.Side == position.Side && o.IsShort == isShortPosition && o.Status == PaperOrderStatus.Filled)
                .Select(o => (Order: o, Gap: Math.Abs(((o.FilledAt ?? o.CreatedAt) - position.OpenedAt).TotalMinutes)))
                .Where(x => x.Gap <= 30)
                .OrderBy(x => x.Gap)
                .Select(x => x.Order)
                .FirstOrDefault();
            dto.EntrySource = dto.EntryOrder != null
                ? (dto.EntryOrder.TradeType == TradeType.Manual ? "MANUAL" : "AUTO_SIGNAL")
                : position.TradeType == TradeType.Manual ? "MANUAL" : "HOLDING";

            // Levels exactly as the monitor derives them (SwingTradeRules infers the ATR from the SL).
            var view = ExitPositionView.From(position);
            decimal entry = position.AverageEntryPrice;
            decimal? PnlAt(decimal? level) => level.HasValue
                ? Math.Round((isShortPosition ? entry - level.Value : level.Value - entry) * position.Quantity, 2)
                : null;

            var levels = new SymbolJourneyLevelsDto
            {
                Invested = Math.Round(entry * position.Quantity, 2),
                UnrealizedPnl = dto.Ltp.HasValue ? position.UnrealizedPnl : null,
                StopLoss = position.StopLoss,
                TakeProfit = position.TakeProfit,
                TrailingStopLoss = position.TrailingStopLoss,
                IsTrailActive = isShortPosition
                    ? position.TrailingStopLoss.HasValue && (!tradeParams.IsSwingClose || position.TrailingStopLoss.Value <= entry)
                    : tradeParams.IsSwingClose
                    ? position.TrailingStopLoss.HasValue && position.TrailingStopLoss.Value >= entry
                    : position.TrailingStopLoss.HasValue,
                Atr = Math.Round(isShortPosition ? SwingTradeRules.InferShortAtr(view, tradeParams) : SwingTradeRules.InferAtr(view, tradeParams), 4)
            };
            // The emergency stop / closing-basis trail activation are long (swing) concepts; a short's stops are all live.
            if (tradeParams.IsSwingClose && !isShortPosition)
            {
                levels.EmergencyStop = SwingTradeRules.GetEmergencyStop(view, tradeParams);
                levels.TrailActivationPrice = Math.Round(SwingTradeRules.GetTrailActivationPrice(view, tradeParams), 2);
            }
            levels.PnlAtTarget = PnlAt(levels.TakeProfit);
            levels.PnlAtStopLoss = PnlAt(levels.StopLoss);
            levels.PnlAtEmergencyStop = PnlAt(levels.EmergencyStop);
            levels.PnlAtTrailingStopLoss = levels.IsTrailActive ? PnlAt(levels.TrailingStopLoss) : null;
            dto.Levels = levels;

            if (dto.Ltp.HasValue)
            {
                var decision = isShortPosition
                    ? SwingTradeRules.EvaluateShortExit(view, dto.Ltp.Value, nowIst, settings.ShortSquareOffTime, tradeParams)
                    : SwingTradeRules.EvaluateExit(view, dto.Ltp.Value, nowIst, dto.TradingDaysHeld,
                        settings.MaxDurationDays, tradeParams);
                dto.WouldSellNow = decision.ShouldExit;
                dto.DecisionReason = decision.ShouldExit ? decision.Reason : null;
            }
        }

        var skip = logs.LastOrDefault(l =>
            string.Equals(l.ActionType, "REAL_SIGNAL_SKIPPED", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(l.ActionType, "CIRCUIT_BREAKER", StringComparison.OrdinalIgnoreCase));
        if (skip != null)
        {
            var guard = RealTradeGuards.Classify(skip.ActionType, skip.Reason);
            dto.LastSkip = new SymbolJourneySkipDto
            {
                AtUtc = skip.ExecutedAt,
                ActionType = skip.ActionType,
                Reason = skip.Reason ?? string.Empty,
                Price = skip.Price,
                GuardNumber = guard?.Number,
                GuardName = guard?.Name,
                TotalGuards = RealTradeGuards.TotalGuards
            };

            if (guard?.Number == 11 && tokenValidation.IsValid)
            {
                var margin = await _brokerService.GetEquityMarginsAsync(userId);
                if (margin.Success) dto.AvailableMargin = margin.AvailableCash;
            }
        }

        dto.Status = position != null ? "OPEN"
            : dto.BrokerPosition != null || dto.BrokerHolding != null ? "NOT_MANAGED"
            : dto.LastSkip != null ? "SKIPPED"
            : "NONE";

        return dto;
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

    private static bool IsWithinTradingWindow(string startTime, string endTime) =>
        SwingTradeRules.IsWithinTradingWindow(startTime, endTime, SwingTradeRules.NowIst());

    // Entry-only gate - never applied to exits (see call site in EvaluateAndExecuteRealBuyCoreAsync).
    private static bool IsPastEntryDelay(string windowStartTime, int entryDelayMinutes) =>
        SwingTradeRules.IsPastEntryDelay(windowStartTime, entryDelayMinutes, SwingTradeRules.NowIst());

    private async Task BroadcastDashboardUpdateAsync(int userId)
    {
        if (_hubBroadcast != null)
        {
            try
            {
                var dashboard = await GetDashboardDataAsync(userId);
                await _hubBroadcast.BroadcastAllAsync("ReceiveRealTradeDashboardUpdate", dashboard);
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

        ApplyLivePrices(positions, brokerPositions, brokerHoldings);
        decimal unrealizedPnl = positions.Sum(p => p.UnrealizedPnl);

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
            TotalEstimatedCharges = Math.Round(positions.Sum(p => p.EstimatedCharges), 2),
            TotalRealizedPnlToday = todayRealizedPnl,
            BrokerPositions = brokerPositions,
            BrokerHoldings = brokerHoldings,
            OpenPositions = positions
        };
    }
}
