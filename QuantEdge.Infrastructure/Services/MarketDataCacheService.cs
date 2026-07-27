using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using QuantEdge.Domain.Entities;
using QuantEdge.Infrastructure.DTOs;
using QuantEdge.Infrastructure.Interfaces;
using QuantEdge.Infrastructure.Persistence.Repositories;

namespace QuantEdge.Infrastructure.Services;

/// <summary>
/// Singleton in-memory market data cache service supporting ultra-fast (< 1ms) candle lookup.
/// Maintains fixed-capacity lists matching Indian Market trading hours (9:00 AM - 3:30 PM).
/// </summary>
public class MarketDataCacheService : IMarketDataCacheService
{
    private readonly ConcurrentDictionary<string, List<MarketCandle>> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, List<MarketIndicator>> _indicatorCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, object> _locks = new(StringComparer.OrdinalIgnoreCase);
    private readonly IMarketCandleRepository _candleRepository;
    private readonly IMarketIndicatorRepository _indicatorRepository;
    private readonly ILogger<MarketDataCacheService> _logger;

    public MarketDataCacheService(
        IMarketCandleRepository candleRepository,
        IMarketIndicatorRepository indicatorRepository,
        ILogger<MarketDataCacheService> logger)
    {
        _candleRepository = candleRepository ?? throw new ArgumentNullException(nameof(candleRepository));
        _indicatorRepository = indicatorRepository ?? throw new ArgumentNullException(nameof(indicatorRepository));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    private static int GetMaxCapacityForTimeframe(string timeframe)
    {
        return timeframe.ToLower() switch
        {
            "1m" => 400,  // Full trading day 375 candles + pre-market & buffer
            "5m" => 80,   // Full trading day 75 candles + buffer
            "15m" => 30,  // Full trading day 25 candles + buffer
            "60m" => 10,  // Full trading day 7 candles + buffer
            "1d" => 365,  // 1 year daily history
            _ => 200
        };
    }

    private static string GetCacheKey(string symbol, string timeframe) => $"{symbol.Trim().ToUpper()}_{timeframe.Trim().ToLower()}";

    /// <summary>
    /// Gets the UTC start timestamp for the current Indian Standard Time (IST) trading day (00:00:00 IST).
    /// </summary>
    public static DateTime GetTodayStartUtc()
    {
        TimeZoneInfo istTz;
        try
        {
            istTz = TimeZoneInfo.FindSystemTimeZoneById("India Standard Time");
        }
        catch (TimeZoneNotFoundException)
        {
            istTz = TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata");
        }

        var istNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, istTz);
        var istTodayStart = new DateTime(istNow.Year, istNow.Month, istNow.Day, 0, 0, 0, DateTimeKind.Unspecified);
        return TimeZoneInfo.ConvertTimeToUtc(istTodayStart, istTz);
    }

    /// <inheritdoc />
    public async Task<List<MarketCandle>> GetRecentCandlesAsync(string symbol, string timeframe, int limit = 200)
    {
        string key = GetCacheKey(symbol, timeframe);

        if (_cache.TryGetValue(key, out var cachedCandles))
        {
            lock (GetLockObject(key))
            {
                return cachedCandles.TakeLast(limit).ToList();
            }
        }

        return await GetTodayCandlesAsync(symbol, timeframe);
    }

    /// <inheritdoc />
    public async Task<List<MarketCandle>> GetTodayCandlesAsync(string symbol, string timeframe)
    {
        string key = GetCacheKey(symbol, timeframe);
        DateTime todayStartUtc = GetTodayStartUtc();
        var lockObj = GetLockObject(key);

        lock (lockObj)
        {
            if (_cache.TryGetValue(key, out var cachedCandles))
            {
                var todayList = cachedCandles.Where(c => c.CandleTime >= todayStartUtc).OrderBy(c => c.CandleTime).ToList();
                if (todayList.Count > 0)
                {
                    return todayList;
                }
            }
        }

        // Cache miss for today: Seed today's candles from DB if available
        _logger.LogDebug("Today memory cache empty for {Key}. Seeding today's candles from DB...", key);
        var dbCandles = await _candleRepository.GetHistoryAsync(symbol, timeframe, limit: 500);
        var todayDbCandles = dbCandles
            .Where(c => c.CandleTime >= todayStartUtc)
            .OrderBy(c => c.CandleTime)
            .ToList();

        lock (lockObj)
        {
            if (!_cache.TryGetValue(key, out var existingList))
            {
                existingList = new List<MarketCandle>();
                _cache[key] = existingList;
            }

            foreach (var candle in todayDbCandles)
            {
                if (!existingList.Any(c => c.CandleTime == candle.CandleTime))
                {
                    existingList.Add(candle);
                }
            }
            existingList.Sort((a, b) => a.CandleTime.CompareTo(b.CandleTime));
            return existingList.Where(c => c.CandleTime >= todayStartUtc).ToList();
        }
    }

