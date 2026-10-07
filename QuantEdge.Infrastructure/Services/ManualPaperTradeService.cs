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
/// Manual Paper Trading - the user Buys (long) or Short Sells, Closes and edits levels from the Manual Trading
/// page. Entry checks mirror AutoRealTradeService's Manual Real Trade path (switch, window, entry delay, daily
/// trade/loss limits, exposure cap, duplicate position, price drift, capital, mandatory SL%/Trailing SL%);
/// shorts add an entry cut-off. Shorts are intraday only, so the one Worker job (ManualShortSquareOffWorker)
/// buys back open shorts at the Short Square-off Time. All data lives in its own manual_paper_* tables, which
/// no paper matching engine, Auto Paper or Auto Real code reads or writes. Prices are the latest stored
/// 1-minute candle close in Postgres (market_candles_1m) - Manual Trading never calls Zerodha.
/// </summary>
public class ManualPaperTradeService : IManualPaperTradeService
{
    // A BUY needs a stored price no older than this, so a symbol whose candles stopped updating can't
    // be bought at a stale price. Close / P&L use the latest stored price whatever its age.
    private static readonly TimeSpan MaxBuyPriceAge = TimeSpan.FromMinutes(10);

    // Manual Short Selling (intraday only) - defaults when a stored setting can't be parsed.
    private static readonly TimeSpan DefaultShortEntryCutoff = new(15, 0, 0);
    private static readonly TimeSpan DefaultShortSquareOffTime = new(15, 15, 0);
    private const string AutoSquareOffReason = "Auto Square-off (Intraday)";

    private readonly IManualPaperTradeRepository _repository;
    private readonly IMarketHoursService _marketHoursService;
    private readonly IHubContext<MarketDataHub>? _hubContext;
    private readonly ILogger<ManualPaperTradeService> _logger;

    // Per-(user, symbol) locks so a double-click can't race two requests past the duplicate-position
    // check - same guard as the Manual Real Trade path.
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> _manualBuyLocks = new();

