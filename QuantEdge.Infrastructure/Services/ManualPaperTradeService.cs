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
/// shorts add an entry cut-off. Shorts are intraday only, so ManualShortSquareOffWorker buys back open shorts at
/// the Short Square-off Time. An entry can be Market or Limit: a Limit order rests Pending (holding its capital)
/// until ManualLimitOrderWorker sees a stored candle trade through its price, or expires it at day end. All data
/// lives in its own manual_paper_* tables, which
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
    public async Task<IEnumerable<ManualPaperPosition>> GetOpenPositionsAsync(int userId = 1)
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
        decimal stopLossPct, decimal trailingSlPct, int userId = 1, bool autoExit = false) =>
        ExecuteManualEntryAsync(TradeSide.BUY, symbol, entryPrice, quantity, stopLossPct, trailingSlPct, userId, null, autoExit);

    public Task<(bool Success, string Message)> ExecuteManualShortAsync(string symbol, decimal entryPrice, int quantity,
        decimal stopLossPct, decimal trailingSlPct, int userId = 1, bool autoExit = false) =>
        ExecuteManualEntryAsync(TradeSide.SELL, symbol, entryPrice, quantity, stopLossPct, trailingSlPct, userId, null, autoExit);

    public Task<(bool Success, string Message)> PlaceManualLimitOrderAsync(TradeSide side, string symbol, decimal limitPrice, int quantity,
        decimal stopLossPct, decimal trailingSlPct, int userId = 1, bool autoExit = false) =>
        ExecuteManualEntryAsync(side, symbol, limitPrice, quantity, stopLossPct, trailingSlPct, userId, limitPrice, autoExit);

    private async Task<(bool Success, string Message)> ExecuteManualEntryAsync(TradeSide side, string symbol, decimal entryPrice,
        int quantity, decimal stopLossPct, decimal trailingSlPct, int userId, decimal? limitPrice, bool autoExit)
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
            return await ExecuteManualEntryCoreAsync(side, symbol, entryPrice, quantity, stopLossPct, trailingSlPct, userId, limitPrice, autoExit);
        }
        finally
        {
            manualLock.Release();
        }
    }

    // Market entry (limitPrice null) or Limit entry. Both run every gate below; a limit that is already at/through
    // the stored price fills now like a market order (at the stored price, never worse than the limit), any other
    // limit rests as a Pending order that ProcessPendingLimitOrdersAsync fills or expires.
    private async Task<(bool Success, string Message)> ExecuteManualEntryCoreAsync(TradeSide side, string symbol, decimal entryPrice,
        int quantity, decimal stopLossPct, decimal trailingSlPct, int userId, decimal? limitPrice, bool autoExit)
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

        // 4b. Portfolio-Level Exposure Cap - a Pending limit order counts as a position (it can fill at any time).
        var pendingOrders = (await _repository.GetPendingOrdersAsync(userId)).ToList();
        int exposure = openPositions.Count + pendingOrders.Count;
        if (exposure >= SwingTradeRules.MaxConcurrentPositions)
        {
            return await RejectAsync(symbol, entryPrice,
                $"Portfolio exposure cap reached ({exposure}/{SwingTradeRules.MaxConcurrentPositions} open positions + pending limit orders)", userId);
        }

        // 5. Duplicate Open Position / Pending Order Check - one per symbol.
        var existingOpenPos = openPositions.FirstOrDefault(p => string.Equals(p.Symbol, symbol, StringComparison.OrdinalIgnoreCase));
        if (existingOpenPos != null)
        {
            return await RejectAsync(symbol, entryPrice,
                $"{symbol} already has an OPEN manual position (Position #{existingOpenPos.Id})", userId);
        }
        var existingPending = pendingOrders.FirstOrDefault(o => string.Equals(o.Symbol, symbol, StringComparison.OrdinalIgnoreCase));
        if (existingPending != null)
        {
            return await RejectAsync(symbol, entryPrice,
                $"{symbol} already has a Pending limit order (Order #{existingPending.Id} @ ₹{existingPending.LimitPrice:F2}) - cancel it first", userId);
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

        // 6b. Market: drift guard, then fill at the stored price. Limit: the price must be within MaxLimitDistancePct
        // of the stored price (catches a typo like ₹148 for ₹1,480) instead of the drift guard; a limit already
        // at/through the stored price fills now at the stored price, any other limit rests as Pending.
        bool restsAsPending = false;
        string? marketableNote = null;
        if (limitPrice.HasValue)
        {
            decimal limit = RoundToTick(limitPrice.Value);
            string? limitReason = CheckLimitPrice(limit, stored.Ltp);
            if (limitReason != null)
            {
                return await RejectAsync(symbol, limit, limitReason, userId);
            }

            restsAsPending = !IsMarketableLimit(side, limit, stored.Ltp);
            if (!restsAsPending)
            {
                marketableNote = $" - limit ₹{limit:F2} was already {(isShort ? "at/below" : "at/above")} the price, so it filled now";
            }
            entryPrice = restsAsPending ? limit : stored.Ltp;
        }
        else
        {
            string? driftReason = SwingTradeRules.CheckSignalDrift(entryPrice, stored.Ltp);
            if (driftReason != null)
            {
                return await RejectAsync(symbol, entryPrice, driftReason, userId);
            }
            entryPrice = stored.Ltp;
        }

        // 7. Quantity, Manual Trading's own capital, mandatory trade-wise SL%/Trailing SL%. Capital held by
        // Pending limit orders is not available (a Pending order holds quantity x limit price).
        if (quantity < 1)
        {
            return await RejectAsync(symbol, entryPrice, "Manual trade rejected: Quantity must be a positive whole number.", userId);
        }

        decimal requiredCapital = quantity * entryPrice;
        decimal manualAvailableMargin = settings.AvailableCapital + figures.ManualRealizedPnl - figures.ManualUsedMargin - figures.ManualBlockedMargin;
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

        // 8. Target & Stop Loss. With Auto Exit ON, ProcessAutoExitsAsync closes the position at these levels (or the
        // trailed stop); with it OFF they are reference only (Close button, or for a short the auto square-off).
        // BUY: shared SwingTradeRules levels with the trade's own SL%, exactly as for Manual Real Trade. SHORT:
        // mirrored - Stop Loss above the entry, Target below it. For a Pending limit they are estimates at the limit
        // price; the fill sets them again from the real fill price.
        decimal effectiveStopLossPct = Math.Abs(stopLossPct);
        decimal effectiveTrailingSlPct = Math.Abs(trailingSlPct);
        var (stopLoss, takeProfit) = ComputeLevels(side, entryPrice, effectiveStopLossPct, effectiveTrailingSlPct, settings);

        if (restsAsPending)
        {
            return await PlacePendingLimitOrderAsync(side, symbol, entryPrice, quantity, effectiveStopLossPct,
                effectiveTrailingSlPct, stopLoss, takeProfit, settings, userId, autoExit);
        }

        try
        {
            string orderType = limitPrice.HasValue ? "Limit " : string.Empty;
            int? positionId = await _repository.CreateEntryAsync(userId, side, symbol, quantity, entryPrice, stopLoss,
                effectiveTrailingSlPct, takeProfit,
                $"Manual Paper {orderType}{label} (SL {effectiveStopLossPct:F2}% / TSL {effectiveTrailingSlPct:F2}%, SL ₹{stopLoss:F2} / Target ₹{takeProfit:F2}{(autoExit ? ", Auto Exit" : "")})",
                $"Manual Paper {orderType}{label} Executed @ ₹{entryPrice:F2}", autoExit);

            if (!positionId.HasValue)
            {
                return await RejectAsync(symbol, entryPrice, $"{symbol} already has an OPEN manual position or a Pending order", userId);
            }

            string message = $"Manual Paper {orderType}{label} executed @ ₹{entryPrice:F2}{marketableNote} (Qty: {quantity}, Target: ₹{takeProfit:F2}, SL: ₹{stopLoss:F2}, TSL: {effectiveTrailingSlPct:F2}%){AutoExitText(autoExit)}";
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

    // Entry levels for either side (see step 8 of ExecuteManualEntryCoreAsync).
    private static (decimal StopLoss, decimal TakeProfit) ComputeLevels(TradeSide side, decimal entryPrice, decimal stopLossPct,
        decimal trailingSlPct, ManualPaperTradeSettings settings)
    {
        if (side == TradeSide.SELL)
        {
            return ComputeShortLevels(entryPrice, stopLossPct, settings.ProfitTargetPct);
        }

        var levels = SwingTradeRules.ComputeEntryLevels(entryPrice, null, null, null,
            stopLossPct, trailingSlPct, SwingTradeParams.From(settings));
        return (levels.StopLoss, levels.TakeProfit);
    }

    // ---------------------------------------------------------------------------------------------
    // Limit orders - placed (above) and cancelled from the page; filled or expired by ManualLimitOrderWorker
    // from stored 1-minute candles. Day orders only.
    // ---------------------------------------------------------------------------------------------

    // NSE equity tick size - a limit price is rounded to it, like a real exchange order.
    private const decimal TickSize = 0.05m;

    /// <summary>A limit price further than this from the stored price is rejected (typo guard).</summary>
    internal const decimal MaxLimitDistancePct = 10m;

    private static readonly TimeSpan DefaultOrderExpiry = new(15, 30, 0);

    internal static decimal RoundToTick(decimal price) =>
        Math.Round(Math.Round(price / TickSize, MidpointRounding.AwayFromZero) * TickSize, 2);

    /// <summary>A BUY limit at/above the price, or a SHORT limit at/below it, would trade at once.</summary>
    internal static bool IsMarketableLimit(TradeSide side, decimal limitPrice, decimal ltp) =>
        side == TradeSide.SELL ? limitPrice <= ltp : limitPrice >= ltp;

    /// <summary>Null when the limit price is acceptable, otherwise the rejection reason.</summary>
    internal static string? CheckLimitPrice(decimal limitPrice, decimal ltp)
    {
        if (limitPrice <= 0m) return "Limit price must be greater than zero.";
        if (ltp <= 0m) return null;

        decimal distancePct = Math.Abs(limitPrice - ltp) / ltp * 100m;
        return distancePct > MaxLimitDistancePct
            ? $"Limit price ₹{limitPrice:F2} is {distancePct:F1}% away from the price ₹{ltp:F2} - it must be within {MaxLimitDistancePct:F0}%."
            : null;
    }

    /// <summary>
    /// Fill price when a candle trades through the limit: the limit price, or the candle's open when it opened past
    /// the limit (a gap gives the better price, as on the exchange) - a BUY never pays more, a SHORT never sells
    /// for less, than its limit.
    /// </summary>
    internal static decimal LimitFillPrice(TradeSide side, decimal limitPrice, decimal? candleOpen)
    {
        if (!candleOpen.HasValue || candleOpen.Value <= 0m) return limitPrice;
        return side == TradeSide.SELL ? Math.Max(limitPrice, candleOpen.Value) : Math.Min(limitPrice, candleOpen.Value);
    }

    /// <summary>IST time a Pending order expires: the trading window end, or for a short the earlier short entry
    /// cut-off (a short must not open after it).</summary>
    internal static TimeSpan LimitOrderExpiry(TradeSide side, ManualPaperTradeSettings settings)
    {
        TimeSpan windowEnd = ParseIstTime(settings.TradingWindowEnd, DefaultOrderExpiry);
        if (side != TradeSide.SELL) return windowEnd;

        TimeSpan cutoff = ParseIstTime(settings.ShortEntryCutoff, DefaultShortEntryCutoff);
        return cutoff < windowEnd ? cutoff : windowEnd;
    }

    private async Task<(bool Success, string Message)> PlacePendingLimitOrderAsync(TradeSide side, string symbol, decimal limitPrice,
        int quantity, decimal stopLossPct, decimal trailingSlPct, decimal estStopLoss, decimal estTakeProfit,
        ManualPaperTradeSettings settings, int userId, bool autoExit)
    {
        bool isShort = side == TradeSide.SELL;
        string label = isShort ? "SHORT SELL" : "BUY";
        try
        {
            int? orderId = await _repository.PlaceLimitOrderAsync(userId, side, symbol, quantity, limitPrice, stopLossPct, trailingSlPct,
                estStopLoss, estTakeProfit,
                $"Manual Paper Limit {label} @ ₹{limitPrice:F2} (SL {stopLossPct:F2}% / TSL {trailingSlPct:F2}%{(autoExit ? ", Auto Exit" : "")})",
                autoExit);

            if (!orderId.HasValue)
            {
                return await RejectAsync(symbol, limitPrice, $"{symbol} already has an OPEN manual position or a Pending order", userId);
            }

            string expiry = LimitOrderExpiry(side, settings).ToString(@"hh\:mm");
            string message = $"Limit {label} order #{orderId} placed: {quantity} @ ₹{limitPrice:F2} - Pending until the price trades at or " +
                             $"{(isShort ? "above" : "below")} ₹{limitPrice:F2} (expires {expiry} IST if not filled; ₹{quantity * limitPrice:N2} held){AutoExitText(autoExit)}";
            await LogAuditAsync(symbol, "LIMIT_PLACED", limitPrice, quantity, message, userId);
            return (true, $"{symbol}: {message}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to place Manual Paper Limit {Label} for {Symbol} (User {UserId})", label, symbol, userId);
            return await RejectAsync(symbol, limitPrice, $"Manual Paper Limit {label} could not be placed: {ex.Message}", userId, "SYSTEM_ERROR");
        }
    }

    public async Task<(bool Success, string Message)> CancelOrderAsync(int orderId, int userId = 1)
    {
        var order = (await _repository.GetPendingOrdersAsync(userId)).FirstOrDefault(o => o.Id == orderId);
        if (order == null)
        {
            return (false, $"Pending manual order #{orderId} not found - it may already be filled, cancelled or expired.");
        }

        if (!await _repository.CancelOrderAsync(userId, orderId, "Cancelled by user"))
        {
            return (false, $"{order.Symbol}: order #{orderId} could not be cancelled - it may have just filled.");
        }

        string message = $"Limit {(order.Side == TradeSide.SELL ? "SHORT SELL" : "BUY")} order #{orderId} ({order.Quantity} @ ₹{order.LimitPrice:F2}) cancelled - ₹{order.Quantity * order.LimitPrice:N2} released";
        await LogAuditAsync(order.Symbol, "LIMIT_CANCELLED", order.LimitPrice, order.Quantity, message, userId);
        return (true, $"{order.Symbol}: {message}");
    }

    // Worker pass (ManualLimitOrderWorker): fills every Pending order whose price was traded through by a stored
    // 1-minute candle placed after it, and expires the ones still Pending at their expiry time (or left from an
    // earlier day). One DB call finds every order's triggering candle; no Zerodha call.
    public async Task<(int Filled, int Cancelled)> ProcessPendingLimitOrdersAsync()
    {
        var orders = (await _repository.GetPendingOrdersAsync()).ToList();
        if (orders.Count == 0) return (0, 0);

        var nowIst = SwingTradeRules.NowIst();
        var settingsByUser = new Dictionary<int, ManualPaperTradeSettings>();
        int filled = 0, cancelled = 0;

        foreach (var order in orders)
        {
            if (!settingsByUser.TryGetValue(order.UserId, out var settings))
            {
                settings = await _repository.GetSettingsAsync(order.UserId);
                settingsByUser[order.UserId] = settings;
            }

            TimeSpan expiry = LimitOrderExpiry(order.Side, settings);
            var createdIst = SwingTradeRules.ToIst(order.CreatedAt);
            string expiredReason = order.Side == TradeSide.SELL
                ? $"Expired - not filled before {expiry:hh\\:mm} IST (short entry cut-off / day end)"
                : $"Expired - Day order not filled by {expiry:hh\\:mm} IST";

            string? cancelReason = null;
            if (order.TriggerCandleTime.HasValue)
            {
                // Only a candle from the order's own day and before its expiry may fill it.
                var triggerIst = SwingTradeRules.ToIst(order.TriggerCandleTime.Value);
                if (triggerIst.Date == createdIst.Date && triggerIst.TimeOfDay < expiry)
                {
                    var (success, reason) = await FillPendingLimitOrderAsync(order, settings, triggerIst);
                    if (success)
                    {
                        filled++;
                        continue;
                    }
                    cancelReason = reason;
                }
                else
                {
                    cancelReason = expiredReason;
                }
            }
            else if (createdIst.Date < nowIst.Date || nowIst.TimeOfDay >= expiry)
            {
                cancelReason = expiredReason;
            }

            if (cancelReason != null && await _repository.CancelOrderAsync(order.UserId, order.Id, cancelReason))
            {
                cancelled++;
                bool expired = cancelReason.StartsWith("Expired", StringComparison.Ordinal);
                await LogAuditAsync(order.Symbol, expired ? "LIMIT_EXPIRED" : "LIMIT_CANCELLED", order.LimitPrice, order.Quantity,
                    $"Limit order #{order.Id} ({order.Quantity} @ ₹{order.LimitPrice:F2}): {cancelReason}", order.UserId);
            }
        }
        return (filled, cancelled);
    }

    // Fills one triggered order after the checks that must still hold at fill time (switch ON, daily loss limit not
    // breached). Levels are set from the real fill price with the order's own SL % / Trailing SL %. Returns the
    // cancel reason when it must not fill.
    private async Task<(bool Success, string? CancelReason)> FillPendingLimitOrderAsync(ManualPaperPendingOrderDto order,
        ManualPaperTradeSettings settings, DateTime triggerIst)
    {
        int userId = order.UserId;
        bool isShort = order.Side == TradeSide.SELL;
        string label = isShort ? "SHORT SELL" : "BUY";

        if (!settings.IsManualTradeEnabled)
        {
            return (false, "Cancelled - Manual Paper Trading switch is OFF");
        }

        var figures = await _repository.GetDashboardAsync(userId, TodayStartUtc());
        var openPositions = (await GetOpenPositionsAsync(userId)).ToList();
        decimal effectiveDailyLossLimit = SwingTradeRules.EffectiveDailyLossLimit(settings.MaxDailyLossLimit, settings.AvailableCapital);
        decimal totalLoss = figures.ManualTodayRealizedPnl + openPositions.Sum(p => p.UnrealizedPnl);
        if (totalLoss <= -effectiveDailyLossLimit)
        {
            return (false, $"Cancelled - daily loss limit ₹{effectiveDailyLossLimit:N2} breached (Total Loss: ₹{totalLoss:N2})");
        }

        decimal fillPrice = LimitFillPrice(order.Side, order.LimitPrice, order.TriggerCandleOpen);
        decimal stopLossPct = Math.Abs(order.StopLossPct ?? settings.StopLossPct);
        decimal trailingSlPct = Math.Abs(order.TrailingSlPct ?? settings.TrailingSlPct);
        var (stopLoss, takeProfit) = ComputeLevels(order.Side, fillPrice, stopLossPct, trailingSlPct, settings);

        try
        {
            int? positionId = await _repository.FillLimitOrderAsync(order.Id, fillPrice, stopLoss, trailingSlPct, takeProfit,
                $"Manual Paper Limit {label} Filled @ ₹{fillPrice:F2} (limit ₹{order.LimitPrice:F2}, candle {triggerIst:HH:mm} IST)");
            if (!positionId.HasValue)
            {
                return (false, $"Cancelled - {order.Symbol} already has an OPEN manual position");
            }

            string message = $"Limit {label} order #{order.Id} filled @ ₹{fillPrice:F2} (limit ₹{order.LimitPrice:F2}, Qty: {order.Quantity}, " +
                             $"Target: ₹{takeProfit:F2}, SL: ₹{stopLoss:F2}, TSL: {trailingSlPct:F2}%){AutoExitText(order.AutoExit)}";
            if (isShort)
            {
                message += $" - auto square-off at {settings.ShortSquareOffTime} IST if not covered";
            }
            await LogAuditAsync(order.Symbol, "LIMIT_FILLED", fillPrice, order.Quantity, message, userId);
            await BroadcastAlertAsync(order.Symbol, isShort ? "SELL" : "BUY", order.Quantity, fillPrice, $"{order.Symbol}: {message}");
            return (true, null);
        }
        catch (Exception ex)
        {
            // Left Pending - the next pass retries it.
            _logger.LogError(ex, "Failed to fill Manual Paper limit order #{OrderId} ({Symbol})", order.Id, order.Symbol);
            return (false, null);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Auto Exit - positions opened with Auto Exit ON are closed at their Target, Stop Loss or trailed stop,
    // judged only from stored 1-minute candles (market_candles_1m) by ManualAutoExitWorker. No live feed.
    // ---------------------------------------------------------------------------------------------

    internal enum AutoExitKind { None, Target, StopLoss, TrailingStop }

    /// <summary>Outcome of checking a position against candles: an exit (Kind, ExitPrice, the candle) or, when
    /// none hit, the updated trail state and the start time of the last candle checked.</summary>
    internal sealed record AutoExitResult(AutoExitKind Kind, decimal ExitPrice, DateTime? ExitCandleTime,
        decimal? BestPrice, decimal? TrailingStopLoss, DateTime? CheckedUntil);

    /// <summary>
    /// Walks completed 1-minute candles oldest first. Per candle:
    /// 1. Gap at the open - the open is already past the stop or the Target: exit at the open (the real fill).
    /// 2. Inside the candle - the stop is checked BEFORE the Target (a candle's high/low order is unknown, so the
    ///    worse case is assumed); exit at the level itself.
    /// 3. No exit - the best price (long: highest high, short: lowest low) and the trailed stop are updated. The trail
    ///    starts once the best price is Trailing SL % in profit, then sits Trailing SL % behind the best price; it
    ///    only ever tightens. The effective stop is the tighter of the Stop Loss and the trailed stop.
    /// </summary>
    internal static AutoExitResult EvaluateAutoExit(TradeSide side, decimal entryPrice, decimal? stopLoss, decimal? takeProfit,
        decimal? trailingSlPct, decimal? trailingStopLoss, decimal? bestPrice, IEnumerable<ManualPaperCandleDto> candles)
    {
        bool isShort = side == TradeSide.SELL;
        decimal? sl = stopLoss > 0m ? stopLoss : null;
        decimal? tp = takeProfit > 0m ? takeProfit : null;
        decimal pct = Math.Abs(trailingSlPct ?? 0m) / 100m;
        decimal? best = bestPrice;
        decimal? trail = trailingStopLoss > 0m ? trailingStopLoss : null;
        DateTime? checkedUntil = null;

        foreach (var c in candles)
        {
            // The tighter stop: highest for a long, lowest for a short.
            bool trailIsTighter = trail.HasValue && (!sl.HasValue || (isShort ? trail.Value < sl.Value : trail.Value > sl.Value));
            decimal? stop = trailIsTighter ? trail : sl;
            var stopKind = trailIsTighter ? AutoExitKind.TrailingStop : AutoExitKind.StopLoss;

            if (stop.HasValue && (isShort ? c.CandleOpen >= stop.Value : c.CandleOpen <= stop.Value))
                return new(stopKind, c.CandleOpen, c.CandleTime, best, trail, checkedUntil);
            if (tp.HasValue && (isShort ? c.CandleOpen <= tp.Value : c.CandleOpen >= tp.Value))
                return new(AutoExitKind.Target, c.CandleOpen, c.CandleTime, best, trail, checkedUntil);
            if (stop.HasValue && (isShort ? c.CandleHigh >= stop.Value : c.CandleLow <= stop.Value))
                return new(stopKind, stop.Value, c.CandleTime, best, trail, checkedUntil);
            if (tp.HasValue && (isShort ? c.CandleLow <= tp.Value : c.CandleHigh >= tp.Value))
                return new(AutoExitKind.Target, tp.Value, c.CandleTime, best, trail, checkedUntil);

            best = isShort ? Math.Min(best ?? entryPrice, c.CandleLow) : Math.Max(best ?? entryPrice, c.CandleHigh);
            if (pct > 0m && (isShort ? best.Value <= entryPrice * (1m - pct) : best.Value >= entryPrice * (1m + pct)))
            {
                decimal newTrail = Math.Round(isShort ? best.Value * (1m + pct) : best.Value * (1m - pct), 2);
                if (!trail.HasValue || (isShort ? newTrail < trail.Value : newTrail > trail.Value))
                {
                    trail = newTrail;
                }
            }
            checkedUntil = c.CandleTime;
        }

        return new(AutoExitKind.None, 0m, null, best, trail, checkedUntil);
    }

    // Worker pass (ManualAutoExitWorker): checks every OPEN Auto Exit position against the completed stored 1-minute
    // candles it hasn't seen yet (those starting after its entry minute / last checked candle) and closes it at the
    // first Target / Stop Loss / trailed-stop hit; otherwise saves the trail state. Returns how many were closed.
    public async Task<int> ProcessAutoExitsAsync()
    {
        var positions = (await _repository.GetAutoExitPositionsAsync()).ToList();
        if (positions.Count == 0) return 0;

        // Only completed candles: the newest one checked started at least a minute ago, so its high/low are final.
        DateTime lastCompletedStart = FloorToMinute(SwingTradeRules.UtcNow()).AddMinutes(-1);
        int closed = 0;

        foreach (var pos in positions)
        {
            DateTime fromUtc = FloorToMinute(pos.ExitCheckedUntil ?? pos.OpenedAt).AddMinutes(1);
            if (fromUtc > lastCompletedStart) continue;

            try
            {
                var candles = (await _repository.GetCandlesAsync(pos.Symbol, fromUtc, lastCompletedStart)).ToList();
                if (candles.Count == 0) continue;

                var result = EvaluateAutoExit(pos.Side, pos.AverageEntryPrice, pos.StopLoss, pos.TakeProfit,
                    pos.TrailingSlPct, pos.TrailingStopLoss, pos.BestPrice, candles);

                if (result.Kind == AutoExitKind.None)
                {
                    bool activated = !(pos.TrailingStopLoss > 0m) && result.TrailingStopLoss.HasValue;
                    bool saved = await _repository.UpdatePositionTrailAsync(pos.Id, pos.TrailingSlPct, result.BestPrice,
                        result.TrailingStopLoss, result.CheckedUntil!.Value);
                    if (saved && activated)
                    {
                        await LogAuditAsync(pos.Symbol, "TRAILING_SL_ACTIVATED", result.TrailingStopLoss, pos.Quantity,
                            $"Trailing SL active at ₹{result.TrailingStopLoss:F2} ({pos.TrailingSlPct:F2}% behind best price ₹{result.BestPrice:F2})",
                            pos.AccountId);
                    }
                    continue;
                }

                string candleIst = SwingTradeRules.ToIst(result.ExitCandleTime!.Value).ToString("HH:mm");
                var (reason, actionType) = result.Kind switch
                {
                    AutoExitKind.Target => ($"Target Hit (Auto Exit, {candleIst} candle)", "AUTO_TARGET"),
                    AutoExitKind.TrailingStop => ($"Trailing SL Hit (Auto Exit, {candleIst} candle)", "AUTO_TSL"),
                    _ => ($"Stop Loss Hit (Auto Exit, {candleIst} candle)", "AUTO_SL")
                };

                var position = new PaperPosition
                {
                    Id = pos.Id, AccountId = pos.AccountId, Symbol = pos.Symbol, Side = pos.Side,
                    Quantity = pos.Quantity, AverageEntryPrice = pos.AverageEntryPrice
                };
                var (success, _) = await ClosePositionCoreAsync(position, pos.AccountId, Math.Round(result.ExitPrice, 2), reason, actionType);
                if (success) closed++;
            }
            catch (Exception ex)
            {
                // Left OPEN and unchanged - the next pass re-checks the same candles.
                _logger.LogError(ex, "Manual auto exit check failed for position #{PositionId} ({Symbol})", pos.Id, pos.Symbol);
            }
        }
        return closed;
    }

    private static DateTime FloorToMinute(DateTime time)
    {
        DateTime utc = time.Kind == DateTimeKind.Utc ? time : DateTime.SpecifyKind(time.ToUniversalTime(), DateTimeKind.Utc);
        return new DateTime(utc.Ticks - utc.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc);
    }

    // ---------------------------------------------------------------------------------------------
    // Close button (SELL a long / BUY TO COVER a short), short auto square-off, Auto Exit and Edit levels -
    // the only ways a manual position changes.
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
        string exitReason, string? logActionType = null)
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
            string actionType = logActionType
                ?? (exitReason == AutoSquareOffReason ? "AUTO_SQUARE_OFF" : (isShort ? "MANUAL_COVER" : "MANUAL_SELL"));
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
        decimal takeProfit, int userId = 1, bool? autoExit = null)
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

        bool updated = await _repository.UpdatePositionLevelsAsync(userId, positionId, stopLoss, trailingSlPct, takeProfit, autoExit);
        if (!updated)
        {
            return (false, $"{position.Symbol}: position could not be updated (it may already be closed).");
        }

        string message = $"Levels updated - SL: ₹{position.StopLoss:F2} → ₹{stopLoss:F2}, " +
                         $"Trailing SL: {position.TrailingSlPct:F2}% → {trailingSlPct:F2}%, " +
                         $"Target: ₹{position.TakeProfit:F2} → ₹{takeProfit:F2}";
        if (autoExit.HasValue && autoExit.Value != position.AutoExit)
        {
            message += autoExit.Value ? ", Auto Exit ON" : ", Auto Exit OFF (levels are reference only)";
        }
        await LogAuditAsync(position.Symbol, "LEVELS_UPDATED", null, position.Quantity, message, userId);
        return (true, $"{position.Symbol}: {message}");
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------

    private static TimeSpan ParseIstTime(string? hhmm, TimeSpan fallback) =>
        TimeSpan.TryParse(hhmm, out var time) ? time : fallback;

    private static string AutoExitText(bool autoExit) =>
        autoExit ? " - Auto Exit ON: closes automatically at Target / Stop Loss / Trailing SL" : string.Empty;

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
