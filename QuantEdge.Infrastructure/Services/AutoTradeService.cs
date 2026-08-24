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
    private readonly IAutoTradeRepository _repository;
    private readonly IPaperTradingRepository _paperRepository;
    private readonly IPaperTradingService _paperService;
    private readonly IIndianHolidayRepository _holidayRepository;
    private readonly IMarketHoursService _marketHoursService;
    private readonly ICacheService _cacheService;
    private readonly IHubContext<MarketDataHub>? _hubContext;
    private readonly ILogger<AutoTradeService> _logger;

    public AutoTradeService(
        IAutoTradeRepository repository,
        IPaperTradingRepository paperRepository,
        IPaperTradingService paperService,
        IIndianHolidayRepository holidayRepository,
        IMarketHoursService marketHoursService,
        ICacheService cacheService,
        ILogger<AutoTradeService> logger,
        IHubContext<MarketDataHub>? hubContext = null)
    {
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
        existing.StopLossPct = (updateDto.StopLossPct.HasValue && updateDto.StopLossPct.Value > 0) ? updateDto.StopLossPct.Value : null;
        existing.MaxDurationDays = updateDto.MaxDurationDays;
        existing.MaxTradesPerDay = updateDto.MaxTradesPerDay;
        existing.FixedAmountPerTrade = updateDto.FixedAmountPerTrade;
        existing.MinConditionsMatch = updateDto.MinConditionsMatch;
        existing.TradingWindowStart = updateDto.TradingWindowStart ?? "09:15";
        existing.TradingWindowEnd = updateDto.TradingWindowEnd ?? "15:30";

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
        decimal todayTradeAmount = todayHistory.Where(t => t.Side == TradeSide.BUY).Sum(t => t.Quantity * (t.EntryPrice > 0 ? t.EntryPrice : t.ExecutedPrice));
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

    public async Task<bool> EvaluateAndExecuteAutoBuyAsync(string symbol, decimal entryPrice, int metConditionsCount, string userId = "default_user", bool isBuySignal = false)
    {
        symbol = symbol.ToUpper().Trim();
        var settings = await GetSettingsAsync(userId);

        // 1. Master Switch Validation
        if (!settings.IsAutoTradeEnabled)
        {
            return false;
        }

        // 2. Trading Window & Market Holiday Check (Using Memory-Cached MarketHoursService)
        if (!await _marketHoursService.IsWithinMarketHoursAsync())
        {
            await LogAuditAsync(symbol, "SIGNAL_SKIPPED", entryPrice, 0, "Outside Market Hours or Holiday", userId);
            return false;
        }

        if (!IsWithinTradingWindow(settings.TradingWindowStart, settings.TradingWindowEnd))
        {
            await LogAuditAsync(symbol, "SIGNAL_SKIPPED", entryPrice, 0,
                $"Outside trading window ({settings.TradingWindowStart} - {settings.TradingWindowEnd})", userId);
            return false;
        }


        // 3. Condition Match Count Check (Executes if BUY Signal is confirmed or metConditionsCount >= user's MinConditionsMatch)
        if (!isBuySignal && metConditionsCount < settings.MinConditionsMatch)
        {
            await LogAuditAsync(symbol, "SIGNAL_SKIPPED", entryPrice, 0,
                $"Condition match score {metConditionsCount}/11 below minimum required {settings.MinConditionsMatch}/11", userId);
            return false;
        }

        // 4. Daily Trade Limit Check (Default 5)
        int todayCount = await GetTodayAutoTradeCountAsync(userId);
        if (todayCount >= settings.MaxTradesPerDay)
        {
            await LogAuditAsync(symbol, "SIGNAL_SKIPPED", entryPrice, 0,
                $"Daily limit of {settings.MaxTradesPerDay} auto trades reached ({todayCount}/{settings.MaxTradesPerDay} executed)", userId);
            return false;
        }

        // 5. Duplicate Open Position Check (Re-entry allowed ONLY if previous same-day position is closed)
        var paperAccount = await _paperRepository.GetAccountAsync(userId);
        if (paperAccount == null) return false;

        var existingOpenPos = await _paperRepository.GetOpenPositionBySymbolAsync(paperAccount.Id, symbol);
        if (existingOpenPos != null)
        {
            await LogAuditAsync(symbol, "SIGNAL_SKIPPED", entryPrice, 0,
                $"Symbol already has an OPEN auto position (Position #{existingOpenPos.Id})", userId);
            return false;
        }

        // 6. Available Capital & Position Sizing Calculation
        if (settings.AvailableCapital < settings.FixedAmountPerTrade)
        {
            await LogAuditAsync(symbol, "SIGNAL_SKIPPED", entryPrice, 0,
                $"Insufficient Available Capital (₹{settings.AvailableCapital:N2} < Fixed Amount ₹{settings.FixedAmountPerTrade:N2})", userId);
            return false;
        }

        int quantity = (int)Math.Floor(settings.FixedAmountPerTrade / entryPrice);
        if (quantity < 1)
        {
            await LogAuditAsync(symbol, "SIGNAL_SKIPPED", entryPrice, 0,
                $"Calculated quantity 0 for entry price ₹{entryPrice:N2} with fixed trade amount ₹{settings.FixedAmountPerTrade:N2}", userId);
            return false;
        }

        // 7. Calculate Target & Stop Loss
        decimal? stopLoss = (settings.StopLossPct.HasValue && settings.StopLossPct.Value > 0)
            ? Math.Round(entryPrice * (1m - Math.Abs(settings.StopLossPct.Value) / 100m), 2)
            : null;
        decimal tpPct = Math.Abs(settings.ProfitTargetPct) / 100m;
        decimal takeProfit = Math.Round(entryPrice * (1m + tpPct), 2);

        try
        {
            // Execute Auto Paper Buy Order
            var order = await _paperRepository.CreateOrderAsync(new PaperOrder
            {
                AccountId = paperAccount.Id,
                Symbol = symbol,
                OrderType = PaperOrderType.Market,
                Side = TradeSide.BUY,
                Quantity = quantity,
                Price = entryPrice,
                StopLoss = stopLoss,
                TakeProfit = takeProfit,
                Status = PaperOrderStatus.Filled,
                FilledPrice = entryPrice,
                FilledAt = DateTime.UtcNow,
                TradeType = TradeType.Auto,
                Remarks = $"Auto BUY (Score {metConditionsCount}/11)"
            });

            // Upsert Auto Paper Position
            var position = await _paperRepository.UpsertPositionAsync(new PaperPosition
            {
                AccountId = paperAccount.Id,
                Symbol = symbol,
                Side = TradeSide.BUY,
                Quantity = quantity,
                AverageEntryPrice = entryPrice,
                CurrentPrice = entryPrice,
                UnrealizedPnl = 0m,
                StopLoss = stopLoss,
                TakeProfit = takeProfit,
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
                Side = TradeSide.BUY,
                Quantity = quantity,
                EntryPrice = entryPrice,
                ExecutedPrice = entryPrice,
                RealizedPnl = 0m,
                TradeType = TradeType.Auto,
                Remarks = $"Auto BUY Executed @ ₹{entryPrice:F2}"
            });

            // Update Account Used Margin
            decimal requiredMargin = quantity * entryPrice;
            decimal newUsedMargin = paperAccount.UsedMargin + requiredMargin;
            await _paperRepository.UpdateAccountBalanceAndMarginAsync(paperAccount.Id, paperAccount.CurrentBalance, newUsedMargin, paperAccount.RealizedPnl);

            // Invalidate today count cache
            string todayKey = $"autotrade:today_count:{userId}:{DateTime.UtcNow:yyyyMMdd}";
            await _cacheService.RemoveAsync(todayKey);

            // Log Audit Event
            string slLog = stopLoss.HasValue ? $"₹{stopLoss.Value:F2}" : "None";
            await LogAuditAsync(symbol, "AUTO_BUY", entryPrice, quantity,
                $"Auto BUY Executed @ ₹{entryPrice:F2} (Qty: {quantity}, Met {metConditionsCount}/11 criteria, Target: ₹{takeProfit:F2}, SL: {slLog})", userId);

            // Broadcast SignalR Toast Alert
            if (_hubContext != null)
            {
                await _hubContext.Clients.All.SendAsync("ReceiveAutoTradeAlert", new
                {
                    symbol,
                    side = "BUY",
                    quantity,
                    price = entryPrice,
                    target = takeProfit,
                    stopLoss,
                    message = $"🤖 Auto BUY: {quantity} shares of {symbol} @ ₹{entryPrice:N2} (Met {metConditionsCount}/11)"
                });
            }

            await BroadcastDashboardUpdateAsync(userId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to execute auto buy order for {Symbol}", symbol);
            await LogAuditAsync(symbol, "SYSTEM_ERROR", entryPrice, quantity, $"Auto BUY execution failed: {ex.Message}", userId);
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

        var settings = await GetSettingsAsync(userId);
        if (!IsWithinTradingWindow(settings.TradingWindowStart, settings.TradingWindowEnd))
        {
            return false;
        }

        var paperAccount = await _paperRepository.GetAccountAsync(userId);
        if (paperAccount == null) return false;

        string exitReason = string.Empty;
        bool shouldExit = false;

        // 1. Target Hit Check
        if (position.TakeProfit.HasValue && currentLtp >= position.TakeProfit.Value)
        {
            shouldExit = true;
            exitReason = "Target Hit";
        }
        // 2. Stop Loss Hit Check (Only evaluate if Stop Loss is enabled in Settings AND position has Stop Loss)
        else if (settings.StopLossPct.HasValue && settings.StopLossPct.Value > 0 && position.StopLoss.HasValue && position.StopLoss.Value > 0 && currentLtp <= position.StopLoss.Value)
        {
            shouldExit = true;
            exitReason = "Stop Loss Hit";
        }
        // 3. Max Duration Check (20 Trading Days default)
        else
        {
            int daysOpen = (DateTime.UtcNow - position.OpenedAt).Days;
            if (daysOpen >= settings.MaxDurationDays)
            {
                shouldExit = true;
                exitReason = "Max Duration Exit";
            }
        }

        if (!shouldExit) return false;

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

    private bool IsWithinTradingWindow(string startStr, string endStr)
    {
        try
        {
            // IST is UTC + 05:30
            var istNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneHelper.IndianTimeZone);
            if (istNow.DayOfWeek == DayOfWeek.Saturday || istNow.DayOfWeek == DayOfWeek.Sunday)
            {
                return false;
            }

            TimeSpan nowTime = istNow.TimeOfDay;
            TimeSpan start = TimeSpan.TryParse(startStr, out var s) ? s : new TimeSpan(9, 15, 0);
            TimeSpan end = TimeSpan.TryParse(endStr, out var e) ? e : new TimeSpan(15, 30, 0);

            return nowTime >= start && nowTime <= end;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error validating trading window in AutoTradeService.");
            return false; // Fail safe CLOSED to prevent off-hours order placement
        }
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