    public ManualPaperTradeService(
        IManualPaperTradeRepository repository,
        IMarketHoursService marketHoursService,
        ILogger<ManualPaperTradeService> logger,
        IHubContext<MarketDataHub>? hubContext = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _marketHoursService = marketHoursService ?? throw new ArgumentNullException(nameof(marketHoursService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _hubContext = hubContext;
    }

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
        existing.ShortEntryCutoff = updateDto.ShortEntryCutoff;
        existing.ShortSquareOffTime = updateDto.ShortSquareOffTime;

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

    // ---------------------------------------------------------------------------------------------
    // Dashboard, positions, orders, history
    // ---------------------------------------------------------------------------------------------

    public async Task<ManualPaperDashboardDto> GetDashboardAsync(int userId = 1)
    {
        var settings = await _repository.GetSettingsAsync(userId);
        var dashboard = await _repository.GetDashboardAsync(userId, TodayStartUtc());

        var positions = await GetOpenPositionsAsync(userId);
        dashboard.ManualUnrealizedPnl = positions.Sum(p => p.UnrealizedPnl);
        dashboard.ManualEstimatedCharges = Math.Round(positions.Sum(p => p.EstimatedCharges), 2);
        dashboard.ManualCapital = settings.AvailableCapital;
        dashboard.IsManualTradeEnabled = settings.IsManualTradeEnabled;
        dashboard.MaxTradesPerDay = settings.MaxTradesPerDay;
        return dashboard;
    }

    // OPEN positions with LTP / unrealized P&L from the latest stored 1-minute close (one DB call for
    // all symbols; falls back to the entry price when a symbol has no stored candle).
    public async Task<IEnumerable<PaperPosition>> GetOpenPositionsAsync(int userId = 1)
    {
        var positions = (await _repository.GetOpenPositionsAsync(userId)).ToList();
        if (positions.Count == 0) return positions;

        var prices = await GetStoredPricesAsync(positions.Select(p => p.Symbol));
        foreach (var pos in positions)
        {
            if (prices.TryGetValue(pos.Symbol, out var price) && price.Ltp > 0m)
            {
                pos.CurrentPrice = price.Ltp;
                pos.PriceSource = "STORED";     // last stored 1-minute candle close
                pos.PriceAsOfUtc = price.PriceTime.Kind == DateTimeKind.Utc ? price.PriceTime : price.PriceTime.ToUniversalTime();
            }
            else
            {
                // No stored candle: CurrentPrice falls back to the entry price, so the ₹0 P&L would be fake.
                pos.PriceSource = "NONE";
                pos.PriceAsOfUtc = null;
            }
            // Long gains when the price rises; a short gains when it falls.
            pos.UnrealizedPnl = pos.Side == TradeSide.SELL
                ? (pos.AverageEntryPrice - pos.CurrentPrice) * pos.Quantity
                : (pos.CurrentPrice - pos.AverageEntryPrice) * pos.Quantity;
            pos.EstimatedCharges = PaperTradingService.EstimateCharges(pos);
        }
        return positions;
    }

    public async Task<PagedResultDto<PaperOrder>> GetOrdersPagedAsync(ManualPaperOrderFilterDto filter, int userId = 1)
    {
        var (items, totalCount) = await _repository.GetOrdersPagedAsync(userId, filter);
        return new PagedResultDto<PaperOrder> { Items = items, TotalCount = totalCount, Page = filter.Page, PageSize = filter.PageSize };
    }

    public async Task<PagedResultDto<PaperTradeHistory>> GetTradeHistoryPagedAsync(PaperTradeHistoryFilterDto filter, int userId = 1)
    {
        var (items, totalCount) = await _repository.GetTradeHistoryPagedAsync(userId, filter);
        return new PagedResultDto<PaperTradeHistory> { Items = items, TotalCount = totalCount, Page = filter.Page, PageSize = filter.PageSize };
    }

    public async Task ResetAsync(int userId = 1)
    {
        await _repository.ResetAsync(userId);
        await LogAuditAsync("SYSTEM", "RESET_MANUAL_PAPER_TRADING", null, null,
            "Manual Paper Trading reset - manual orders, positions, trade history and logs cleared", userId);
    }

    // ---------------------------------------------------------------------------------------------
    // Manual BUY (long) and Manual SHORT SELL - one entry path, so both run the same gates
    // ---------------------------------------------------------------------------------------------

    public Task<(bool Success, string Message)> ExecuteManualBuyAsync(string symbol, decimal entryPrice, int quantity,
        decimal stopLossPct, decimal trailingSlPct, int userId = 1) =>
        ExecuteManualEntryAsync(TradeSide.BUY, symbol, entryPrice, quantity, stopLossPct, trailingSlPct, userId);

    public Task<(bool Success, string Message)> ExecuteManualShortAsync(string symbol, decimal entryPrice, int quantity,
        decimal stopLossPct, decimal trailingSlPct, int userId = 1) =>
        ExecuteManualEntryAsync(TradeSide.SELL, symbol, entryPrice, quantity, stopLossPct, trailingSlPct, userId);

    private async Task<(bool Success, string Message)> ExecuteManualEntryAsync(TradeSide side, string symbol, decimal entryPrice,
        int quantity, decimal stopLossPct, decimal trailingSlPct, int userId)
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
            return await ExecuteManualEntryCoreAsync(side, symbol, entryPrice, quantity, stopLossPct, trailingSlPct, userId);
        }
        finally
        {
            manualLock.Release();
        }
    }

