using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using QuantEdge.Domain.Entities;
using QuantEdge.Infrastructure.DTOs;
using QuantEdge.Infrastructure.Interfaces;
using QuantEdge.Infrastructure.Persistence.Repositories;

namespace QuantEdge.Infrastructure.Services;

/// <summary>
/// Builds the header bell's "today only" feed from data the app already records - no notification
/// table. Everything is scoped to the IST calendar day, so the feed empties itself at midnight IST.
/// </summary>
public class NotificationService : INotificationService
{
    // Real trade log action types worth a notification. Everything else (REAL_SIGNAL_SKIPPED,
    // LTP_REST_FALLBACK, WS_RESUBSCRIBED, ...) is scan noise that stays in the Real Trading log only.
    private static readonly Dictionary<string, (string Level, string Title)> RealTradeActions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["REAL_BUY"] = ("success", "Buy executed"),
        ["REAL_SELL"] = ("success", "Sell executed"),
        ["BUY_ORDER_OPEN"] = ("info", "Buy order open"),
        ["SELL_ORDER_OPEN"] = ("info", "Sell order open"),
        ["TRAILING_SL_ACTIVATED"] = ("info", "Trailing SL activated"),
        ["HOLDING_MONITOR_ENABLED"] = ("info", "Holding monitored"),
        ["ORDER_REJECTED"] = ("error", "Order rejected"),
        ["SELL_FAILED"] = ("error", "Sell failed"),
        ["SYSTEM_ERROR"] = ("error", "System error"),
        ["LIVE_ENABLE_FAILED"] = ("error", "Auto Real Trade not enabled"),
        ["TOKEN_EXPIRED"] = ("warning", "Zerodha session expired"),
        ["KILL_SWITCH_ACTIVE"] = ("warning", "Kill switch active"),
        ["CIRCUIT_BREAKER"] = ("warning", "Circuit breaker hit"),
    };

    private const int MaxRealTradeLogs = 500;
    private const int MaxSymbolsInSwingMessage = 5;
    private static readonly TimeSpan MarketFeedCacheTtl = TimeSpan.FromSeconds(30);

    private readonly IRealTradingRepository _realTradingRepository;
    private readonly ISwingSlotRecommendationRepository _slotRepository;
    private readonly IMarketCandleRepository _candleRepository;
    private readonly ISwingStrategySettingsRepository _strategySettingsRepository;
    private readonly ILogger<NotificationService> _logger;
    private readonly ICacheService? _cacheService;

    public NotificationService(
        IRealTradingRepository realTradingRepository,
        ISwingSlotRecommendationRepository slotRepository,
        IMarketCandleRepository candleRepository,
        ISwingStrategySettingsRepository strategySettingsRepository,
        ILogger<NotificationService> logger,
        ICacheService? cacheService = null)
    {
        _realTradingRepository = realTradingRepository ?? throw new ArgumentNullException(nameof(realTradingRepository));
        _slotRepository = slotRepository ?? throw new ArgumentNullException(nameof(slotRepository));
        _candleRepository = candleRepository ?? throw new ArgumentNullException(nameof(candleRepository));
        _strategySettingsRepository = strategySettingsRepository ?? throw new ArgumentNullException(nameof(strategySettingsRepository));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _cacheService = cacheService;
    }

    public async Task<TodayNotificationsDto> GetTodayAsync(int userId, CancellationToken cancellationToken = default)
    {
        var istZone = GetIstZone();
        DateTime todayIst = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, istZone).Date;
        DateTime todayStartUtc = TimeZoneInfo.ConvertTimeToUtc(todayIst, istZone);

        var items = new List<NotificationItemDto>();
        items.AddRange(await GetRealTradeItemsAsync(userId, todayStartUtc));
        items.AddRange(await GetMarketItemsAsync(todayIst, istZone, cancellationToken));

        return new TodayNotificationsDto(
            todayIst,
            items.Where(i => i.TimeUtc >= todayStartUtc).OrderByDescending(i => i.TimeUtc).ToList());
    }

    private async Task<IEnumerable<NotificationItemDto>> GetRealTradeItemsAsync(int userId, DateTime todayStartUtc)
    {
        try
        {
            // The repository's "today" starts at UTC midnight, so re-filter to the IST day.
            var logs = await _realTradingRepository.GetTodayLogsAsync(userId, MaxRealTradeLogs);
            return logs
                .Where(l => RealTradeActions.ContainsKey(l.ActionType) && AsUtc(l.ExecutedAt) >= todayStartUtc)
                .Select(l =>
                {
                    var (level, title) = RealTradeActions[l.ActionType];
                    return new NotificationItemDto(
                        Id: $"rt-{l.Id}",
                        Category: "real-trade",
                        Level: level,
                        Title: string.IsNullOrWhiteSpace(l.Symbol) || l.Symbol == "SYSTEM" ? title : $"{title}: {l.Symbol}",
                        Message: l.Reason ?? title,
                        Symbol: l.Symbol,
                        TimeUtc: AsUtc(l.ExecutedAt));
                })
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load real trade notifications for user {UserId}.", userId);
            return Array.Empty<NotificationItemDto>();
        }
    }

    /// <summary>Swing BUY slots + NIFTY flip. Same for every user, so cached briefly across requests.</summary>
    private async Task<IEnumerable<NotificationItemDto>> GetMarketItemsAsync(DateTime todayIst, TimeZoneInfo istZone, CancellationToken cancellationToken)
    {
        string cacheKey = $"notifications_market_{todayIst:yyyyMMdd}";
        if (_cacheService != null)
        {
            var cached = await _cacheService.GetAsync<List<NotificationItemDto>>(cacheKey);
            if (cached != null) return cached;
        }

        var items = new List<NotificationItemDto>();
        items.AddRange(await GetSwingItemsAsync(todayIst, istZone, cancellationToken));

        var niftyItem = await GetNiftyFlipItemAsync(todayIst);
        if (niftyItem != null) items.Add(niftyItem);

        if (_cacheService != null)
        {
            await _cacheService.SetAsync(cacheKey, items, MarketFeedCacheTtl);
        }
        return items;
    }

    private async Task<IEnumerable<NotificationItemDto>> GetSwingItemsAsync(DateTime todayIst, TimeZoneInfo istZone, CancellationToken cancellationToken)
    {
        var items = new List<NotificationItemDto>();
        try
        {
            var slots = await _slotRepository.GetScanSlotsAsync(todayIst, cancellationToken);
            foreach (var slot in slots.Where(s => s.BuyCount > 0))
            {
                var recs = await _slotRepository.GetSlotRecommendationsAsync(todayIst, slot.SlotLabel, cancellationToken);
                var buySymbols = recs
                    .Where(r => r.Decision.Equals("BUY", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(r => r.Score)
                    .Select(r => r.Symbol)
                    .ToList();

                string symbolList = string.Join(", ", buySymbols.Take(MaxSymbolsInSwingMessage));
                if (buySymbols.Count > MaxSymbolsInSwingMessage) symbolList += $" +{buySymbols.Count - MaxSymbolsInSwingMessage} more";

                items.Add(new NotificationItemDto(
                    Id: $"swing-{todayIst:yyyyMMdd}-{slot.SlotLabel}",
                    Category: "swing",
                    Level: "success",
                    Title: $"Swing scan {slot.SlotLabel}: {slot.BuyCount} BUY",
                    Message: buySymbols.Count > 0 ? symbolList : $"{slot.BuyCount} BUY recommendation(s) in this slot.",
                    Symbol: null,
                    TimeUtc: SlotTimeToUtc(slot.SlotTime, istZone)));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load swing scan notifications for {Date:yyyy-MM-dd}.", todayIst);
        }
        return items;
    }

    /// <summary>
    /// A flip is when today's daily NIFTY candle changes the market filter result compared with the
    /// previous session. The daily candle is refreshed intraday by the 30-min swing job, so a flip can
    /// appear (and revert) during the day; each direction gets its own id.
    /// </summary>
    private async Task<NotificationItemDto?> GetNiftyFlipItemAsync(DateTime todayIst)
    {
        try
        {
            var candles = (await _candleRepository.GetHistoryAsync("NIFTY 50", "1d", limit: 100))
                .OrderBy(c => c.CandleTime)
                .ToList();
            if (candles.Count < 51 || candles[^1].CandleTime.Date != todayIst) return null;

            var settings = await _strategySettingsRepository.GetSettingsAsync() ?? SwingStrategySettings.Default;
            bool passedNow = SwingDecisionEngine.IsNiftyMarketFilterPassed(candles, settings);
            bool passedBefore = SwingDecisionEngine.IsNiftyMarketFilterPassed(candles.Take(candles.Count - 1).ToList(), settings);
            if (passedNow == passedBefore) return null;

            string id = $"nifty-{todayIst:yyyyMMdd}-{(passedNow ? "passed" : "failed")}";
            string effect = settings.RequireNiftyMarketFilter
                ? (passedNow ? "The bots can buy again." : "The bots will buy nothing until it recovers. Exits on open positions keep running.")
                : (passedNow ? "The score penalty no longer applies." : "Soft mode: scores get a penalty and positions are sized down.");

            return new NotificationItemDto(
                Id: id,
                Category: "nifty",
                Level: passedNow ? "success" : "warning",
                Title: passedNow ? "NIFTY market filter passed" : "NIFTY market filter failed",
                Message: (passedNow
                    ? "NIFTY 50 is back above its 50 DMA with EMA20 above EMA50. "
                    : "NIFTY 50 fell below its 50 DMA or its EMA20 dropped under EMA50. ") + effect,
                Symbol: "NIFTY 50",
                TimeUtc: await GetFirstSeenUtcAsync(id));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to evaluate NIFTY market filter flip notification.");
            return null;
        }
    }

    // Daily candles carry no intraday timestamp, so a flip is stamped with the time it was first seen.
    private async Task<DateTime> GetFirstSeenUtcAsync(string id)
    {
        if (_cacheService == null) return DateTime.UtcNow;

        string key = $"notifications_first_seen_{id}";
        var seen = await _cacheService.GetAsync<DateTime?>(key);
        if (seen.HasValue) return seen.Value;

        var now = DateTime.UtcNow;
        await _cacheService.SetAsync<DateTime?>(key, now, TimeSpan.FromHours(24));
        return now;
    }

    private static DateTime SlotTimeToUtc(DateTime slotTime, TimeZoneInfo istZone) =>
        slotTime.Kind == DateTimeKind.Unspecified
            ? TimeZoneInfo.ConvertTimeToUtc(slotTime, istZone)
            : slotTime.ToUniversalTime();

    private static DateTime AsUtc(DateTime value) =>
        value.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(value, DateTimeKind.Utc) : value.ToUniversalTime();

    private static TimeZoneInfo GetIstZone()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("India Standard Time"); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata"); }
    }
}
