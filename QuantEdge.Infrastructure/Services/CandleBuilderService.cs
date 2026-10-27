using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using QuantEdge.Infrastructure.Configurations;
using QuantEdge.Infrastructure.DTOs;
using QuantEdge.Infrastructure.Interfaces;

namespace QuantEdge.Infrastructure.Services;

/// <summary>
/// Thread-safe implementation of ICandleBuilderService that aggregates raw ticks into OHLCV candles
/// across multiple timeframes with fine-grained locking per asset/timeframe state.
/// </summary>
public class CandleBuilderService : ICandleBuilderService
{
    public event Func<CandleDto, Task>? OnCandleClosed;
    public event Func<CandleDto, Task>? OnCandleUpdated;

    private readonly BrokerConfig _config;
    private readonly ConcurrentDictionary<(string Symbol, TimeSpan Interval), CandleBuilderState> _activeStates = new();
    private readonly ConcurrentDictionary<(string Symbol, TimeSpan Interval), List<CandleDto>> _history = new();
    private readonly object _historyLock = new();
    private const int MaxHistorySize = 1000;

    public CandleBuilderService(IOptions<BrokerConfig> config)
    {
        _config = config?.Value ?? throw new ArgumentNullException(nameof(config));
    }

    /// <summary>
    /// Processes a new market tick and aggregates it into current active candles dynamically based on configuration.
    /// </summary>
    public async Task ProcessTickAsync(TickDataDto tick)
    {
        var intervals = _config.Timeframes.Select(ParseTimeframe).ToArray();

        foreach (var interval in intervals)
        {
            await ProcessTickForIntervalAsync(tick, interval);
        }
    }

    private static TimeSpan ParseTimeframe(string timeframe)
    {
        return timeframe.ToLower() switch
        {
            "1s" => TimeSpan.FromSeconds(1),
            "5s" => TimeSpan.FromSeconds(5),
            "1m" => TimeSpan.FromMinutes(1),
            "3m" => TimeSpan.FromMinutes(3),
            "5m" => TimeSpan.FromMinutes(5),
            "15m" => TimeSpan.FromMinutes(15),
            "30m" => TimeSpan.FromMinutes(30),
            "60m" => TimeSpan.FromMinutes(60),
            "1d" => TimeSpan.FromDays(1),
            _ => TimeSpan.FromMinutes(1)
        };
    }

    private async Task ProcessTickForIntervalAsync(TickDataDto tick, TimeSpan interval)
    {
        var key = (tick.Symbol, interval);
        var state = _activeStates.GetOrAdd(key, _ => new CandleBuilderState(tick.Symbol, interval));

        CandleDto? closedCandle = null;
        CandleDto updatedCandle;

        // Fine-grained lock per symbol & timeframe state to ensure high performance and absolute thread safety
        lock (state)
        {
            var roundedTimestamp = GetIntervalStart(tick.Timestamp, interval);

            if (state.CurrentCandle == null && state.LastClosedStart.HasValue && roundedTimestamp <= state.LastClosedStart.Value)
            {
                // Late tick for a period CloseCompletedCandlesAsync already closed and saved - starting a candle
                // from it would later overwrite the saved row with a one-tick candle.
                return;
            }

            if (state.CurrentCandle == null)
            {
                // First tick for this asset & timeframe (or the first one after a period was closed by time)
                state.CurrentCandle = new CandleDto(
                    tick.Symbol,
                    interval,
                    tick.LTP,
                    tick.LTP,
                    tick.LTP,
                    tick.LTP,
                    tick.Volume,
                    roundedTimestamp,
                    IsClosed: false
                );
                updatedCandle = state.CurrentCandle;
            }
            else if (roundedTimestamp > state.CurrentCandle.Timestamp)
            {
                // The tick belongs to a new candle period. Close the old one and initialize a new one.
                closedCandle = state.CurrentCandle with { IsClosed = true };
                
                // Start a new candle
                state.CurrentCandle = new CandleDto(
                    tick.Symbol,
                    interval,
                    tick.LTP,
                    tick.LTP,
                    tick.LTP,
                    tick.LTP,
                    tick.Volume,
                    roundedTimestamp,
                    IsClosed: false
                );
                updatedCandle = state.CurrentCandle;
            }
            else
            {
                // Update the current active candle
                state.CurrentCandle = state.CurrentCandle with
                {
                    High = Math.Max(state.CurrentCandle.High, tick.LTP),
                    Low = Math.Min(state.CurrentCandle.Low, tick.LTP),
                    Close = tick.LTP,
                    Volume = state.CurrentCandle.Volume + tick.Volume
                };
                updatedCandle = state.CurrentCandle;
            }
        }

        // If a candle closed, record it in history and notify subscribers outside of the state lock to avoid deadlocks
        if (closedCandle != null)
        {
            await PublishClosedCandleAsync(key, closedCandle);
        }

        // Notify subscribers about the active candle tick updates
        if (OnCandleUpdated != null)
        {
            await OnCandleUpdated.Invoke(updatedCandle);
        }
    }