    private async Task<(bool Success, string Message)> ExecuteManualEntryCoreAsync(TradeSide side, string symbol, decimal entryPrice,
        int quantity, decimal stopLossPct, decimal trailingSlPct, int userId)
    {
        bool isShort = side == TradeSide.SELL;
        string label = isShort ? "SHORT SELL" : "BUY";
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

        // 2b. Opening Entry Delay
        if (!SwingTradeRules.IsPastEntryDelay(settings.TradingWindowStart, settings.EntryDelayMinutes, nowIst))
        {
            return await RejectAsync(symbol, entryPrice,
                $"Opening entry delay active - new {(isShort ? "shorts" : "BUYs")} held back for {settings.EntryDelayMinutes} min after {settings.TradingWindowStart}", userId);
        }

        // 2c. Short selling is intraday only: no new short at/after the cut-off, so every short has time to be
        // bought back before the auto square-off.
        if (isShort && nowIst.TimeOfDay >= ParseIstTime(settings.ShortEntryCutoff, DefaultShortEntryCutoff))
        {
            return await RejectAsync(symbol, entryPrice,
                $"Short selling closed for today - no new shorts at/after {settings.ShortEntryCutoff} IST (shorts are intraday only; open shorts are squared off at {settings.ShortSquareOffTime} IST)", userId);
        }

        // 3. Daily Trade Limit Check
        int todayCount = await _repository.GetTodayManualTradeCountAsync(userId);
        if (todayCount >= settings.MaxTradesPerDay)
        {
            return await RejectAsync(symbol, entryPrice,
                $"Daily limit of {settings.MaxTradesPerDay} manual trades reached ({todayCount}/{settings.MaxTradesPerDay})", userId);
        }

        // 4. Daily Loss Circuit Breaker - today's realized + open unrealized manual P&L. Like Real, a breach
        // switches Manual Paper Trading OFF (its own switch - Auto Paper is untouched).
        var figures = await _repository.GetDashboardAsync(userId, TodayStartUtc());
        var openPositions = (await GetOpenPositionsAsync(userId)).ToList();
        decimal effectiveDailyLossLimit = SwingTradeRules.EffectiveDailyLossLimit(settings.MaxDailyLossLimit, settings.AvailableCapital);
        decimal totalLoss = figures.ManualTodayRealizedPnl + openPositions.Sum(p => p.UnrealizedPnl);
        if (totalLoss <= -effectiveDailyLossLimit)
        {
            await ToggleManualTradeAsync(false, userId);
            return await RejectAsync(symbol, entryPrice,
                $"Daily loss limit ₹{effectiveDailyLossLimit:N2} breached (Total Loss: ₹{totalLoss:N2}). Manual Paper Trading switched OFF.",
                userId, "CIRCUIT_BREAKER");
        }

        // 4b. Portfolio-Level Exposure Cap
        if (openPositions.Count >= SwingTradeRules.MaxConcurrentPositions)
        {
            return await RejectAsync(symbol, entryPrice,
                $"Portfolio exposure cap reached ({openPositions.Count}/{SwingTradeRules.MaxConcurrentPositions} concurrent open positions)", userId);
        }

        // 5. Duplicate Open Position Check
        var existingOpenPos = openPositions.FirstOrDefault(p => string.Equals(p.Symbol, symbol, StringComparison.OrdinalIgnoreCase));
        if (existingOpenPos != null)
        {
            return await RejectAsync(symbol, entryPrice,
                $"{symbol} already has an OPEN manual position (Position #{existingOpenPos.Id})", userId);
        }

        // 6. Price Re-check against the latest stored 1-minute close (same drift guard as Real). The trade
        // fills at that stored price; a missing or stale price (> MaxBuyPriceAge) rejects the BUY.
        var prices = await GetStoredPricesAsync(new[] { symbol });
        if (!prices.TryGetValue(symbol, out var stored) || stored.Ltp <= 0m)
        {
            return await RejectAsync(symbol, entryPrice, $"No stored price found for {symbol} (market_candles_1m).", userId);
        }
        if (DateTime.UtcNow - stored.PriceTime.ToUniversalTime() > MaxBuyPriceAge)
        {
            return await RejectAsync(symbol, entryPrice,
                $"Stored price for {symbol} is stale (last candle {TimeZoneInfo.ConvertTimeFromUtc(stored.PriceTime.ToUniversalTime(), TimeZoneHelper.IndianTimeZone):dd-MMM HH:mm} IST) - not {(isShort ? "shorting" : "buying")} at an old price.", userId);
        }

        string? driftReason = SwingTradeRules.CheckSignalDrift(entryPrice, stored.Ltp);
        if (driftReason != null)
        {
            return await RejectAsync(symbol, entryPrice, driftReason, userId);
        }
        entryPrice = stored.Ltp;

        // 7. Quantity, Manual Trading's own capital, mandatory trade-wise SL%/Trailing SL%
        if (quantity < 1)
        {
            return await RejectAsync(symbol, entryPrice, "Manual trade rejected: Quantity must be a positive whole number.", userId);
        }

        decimal requiredCapital = quantity * entryPrice;
        decimal manualAvailableMargin = settings.AvailableCapital + figures.ManualRealizedPnl - figures.ManualUsedMargin;
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

        // 8. Target & Stop Loss - reference levels only (the position is exited with the Close button, or for a
        // short also by the auto square-off). BUY: shared SwingTradeRules levels with the trade's own SL%, exactly
        // as for Manual Real Trade. SHORT: mirrored - Stop Loss above the entry, Target below it.
        decimal effectiveStopLossPct = Math.Abs(stopLossPct);
        decimal effectiveTrailingSlPct = Math.Abs(trailingSlPct);
        decimal takeProfit;
        decimal stopLoss;
        if (isShort)
        {
            (stopLoss, takeProfit) = ComputeShortLevels(entryPrice, effectiveStopLossPct, settings.ProfitTargetPct);
        }
        else
        {
            var levels = SwingTradeRules.ComputeEntryLevels(entryPrice, null, null, null,
                effectiveStopLossPct, effectiveTrailingSlPct, SwingTradeParams.From(settings));
            takeProfit = levels.TakeProfit;
            stopLoss = levels.StopLoss;
        }

        try
        {
            int? positionId = await _repository.CreateEntryAsync(userId, side, symbol, quantity, entryPrice, stopLoss,
                effectiveTrailingSlPct, takeProfit,
                $"Manual Paper {label} (SL {effectiveStopLossPct:F2}% / TSL {effectiveTrailingSlPct:F2}%, SL ₹{stopLoss:F2} / Target ₹{takeProfit:F2})",
                $"Manual Paper {label} Executed @ ₹{entryPrice:F2}");

            if (!positionId.HasValue)
            {
                return await RejectAsync(symbol, entryPrice, $"{symbol} already has an OPEN manual position", userId);
            }

            string message = $"Manual Paper {label} executed @ ₹{entryPrice:F2} (Qty: {quantity}, Target: ₹{takeProfit:F2}, SL: ₹{stopLoss:F2}, TSL: {effectiveTrailingSlPct:F2}%)";
            if (isShort)
            {
                message += $" - auto square-off at {settings.ShortSquareOffTime} IST if not covered";
            }
            await LogAuditAsync(symbol, isShort ? "MANUAL_SHORT" : "MANUAL_BUY", entryPrice, quantity, message, userId);
            await BroadcastAlertAsync(symbol, isShort ? "SELL" : "BUY", quantity, entryPrice, $"{symbol}: {message}");
            return (true, $"{symbol}: {message}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to execute Manual Paper {Label} for {Symbol} (User {UserId})", label, symbol, userId);
            return await RejectAsync(symbol, entryPrice, $"Manual Paper {label} execution failed: {ex.Message}", userId, "SYSTEM_ERROR");
        }
    }

