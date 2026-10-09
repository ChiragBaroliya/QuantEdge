using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using QuantEdge.Domain.Entities;
using QuantEdge.Infrastructure.DTOs;
using QuantEdge.Infrastructure.Hubs;
using QuantEdge.Infrastructure.Helpers;
using QuantEdge.Infrastructure.Interfaces;
using QuantEdge.Infrastructure.Persistence.Repositories;

namespace QuantEdge.Infrastructure.Services;

public class AutoTradeService : IAutoTradeService
{
    // Buy/sell rules live in SwingTradeRules, shared with AutoRealTradeService - this service only
    // simulates execution around them.
    private readonly IAutoTradeRepository _repository;
    private readonly IPaperTradingRepository _paperRepository;
    private readonly IPaperTradingService _paperService;
    private readonly IIndianHolidayRepository _holidayRepository;
    private readonly IMarketHoursService _marketHoursService;
    private readonly ICacheService _cacheService;
    private readonly IHubContext<MarketDataHub>? _hubContext;
    private readonly PaperMatchingEngine? _matchingEngine;
    private readonly ILogger<AutoTradeService> _logger;

    public AutoTradeService(
        IAutoTradeRepository repository,
        IPaperTradingRepository paperRepository,
        IPaperTradingService paperService,
        IIndianHolidayRepository holidayRepository,
        IMarketHoursService marketHoursService,
        ICacheService cacheService,
        ILogger<AutoTradeService> logger,
        IHubContext<MarketDataHub>? hubContext = null,
        PaperMatchingEngine? matchingEngine = null)
    {
        _matchingEngine = matchingEngine;
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _paperRepository = paperRepository ?? throw new ArgumentNullException(nameof(paperRepository));
        _paperService = paperService ?? throw new ArgumentNullException(nameof(paperService));
        _holidayRepository = holidayRepository ?? throw new ArgumentNullException(nameof(holidayRepository));
        _marketHoursService = marketHoursService ?? throw new ArgumentNullException(nameof(marketHoursService));
        _cacheService = cacheService ?? throw new ArgumentNullException(nameof(cacheService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _hubContext = hubContext;
    }

    public async Task<AutoTradeSettings> GetSettingsAsync(string userId = "default_user")
    {
        string cacheKey = $"autotrade:settings:{userId}";
        var cached = await _cacheService.GetAsync<AutoTradeSettings>(cacheKey);
        if (cached != null) return cached;

        var settings = await _repository.GetSettingsAsync(userId);
        await _cacheService.SetAsync(cacheKey, settings, TimeSpan.FromMinutes(15));
        return settings;
    }

    public async Task<AutoTradeSettings> UpdateSettingsAsync(AutoTradeSettingsUpdateDto updateDto, string userId = "default_user")
    {
        var existing = await GetSettingsAsync(userId);

        existing.IsAutoTradeEnabled = updateDto.IsAutoTradeEnabled;
        existing.AvailableCapital = updateDto.AvailableCapital;
        existing.ProfitTargetPct = updateDto.ProfitTargetPct;
        // StopLossPct/TrailingSlPct are intentionally left untouched - no longer global settings
        // (same as Real Trade); SL/Target/Trailing SL come from the swing exit policy below.
        existing.MaxDailyLossLimit = updateDto.MaxDailyLossLimit;
        existing.EntryDelayMinutes = updateDto.EntryDelayMinutes;
        existing.ExitMode = string.IsNullOrWhiteSpace(updateDto.ExitMode) ? SwingTradeRules.ExitModeSwingClose : updateDto.ExitMode.ToUpperInvariant();
        existing.CloseCheckTime = string.IsNullOrWhiteSpace(updateDto.CloseCheckTime) ? "15:15" : updateDto.CloseCheckTime;
        existing.StopLossAtrMult = updateDto.StopLossAtrMult;
        existing.TrailAtrMult = updateDto.TrailAtrMult;
        existing.TargetAtrMult = updateDto.TargetAtrMult;
        existing.MaxDurationDays = updateDto.MaxDurationDays;
        existing.MaxTradesPerDay = updateDto.MaxTradesPerDay;
        existing.FixedAmountPerTrade = updateDto.FixedAmountPerTrade;
        existing.MinConditionsMatch = updateDto.MinConditionsMatch;
        existing.TradingWindowStart = updateDto.TradingWindowStart ?? "09:15";
        existing.TradingWindowEnd = updateDto.TradingWindowEnd ?? "15:30";
        existing.IsAutoShortEnabled = updateDto.IsAutoShortEnabled;
        existing.ShortEntryCutoff = string.IsNullOrWhiteSpace(updateDto.ShortEntryCutoff) ? "15:00" : updateDto.ShortEntryCutoff;
        existing.ShortSquareOffTime = string.IsNullOrWhiteSpace(updateDto.ShortSquareOffTime) ? "15:15" : updateDto.ShortSquareOffTime;

        var updated = await _repository.UpsertSettingsAsync(existing);

        // Synchronize paper account balance with newly configured Available Capital
        var paperAccount = await _paperRepository.GetAccountAsync(userId);
        if (paperAccount != null)
        {
            if (paperAccount.UsedMargin == 0)
            {
                await _paperRepository.UpdateAccountBalanceAndMarginAsync(
                    paperAccount.Id, 
                    updateDto.AvailableCapital, 
                    0m, 
                    paperAccount.RealizedPnl);
            }
            else
            {
                await _paperRepository.UpdateAccountBalanceAndMarginAsync(
                    paperAccount.Id, 
                    updateDto.AvailableCapital, 
                    paperAccount.UsedMargin, 
                    paperAccount.RealizedPnl);
            }
        }

        // Invalidate Memory Cache
        string cacheKey = $"autotrade:settings:{userId}";
        await _cacheService.RemoveAsync(cacheKey);
        await _cacheService.SetAsync(cacheKey, updated, TimeSpan.FromMinutes(15));

        await BroadcastDashboardUpdateAsync(userId);
        return updated;
    }

    public async Task ToggleAutoTradeAsync(bool enabled, string userId = "default_user")
    {
        await _repository.ToggleAutoTradeAsync(userId, enabled);
        
        string cacheKey = $"autotrade:settings:{userId}";
        await _cacheService.RemoveAsync(cacheKey);

        await LogAuditAsync("SYSTEM", enabled ? "AUTO_TRADE_ENABLED" : "AUTO_TRADE_DISABLED", null, null,
            enabled ? "Auto Trading Master Switch turned ON" : "Auto Trading Master Switch turned OFF", userId);

        await BroadcastDashboardUpdateAsync(userId);
    }

    public async Task<int> GetTodayAutoTradeCountAsync(string userId = "default_user")
    {
        string todayKey = $"autotrade:today_count:{userId}:{DateTime.UtcNow:yyyyMMdd}";
        var cachedCount = await _cacheService.GetAsync<int?>(todayKey);
        if (cachedCount.HasValue) return cachedCount.Value;

        int count = await _repository.GetTodayAutoTradeCountAsync(userId);
        await _cacheService.SetAsync(todayKey, (int?)count, TimeSpan.FromMinutes(5));
        return count;
    }

    public async Task<AutoTradeDashboardDto> GetDashboardDataAsync(string userId = "default_user")
    {
        var settings = await GetSettingsAsync(userId);
        var paperAccount = await _paperRepository.GetAccountAsync(userId);
        var positions = (await _paperService.GetOpenPositionsAsync(userId))
            .Where(p => p.TradeType == TradeType.Auto)
            .ToList();
        
        var todayLogs = await GetTodayLogsAsync(userId, 50);
        int todayCount = await GetTodayAutoTradeCountAsync(userId);

        decimal unrealizedPnl = positions.Sum(p => p.UnrealizedPnl);
        decimal estimatedCharges = Math.Round(positions.Sum(p => p.EstimatedCharges), 2);

        DateTime nowIst = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneHelper.IndianTimeZone);
        DateTime todayStartIst = nowIst.Date;
        DateTime todayStartUtc = TimeZoneInfo.ConvertTimeToUtc(todayStartIst, TimeZoneHelper.IndianTimeZone);

        var historyFilter = new PaperTradeHistoryFilterDto
        {
            Page = 1,
            PageSize = 1000,
            FromDate = todayStartUtc,
            ToDate = null
        };
        var (historyItems, _) = await _paperRepository.GetTradeHistoryPagedAsync(paperAccount?.Id ?? 0, historyFilter);
        var todayHistory = historyItems.Where(t => t.TradeType == TradeType.Auto).ToList();

        decimal todayRealizedPnl = todayHistory.Sum(t => t.RealizedPnl);
        // Entries (a BUY, or an Auto Short's SELL) carry no exit reason; every Auto exit does.
        decimal todayTradeAmount = todayHistory.Where(t => string.IsNullOrEmpty(t.ExitReason)).Sum(t => t.Quantity * (t.EntryPrice > 0 ? t.EntryPrice : t.ExecutedPrice));
        if (todayTradeAmount == 0 && todayCount > 0)
        {
            todayTradeAmount = todayCount * settings.FixedAmountPerTrade;
        }

        var nextRunInfo = Calculate15MinNextRunInfo();

        return new AutoTradeDashboardDto
        {
            Settings = settings,
            TodayTradeCount = todayCount,
            TodayTradeAmount = todayTradeAmount,
            ActivePositionsCount = positions.Count,
            TotalUnrealizedPnl = unrealizedPnl,
            TotalEstimatedCharges = estimatedCharges,
            TotalRealizedPnlToday = todayRealizedPnl,
            AvailableMargin = paperAccount?.AvailableMargin ?? 0m,
            UsedMargin = paperAccount?.UsedMargin ?? 0m,
            IsWebSocketConnected = true,
            IsRestPollingFallback = false,
            SystemStatus = settings.IsAutoTradeEnabled ? "ACTIVE" : "PAUSED",
            OpenPositions = positions,
            TodayLogs = todayLogs,
            NextRunTime = nextRunInfo.NextRunTime,
            NextRunSeconds = nextRunInfo.NextRunSeconds,
            NextRunFormatted = nextRunInfo.FormattedText,
            IsMarketOpen = nextRunInfo.IsMarketOpen
        };
    }

    public static (DateTime NextRunTime, int NextRunSeconds, string FormattedText, bool IsMarketOpen) Calculate15MinNextRunInfo()
    {
        DateTime nowIst = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneHelper.IndianTimeZone);

        bool isWeekend = nowIst.DayOfWeek == DayOfWeek.Saturday || nowIst.DayOfWeek == DayOfWeek.Sunday;
        TimeSpan marketStart = new TimeSpan(9, 15, 0);
        TimeSpan marketEnd = new TimeSpan(15, 30, 0);
        TimeSpan timeOfDay = nowIst.TimeOfDay;

        bool isWithinMarketHours = !isWeekend && timeOfDay >= marketStart && timeOfDay <= marketEnd;

        DateTime targetNextRun;

        if (isWithinMarketHours)
        {
            // Calculate next 15-minute boundary (e.g. 09:30, 09:45, 10:00, 10:15, 10:30...)
            int currentMinute = nowIst.Minute;
            int minuteRemainder = currentMinute % 15;
            int minuteOffset = 15 - minuteRemainder;
            targetNextRun = nowIst.AddMinutes(minuteOffset).AddSeconds(-nowIst.Second).AddMilliseconds(-nowIst.Millisecond);

            if (targetNextRun.TimeOfDay > marketEnd)
            {
                targetNextRun = nowIst.Date.AddDays(1).Add(new TimeSpan(9, 15, 0));
                while (targetNextRun.DayOfWeek == DayOfWeek.Saturday || targetNextRun.DayOfWeek == DayOfWeek.Sunday)
                {
                    targetNextRun = targetNextRun.AddDays(1);
                }
            }
        }
        else
        {
            // Market is closed or weekend. Next run is 09:15 AM IST on next trading day
            DateTime nextDay = nowIst.TimeOfDay > marketEnd ? nowIst.Date.AddDays(1) : nowIst.Date;
            targetNextRun = nextDay.Add(new TimeSpan(9, 15, 0));
            while (targetNextRun.DayOfWeek == DayOfWeek.Saturday || targetNextRun.DayOfWeek == DayOfWeek.Sunday)
            {
                targetNextRun = targetNextRun.AddDays(1);
            }
        }

        int remainingSeconds = Math.Max(0, (int)(targetNextRun - nowIst).TotalSeconds);
        TimeSpan remainingTime = TimeSpan.FromSeconds(remainingSeconds);

        string formatted;
        if (isWithinMarketHours)
        {
            formatted = remainingTime.Hours > 0 
                ? $"{remainingTime.Hours}h {remainingTime.Minutes}m" 
                : $"{remainingTime.Minutes}m {remainingTime.Seconds}s";
        }
        else
        {
            formatted = targetNextRun.ToString("ddd, dd MMM HH:mm IST");
        }

        return (targetNextRun, remainingSeconds, formatted, isWithinMarketHours);
    }

