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
                if (await marketHoursService.IsWithinMarketHoursAsync())
                {
                    var realTradeService = scope.ServiceProvider.GetRequiredService<IAutoRealTradeService>();
                    var realTradeRepo = scope.ServiceProvider.GetRequiredService<IRealTradingRepository>();
                    var wsService = scope.ServiceProvider.GetService<IWebSocketMarketDataService>();
                    var marketDataCache = scope.ServiceProvider.GetService<IMarketDataCacheService>();

                    // Fetch OPEN Real Positions for active users
                    var activeUsers = (await realTradeRepo.GetActiveSettingsAsync()).ToList();
                    var userIds = activeUsers.Select(u => u.UserId).Distinct().ToList();
                    if (!userIds.Contains(1)) userIds.Add(1);

                    foreach (var uid in userIds)
                    {
                        var openRealPositions = (await realTradeRepo.GetOpenPositionsAsync(uid)).ToList();

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
                                    await realTradeService.EvaluateAndExecuteRealSellAsync(position, ltp, uid);
                                }
                            }
                        }
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
