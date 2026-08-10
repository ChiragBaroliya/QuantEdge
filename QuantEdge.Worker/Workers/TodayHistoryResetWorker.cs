using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using QuantEdge.Infrastructure.Configurations;
using QuantEdge.Infrastructure.Interfaces;
using QuantEdge.Infrastructure.Persistence.Repositories;
using QuantEdge.Domain.Entities;

namespace QuantEdge.Worker.Workers;

/// <summary>
/// Background worker service that deletes today's candles and indicators for active stocks
/// and fetches fresh historical candles from Zerodha & recalculates technical indicators.
/// </summary>
public class TodayHistoryResetWorker : BackgroundService
{
    private readonly IHistoricalDataService _historicalDataService;
    private readonly IIndicatorService _indicatorService;
    private readonly IStockMasterRepository _stockMasterRepository;
    private readonly IMarketCandleRepository _candleRepository;
    private readonly IMarketIndicatorRepository _indicatorRepository;
    private readonly BrokerConfig _config;
    private readonly ILogger<TodayHistoryResetWorker> _logger;

    public TodayHistoryResetWorker(
        IHistoricalDataService historicalDataService,
        IIndicatorService indicatorService,
        IStockMasterRepository stockMasterRepository,
        IMarketCandleRepository candleRepository,
        IMarketIndicatorRepository indicatorRepository,
        IOptions<BrokerConfig> config,
        ILogger<TodayHistoryResetWorker> logger)
    {
        _historicalDataService = historicalDataService ?? throw new ArgumentNullException(nameof(historicalDataService));
        _indicatorService = indicatorService ?? throw new ArgumentNullException(nameof(indicatorService));
        _stockMasterRepository = stockMasterRepository ?? throw new ArgumentNullException(nameof(stockMasterRepository));
        _candleRepository = candleRepository ?? throw new ArgumentNullException(nameof(candleRepository));
        _indicatorRepository = indicatorRepository ?? throw new ArgumentNullException(nameof(indicatorRepository));
        _config = config?.Value ?? throw new ArgumentNullException(nameof(config));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("TodayHistoryResetWorker background task is starting up...");

        try
        {
            await Task.Delay(2000, stoppingToken);

            DateTime todayStartUtc = DateTime.UtcNow.Date;
            DateTime todayEndUtc = DateTime.UtcNow;

            var activeStocks = (await _stockMasterRepository.GetActiveStocksAsync()).ToList();
            if (!activeStocks.Any())
            {
                activeStocks = (await _stockMasterRepository.GetAllAsync()).ToList();
            }

            var timeframes = _config.Timeframes ?? new[] { "1m", "5m", "15m", "60m", "1d" };

            _logger.LogInformation("TodayHistoryResetWorker: Starting today history reset (clear & recreate) for {Count} active stocks across timeframes [{Timeframes}] from {Start} to {End}...", 
                activeStocks.Count, string.Join(", ", timeframes), todayStartUtc, todayEndUtc);

            foreach (var tf in timeframes)
            {
                if (stoppingToken.IsCancellationRequested) break;

                _logger.LogInformation("TodayHistoryResetWorker: Executing bulk range deletion for today candles & indicators (Timeframe: {Timeframe})...", tf);

                // 1. Bulk delete today's candles & indicators across all active stocks for this timeframe
                await _candleRepository.DeleteHistoryRangeAsync(null, tf, todayStartUtc, todayEndUtc);
                await _indicatorRepository.DeleteIndicatorsRangeAsync(null, tf, todayStartUtc, todayEndUtc);

                // 2. Fetch fresh historical candles and backfill indicators for each active stock
                int total = activeStocks.Count;
                int processed = 0;

                var semaphore = new SemaphoreSlim(5);
                var tasks = activeStocks.Select(async stock =>
                {
                    await semaphore.WaitAsync(stoppingToken);
                    try
                    {
                        await _historicalDataService.FetchHistoricalCandlesAsync(stock.Symbol, tf, todayStartUtc, todayEndUtc, stoppingToken);
                        await _indicatorService.BackfillHistoricalIndicatorsAsync(stock.Symbol, tf);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "TodayHistoryResetWorker: Failed to fetch/backfill today's data for symbol {Symbol} ({Timeframe}).", stock.Symbol, tf);
                    }
                    finally
                    {
                        semaphore.Release();
                        int current = Interlocked.Increment(ref processed);
                        if (current % 10 == 0 || current == total)
                        {
                            _logger.LogInformation("TodayHistoryResetWorker ({Timeframe}): Reset progress {Processed}/{Total} stocks.", tf, current, total);
                        }
                    }
                });

                await Task.WhenAll(tasks);
                _logger.LogInformation("TodayHistoryResetWorker: Completed today reset for timeframe {Timeframe}.", tf);
            }

            _logger.LogInformation("TodayHistoryResetWorker: Successfully completed today reset task for all active stocks and timeframes.");
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("TodayHistoryResetWorker task was cancelled.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception in TodayHistoryResetWorker execution.");
        }
    }
}
