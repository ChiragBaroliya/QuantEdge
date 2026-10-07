using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using QuantEdge.Infrastructure.DTOs;
using QuantEdge.Infrastructure.Persistence.Repositories;

namespace QuantEdge.Infrastructure.Services;

/// <summary>
/// Keeps the latest tick per symbol in memory and flushes them to live_quotes every FlushInterval, so the
/// API (a different process from the market-data feed) can show the live price and the previous close.
/// One batched upsert per interval instead of one write per tick.
/// </summary>
public sealed class LiveQuoteRecorder : IDisposable
{
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(2);

    private readonly ILiveQuoteRepository _repository;
    private readonly ILogger<LiveQuoteRecorder> _logger;
    private readonly ConcurrentDictionary<string, LiveQuote> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly Timer _timer;
    private int _flushing;

    public LiveQuoteRecorder(ILiveQuoteRepository repository, ILogger<LiveQuoteRecorder> logger)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timer = new Timer(_ => _ = FlushAsync(), null, FlushInterval, FlushInterval);
    }

    public void Record(TickDataDto tick)
    {
        if (tick == null || string.IsNullOrWhiteSpace(tick.Symbol) || tick.LTP <= 0m) return;

        // Receive time, not tick.Timestamp: quote-mode ticks carry no exchange timestamp and the Kite
        // library's DateTime kind is not reliable across modes.
        _pending[tick.Symbol.ToUpperInvariant()] = new LiveQuote(
            tick.Symbol.ToUpperInvariant(), tick.LTP, tick.PrevClose, tick.DayOpen, tick.DayHigh, tick.DayLow, DateTime.UtcNow);
    }

    private async Task FlushAsync()
    {
        if (_pending.IsEmpty || Interlocked.Exchange(ref _flushing, 1) == 1) return;
        try
        {
            var batch = _pending.Keys.ToList()
                .Select(k => _pending.TryRemove(k, out var q) ? q : null)
                .Where(q => q != null)
                .Select(q => q!)
                .ToList();
            await _repository.UpsertBatchAsync(batch);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LiveQuoteRecorder: failed to flush live quotes.");
        }
        finally
        {
            Interlocked.Exchange(ref _flushing, 0);
        }
    }

    public void Dispose() => _timer.Dispose();
}
