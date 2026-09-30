using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using QuantEdge.Domain.Entities;
using QuantEdge.Infrastructure.DTOs;
using QuantEdge.Infrastructure.Helpers;
using QuantEdge.Infrastructure.Hubs;
using QuantEdge.Infrastructure.Interfaces;
using QuantEdge.Infrastructure.Persistence.Repositories;

namespace QuantEdge.Infrastructure.Services;

/// <summary>
/// Manual Paper Trading - mirrors AutoRealTradeService's Manual Real Trade path (entry gates in the same
/// order, user Quantity, mandatory SL%/Trailing SL%, SwingTradeRules levels) against the virtual paper
/// account; positions are sold manually only (Close button). Fully separate from AutoTradeService: its own settings
/// (manual_paper_trade_settings), execution logs (manual_paper_trade_execution_logs), master switch and
/// limits, which only count Manual paper trades. Orders/positions/history use the shared paper_* tables.
/// </summary>
public class ManualPaperTradeService : IManualPaperTradeService
{
    private readonly IManualPaperTradeRepository _repository;
    private readonly IPaperTradingRepository _paperRepository;
    private readonly IPaperTradingService _paperService;
    private readonly IMarketHoursService _marketHoursService;
    private readonly PaperMatchingEngine? _matchingEngine;
    private readonly IHubContext<MarketDataHub>? _hubContext;
    private readonly ILogger<ManualPaperTradeService> _logger;

    // Manual settings/logs are keyed by the numeric app user id (like the real_* tables); the shared
    // paper account and its orders/positions/history are still keyed by this text id (paper_accounts).
    private const string PaperAccountUserId = "default_user";

    // Per-(user, symbol) locks so a double-click can't race two requests past the duplicate-position
    // check - same guard as the Manual Real Trade path.
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> _manualBuyLocks = new();

