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
    private const int MaxBrokerEvents = 200;
    private const int MaxSymbolsInSwingMessage = 5;

    // View All Notifications page: at most this many IST days per request, and this many rows per source.
    public const int MaxHistoryDays = 31;
    private const int MaxHistoryRowsPerSource = 5000;
    private static readonly TimeSpan MarketFeedCacheTtl = TimeSpan.FromSeconds(30);

    private readonly IRealTradingRepository _realTradingRepository;
    private readonly ISwingSlotRecommendationRepository _slotRepository;
    private readonly IMarketCandleRepository _candleRepository;
    private readonly ISwingStrategySettingsRepository _strategySettingsRepository;
    private readonly ILogger<NotificationService> _logger;
    private readonly ICacheService? _cacheService;
    private readonly IBrokerApiEventRepository? _brokerApiEventRepository;

    public NotificationService(
        IRealTradingRepository realTradingRepository,
        ISwingSlotRecommendationRepository slotRepository,
        IMarketCandleRepository candleRepository,
        ISwingStrategySettingsRepository strategySettingsRepository,
        ILogger<NotificationService> logger,
        ICacheService? cacheService = null,
        IBrokerApiEventRepository? brokerApiEventRepository = null)
    {
        _brokerApiEventRepository = brokerApiEventRepository;
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
        items.AddRange((await GetRealTradeItemsAsync(userId, todayStartUtc, MaxRealTradeLogs)).Items);
        items.AddRange(await GetMarketItemsAsync(todayIst, istZone, cancellationToken));
        items.AddRange((await GetBrokerApiItemsAsync(userId, todayStartUtc, MaxBrokerEvents)).Items);

        return new TodayNotificationsDto(
            todayIst,
            items.Where(i => i.TimeUtc >= todayStartUtc).OrderByDescending(i => i.TimeUtc).ToList());
    }

    public async Task<NotificationHistoryDto> GetHistoryAsync(int userId, DateTime fromDateIst, DateTime toDateIst,
        CancellationToken cancellationToken = default)
    {
        var istZone = GetIstZone();
        DateTime todayIst = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, istZone).Date;

        // Normalise the range: whole IST days, oldest first, never past today, at most MaxHistoryDays.
        DateTime from = fromDateIst.Date, to = toDateIst.Date;
        if (from > to) (from, to) = (to, from);
        if (to > todayIst) to = todayIst;
        if (from > to) from = to;
        if ((to - from).TotalDays >= MaxHistoryDays) from = to.AddDays(-(MaxHistoryDays - 1));

        DateTime fromUtc = TimeZoneInfo.ConvertTimeToUtc(from, istZone);
        DateTime toUtcExclusive = TimeZoneInfo.ConvertTimeToUtc(to.AddDays(1), istZone);

        var items = new List<NotificationItemDto>();
        var realTrade = await GetRealTradeItemsAsync(userId, fromUtc, MaxHistoryRowsPerSource);
        var broker = await GetBrokerApiItemsAsync(userId, fromUtc, MaxHistoryRowsPerSource);
        items.AddRange(realTrade.Items);
        items.AddRange(broker.Items);
        for (DateTime day = from; day <= to; day = day.AddDays(1))
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Today's swing/NIFTY items come through the bell's cached path, so both screens show the same entries.
            items.AddRange(day == todayIst
                ? await GetMarketItemsAsync(day, istZone, cancellationToken)
                : await GetSwingItemsAsync(day, istZone, cancellationToken));
        }
        items.AddRange(await GetPastNiftyFlipItemsAsync(from, to < todayIst ? to : todayIst.AddDays(-1), istZone));

        return new NotificationHistoryDto(
            from,
            to,
            items.Where(i => i.TimeUtc >= fromUtc && i.TimeUtc < toUtcExclusive).OrderByDescending(i => i.TimeUtc).ToList(),
            realTrade.Truncated || broker.Truncated);
    }

    private async Task<(IEnumerable<NotificationItemDto> Items, bool Truncated)> GetRealTradeItemsAsync(int userId, DateTime sinceUtc, int limit)
    {
        try
        {
            var logs = (await _realTradingRepository.GetLogsSinceAsync(userId, sinceUtc, limit)).ToList();
            var items = logs
                .Where(l => RealTradeActions.ContainsKey(l.ActionType) && AsUtc(l.ExecutedAt) >= sinceUtc)
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
            return (items, logs.Count >= limit);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load real trade notifications for user {UserId}.", userId);
            return (Array.Empty<NotificationItemDto>(), false);
        }
    }

    /// <summary>
    /// Failed, rate-limited or skipped Zerodha calls from any process (broker_api_events). Events without a user
    /// (historical sync, live feed) are shown to everyone; user-specific ones only to that user.
    /// </summary>
    private async Task<(IEnumerable<NotificationItemDto> Items, bool Truncated)> GetBrokerApiItemsAsync(int userId, DateTime sinceUtc, int limit)
    {
        if (_brokerApiEventRepository == null) return (Array.Empty<NotificationItemDto>(), false);
        try
        {
            var events = await _brokerApiEventRepository.GetSinceAsync(sinceUtc, limit);
            var items = events
                .Where(e => e.UserId == null || e.UserId == userId)
                .Select(e =>
                {
                    string title = e.HttpStatus == 429
                        ? "Zerodha rate limit hit"
                        : e.Source switch
                        {
                            BrokerApiSource.Session => "Zerodha call skipped",
                            BrokerApiSource.WebSocket => "Zerodha live feed problem",
                            BrokerApiSource.Historical => "Zerodha candle sync failed",
                            BrokerApiSource.Job => "Zerodha job failed",
                            BrokerApiSource.Nse => "NSE data download problem",
                            _ => "Zerodha API call failed"
                        };
                    if (!string.IsNullOrWhiteSpace(e.Symbol)) title += $": {e.Symbol}";
                    string repeats = e.RepeatCount > 1 ? $" (+{e.RepeatCount - 1} more like this in the previous 5 min)" : string.Empty;
                    return new NotificationItemDto(
                        Id: $"zapi-{e.Id}",
                        Category: "zerodha",
                        Level: e.Level,
                        Title: title,
                        Message: $"{e.Operation} — {e.Message}{repeats}",
                        Symbol: e.Symbol,
                        TimeUtc: AsUtc(e.OccurredAt));
                })
                .ToList();
            return (items, events.Count >= limit);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load Zerodha API notifications (apply broker_api_events in schema.sql).");
            return (Array.Empty<NotificationItemDto>(), false);
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
            var candles = (await _candleRepository.GetHistoryAsync("NIFTY 50", "1d", limit: QuantEdge.Infrastructure.Constants.RealTradeSchedule.DailyCandleHistoryCount))
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

    /// <summary>
    /// NIFTY market filter flips on finished past days (<paramref name="fromIst"/>..<paramref name="toIst"/>), for the
    /// history page - same rule and texts as <see cref="GetNiftyFlipItemAsync"/>, stamped at that day's close (15:30 IST).
    /// </summary>
    private async Task<IEnumerable<NotificationItemDto>> GetPastNiftyFlipItemsAsync(DateTime fromIst, DateTime toIst, TimeZoneInfo istZone)
    {
        var items = new List<NotificationItemDto>();
        if (toIst < fromIst) return items;
        try
        {
            int limit = QuantEdge.Infrastructure.Constants.RealTradeSchedule.DailyCandleHistoryCount + MaxHistoryDays + 5;
            var candles = (await _candleRepository.GetHistoryAsync("NIFTY 50", "1d", limit: limit))
                .OrderBy(c => c.CandleTime)
                .ToList();
            var settings = await _strategySettingsRepository.GetSettingsAsync() ?? SwingStrategySettings.Default;

            for (int i = 51; i < candles.Count; i++)
            {
                DateTime day = candles[i].CandleTime.Date;
                if (day < fromIst || day > toIst) continue;

                bool passedNow = SwingDecisionEngine.IsNiftyMarketFilterPassed(candles.Take(i + 1).ToList(), settings);
                bool passedBefore = SwingDecisionEngine.IsNiftyMarketFilterPassed(candles.Take(i).ToList(), settings);
                if (passedNow == passedBefore) continue;

                items.Add(new NotificationItemDto(
                    Id: $"nifty-{day:yyyyMMdd}-{(passedNow ? "passed" : "failed")}",
                    Category: "nifty",
                    Level: passedNow ? "success" : "warning",
                    Title: passedNow ? "NIFTY market filter passed" : "NIFTY market filter failed",
                    Message: passedNow
                        ? "NIFTY 50 closed back above its 50 DMA with EMA20 above EMA50."
                        : "NIFTY 50 closed below its 50 DMA or its EMA20 dropped under EMA50.",
                    Symbol: "NIFTY 50",
                    TimeUtc: TimeZoneInfo.ConvertTimeToUtc(day.Add(new TimeSpan(15, 30, 0)), istZone)));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to evaluate past NIFTY market filter flips for {From:yyyy-MM-dd}..{To:yyyy-MM-dd}.", fromIst, toIst);
        }
        return items;
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