    // Short levels, mirrored from a long: the trade loses when the price RISES, so the Stop Loss sits SL% above
    // the entry and the Target sits Profit Target % below it.
    internal static (decimal StopLoss, decimal TakeProfit) ComputeShortLevels(decimal entryPrice, decimal stopLossPct, decimal profitTargetPct)
    {
        decimal stopLoss = Math.Round(entryPrice * (1m + Math.Abs(stopLossPct) / 100m), 2);
        decimal takeProfit = Math.Round(entryPrice * (1m - Math.Abs(profitTargetPct) / 100m), 2);
        return (stopLoss, takeProfit);
    }

    // ---------------------------------------------------------------------------------------------
    // Close button (SELL a long / BUY TO COVER a short), short auto square-off and Edit levels - the
    // only ways a manual position changes.
    // ---------------------------------------------------------------------------------------------

    public async Task<(bool Success, string Message)> ClosePositionAsync(int positionId, int userId = 1)
    {
        var position = (await GetOpenPositionsAsync(userId)).FirstOrDefault(p => p.Id == positionId);
        if (position == null) return (false, $"Open manual position #{positionId} not found.");

        // GetOpenPositionsAsync set CurrentPrice to the latest stored 1-minute close (entry price if none stored).
        decimal exitPrice = position.CurrentPrice > 0m ? position.CurrentPrice : position.AverageEntryPrice;
        return await ClosePositionCoreAsync(position, userId, exitPrice, "Manual Close");
    }

