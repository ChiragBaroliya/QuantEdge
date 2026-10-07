using System;
using System.Linq;
using System.Threading.Tasks;
using QuantEdge.Infrastructure.DTOs;
using QuantEdge.Infrastructure.Persistence.Repositories;

namespace QuantEdge.Infrastructure.Services;

/// <summary>Loads what <see cref="DayChangeCalculator"/> needs for one symbol (live quote + recent candles).</summary>
public interface IDayQuoteService
{
    Task<DayQuoteDto?> GetAsync(string symbol);
}

public class DayQuoteService : IDayQuoteService
{
    private readonly ILiveQuoteRepository _liveQuoteRepository;
    private readonly IMarketCandleRepository _candleRepository;

    public DayQuoteService(ILiveQuoteRepository liveQuoteRepository, IMarketCandleRepository candleRepository)
    {
        _liveQuoteRepository = liveQuoteRepository ?? throw new ArgumentNullException(nameof(liveQuoteRepository));
        _candleRepository = candleRepository ?? throw new ArgumentNullException(nameof(candleRepository));
    }

    public async Task<DayQuoteDto?> GetAsync(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol)) return null;
        symbol = symbol.Trim().ToUpperInvariant();

        var liveTask = _liveQuoteRepository.GetAsync(new[] { symbol });
        var dailyTask = _candleRepository.GetHistoryAsync(symbol, "1d", 10);
        var m1Task = _candleRepository.GetHistoryAsync(symbol, "1m", 1);
        var m15Task = _candleRepository.GetHistoryAsync(symbol, "15m", 1);
        await Task.WhenAll(liveTask, dailyTask, m1Task, m15Task);

        var daily = dailyTask.Result.OrderBy(c => c.CandleTime).ToList();
        // Whichever intraday timeframe has the newer candle (1m is live-built, 15m is synced from Kite).
        var intraday = m1Task.Result.Concat(m15Task.Result)
            .OrderBy(c => c.CandleTime.Add(c.Timeframe == "15m" ? TimeSpan.FromMinutes(15) : TimeSpan.FromMinutes(1)))
            .TakeLast(1)
            .ToList();

        return DayChangeCalculator.Resolve(symbol, liveTask.Result.TryGetValue(symbol, out var q) ? q : null, daily, intraday);
    }
}