    public ManualPaperTradeService(
        IManualPaperTradeRepository repository,
        IPaperTradingRepository paperRepository,
        IPaperTradingService paperService,
        IMarketHoursService marketHoursService,
        ILogger<ManualPaperTradeService> logger,
        PaperMatchingEngine? matchingEngine = null,
        IHubContext<MarketDataHub>? hubContext = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _paperRepository = paperRepository ?? throw new ArgumentNullException(nameof(paperRepository));
        _paperService = paperService ?? throw new ArgumentNullException(nameof(paperService));
        _marketHoursService = marketHoursService ?? throw new ArgumentNullException(nameof(marketHoursService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _matchingEngine = matchingEngine;
        _hubContext = hubContext;
    }

    /// <summary>
    /// A manual paper position opened through this service: Manual, long, and carrying its own trade-wise
    /// Trailing SL % (legacy manual paper orders never set it). Such positions are sold only by the user
    /// (<see cref="ClosePositionAsync"/>) - never automatically, not even by the matching engine's SL/TP ticks.
    /// </summary>
    public static bool IsManagedPosition(PaperPosition position) =>
        position.TradeType == TradeType.Manual && position.Side == TradeSide.BUY && position.TrailingSlPct.HasValue;

    // ---------------------------------------------------------------------------------------------
    // Settings & Logs
    // ---------------------------------------------------------------------------------------------

    public Task<ManualPaperTradeSettings> GetSettingsAsync(int userId = 1) =>
        _repository.GetSettingsAsync(userId);

    public async Task<ManualPaperTradeSettings> UpdateSettingsAsync(ManualPaperTradeSettingsUpdateDto updateDto, int userId = 1)
    {
        var existing = await _repository.GetSettingsAsync(userId);
        existing.IsManualTradeEnabled = updateDto.IsManualTradeEnabled;
        existing.AvailableCapital = updateDto.AvailableCapital;
        existing.ProfitTargetPct = Math.Abs(updateDto.ProfitTargetPct);
        existing.StopLossPct = Math.Abs(updateDto.StopLossPct);
        existing.TrailingSlPct = Math.Abs(updateDto.TrailingSlPct);
        existing.MaxDurationDays = updateDto.MaxDurationDays;
        existing.MaxTradesPerDay = updateDto.MaxTradesPerDay;
        existing.FixedAmountPerTrade = updateDto.FixedAmountPerTrade;
        existing.TradingWindowStart = updateDto.TradingWindowStart;
        existing.TradingWindowEnd = updateDto.TradingWindowEnd;
        existing.EntryDelayMinutes = updateDto.EntryDelayMinutes;
        existing.MaxDailyLossLimit = updateDto.MaxDailyLossLimit;
        existing.ExitMode = string.IsNullOrWhiteSpace(updateDto.ExitMode) ? SwingTradeRules.ExitModeSwingClose : updateDto.ExitMode.ToUpperInvariant();
        existing.CloseCheckTime = updateDto.CloseCheckTime;
        existing.StopLossAtrMult = updateDto.StopLossAtrMult;
        existing.TrailAtrMult = updateDto.TrailAtrMult;
        existing.TargetAtrMult = updateDto.TargetAtrMult;

        var updated = await _repository.UpsertSettingsAsync(existing);
        await LogAuditAsync("SYSTEM", "SETTINGS_UPDATED", null, null, "Manual Paper Trading settings updated", userId);
        return updated;
    }

    public async Task ToggleManualTradeAsync(bool enabled, int userId = 1)
    {
        await _repository.GetSettingsAsync(userId); // ensures the settings row exists
        await _repository.ToggleManualTradeAsync(userId, enabled);
        await LogAuditAsync("SYSTEM", "SYSTEM_ALERT", null, null,
            enabled ? "Manual Paper Trading switch turned ON" : "Manual Paper Trading switch turned OFF", userId);
    }

    public Task<int> GetTodayTradeCountAsync(int userId = 1) =>
        _repository.GetTodayManualTradeCountAsync(userId);

    public Task<IEnumerable<ManualPaperTradeExecutionLog>> GetTodayLogsAsync(int userId = 1, int limit = 50) =>
        _repository.GetTodayLogsAsync(userId, limit);

    public async Task<ManualPaperDashboardDto> GetDashboardAsync(int userId = 1)
    {
        var settings = await _repository.GetSettingsAsync(userId);
        var paperAccount = await _paperRepository.GetAccountAsync(PaperAccountUserId);

        var dashboard = new ManualPaperDashboardDto();
        if (paperAccount != null)
        {
            DateTime todayStartUtc = TimeZoneInfo.ConvertTimeToUtc(SwingTradeRules.NowIst().Date, TimeZoneHelper.IndianTimeZone);
            dashboard = await _repository.GetDashboardAsync(paperAccount.Id, todayStartUtc);

            // Unrealized P&L at the latest tick (the stored value only refreshes when the engine writes it),
            // same approach as PaperTradingService.GetPortfolioAsync.
            var positions = await GetOpenPositionsAsync();
            dashboard.ManualUnrealizedPnl = positions.Sum(p => p.UnrealizedPnl);
        }

        dashboard.ManualCapital = settings.AvailableCapital;
        dashboard.IsManualTradeEnabled = settings.IsManualTradeEnabled;
        dashboard.MaxTradesPerDay = settings.MaxTradesPerDay;
        return dashboard;
    }

    public async Task<IEnumerable<PaperPosition>> GetOpenPositionsAsync()
    {
        var paperAccount = await _paperRepository.GetAccountAsync(PaperAccountUserId);
        if (paperAccount == null) return Enumerable.Empty<PaperPosition>();

        var positions = (await _repository.GetOpenPositionsAsync(paperAccount.Id)).ToList();
        foreach (var pos in positions)
        {
            decimal ltp = _matchingEngine?.GetLtp(pos.Symbol) ?? 0m;
            if (ltp > 0m)
            {
                pos.CurrentPrice = ltp;
                pos.UnrealizedPnl = pos.Side == TradeSide.BUY
                    ? (ltp - pos.AverageEntryPrice) * pos.Quantity
                    : (pos.AverageEntryPrice - ltp) * pos.Quantity;
            }
        }
        return positions;
    }

    public async Task<PagedResultDto<PaperOrder>> GetOrdersPagedAsync(ManualPaperOrderFilterDto filter)
    {
        var paperAccount = await _paperRepository.GetAccountAsync(PaperAccountUserId);
        if (paperAccount == null)
        {
            return new PagedResultDto<PaperOrder> { Page = filter.Page, PageSize = filter.PageSize };
        }

        var (items, totalCount) = await _repository.GetOrdersPagedAsync(paperAccount.Id, filter);
        return new PagedResultDto<PaperOrder> { Items = items, TotalCount = totalCount, Page = filter.Page, PageSize = filter.PageSize };
    }

    public async Task<PagedResultDto<PaperTradeHistory>> GetTradeHistoryPagedAsync(PaperTradeHistoryFilterDto filter)
    {
        var paperAccount = await _paperRepository.GetAccountAsync(PaperAccountUserId);
        if (paperAccount == null)
        {
            return new PagedResultDto<PaperTradeHistory> { Page = filter.Page, PageSize = filter.PageSize };
        }

        var (items, totalCount) = await _repository.GetTradeHistoryPagedAsync(paperAccount.Id, filter);
        return new PagedResultDto<PaperTradeHistory> { Items = items, TotalCount = totalCount, Page = filter.Page, PageSize = filter.PageSize };
    }

    private async Task LogAuditAsync(string symbol, string actionType, decimal? price, int? quantity, string? reason, int userId)
    {
        var log = new ManualPaperTradeExecutionLog
        {
            UserId = userId,
            Symbol = symbol.ToUpper().Trim(),
            ActionType = actionType,
            Price = price,
            Quantity = quantity,
            Reason = reason,
            ExecutedAt = DateTime.UtcNow
        };

        try
        {
            await _repository.LogExecutionAsync(log);
            if (_hubContext != null)
            {
                await _hubContext.Clients.All.SendAsync("ReceiveManualPaperTradeLogEvent", log);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write Manual Paper Trade log {ActionType} for {Symbol}", actionType, symbol);
        }
    }

    // Logs a skipped/rejected manual BUY and returns the same reason to the caller.
    private async Task<(bool Success, string Message)> RejectAsync(string symbol, decimal price, string reason, int userId,
        string actionType = "TRADE_SKIPPED")
    {
        await LogAuditAsync(symbol, actionType, price, 0, reason, userId);
        return (false, reason);
    }

    // ---------------------------------------------------------------------------------------------
    // Manual BUY
    // ---------------------------------------------------------------------------------------------

    public async Task<(bool Success, string Message)> ExecuteManualBuyAsync(string symbol, decimal entryPrice, int quantity,
        decimal stopLossPct, decimal trailingSlPct, int userId = 1)
    {
        symbol = symbol.ToUpper().Trim();
        string lockKey = $"{userId}:{symbol}";
        var manualLock = _manualBuyLocks.GetOrAdd(lockKey, _ => new SemaphoreSlim(1, 1));
        if (!await manualLock.WaitAsync(TimeSpan.Zero))
        {
            return (false, $"A previous Manual Paper Trade request for {symbol} is still in progress.");
        }

        try
        {
            return await ExecuteManualBuyCoreAsync(symbol, entryPrice, quantity, stopLossPct, trailingSlPct, userId);
        }
        finally
        {
            manualLock.Release();
        }
    }

    private async Task<(bool Success, string Message)> ExecuteManualBuyCoreAsync(string symbol, decimal entryPrice, int quantity,
        decimal stopLossPct, decimal trailingSlPct, int userId)
    {
        var settings = await _repository.GetSettingsAsync(userId);
        var nowIst = SwingTradeRules.NowIst();

        // Gates follow AutoRealTradeService.EvaluateAndExecuteRealBuyCoreAsync's manual path, in order.
        // Broker-only steps (token check, pending broker order) have no paper equivalent.

        // 1. Master Switch Validation (Manual Paper Trading's own switch)
        if (!settings.IsManualTradeEnabled)
        {
            return (false, "Manual Paper Trade rejected: the Manual Paper Trading switch is OFF.");
        }

        // 2. Market Hours & Trading Window Check
        if (!await _marketHoursService.IsWithinMarketHoursAsync())
        {
            return await RejectAsync(symbol, entryPrice, "Outside Market Hours or Holiday", userId);
        }

        if (!SwingTradeRules.IsWithinTradingWindow(settings.TradingWindowStart, settings.TradingWindowEnd, nowIst))
        {
            return await RejectAsync(symbol, entryPrice,
                $"Outside trading window ({settings.TradingWindowStart} - {settings.TradingWindowEnd})", userId);
        }

        // 2b. Opening Entry Delay - entries only, exits are never delayed.
        if (!SwingTradeRules.IsPastEntryDelay(settings.TradingWindowStart, settings.EntryDelayMinutes, nowIst))
        {
            return await RejectAsync(symbol, entryPrice,
                $"Opening entry delay active - new BUYs held back for {settings.EntryDelayMinutes} min after {settings.TradingWindowStart}", userId);
        }

        // 3. Daily Trade Limit Check (manual paper BUYs only)
        int todayCount = await _repository.GetTodayManualTradeCountAsync(userId);
        if (todayCount >= settings.MaxTradesPerDay)
        {
            return await RejectAsync(symbol, entryPrice,
                $"Daily limit of {settings.MaxTradesPerDay} manual trades reached ({todayCount}/{settings.MaxTradesPerDay})", userId);
        }

        var paperAccount = await _paperRepository.GetAccountAsync(PaperAccountUserId)
            ?? await _paperRepository.CreateAccountAsync(PaperAccountUserId, "Virtual Trading Account", 100000m);

        // 4. Daily Loss Circuit Breaker - manual paper positions and today's manual realized P&L. Like
        // Real, a breach switches Manual Paper Trading OFF (its own switch - Auto Paper is untouched).
        decimal effectiveDailyLossLimit = SwingTradeRules.EffectiveDailyLossLimit(settings.MaxDailyLossLimit, settings.AvailableCapital);
        var openManualPositions = (await _paperService.GetOpenPositionsAsync(PaperAccountUserId))
            .Where(p => p.TradeType == TradeType.Manual)
            .ToList();
        decimal todayRealizedPnl = (await GetTodayPaperHistoryAsync(paperAccount.Id))
            .Where(t => t.TradeType == TradeType.Manual)
            .Sum(t => t.RealizedPnl);
        decimal totalLoss = todayRealizedPnl + openManualPositions.Sum(p => p.UnrealizedPnl);

        if (totalLoss <= -effectiveDailyLossLimit)
        {
            await ToggleManualTradeAsync(false, userId);
            return await RejectAsync(symbol, entryPrice,
                $"Daily loss limit ₹{effectiveDailyLossLimit:N2} breached (Total Loss: ₹{totalLoss:N2}). Manual Paper Trading switched OFF.",
                userId, "CIRCUIT_BREAKER");
        }

        // 4b. Portfolio-Level Exposure Cap (manual paper positions)
        if (openManualPositions.Count >= SwingTradeRules.MaxConcurrentPositions)
        {
            return await RejectAsync(symbol, entryPrice,
                $"Portfolio exposure cap reached ({openManualPositions.Count}/{SwingTradeRules.MaxConcurrentPositions} concurrent open positions)", userId);
        }

        // 5. Duplicate Open Position Check - the paper account holds one position per symbol.
        var existingOpenPos = await _paperRepository.GetOpenPositionBySymbolAsync(paperAccount.Id, symbol);
        if (existingOpenPos != null)
        {
            return await RejectAsync(symbol, entryPrice,
                $"{symbol} already has an OPEN paper position (Position #{existingOpenPos.Id})", userId);
        }

        // 6. Live Quote Re-check against the latest tick, same drift guard as Real. If no tick has
        // arrived yet, proceed with the submitted price (Real does the same when its quote fetch fails).
        decimal liveLtp = _matchingEngine?.GetLtp(symbol) ?? 0m;
        if (liveLtp > 0m)
        {
            string? driftReason = SwingTradeRules.CheckSignalDrift(entryPrice, liveLtp);
            if (driftReason != null)
            {
                return await RejectAsync(symbol, entryPrice, driftReason, userId);
            }
            entryPrice = liveLtp;
        }

        // 7. Quantity, capital, and mandatory trade-wise SL%/Trailing SL%
        if (quantity < 1)
        {
            return await RejectAsync(symbol, entryPrice, "Manual trade rejected: Quantity must be a positive whole number.", userId);
        }

        // Capital check against Manual Trading's own cash (Manual Capital + manual realized P&L - money in
        // open manual positions), not the shared paper account the Auto Paper bot also draws on.
        decimal requiredCapital = quantity * entryPrice;
        decimal manualAvailableMargin = await GetManualAvailableMarginAsync(paperAccount.Id, settings);
        if (manualAvailableMargin < requiredCapital)
        {
            return await RejectAsync(symbol, entryPrice,
                $"Insufficient Manual Capital for requested quantity (₹{manualAvailableMargin:N2} < ₹{requiredCapital:N2} for {quantity} @ ₹{entryPrice:N2})", userId);
        }

        if (stopLossPct <= 0)
        {
            return await RejectAsync(symbol, entryPrice, "Manual trade rejected: Stop Loss % must be greater than zero.", userId);
        }
        if (trailingSlPct <= 0)
        {
            return await RejectAsync(symbol, entryPrice, "Manual trade rejected: Trailing Stop Loss % must be greater than zero.", userId);
        }

        // 8. Target, Stop Loss & initial Trailing SL - shared SwingTradeRules levels; the trade's own
        // %s override the ATR/engine levels, exactly as for Manual Real Trade.
        decimal effectiveStopLossPct = Math.Abs(stopLossPct);
        decimal effectiveTrailingSlPct = Math.Abs(trailingSlPct);
        var levels = SwingTradeRules.ComputeEntryLevels(entryPrice, null, null, null,
            effectiveStopLossPct, effectiveTrailingSlPct, SwingTradeParams.From(settings));
        decimal takeProfit = levels.TakeProfit;
        decimal stopLoss = levels.StopLoss;
        decimal? trailingSl = levels.TrailingStopLoss;
        string tslText = trailingSl.HasValue ? $"₹{trailingSl.Value:F2}" : "activates after +1 ATR";

        try
        {
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
                TradeType = TradeType.Manual,
                Remarks = $"Manual Paper BUY (SL {effectiveStopLossPct:F2}% / TSL {effectiveTrailingSlPct:F2}%, SL ₹{stopLoss:F2} / Target ₹{takeProfit:F2})"
            });

            await _paperRepository.UpsertPositionAsync(new PaperPosition
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
                TrailingStopLoss = trailingSl,
                StopLossPct = effectiveStopLossPct,
                TrailingSlPct = effectiveTrailingSlPct,
                Status = PositionStatus.OPEN,
                TradeType = TradeType.Manual,
                RealizedPnl = 0m
            });

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
                TradeType = TradeType.Manual,
                Remarks = $"Manual Paper BUY Executed @ ₹{entryPrice:F2}"
            });