    /// <inheritdoc />
    public async Task CloseCompletedCandlesAsync(DateTime nowUtc)
    {
        var closed = new List<((string Symbol, TimeSpan Interval) Key, CandleDto Candle)>();

        foreach (var (key, state) in _activeStates)
        {
            lock (state)
            {
                var current = state.CurrentCandle;
                if (current == null || GetPeriodEnd(current.Timestamp, state.Interval) > nowUtc) continue;

                closed.Add((key, current with { IsClosed = true }));
                state.LastClosedStart = current.Timestamp;
                state.CurrentCandle = null;   // the next period's first tick starts a fresh candle
            }
        }

        foreach (var (key, candle) in closed)
        {
            await PublishClosedCandleAsync(key, candle);
        }
    }

    private async Task PublishClosedCandleAsync((string Symbol, TimeSpan Interval) key, CandleDto closedCandle)
    {
        lock (_historyLock)
        {
            var historyList = _history.GetOrAdd(key, _ => new List<CandleDto>());
            historyList.Add(closedCandle);
            if (historyList.Count > MaxHistorySize)
            {
                historyList.RemoveAt(0); // Maintain a fixed-size buffer
            }
        }

        if (OnCandleClosed != null)
        {
            await OnCandleClosed.Invoke(closedCandle);
        }
    }

    private static readonly TimeSpan SessionCloseIst = new(15, 30, 0);

    /// <summary>
    /// End of the candle period starting at <paramref name="startUtc"/>, capped at that day's 15:30 IST session close:
    /// a daily candle ends at 15:30 (not midnight) and the last 60m bar (15:15) ends at 15:30 too. Returned as UTC.
    /// </summary>
    public static DateTime GetPeriodEnd(DateTime startUtc, TimeSpan interval)
    {
        var utc = startUtc.Kind == DateTimeKind.Utc ? startUtc : DateTime.SpecifyKind(startUtc, DateTimeKind.Utc);
        var startIst = TimeZoneInfo.ConvertTimeFromUtc(utc, Helpers.TimeZoneHelper.IndianTimeZone);
        DateTime sessionCloseIst = startIst.Date + SessionCloseIst;

        DateTime endIst = startIst + interval;
        if (startIst < sessionCloseIst && endIst > sessionCloseIst) endIst = sessionCloseIst;

        return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(endIst, DateTimeKind.Unspecified), Helpers.TimeZoneHelper.IndianTimeZone);
    }

    /// <summary>
    /// Retrieves a thread-safe snapshot of candle history for a given symbol and interval.
    /// </summary>
    public IReadOnlyList<CandleDto> GetHistory(string symbol, TimeSpan interval)
    {
        lock (_historyLock)
        {
            if (_history.TryGetValue((symbol, interval), out var historyList))
            {
                return historyList.ToList(); // Return a copy of the list for safe external iteration
            }
            return Array.Empty<CandleDto>();
        }
    }

    private static readonly TimeSpan SessionOpenIst = new(9, 15, 0);

    /// <summary>
    /// Candle start for a tick, matching NSE / Kite / TradingView (Plan L.1 P8): buckets are counted in IST from the
    /// 09:15 session open (60m = 09:15, 10:15, ...; 30m = 09:15, 09:45, ...), and a daily candle starts at 00:00 IST -
    /// the same timestamp Kite's historical API uses, so live and synced candles land on the same row. The old version
    /// floored the raw UTC ticks, which put hourly candles at 09:30/10:30 IST and daily candles at 05:30 IST.
    /// Returned as UTC.
    /// </summary>
    public static DateTime GetIntervalStart(DateTime dateTime, TimeSpan interval)
    {
        var utc = dateTime.Kind switch
        {
            DateTimeKind.Utc => dateTime,
            DateTimeKind.Local => dateTime.ToUniversalTime(),
            _ => DateTime.SpecifyKind(dateTime, DateTimeKind.Utc)
        };

        // Sub-minute buckets (1s / 5s) don't need session alignment.
        if (interval < TimeSpan.FromMinutes(1))
        {
            return new DateTime(utc.Ticks / interval.Ticks * interval.Ticks, DateTimeKind.Utc);
        }

        var ist = TimeZoneInfo.ConvertTimeFromUtc(utc, Helpers.TimeZoneHelper.IndianTimeZone);
        DateTime startIst;
        if (interval >= TimeSpan.FromDays(1))
        {
            startIst = ist.Date;
        }
        else
        {
            DateTime sessionOpen = ist.Date + SessionOpenIst;
            if (ist < sessionOpen)
            {
                // Pre-open ticks (normally filtered out upstream): plain clock alignment.
                startIst = ist.Date + TimeSpan.FromTicks((ist.TimeOfDay.Ticks / interval.Ticks) * interval.Ticks);
            }
            else
            {
                long buckets = (ist - sessionOpen).Ticks / interval.Ticks;
                startIst = sessionOpen + TimeSpan.FromTicks(buckets * interval.Ticks);
            }
        }

        return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(startIst, DateTimeKind.Unspecified), Helpers.TimeZoneHelper.IndianTimeZone);
    }

    private class CandleBuilderState
    {
        public string Symbol { get; }
        public TimeSpan Interval { get; }
        public CandleDto? CurrentCandle { get; set; }
        /// <summary>Start of the last candle closed by time; ticks for that period or earlier are ignored.</summary>
        public DateTime? LastClosedStart { get; set; }

        public CandleBuilderState(string symbol, TimeSpan interval)
        {
            Symbol = symbol;
            Interval = interval;
        }
    }
}
