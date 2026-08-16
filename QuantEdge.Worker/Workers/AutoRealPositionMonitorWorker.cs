using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using QuantEdge.Domain.Entities;
using QuantEdge.Infrastructure.Helpers;
using QuantEdge.Infrastructure.Interfaces;
using QuantEdge.Infrastructure.Persistence.Repositories;

namespace QuantEdge.Worker.Workers;

public class AutoRealPositionMonitorWorker : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<AutoRealPositionMonitorWorker> _logger;
    private readonly TimeSpan _fallbackInterval = TimeSpan.FromSeconds(20);

    public AutoRealPositionMonitorWorker(
        IServiceProvider serviceProvider,
        ILogger<AutoRealPositionMonitorWorker> logger)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("AutoRealPositionMonitorWorker (REAL POSITIONS MONITOR) background service starting up...");

        await Task.Delay(5000, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var marketHoursService = scope.ServiceProvider.GetRequiredService<IMarketHoursService>();
                var realTradeCache = scope.ServiceProvider.GetService<IRealTradeCacheService>();
                bool isMarketOpen = await marketHoursService.IsWithinMarketHoursAsync();

                if (isMarketOpen)
                {
                    // If not warmed up yet, warmup cache
                    if (realTradeCache != null && !realTradeCache.IsWarmedUp)
                    {
                        await realTradeCache.WarmupMarketCacheAsync();
                    }

                    var realTradeService = scope.ServiceProvider.GetRequiredService<IAutoRealTradeService>();
                    var marketDataCache = scope.ServiceProvider.GetService<IMarketDataCacheService>();

                    // Fetch all OPEN real positions from RAM (or DB fallback)
                    var openRealPositions = realTradeCache != null && realTradeCache.IsWarmedUp
                        ? realTradeCache.GetAllOpenPositions().ToList()
                        : (await scope.ServiceProvider.GetRequiredService<IRealTradingRepository>().GetAllOpenPositionsAsync()).ToList();

                    if (openRealPositions.Any())
                    {
                        foreach (var position in openRealPositions)
                        {
                            if (stoppingToken.IsCancellationRequested) break;

                            decimal ltp = 0m;
                            if (marketDataCache != null)
                            {
                                var recentCandles = await marketDataCache.GetRecentCandlesAsync(position.Symbol, "1m", 1);
                                if (recentCandles != null && recentCandles.Any())
                                {
                                    ltp = recentCandles.First().Close;
                                }
                            }

                            if (ltp <= 0m)
                            {
                                ltp = position.CurrentPrice > 0m ? position.CurrentPrice : position.AverageEntryPrice;
                            }

                            if (ltp > 0m)
                            {
                                await realTradeService.EvaluateAndExecuteRealSellAsync(position, ltp, position.UserId);
                            }
                        }
                    }
                }
                else
                {
                    // If market closed and cache is still warmed up, release it
                    if (realTradeCache != null && realTradeCache.IsWarmedUp)
                    {
                        await realTradeCache.ReleaseMarketCacheAsync();
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error occurred in AutoRealPositionMonitorWorker loop.");
            }

            await Task.Delay(_fallbackInterval, stoppingToken);
        }
    }
}
