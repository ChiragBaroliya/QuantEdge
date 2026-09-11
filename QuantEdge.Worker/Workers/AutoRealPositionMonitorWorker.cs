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
                    var brokerService = scope.ServiceProvider.GetRequiredService<IZerodhaKiteBrokerService>();

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
                        // Cache each user's live Zerodha holdings once per cycle (not once per position) — used
                        // as a fallback LTP source for symbols not in the bot's WebSocket-fed 1m candle universe
                        // (e.g. a demat holding enrolled for monitoring that isn't part of the scan universe).
                        var holdingsByUser = new Dictionary<int, List<ZerodhaHoldingDto>>();

                        foreach (var position in openRealPositions)
                        {
                            if (stoppingToken.IsCancellationRequested) break;

                            try
                            {
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
                                    if (!holdingsByUser.TryGetValue(position.UserId, out var userHoldings))
                                    {
                                        var holdingsResult = await brokerService.GetLiveHoldingsAsync(position.UserId);
                                        userHoldings = holdingsResult.Success && holdingsResult.Holdings != null
                                            ? holdingsResult.Holdings
                                            : new List<ZerodhaHoldingDto>();
                                        holdingsByUser[position.UserId] = userHoldings;
                                    }

                                    var matchingHolding = userHoldings.FirstOrDefault(h =>
                                        string.Equals(h.TradingSymbol, position.Symbol, StringComparison.OrdinalIgnoreCase));
                                    if (matchingHolding != null && matchingHolding.LastPrice > 0m)
                                    {
                                        ltp = matchingHolding.LastPrice;
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