    /// <inheritdoc />
    public async Task<List<MarketIndicator>> GetTodayIndicatorsAsync(string symbol, string timeframe)
    {
        string key = GetCacheKey(symbol, timeframe);
        DateTime todayStartUtc = GetTodayStartUtc();
        var lockObj = GetLockObject(key);

        lock (lockObj)
        {
            if (_indicatorCache.TryGetValue(key, out var cachedIndicators))
            {
                var todayList = cachedIndicators.Where(i => i.CandleTime >= todayStartUtc).OrderBy(i => i.CandleTime).ToList();
                if (todayList.Count > 0)
                {
                    return todayList;
                }
            }
        }

        // Cache miss for today: Seed today's indicators from DB
        var dbIndicators = await _indicatorRepository.GetHistoryAsync(symbol, timeframe, limit: 500);
        var todayDbIndicators = dbIndicators
            .Where(i => i.CandleTime >= todayStartUtc)
            .OrderBy(i => i.CandleTime)
            .ToList();

        lock (lockObj)
        {
            if (!_indicatorCache.TryGetValue(key, out var existingList))
            {
                existingList = new List<MarketIndicator>();
                _indicatorCache[key] = existingList;
            }

            foreach (var ind in todayDbIndicators)
            {
                if (!existingList.Any(i => i.CandleTime == ind.CandleTime))
                {
                    existingList.Add(ind);
                }
            }
            existingList.Sort((a, b) => a.CandleTime.CompareTo(b.CandleTime));
            return existingList.Where(i => i.CandleTime >= todayStartUtc).ToList();
        }
    }

    /// <inheritdoc />
    public async Task RefreshTodayCacheFromDbAsync(string symbol, string timeframe)
    {
        string key = GetCacheKey(symbol, timeframe);
        DateTime todayStartUtc = GetTodayStartUtc();

        var dbCandles = (await _candleRepository.GetHistoryAsync(symbol, timeframe, limit: 500))
            .Where(c => c.CandleTime >= todayStartUtc)
            .OrderBy(c => c.CandleTime)
            .ToList();

        var dbIndicators = (await _indicatorRepository.GetHistoryAsync(symbol, timeframe, limit: 500))
            .Where(i => i.CandleTime >= todayStartUtc)
            .OrderBy(i => i.CandleTime)
            .ToList();

        var lockObj = GetLockObject(key);
        lock (lockObj)
        {
            _cache[key] = dbCandles;
            _indicatorCache[key] = dbIndicators;
        }

        _logger.LogInformation("Refreshed today's memory cache from DB for {Key}. Count: {Count} candles.", key, dbCandles.Count);
    }

    /// <inheritdoc />
    public void AddOrUpdateCandle(MarketCandle candle)
    {
        if (candle == null || string.IsNullOrWhiteSpace(candle.Symbol) || string.IsNullOrWhiteSpace(candle.Timeframe))
            return;

        string key = GetCacheKey(candle.Symbol, candle.Timeframe);
        int maxCap = GetMaxCapacityForTimeframe(candle.Timeframe);
        var lockObj = GetLockObject(key);

        lock (lockObj)
        {
            if (!_cache.TryGetValue(key, out var list))
            {
                list = new List<MarketCandle>();
                _cache[key] = list;
            }

            int existingIndex = list.FindIndex(c => c.CandleTime == candle.CandleTime);
            if (existingIndex >= 0)
            {
                list[existingIndex] = candle; // Update existing candle bar
            }
            else
            {
                list.Add(candle); // Append new candle
                list.Sort((a, b) => a.CandleTime.CompareTo(b.CandleTime));

                if (list.Count > maxCap)
                {
                    list.RemoveAt(0); // Evict oldest candle
                }
            }
        }
    }

    /// <inheritdoc />
    public void AddOrUpdateCandleBatch(IEnumerable<MarketCandle> candles)
    {
        if (candles == null) return;
        foreach (var candle in candles)
        {
            AddOrUpdateCandle(candle);
        }
    }

    /// <inheritdoc />
    public async Task<List<MarketIndicator>> GetRecentIndicatorsAsync(string symbol, string timeframe, int limit = 1)
    {
        string key = GetCacheKey(symbol, timeframe);

        if (_indicatorCache.TryGetValue(key, out var cachedIndicators))
        {
            lock (GetLockObject(key))
            {
                return cachedIndicators.TakeLast(limit).ToList();
            }
        }

        return await GetTodayIndicatorsAsync(symbol, timeframe);
    }

