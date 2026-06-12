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
    private readonly ILogger<IndicatorService> _logger;

    public IndicatorService(
        IMarketCandleRepository candleRepository,
        IMarketIndicatorRepository indicatorRepository,
        ILogger<IndicatorService> logger)
    {
        _candleRepository = candleRepository ?? throw new ArgumentNullException(nameof(candleRepository));
        _indicatorRepository = indicatorRepository ?? throw new ArgumentNullException(nameof(indicatorRepository));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task CalculateAndSaveLatestIndicatorAsync(string symbol, string timeframe)
    {
        _logger.LogInformation("Calculating latest indicator for {Symbol} ({Timeframe})...", symbol, timeframe);

        try
        {
            // Fetch the last 200 candles ordered by time DESC, then reverse to ASC for indicator loops
            var recentCandles = (await _candleRepository.GetHistoryAsync(symbol, timeframe, limit: 200))
                .OrderBy(c => c.CandleTime)
                .ToList();

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

            await _indicatorRepository.InsertAsync(indicator);
            _logger.LogInformation("Successfully persisted latest indicators for {Symbol} ({Timeframe}) at {Time}.", symbol, timeframe, latestCandle.CandleTime);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred during latest indicator calculation for {Symbol} ({Timeframe}).", symbol, timeframe);
        }
    }

    /// <inheritdoc />
    public async Task BackfillHistoricalIndicatorsAsync(string symbol, string timeframe)
    {
        _logger.LogInformation("Executing historical indicators backfill for {Symbol} ({Timeframe})...", symbol, timeframe);

        try
        {
            // Fetch historical candles (limit 500) and order by time ASC
            var historyCandles = (await _candleRepository.GetHistoryAsync(symbol, timeframe, limit: 500))
                .OrderBy(c => c.CandleTime)
                .ToList();

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

            _logger.LogInformation("Calculated indicators for {Count} candles. Writing to database...", historyCandles.Count);

            int savedCount = 0;
            for (int i = 0; i < historyCandles.Count; i++)
            {
                var candle = historyCandles[i];
                var targetDate = candle.CandleTime.Date;

                // Cumulative VWAP for this calendar day up to index i
                var dayCandles = historyCandles.Take(i + 1).Where(c => c.CandleTime.Date == targetDate).ToList();
                decimal sumPV = dayCandles.Sum(c => c.Close * c.Volume);
                long sumV = dayCandles.Sum(c => c.Volume);
                decimal vwap = sumV > 0 ? sumPV / sumV : candle.Close;

                var indicator = new MarketIndicator
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
                };

                await _indicatorRepository.InsertAsync(indicator);
                savedCount++;
            }

            _logger.LogInformation("Completed backfill of {Count} historical indicators for {Symbol} ({Timeframe}).", savedCount, symbol, timeframe);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to backfill historical indicators for {Symbol} ({Timeframe}).", symbol, timeframe);
        }
    }
}