    // Closes one OPEN manual position at exitPrice: a long is SOLD, a short is BOUGHT back (covered).
    private async Task<(bool Success, string Message)> ClosePositionCoreAsync(PaperPosition position, int userId, decimal exitPrice,
        string exitReason)
    {
        bool isShort = position.Side == TradeSide.SELL;
        string label = isShort ? "BUY TO COVER" : "SELL";
        try
        {
            var result = await _repository.ClosePositionAsync(userId, position.Id, exitPrice, exitReason);
            if (!result.Success)
            {
                return (false, $"{position.Symbol}: position could not be closed (it may already be closed).");
            }

            string message = $"Manual Paper {label} Executed ({exitReason}) @ ₹{result.ExitPrice:F2} | Realized P&L: ₹{result.RealizedPnl:N2}";
            string actionType = exitReason == AutoSquareOffReason ? "AUTO_SQUARE_OFF" : (isShort ? "MANUAL_COVER" : "MANUAL_SELL");
            await LogAuditAsync(position.Symbol, actionType, result.ExitPrice, result.Quantity, message, userId);
            await BroadcastAlertAsync(position.Symbol, isShort ? "BUY" : "SELL", result.Quantity, result.ExitPrice, $"{position.Symbol}: {message}");
            return (true, $"{position.Symbol}: {message}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to close Manual Paper position #{PositionId} on {Symbol}", position.Id, position.Symbol);
            await LogAuditAsync(position.Symbol, "SYSTEM_ERROR", exitPrice, position.Quantity, $"Manual Paper {label} execution failed: {ex.Message}", userId);
            return (false, $"{position.Symbol}: close failed - {ex.Message}");
        }
    }

    // Intraday auto square-off (ManualShortSquareOffWorker): buys back every OPEN short that is due - opened on an
    // earlier IST day (a missed run, never left overnight) or past its user's Short Square-off Time today. Exits at
    // the latest stored 1-minute close (entry price if none stored); one DB price call for all symbols, no Zerodha.
    public async Task<int> SquareOffDueShortsAsync()
    {
        var shorts = (await _repository.GetAllOpenShortPositionsAsync()).ToList();
        if (shorts.Count == 0) return 0;

        var nowIst = SwingTradeRules.NowIst();
        var settingsByUser = new Dictionary<int, ManualPaperTradeSettings>();
        Dictionary<string, ManualPaperPriceDto>? prices = null;
        int closed = 0;

        foreach (var pos in shorts)
        {
            int userId = pos.AccountId;
            if (!settingsByUser.TryGetValue(userId, out var settings))
            {
                settings = await _repository.GetSettingsAsync(userId);
                settingsByUser[userId] = settings;
            }

            bool openedEarlierDay = SwingTradeRules.ToIst(pos.OpenedAt).Date < nowIst.Date;
            bool pastSquareOff = nowIst.TimeOfDay >= ParseIstTime(settings.ShortSquareOffTime, DefaultShortSquareOffTime);
            if (!openedEarlierDay && !pastSquareOff) continue;

            prices ??= await GetStoredPricesAsync(shorts.Select(s => s.Symbol));
            decimal exitPrice = prices.TryGetValue(pos.Symbol, out var price) && price.Ltp > 0m ? price.Ltp : pos.AverageEntryPrice;

            var (success, message) = await ClosePositionCoreAsync(pos, userId, exitPrice, AutoSquareOffReason);
            if (success) closed++;
            _logger.LogInformation("Manual short auto square-off #{PositionId} ({Symbol}, user {UserId}): {Message}",
                pos.Id, pos.Symbol, userId, message);
        }
        return closed;
    }