    /// <inheritdoc />
    public async Task<MarketIndicator?> GetLatestIndicatorAsync(string symbol, string timeframe)
    {
        var list = await GetRecentIndicatorsAsync(symbol, timeframe, limit: 1);
        return list.Count > 0 ? list[^1] : null;
    }

    /// <inheritdoc />
    public void AddOrUpdateIndicator(MarketIndicator indicator)
    {
        if (indicator == null || string.IsNullOrWhiteSpace(indicator.Symbol) || string.IsNullOrWhiteSpace(indicator.Timeframe))
            return;

        string key = GetCacheKey(indicator.Symbol, indicator.Timeframe);
        int maxCap = GetMaxCapacityForTimeframe(indicator.Timeframe);
        var lockObj = GetLockObject(key);

        lock (lockObj)
        {
            if (!_indicatorCache.TryGetValue(key, out var list))
            {
                list = new List<MarketIndicator>();
                _indicatorCache[key] = list;
            }

            int existingIndex = list.FindIndex(i => i.CandleTime == indicator.CandleTime);
            if (existingIndex >= 0)
            {
                list[existingIndex] = indicator;
            }
            else
            {
                list.Add(indicator);
                list.Sort((a, b) => a.CandleTime.CompareTo(b.CandleTime));

                if (list.Count > maxCap)
                {
                    list.RemoveAt(0);
                }
            }
        }
    }

    /// <inheritdoc />
    public void ClearCache(string? symbol = null, string? timeframe = null)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            _cache.Clear();
            _locks.Clear();
            _logger.LogInformation("MarketDataCache cleared completely.");
        }
        else if (!string.IsNullOrWhiteSpace(timeframe))
        {
            string key = GetCacheKey(symbol, timeframe);
            _cache.TryRemove(key, out _);
        }
        else
        {
            string prefix = $"{symbol.Trim().ToUpper()}_";
            var keysToRemove = _cache.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var k in keysToRemove)
            {
                _cache.TryRemove(k, out _);
            }
        }
    }

    /// <inheritdoc />
    public CacheMemoryMetricsDto GetMemoryMetrics()
    {
        long workingSetBytes = Process.GetCurrentProcess().WorkingSet64;
        long gcHeapBytes = GC.GetTotalMemory(false);

        int totalCandles = 0;
        var timeframeCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["1m"] = 0,
            ["5m"] = 0,
            ["15m"] = 0,
            ["60m"] = 0,
            ["1d"] = 0
        };

        var symbolsSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var kvp in _cache)
        {
            var parts = kvp.Key.Split('_');
            if (parts.Length == 2)
            {
                symbolsSet.Add(parts[0]);
                string tf = parts[1].ToLower();
                int count = kvp.Value.Count;
                totalCandles += count;

                if (timeframeCounts.ContainsKey(tf))
                {
                    timeframeCounts[tf] += count;
                }
                else
                {
                    timeframeCounts[tf] = count;
                }
            }
        }

        int totalIndicators = 0;
        foreach (var kvp in _indicatorCache)
        {
            var parts = kvp.Key.Split('_');
            if (parts.Length == 2)
            {
                symbolsSet.Add(parts[0]);
                totalIndicators += kvp.Value.Count;
            }
        }

        // Estimate RAM size: MarketCandle ~200 Bytes, MarketIndicator ~150 Bytes
        double estimatedBytes = (totalCandles * 200.0) + (totalIndicators * 150.0);
        double estimatedCacheMB = Math.Round(estimatedBytes / (1024.0 * 1024.0), 3);

        return new CacheMemoryMetricsDto
        {
            ProcessWorkingSetMB = Math.Round(workingSetBytes / (1024.0 * 1024.0), 2),
            GcTotalMemoryMB = Math.Round(gcHeapBytes / (1024.0 * 1024.0), 2),
            Gen0Collections = GC.CollectionCount(0),
            Gen1Collections = GC.CollectionCount(1),
            Gen2Collections = GC.CollectionCount(2),
            TotalCachedSymbols = symbolsSet.Count,
            TotalCachedCandles = totalCandles,
            TotalCachedIndicators = totalIndicators,
            EstimatedCacheMemoryMB = estimatedCacheMB,
            TimeframeCandleCounts = timeframeCounts
        };
    }

    private object GetLockObject(string key)
    {
        return _locks.GetOrAdd(key, _ => new object());
    }
}