    public async Task LogAuditAsync(string symbol, string actionType, decimal? price, int? quantity, string? reason, string userId = "default_user")
    {
        var log = new AutoTradeExecutionLog
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
            await _hubContext.Clients.All.SendAsync("ReceiveAutoTradeLogEvent", log);
        }
    }

    public async Task<IEnumerable<AutoTradeExecutionLog>> GetTodayLogsAsync(string userId = "default_user", int limit = 50)
    {
        return await _repository.GetTodayLogsAsync(userId, limit);
    }

    // Entry gates run in the same order, with the same rules, as AutoRealTradeService's
    // EvaluateAndExecuteRealBuyCoreAsync - only broker-specific steps (token check, pending broker
    // orders, broker margin) are replaced by their paper-account equivalents.
    public Task<bool> EvaluateAndExecuteAutoBuyAsync(string symbol, decimal entryPrice, int metConditionsCount, string userId = "default_user", bool isBuySignal = false,
        decimal? engineStopLoss = null, decimal? engineTarget = null, decimal? dailyAtr = null, decimal? riskPct = null) =>
        EvaluateAndExecuteAutoEntryAsync(TradeSide.BUY, symbol, entryPrice, metConditionsCount, userId, isBuySignal,
            engineStopLoss, engineTarget, dailyAtr, riskPct);

    // Auto Short Selling - one entry path with BUY, so a short passes exactly the same gates, plus the short switch and
    // the short entry cut-off. Paper only: a simulated SELL on the paper account, never a broker order.
    public Task<bool> EvaluateAndExecuteAutoShortAsync(string symbol, decimal entryPrice, int metConditionsCount, string userId = "default_user", bool isSellSignal = false,
        decimal? engineStopLoss = null, decimal? engineTarget = null, decimal? dailyAtr = null, decimal? riskPct = null) =>
        EvaluateAndExecuteAutoEntryAsync(TradeSide.SELL, symbol, entryPrice, metConditionsCount, userId, isSellSignal,
            engineStopLoss, engineTarget, dailyAtr, riskPct);

    private async Task<bool> EvaluateAndExecuteAutoEntryAsync(TradeSide side, string symbol, decimal entryPrice, int metConditionsCount, string userId,
        bool isBuySignal, decimal? engineStopLoss, decimal? engineTarget, decimal? dailyAtr, decimal? riskPct)
    {
        bool isShort = side == TradeSide.SELL;
        string label = isShort ? "SHORT" : "BUY";
        symbol = symbol.ToUpper().Trim();
        var settings = await GetSettingsAsync(userId);
        var nowIst = SwingTradeRules.NowIst();

        // 1. Master Switch Validation (a short also needs the Auto Short switch - OFF by default)
        if (!settings.IsAutoTradeEnabled || (isShort && !settings.IsAutoShortEnabled))
        {
            return false;
        }

        // 2. Market Hours & Trading Window Check
        if (!await _marketHoursService.IsWithinMarketHoursAsync())
        {
            await LogAuditAsync(symbol, "SIGNAL_SKIPPED", entryPrice, 0, "Outside Market Hours or Holiday", userId);
            return false;
        }

        if (!SwingTradeRules.IsWithinTradingWindow(settings.TradingWindowStart, settings.TradingWindowEnd, nowIst))
        {
            await LogAuditAsync(symbol, "SIGNAL_SKIPPED", entryPrice, 0,
                $"Outside trading window ({settings.TradingWindowStart} - {settings.TradingWindowEnd})", userId);
            return false;
        }

        // 2b. Opening Entry Delay - entries only, exits are never delayed.
        if (!SwingTradeRules.IsPastEntryDelay(settings.TradingWindowStart, settings.EntryDelayMinutes, nowIst))
        {
            await LogAuditAsync(symbol, "SIGNAL_SKIPPED", entryPrice, 0,
                $"Opening entry delay active - new {label} signals held back for {settings.EntryDelayMinutes} min after {settings.TradingWindowStart}", userId);
            return false;
        }

        // 2c. Shorts are intraday only: no new short at/after the cut-off, so each has time to be bought back.
        if (isShort && !SwingTradeRules.IsBeforeShortEntryCutoff(settings.ShortEntryCutoff, nowIst))
        {
            await LogAuditAsync(symbol, "SIGNAL_SKIPPED", entryPrice, 0,
                $"Short selling closed for today - no new shorts at/after {settings.ShortEntryCutoff} IST (open shorts are squared off at {settings.ShortSquareOffTime} IST)", userId);
            return false;
        }

        // 3. Condition Match Check
        if (!isBuySignal && metConditionsCount < settings.MinConditionsMatch)
        {
            await LogAuditAsync(symbol, "SIGNAL_SKIPPED", entryPrice, 0,
                $"Condition match score {metConditionsCount}/11 below minimum required {settings.MinConditionsMatch}/11", userId);
            return false;
        }

        // 4. Daily Trade Limit Check
        int todayCount = await GetTodayAutoTradeCountAsync(userId);
        if (todayCount >= settings.MaxTradesPerDay)
        {
            await LogAuditAsync(symbol, "SIGNAL_SKIPPED", entryPrice, 0,
                $"Daily limit of {settings.MaxTradesPerDay} auto trades reached ({todayCount}/{settings.MaxTradesPerDay} executed)", userId);
            return false;
        }

        var paperAccount = await _paperRepository.GetAccountAsync(userId);
        if (paperAccount == null) return false;

        // 5. Daily Loss Circuit Breaker (defaults to 10% of Available Capital when not overridden)
        decimal effectiveDailyLossLimit = SwingTradeRules.EffectiveDailyLossLimit(settings.MaxDailyLossLimit, settings.AvailableCapital);
        var openAutoPositions = (await _paperService.GetOpenPositionsAsync(userId))
            .Where(p => p.TradeType == TradeType.Auto)
            .ToList();
        decimal todayRealizedPnl = await GetTodayAutoRealizedPnlAsync(paperAccount.Id);
        decimal totalLoss = todayRealizedPnl + openAutoPositions.Sum(p => p.UnrealizedPnl);

        if (totalLoss <= -effectiveDailyLossLimit)
        {
            await LogAuditAsync(symbol, "CIRCUIT_BREAKER", entryPrice, 0,
                $"Daily loss limit ₹{effectiveDailyLossLimit:N2} breached (Total Loss: ₹{totalLoss:N2}). Pausing auto paper bot.", userId);
            await ToggleAutoTradeAsync(false, userId);
            return false;
        }

        // 5b. Portfolio-Level Exposure Cap
        if (openAutoPositions.Count >= SwingTradeRules.MaxConcurrentPositions)
        {
            await LogAuditAsync(symbol, "SIGNAL_SKIPPED", entryPrice, 0,
                $"Portfolio exposure cap reached ({openAutoPositions.Count}/{SwingTradeRules.MaxConcurrentPositions} concurrent open positions)", userId);
            return false;
        }

        // 6. Duplicate Open Position Check - any open position in the symbol, long or short, blocks a new entry.
        var existingOpenPos = await _paperRepository.GetOpenPositionBySymbolAsync(paperAccount.Id, symbol);
        if (existingOpenPos != null)
        {
            await LogAuditAsync(symbol, "SIGNAL_SKIPPED", entryPrice, 0,
                $"Symbol already has an OPEN auto position (Position #{existingOpenPos.Id})", userId);
            return false;
        }

        // 7. Capital Validation (paper account margin stands in for the broker margin)
        decimal availableMargin = paperAccount.AvailableMargin;
        if (availableMargin < settings.FixedAmountPerTrade)
        {
            await LogAuditAsync(symbol, "SIGNAL_SKIPPED", entryPrice, 0,
                $"Insufficient Available Capital (₹{availableMargin:N2} < Trade Amount ₹{settings.FixedAmountPerTrade:N2})", userId);
            return false;
        }

        // 7b. Live Quote Re-check against the latest tick, same drift guard as Real Trade. If no tick
        // has arrived for the symbol yet, proceed with the scan-time price (Real does the same when
        // its live quote fetch fails).
        decimal liveLtp = _matchingEngine?.GetLtp(symbol) ?? 0m;
        if (liveLtp > 0m)
        {
            string? driftReason = isShort
                ? SwingTradeRules.CheckShortSignalDrift(entryPrice, liveLtp)
                : SwingTradeRules.CheckSignalDrift(entryPrice, liveLtp);
            if (driftReason != null)
            {
                await LogAuditAsync(symbol, "SIGNAL_SKIPPED", entryPrice, 0, driftReason, userId);
                return false;
            }

            decimal priceDelta = liveLtp - entryPrice;
            if (engineStopLoss.HasValue) engineStopLoss = engineStopLoss.Value + priceDelta;
            if (engineTarget.HasValue) engineTarget = engineTarget.Value + priceDelta;
            entryPrice = liveLtp;
        }

        // 8. Target, Stop Loss & initial Trailing SL - shared SwingTradeRules levels (mirrored for a short:
        // Stop Loss above the entry, Target below).
        var levels = isShort
            ? SwingTradeRules.ComputeShortEntryLevels(entryPrice, dailyAtr, engineStopLoss, engineTarget, SwingTradeParams.From(settings))
            : SwingTradeRules.ComputeEntryLevels(entryPrice, dailyAtr, engineStopLoss, engineTarget, null, null, SwingTradeParams.From(settings));
        decimal takeProfit = levels.TakeProfit;
        decimal stopLoss = levels.StopLoss;
        decimal? trailingSl = levels.TrailingStopLoss;
        string tslText = trailingSl.HasValue ? $"₹{trailingSl.Value:F2}" : (isShort ? "activates after -1 ATR" : "activates after +1 ATR");

        // Risk-based size (Plan D5): at most FixedAmountPerTrade of stock AND at most DefaultRiskPerTradePct of capital
        // lost if the stop is hit - a volatile stock (wide stop) gets fewer shares instead of 3-4x the rupee risk.
        // riskPct: the market regime's risk per trade in REGIME mode (smaller in weak markets), else the default 1%.
        var sizing = isShort
            ? SwingTradeRules.RiskSizedShortQuantity(entryPrice, stopLoss, settings.AvailableCapital, settings.FixedAmountPerTrade,
                riskPct ?? SwingTradeRules.DefaultRiskPerTradePct)
            : SwingTradeRules.RiskSizedQuantity(entryPrice, stopLoss, settings.AvailableCapital, settings.FixedAmountPerTrade,
                riskPct ?? SwingTradeRules.DefaultRiskPerTradePct);
        int quantity = sizing.Quantity;
        if (quantity < 1)
        {
            await LogAuditAsync(symbol, "SIGNAL_SKIPPED", entryPrice, 0,
                $"Calculated quantity 0 for entry price ₹{entryPrice:N2} ({sizing.Reason})", userId);
            return false;
        }

        try
        {
            // Execute Auto Paper entry order (BUY, or the SELL that opens a short) - simulated, no broker involved.
            var order = await _paperRepository.CreateOrderAsync(new PaperOrder
            {
                AccountId = paperAccount.Id,
                Symbol = symbol,
                OrderType = PaperOrderType.Market,
                Side = side,
                Quantity = quantity,
                Price = entryPrice,
                StopLoss = stopLoss,
                TakeProfit = takeProfit,
                Status = PaperOrderStatus.Filled,
                FilledPrice = entryPrice,
                FilledAt = DateTime.UtcNow,
                TradeType = TradeType.Auto,
                Remarks = $"Auto {label} (Score {metConditionsCount}/11, SL ₹{stopLoss:F2} / Target ₹{takeProfit:F2})"
            });

            // Upsert Auto Paper Position
            var position = await _paperRepository.UpsertPositionAsync(new PaperPosition
            {
                AccountId = paperAccount.Id,
                Symbol = symbol,
                Side = side,
                Quantity = quantity,
                AverageEntryPrice = entryPrice,
                CurrentPrice = entryPrice,
                UnrealizedPnl = 0m,
                StopLoss = stopLoss,
                TakeProfit = takeProfit,
                TrailingStopLoss = trailingSl,
                StopLossPct = null,
                TrailingSlPct = null,
                Status = PositionStatus.OPEN,
                TradeType = TradeType.Auto,
                RealizedPnl = 0m
            });

            // Record Trade History
            await _paperRepository.RecordTradeHistoryAsync(new PaperTradeHistory
            {
                AccountId = paperAccount.Id,
                OrderId = order.Id,
                Symbol = symbol,
                Side = side,
                Quantity = quantity,
                EntryPrice = entryPrice,
                ExecutedPrice = entryPrice,
                RealizedPnl = 0m,
                TradeType = TradeType.Auto,
                IsExit = false,
                Remarks = $"Auto {label} Executed @ ₹{entryPrice:F2}"
            });

            // Update Account Used Margin
            decimal requiredMargin = quantity * entryPrice;
            decimal newUsedMargin = paperAccount.UsedMargin + requiredMargin;
            await _paperRepository.UpdateAccountBalanceAndMarginAsync(paperAccount.Id, paperAccount.CurrentBalance, newUsedMargin, paperAccount.RealizedPnl);

            // Invalidate today count cache
            string todayKey = $"autotrade:today_count:{userId}:{DateTime.UtcNow:yyyyMMdd}";
            await _cacheService.RemoveAsync(todayKey);

            // Log Audit Event
            string squareOffNote = isShort ? $" - auto square-off at {settings.ShortSquareOffTime} IST if not covered" : string.Empty;
            await LogAuditAsync(symbol, isShort ? "AUTO_SHORT" : "AUTO_BUY", entryPrice, quantity,
                $"Auto {label} Executed @ ₹{entryPrice:F2} (Qty: {quantity} - {sizing.Reason}; Met {metConditionsCount}/11 criteria, Target: ₹{takeProfit:F2}, SL: ₹{stopLoss:F2}, TSL: {tslText}){squareOffNote}", userId);

            // Broadcast SignalR Toast Alert
            if (_hubContext != null)
            {
                await _hubContext.Clients.All.SendAsync("ReceiveAutoTradeAlert", new
                {
                    symbol,
                    side = isShort ? "SELL" : "BUY",
                    quantity,
                    price = entryPrice,
                    target = takeProfit,
                    stopLoss,
                    trailingSl,
                    message = $"🤖 Auto {label}: {quantity} shares of {symbol} @ ₹{entryPrice:N2} (Met {metConditionsCount}/11)"
                });
            }

            await BroadcastDashboardUpdateAsync(userId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to execute auto {Label} order for {Symbol}", label, symbol);
            await LogAuditAsync(symbol, "SYSTEM_ERROR", entryPrice, quantity, $"Auto {label} execution failed: {ex.Message}", userId);
            return false;
        }
    }

    public async Task<bool> EvaluateAndExecuteAutoSellAsync(PaperPosition position, decimal currentLtp, string userId = "default_user")
    {
        if (position == null || position.Status != PositionStatus.OPEN || position.TradeType != TradeType.Auto)
            return false;

        if (!await _marketHoursService.IsWithinMarketHoursAsync())
        {
            return false;
        }

        // An open short is exited by buying it back - its own (intraday) exit policy, see EvaluateAndExecuteAutoCoverAsync.
        if (position.Side == TradeSide.SELL)
        {
            return await EvaluateAndExecuteAutoCoverAsync(position, currentLtp, userId);
        }

        var settings = await GetSettingsAsync(userId);
        var nowIst = SwingTradeRules.NowIst();
        if (!SwingTradeRules.IsWithinTradingWindow(settings.TradingWindowStart, settings.TradingWindowEnd, nowIst))
        {
            return false;
        }

        var paperAccount = await _paperRepository.GetAccountAsync(userId);
        if (paperAccount == null) return false;

        // Same exit policy as Auto Real Trading - Max Days Hold counted in NSE trading days.
        int tradingDaysOpen = settings.MaxDurationDays > 0
            ? await _marketHoursService.CountTradingDaysElapsedAsync(position.OpenedAt, DateTime.UtcNow)
            : 0;
        var tradeParams = SwingTradeParams.From(settings);
        var decision = SwingTradeRules.EvaluateExit(ExitPositionView.From(position), currentLtp, nowIst,
            tradingDaysOpen, settings.MaxDurationDays, tradeParams);

        if (!decision.ShouldExit)
        {
            if (decision.NewTrailingStopLoss.HasValue)
            {
                bool activated = !position.TrailingStopLoss.HasValue || position.TrailingStopLoss.Value < position.AverageEntryPrice;
                await _paperRepository.UpdateTrailingStopLossAsync(position.Id, decision.NewTrailingStopLoss.Value);
                position.TrailingStopLoss = decision.NewTrailingStopLoss.Value;

                if (activated && tradeParams.IsSwingClose)
                {
                    await LogAuditAsync(position.Symbol, "TRAILING_SL_ACTIVATED", currentLtp, position.Quantity,
                        $"Trailing SL activated @ ₹{decision.NewTrailingStopLoss.Value:F2} (entry ₹{position.AverageEntryPrice:F2}) - now checked on closing basis", userId);
                }
            }

            return false;
        }

        string exitReason = decision.Reason;

        try
        {
            decimal realizedPnl = (currentLtp - position.AverageEntryPrice) * position.Quantity;

            // Close Position
            bool closedSuccessfully = await _paperRepository.ClosePositionAsync(position.Id, currentLtp, realizedPnl, exitReason);
            if (!closedSuccessfully)
            {
                _logger.LogWarning("AutoTradeService: Position {PositionId} was already closed. Skipping duplicate history entry.", position.Id);
                return false;
            }

            // Record Trade History
            await _paperRepository.RecordTradeHistoryAsync(new PaperTradeHistory
            {
                AccountId = paperAccount.Id,
                OrderId = 0,
                Symbol = position.Symbol,
                Side = TradeSide.SELL,
                Quantity = position.Quantity,
                EntryPrice = position.AverageEntryPrice,
                ExecutedPrice = currentLtp,
                RealizedPnl = realizedPnl,
                TradeType = TradeType.Auto,
                ExitReason = exitReason,
                IsExit = true,
                Remarks = $"Auto SELL ({exitReason}) @ ₹{currentLtp:F2}"
            });

            // Update Balance & Margin
            decimal releasedMargin = position.Quantity * position.AverageEntryPrice;
            decimal updatedBalance = paperAccount.CurrentBalance + realizedPnl;
            decimal updatedUsedMargin = Math.Max(0m, paperAccount.UsedMargin - releasedMargin);
            decimal updatedRealizedPnl = paperAccount.RealizedPnl + realizedPnl;

            await _paperRepository.UpdateAccountBalanceAndMarginAsync(paperAccount.Id, updatedBalance, updatedUsedMargin, updatedRealizedPnl);

            // Log Audit Event
            await LogAuditAsync(position.Symbol, "AUTO_SELL", currentLtp, position.Quantity,
                $"Auto SELL Executed ({exitReason}) @ ₹{currentLtp:F2} | Realized P&L: ₹{realizedPnl:N2}", userId);

            // SignalR Toast Alert
            if (_hubContext != null)
            {
                await _hubContext.Clients.All.SendAsync("ReceiveAutoTradeAlert", new
                {
                    symbol = position.Symbol,
                    side = "SELL",
                    quantity = position.Quantity,
                    price = currentLtp,
                    reason = exitReason,
                    realizedPnl,
                    message = $"🎯 Auto SELL ({exitReason}): {position.Symbol} @ ₹{currentLtp:N2} | P&L: ₹{realizedPnl:N2}"
                });
            }

            await BroadcastDashboardUpdateAsync(userId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to execute auto sell for position #{PositionId} on {Symbol}", position.Id, position.Symbol);
            return false;
        }
    }

    // Exit for an open Auto SHORT (paper): SwingTradeRules.EvaluateShortExit - auto square-off, then live target / stop /
    // trailing SL. Not gated by the user's trading window (only market hours): a short must always be closable today.
    private async Task<bool> EvaluateAndExecuteAutoCoverAsync(PaperPosition position, decimal currentLtp, string userId)
    {
        var settings = await GetSettingsAsync(userId);
        var nowIst = SwingTradeRules.NowIst();
        var tradeParams = SwingTradeParams.From(settings);
        var decision = SwingTradeRules.EvaluateShortExit(ExitPositionView.From(position), currentLtp, nowIst,
            settings.ShortSquareOffTime, tradeParams);

        if (!decision.ShouldExit)
        {
            if (decision.NewTrailingStopLoss.HasValue)
            {
                bool activated = !position.TrailingStopLoss.HasValue || position.TrailingStopLoss.Value > position.AverageEntryPrice;
                await _paperRepository.UpdateTrailingStopLossAsync(position.Id, decision.NewTrailingStopLoss.Value);
                position.TrailingStopLoss = decision.NewTrailingStopLoss.Value;

                if (activated && tradeParams.IsSwingClose)
                {
                    await LogAuditAsync(position.Symbol, "TRAILING_SL_ACTIVATED", currentLtp, position.Quantity,
                        $"Short trailing SL activated @ ₹{decision.NewTrailingStopLoss.Value:F2} (entry ₹{position.AverageEntryPrice:F2})", userId);
                }
            }
            return false;
        }

        var paperAccount = await _paperRepository.GetAccountAsync(userId);
        if (paperAccount == null) return false;

        string exitReason = decision.Reason;
        try
        {
            // A short gains when the price falls.
            decimal realizedPnl = (position.AverageEntryPrice - currentLtp) * position.Quantity;

            bool closedSuccessfully = await _paperRepository.ClosePositionAsync(position.Id, currentLtp, realizedPnl, exitReason);
            if (!closedSuccessfully)
            {
                _logger.LogWarning("AutoTradeService: Short position {PositionId} was already closed. Skipping duplicate history entry.", position.Id);
                return false;
            }

            await _paperRepository.RecordTradeHistoryAsync(new PaperTradeHistory
            {
                AccountId = paperAccount.Id,
                OrderId = 0,
                Symbol = position.Symbol,
                Side = TradeSide.BUY,
                Quantity = position.Quantity,
                EntryPrice = position.AverageEntryPrice,
                ExecutedPrice = currentLtp,
                RealizedPnl = realizedPnl,
                TradeType = TradeType.Auto,
                ExitReason = exitReason,
                IsExit = true,
                Remarks = $"Auto BUY TO COVER ({exitReason}) @ ₹{currentLtp:F2}"
            });

            decimal releasedMargin = position.Quantity * position.AverageEntryPrice;
            await _paperRepository.UpdateAccountBalanceAndMarginAsync(paperAccount.Id,
                paperAccount.CurrentBalance + realizedPnl,
                Math.Max(0m, paperAccount.UsedMargin - releasedMargin),
                paperAccount.RealizedPnl + realizedPnl);

            string actionType = exitReason == SwingTradeRules.ShortSquareOffReason ? "AUTO_SQUARE_OFF" : "AUTO_COVER";
            await LogAuditAsync(position.Symbol, actionType, currentLtp, position.Quantity,
                $"Auto BUY TO COVER Executed ({exitReason}) @ ₹{currentLtp:F2} | Realized P&L: ₹{realizedPnl:N2}", userId);

            if (_hubContext != null)
            {
                await _hubContext.Clients.All.SendAsync("ReceiveAutoTradeAlert", new
                {
                    symbol = position.Symbol,
                    side = "BUY",
                    quantity = position.Quantity,
                    price = currentLtp,
                    reason = exitReason,
                    realizedPnl,
                    message = $"🎯 Auto COVER ({exitReason}): {position.Symbol} @ ₹{currentLtp:N2} | P&L: ₹{realizedPnl:N2}"
                });
            }

            await BroadcastDashboardUpdateAsync(userId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to cover auto short position #{PositionId} on {Symbol}", position.Id, position.Symbol);
            return false;
        }
    }

    public async Task ResetAutoPaperTradingAsync(string userId = "default_user")
    {
        var settings = await GetSettingsAsync(userId);
        var paperAccount = await _paperRepository.GetAccountAsync(userId);
        decimal initialBalance = settings.AvailableCapital > 0 ? settings.AvailableCapital : 100000m;

        if (paperAccount == null)
        {
            paperAccount = await _paperRepository.CreateAccountAsync(userId, "Virtual Trading Account", initialBalance);
        }
        else
        {
            await _paperRepository.ResetAccountAsync(paperAccount.Id, initialBalance);
        }

        // Clear execution logs
        await _repository.ClearLogsAsync(userId);

        // Clear memory/distributed cache
        string todayKey = $"autotrade:today_count:{userId}:{DateTime.UtcNow:yyyyMMdd}";
        string settingsKey = $"autotrade:settings:{userId}";
        await _cacheService.RemoveAsync(todayKey);
        await _cacheService.RemoveAsync(settingsKey);

        await LogAuditAsync("SYSTEM", "RESET_PAPER_TRADING", null, null,
            "All paper trading positions, orders, trade history, and logs have been reset & cleared. Account balance reset to initial capital.", userId);

        await BroadcastDashboardUpdateAsync(userId);
    }

    // Today's realized P&L from closed Auto paper trades - the paper counterpart of
    // IRealTradingRepository.GetTodayRealizedPnlAsync used by the daily loss circuit breaker.
    private async Task<decimal> GetTodayAutoRealizedPnlAsync(int accountId)
    {
        DateTime todayStartIst = SwingTradeRules.NowIst().Date;
        DateTime todayStartUtc = TimeZoneInfo.ConvertTimeToUtc(todayStartIst, TimeZoneHelper.IndianTimeZone);
        var (items, _) = await _paperRepository.GetTradeHistoryPagedAsync(accountId, new PaperTradeHistoryFilterDto
        {
            Page = 1,
            PageSize = 1000,
            FromDate = todayStartUtc,
            ToDate = null
        });
        return items.Where(t => t.TradeType == TradeType.Auto).Sum(t => t.RealizedPnl);
    }

    private async Task BroadcastDashboardUpdateAsync(string userId)
    {
        try
        {
            if (_hubContext != null)
            {
                var dashboardData = await GetDashboardDataAsync(userId);
                await _hubContext.Clients.All.SendAsync("ReceiveAutoTradeDashboardUpdate", dashboardData);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to broadcast AutoTrade dashboard updates over SignalR.");
        }
    }
}