            // No paper_accounts balance/margin update: manual cash is derived from the manual rows
            // (GetManualAvailableMarginAsync), so manual trades never consume the Auto Paper bot's cash.
            _matchingEngine?.InvalidateCache();

            string message = $"Manual Paper BUY executed @ ₹{entryPrice:F2} (Qty: {quantity}, Target: ₹{takeProfit:F2}, SL: ₹{stopLoss:F2}, TSL: {tslText})";
            await LogAuditAsync(symbol, "MANUAL_BUY", entryPrice, quantity, message, userId);
            await BroadcastAlertAsync(symbol, "BUY", quantity, entryPrice, $"{symbol}: {message}");
            return (true, $"{symbol}: {message}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to execute Manual Paper BUY for {Symbol} (User {UserId})", symbol, userId);
            return await RejectAsync(symbol, entryPrice, $"Manual Paper BUY execution failed: {ex.Message}", userId, "SYSTEM_ERROR");
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Manual SELL - only by the user (Close button). There is no background exit job: Stop Loss,
    // Trailing SL % and Target are stored on the position for reference but never sell it automatically.
    // ---------------------------------------------------------------------------------------------

    public async Task<(bool Success, string Message)> ClosePositionAsync(int positionId, int userId = 1)
    {
        var paperAccount = await _paperRepository.GetAccountAsync(PaperAccountUserId);
        if (paperAccount == null) return (false, "Paper account not found.");

        var position = (await GetOpenPositionsAsync()).FirstOrDefault(p => p.Id == positionId);
        if (position == null) return (false, $"Open manual position #{positionId} not found.");

        // Legacy manual positions (placed before Manual Trading had its own cash) were funded from the
        // shared paper account, so they are closed the old way, which releases that account's margin.
        if (!IsManagedPosition(position))
        {
            await _paperService.ClosePositionAsync(positionId, 0m, PaperAccountUserId);
            return (true, $"{position.Symbol}: position closed.");
        }

        // GetOpenPositionsAsync already set CurrentPrice to the latest tick where available.
        decimal exitPrice = position.CurrentPrice > 0m ? position.CurrentPrice : position.AverageEntryPrice;
        bool closed = await ExecuteManualSellAsync(position, exitPrice, "Manual Close", paperAccount.Id, userId);
        return closed
            ? (true, $"{position.Symbol}: position closed @ ₹{exitPrice:F2}.")
            : (false, $"{position.Symbol}: position could not be closed (it may already be closed).");
    }