    public async Task<(bool Success, string Message)> UpdatePositionLevelsAsync(int positionId, decimal stopLoss, decimal trailingSlPct,
        decimal takeProfit, int userId = 1)
    {
        var position = (await _repository.GetOpenPositionsAsync(userId)).FirstOrDefault(p => p.Id == positionId);
        if (position == null) return (false, $"Open manual position #{positionId} not found.");

        stopLoss = Math.Round(stopLoss, 2);
        takeProfit = Math.Round(takeProfit, 2);
        trailingSlPct = Math.Round(Math.Abs(trailingSlPct), 2);

        if (stopLoss <= 0m || takeProfit <= 0m || trailingSlPct <= 0m)
        {
            return (false, "Stop Loss, Trailing SL % and Target must all be greater than zero.");
        }
        if (position.Side == TradeSide.SELL)
        {
            // Short: loses when the price rises, so the Stop Loss is above the Target.
            if (stopLoss <= takeProfit)
            {
                return (false, $"For a short, Stop Loss ₹{stopLoss:F2} must be above Target ₹{takeProfit:F2}.");
            }
        }
        else if (stopLoss >= takeProfit)
        {
            return (false, $"Stop Loss ₹{stopLoss:F2} must be below Target ₹{takeProfit:F2}.");
        }

        bool updated = await _repository.UpdatePositionLevelsAsync(userId, positionId, stopLoss, trailingSlPct, takeProfit);
        if (!updated)
        {
            return (false, $"{position.Symbol}: position could not be updated (it may already be closed).");
        }

        string message = $"Levels updated - SL: ₹{position.StopLoss:F2} → ₹{stopLoss:F2}, " +
                         $"Trailing SL: {position.TrailingSlPct:F2}% → {trailingSlPct:F2}%, " +
                         $"Target: ₹{position.TakeProfit:F2} → ₹{takeProfit:F2}";
        await LogAuditAsync(position.Symbol, "LEVELS_UPDATED", null, position.Quantity, message, userId);
        return (true, $"{position.Symbol}: {message}");
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------

    private static TimeSpan ParseIstTime(string? hhmm, TimeSpan fallback) =>
        TimeSpan.TryParse(hhmm, out var time) ? time : fallback;

    private static DateTime TodayStartUtc() =>
        TimeZoneInfo.ConvertTimeToUtc(SwingTradeRules.NowIst().Date, TimeZoneHelper.IndianTimeZone);

    // Latest stored 1-minute close per symbol from Postgres (fn_get_manual_paper_latest_prices).
    private async Task<Dictionary<string, ManualPaperPriceDto>> GetStoredPricesAsync(IEnumerable<string> symbols)
    {
        var rows = await _repository.GetLatestPricesAsync(symbols);
        return rows.GroupBy(r => r.Symbol, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
    }

    public async Task<ManualPaperPriceDto?> GetQuoteAsync(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol)) return null;
        var prices = await GetStoredPricesAsync(new[] { symbol });
        return prices.TryGetValue(symbol.Trim(), out var price) ? price : null;
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
