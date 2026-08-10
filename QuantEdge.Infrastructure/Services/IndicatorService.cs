using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using QuantEdge.Domain.Entities;
using QuantEdge.Infrastructure.Interfaces;
using QuantEdge.Infrastructure.Persistence.Repositories;

namespace QuantEdge.Infrastructure.Services;

/// <summary>
/// Service responsible for fetching candles, calculating technical indicators, 
/// and persisting them in PostgreSQL.
/// </summary>
public class IndicatorService : IIndicatorService
{
    private readonly IMarketCandleRepository _candleRepository;
    private readonly IMarketIndicatorRepository _indicatorRepository;
    private readonly IMarketDataCacheService _cacheService;
    private readonly ILogger<IndicatorService> _logger;

    public IndicatorService(
        IMarketCandleRepository candleRepository,
        IMarketIndicatorRepository indicatorRepository,
        IMarketDataCacheService cacheService,
        ILogger<IndicatorService> logger)
    {
        _candleRepository = candleRepository ?? throw new ArgumentNullException(nameof(candleRepository));
        _indicatorRepository = indicatorRepository ?? throw new ArgumentNullException(nameof(indicatorRepository));
        _cacheService = cacheService ?? throw new ArgumentNullException(nameof(cacheService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task CalculateAndSaveLatestIndicatorAsync(string symbol, string timeframe)
    {
        _logger.LogInformation("Calculating latest indicator for {Symbol} ({Timeframe})...", symbol, timeframe);

        try
        {
            // Fetch recent candles from In-Memory Cache (< 1ms microsecond lookup) with DB fallback
            var recentCandles = await _cacheService.GetRecentCandlesAsync(symbol, timeframe, limit: 200);

            if (recentCandles.Count == 0)
            {
                _logger.LogWarning("No candles found in database for symbol {Symbol} ({Timeframe}). Cannot calculate indicators.", symbol, timeframe);
                return;
            }

            var latestCandle = recentCandles[^1];

            // 1. Calculate indicator lists
            var closes = recentCandles.Select(c => c.Close).ToList();
            var ema20List = IndicatorCalculator.CalculateEma(closes, 20);
            var ema50List = IndicatorCalculator.CalculateEma(closes, 50);
            var rsiList = IndicatorCalculator.CalculateRsi(closes, 14);
            var (macdList, signalList) = IndicatorCalculator.CalculateMacd(closes);

            // 2. Calculate daily VWAP for the latest candle
            // Get all candles on the same local calendar date as the latest candle
            var targetDate = latestCandle.CandleTime.Date;
            var dayCandles = recentCandles.Where(c => c.CandleTime.Date == targetDate).ToList();
            decimal sumPV = dayCandles.Sum(c => c.Close * c.Volume);
            long sumV = dayCandles.Sum(c => c.Volume);
            decimal vwap = sumV > 0 ? sumPV / sumV : latestCandle.Close;

            // 3. Persist indicator values for latest candle
            int lastIndex = recentCandles.Count - 1;
            var indicator = new MarketIndicator
            {
                Id = latestCandle.Id,
                Symbol = symbol.ToUpper(),
                Timeframe = timeframe,
                EMA20 = ema20List[lastIndex],
                EMA50 = ema50List[lastIndex],
                RSI = rsiList[lastIndex],
                MACD = macdList[lastIndex],
                SignalLine = signalList[lastIndex],
                VWAP = vwap,
                CandleTime = latestCandle.CandleTime.ToUniversalTime(),
                CreatedAt = DateTime.UtcNow
            };

            // Update RAM Cache instantly (< 1ms)
            _cacheService.AddOrUpdateIndicator(indicator);

            await _indicatorRepository.InsertAsync(indicator);
            _logger.LogInformation("Successfully persisted latest indicators for {Symbol} ({Timeframe}) at {Time}.", symbol, timeframe, latestCandle.CandleTime);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred during latest indicator calculation for {Symbol} ({Timeframe}).", symbol, timeframe);
        }
    }

    /// <inheritdoc />
    /// <inheritdoc />
    public async Task BackfillHistoricalIndicatorsAsync(string symbol, string timeframe, DateTime? fromTime = null, DateTime? toTime = null)
    {
        _logger.LogInformation("Executing historical indicators backfill for {Symbol} ({Timeframe}) Range: {Start} to {End}...", 
            symbol, timeframe, fromTime.HasValue ? fromTime.Value.ToString("yyyy-MM-dd HH:mm") : "ALL", toTime.HasValue ? toTime.Value.ToString("yyyy-MM-dd HH:mm") : "ALL");

        try
        {
            List<MarketCandle> historyCandles;

            if (fromTime.HasValue && toTime.HasValue)
            {
                // Fetch up to 200 preceding candles before fromTime to warm up EMA50/RSI/MACD states accurately
                var warmupCandles = (await _candleRepository.GetHistoryAsync(symbol, timeframe, limit: 200, beforeTime: fromTime.Value))
                    .OrderBy(c => c.CandleTime)
                    .ToList();

                // Fetch target range candles
                var rangeCandles = (await _candleRepository.GetHistoryAsync(symbol, timeframe, limit: null))
                    .Where(c => c.CandleTime >= fromTime.Value && c.CandleTime <= toTime.Value)
                    .OrderBy(c => c.CandleTime)
                    .ToList();

                if (!rangeCandles.Any())
                {
                    _logger.LogInformation("No historical candles available in specified date range for indicator backfill for {Symbol} ({Timeframe}).", symbol, timeframe);
                    return;
                }

                // Combine warmup + target range candles ordered ASC by CandleTime
                historyCandles = warmupCandles.Concat(rangeCandles)
                    .GroupBy(c => c.Id)
                    .Select(g => g.First())
                    .OrderBy(c => c.CandleTime)
                    .ToList();
            }
            else
            {
                // Fetch ALL historical candles for this symbol & timeframe (no limit) and order by time ASC
                historyCandles = (await _candleRepository.GetHistoryAsync(symbol, timeframe, limit: null))
                    .OrderBy(c => c.CandleTime)
                    .ToList();
            }

            if (historyCandles.Count == 0)
            {
                _logger.LogInformation("No historical candles available for indicator backfill for {Symbol} ({Timeframe}).", symbol, timeframe);
                return;
            }

            var closes = historyCandles.Select(c => c.Close).ToList();
            var ema20List = IndicatorCalculator.CalculateEma(closes, 20);
            var ema50List = IndicatorCalculator.CalculateEma(closes, 50);
            var rsiList = IndicatorCalculator.CalculateRsi(closes, 14);
            var (macdList, signalList) = IndicatorCalculator.CalculateMacd(closes);

            var batchIndicators = new List<MarketIndicator>();
            DateTime? currentDay = null;
            decimal runningSumPV = 0;
            long runningSumV = 0;

            for (int i = 0; i < historyCandles.Count; i++)
            {
                var candle = historyCandles[i];

                // Skip warmup candles - only store indicators for the target range when fromTime/toTime are specified
                if (fromTime.HasValue && candle.CandleTime < fromTime.Value)
                {
                    var cDate = candle.CandleTime.Date;
                    if (currentDay != cDate)
                    {
                        currentDay = cDate;
                        runningSumPV = 0;
                        runningSumV = 0;
                    }
                    runningSumPV += candle.Close * candle.Volume;
                    runningSumV += candle.Volume;
                    continue;
                }

                if (toTime.HasValue && candle.CandleTime > toTime.Value)
                {
                    continue;
                }

                var candleDate = candle.CandleTime.Date;

                // Reset intra-day cumulative VWAP tracking at start of new calendar day
                if (currentDay != candleDate)
                {
                    currentDay = candleDate;
                    runningSumPV = 0;
                    runningSumV = 0;
                }

                runningSumPV += candle.Close * candle.Volume;
                runningSumV += candle.Volume;

                decimal vwap = runningSumV > 0 ? runningSumPV / runningSumV : candle.Close;

                batchIndicators.Add(new MarketIndicator
                {
                    Id = candle.Id,
                    Symbol = symbol.ToUpper(),
                    Timeframe = timeframe,
                    EMA20 = ema20List[i],
                    EMA50 = ema50List[i],
                    RSI = rsiList[i],
                    MACD = macdList[i],
                    SignalLine = signalList[i],
                    VWAP = vwap,
                    CandleTime = candle.CandleTime.ToUniversalTime(),
                    CreatedAt = DateTime.UtcNow
                });
            }

            if (batchIndicators.Any())
            {
                await _indicatorRepository.InsertBatchAsync(batchIndicators);
            }

            _logger.LogInformation("Completed backfill of {Count} historical indicators for {Symbol} ({Timeframe}).", batchIndicators.Count, symbol, timeframe);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to backfill historical indicators for {Symbol} ({Timeframe}).", symbol, timeframe);
        }
    }
}