    // Closes a manual position, records the SELL and logs it (Close button).
    private async Task<bool> ExecuteManualSellAsync(PaperPosition position, decimal currentLtp, string exitReason, int accountId, int userId)
    {
        try
        {
            decimal realizedPnl = (currentLtp - position.AverageEntryPrice) * position.Quantity;

            bool closedSuccessfully = await _paperRepository.ClosePositionAsync(position.Id, currentLtp, realizedPnl, exitReason);
            if (!closedSuccessfully)
            {
                _logger.LogWarning("ManualPaperTradeService: Position {PositionId} was already closed. Skipping duplicate history entry.", position.Id);
                return false;
            }

            await _paperRepository.RecordTradeHistoryAsync(new PaperTradeHistory
            {
                AccountId = accountId,
                OrderId = 0,
                Symbol = position.Symbol,
                Side = TradeSide.SELL,
                Quantity = position.Quantity,
                EntryPrice = position.AverageEntryPrice,
                ExecutedPrice = currentLtp,
                RealizedPnl = realizedPnl,
                TradeType = TradeType.Manual,
                ExitReason = exitReason,
                Remarks = $"Manual Paper SELL ({exitReason}) @ ₹{currentLtp:F2}"
            });

            // No paper_accounts update - see ExecuteManualBuyCoreAsync (manual cash is derived).
            _matchingEngine?.InvalidateCache();

            string message = $"Manual Paper SELL Executed ({exitReason}) @ ₹{currentLtp:F2} | Realized P&L: ₹{realizedPnl:N2}";
            await LogAuditAsync(position.Symbol, "MANUAL_SELL", currentLtp, position.Quantity, message, userId);
            await BroadcastAlertAsync(position.Symbol, "SELL", position.Quantity, currentLtp, $"{position.Symbol}: {message}");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to execute Manual Paper SELL for position #{PositionId} on {Symbol}", position.Id, position.Symbol);
            await LogAuditAsync(position.Symbol, "SYSTEM_ERROR", currentLtp, position.Quantity, $"Manual Paper SELL execution failed: {ex.Message}", userId);
            return false;
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------

    // Manual Trading's own cash: Manual Capital + all-time manual realized P&L - money in OPEN manual positions.
    private async Task<decimal> GetManualAvailableMarginAsync(int accountId, ManualPaperTradeSettings settings)
    {
        DateTime todayStartUtc = TimeZoneInfo.ConvertTimeToUtc(SwingTradeRules.NowIst().Date, TimeZoneHelper.IndianTimeZone);
        var figures = await _repository.GetDashboardAsync(accountId, todayStartUtc);
        return settings.AvailableCapital + figures.ManualRealizedPnl - figures.ManualUsedMargin;
    }

    private async Task<IEnumerable<PaperTradeHistory>> GetTodayPaperHistoryAsync(int accountId)
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
        return items;
    }

    // Own SignalR event, so Auto Paper Trading's "ReceiveAutoTradeAlert" listeners are unaffected.
    private async Task BroadcastAlertAsync(string symbol, string side, int quantity, decimal price, string message)
    {
        if (_hubContext == null) return;
        try
        {
            await _hubContext.Clients.All.SendAsync("ReceiveManualPaperTradeAlert", new { symbol, side, quantity, price, message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to broadcast Manual Paper Trade alert over SignalR.");
        }
    }
}
