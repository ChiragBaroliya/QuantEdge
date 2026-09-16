using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using QuantEdge.Domain.Entities;
using QuantEdge.Infrastructure.DTOs;
using QuantEdge.Infrastructure.Helpers;
using QuantEdge.Infrastructure.Interfaces;
using QuantEdge.Infrastructure.Persistence.Repositories;

namespace QuantEdge.Worker.Workers;

public class AutoRealPositionMonitorWorker : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<AutoRealPositionMonitorWorker> _logger;
    private readonly TimeSpan _fallbackInterval = TimeSpan.FromSeconds(20);

    // A live position's SL/TSL check trusts only a WebSocket tick received within this window - never
    // a REST poll, and never RealPosition.CurrentPrice (which is written once at buy time and frozen
    // forever after). If no tick this fresh is cached for the symbol, the cycle is skipped rather than
    // risking a decision on a stale/wrong price.
    private static readonly TimeSpan LtpFreshnessWindow = TimeSpan.FromSeconds(60);

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
                    var webSocketService = scope.ServiceProvider.GetService<IWebSocketMarketDataService>();

                    // Confirm real fill status with the broker for any SELL order still recorded as Open
                    // (e.g. a limit order placed by the kill switch that hasn't traded yet). Must run before
                    // evaluating exits below so a freshly-confirmed close is reflected in this cycle's positions.
                    try
                    {
                        await realTradeService.ReconcilePendingRealOrdersAsync();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error reconciling pending real orders in AutoRealPositionMonitorWorker cycle.");
                    }

                    // Fetch all OPEN real positions from RAM (or DB fallback)
                    var openRealPositions = realTradeCache != null && realTradeCache.IsWarmedUp
                        ? realTradeCache.GetAllOpenPositions().ToList()
                        : (await scope.ServiceProvider.GetRequiredService<IRealTradingRepository>().GetAllOpenPositionsAsync()).ToList();

                    if (openRealPositions.Any())
                    {
                        bool wsConnected = webSocketService != null && webSocketService.IsConnected;

                        foreach (var position in openRealPositions)
                        {
                            if (stoppingToken.IsCancellationRequested) break;

                            try
                            {
                                if (realTradeCache == null || !wsConnected ||
                                    !realTradeCache.TryGetFreshLtp(position.Symbol, LtpFreshnessWindow, out var ltp))
                                {
                                    _logger.LogWarning(
                                        "LTP_UNAVAILABLE for {Symbol} (Position #{PositionId}, User {UserId}) - no fresh WebSocket tick within {Window}s (WebSocket connected: {Connected}). Skipping SL/TSL check this cycle.",
                                        position.Symbol, position.Id, position.UserId, LtpFreshnessWindow.TotalSeconds, wsConnected);
                                    continue;
                                }

                                _logger.LogDebug("LTP source=WebSocket for {Symbol}: {Ltp}", position.Symbol, ltp);
                                await realTradeService.EvaluateAndExecuteRealSellAsync(position, ltp, position.UserId);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "Error evaluating real position #{PositionId} ({Symbol}, User {UserId}); continuing with remaining positions.",
                                    position.Id, position.Symbol, position.UserId);
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
