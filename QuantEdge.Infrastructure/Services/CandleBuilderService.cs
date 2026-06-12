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

            if (state.CurrentCandle == null)
            {
                // First tick for this asset & timeframe
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

        // Notify subscribers about the active candle tick updates
        if (OnCandleUpdated != null)
        {
            await OnCandleUpdated.Invoke(updatedCandle);
        }
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

    private static DateTime GetIntervalStart(DateTime dateTime, TimeSpan interval)
    {
        var ticks = dateTime.Ticks / interval.Ticks;
        return new DateTime(ticks * interval.Ticks, dateTime.Kind);
    }

    private class CandleBuilderState
    {
        public string Symbol { get; }
        public TimeSpan Interval { get; }
        public CandleDto? CurrentCandle { get; set; }

        public CandleBuilderState(string symbol, TimeSpan interval)
        {
            Symbol = symbol;
            Interval = interval;
        }
    }
}
