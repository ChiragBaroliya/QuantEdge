using System;
using System.Collections.Concurrent;
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

    // A single missed tick within the freshness window is often just quiet trading, not a dead
    // subscription - only fall back to a REST quote once a symbol has been stale for this many
    // consecutive cycles (~40s), so a brief WS gap never triggers an extra API call.
    private const int RestFallbackMissThreshold = 2;

    // How often the same symbol is allowed to write an LTP_UNAVAILABLE / LTP_REST_FALLBACK /
    // WS_RESUBSCRIBED audit entry, so a stuck symbol doesn't spam the Live Real Trade Audit Stream
    // once per 20s cycle for as long as it stays broken.
    private static readonly TimeSpan AuditLogDebounceWindow = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, int> _consecutiveStaleMisses = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTime> _lastAuditLogUtc = new(StringComparer.OrdinalIgnoreCase);

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
                    var brokerService = scope.ServiceProvider.GetService<IZerodhaKiteBrokerService>();

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

                        // Self-heal: a position's WS subscription is normally registered once, at buy
                        // time. If that registration was ever lost (e.g. service restart, transient
                        // failure) the symbol would silently sit unmonitored forever - so every cycle,
                        // confirm each open position is still registered and re-subscribe if not.
                        if (webSocketService != null && wsConnected)
                        {
                            foreach (var position in openRealPositions)
                            {
                                if (webSocketService.IsSubscribed(position.Symbol)) continue;

                                try
                                {
                                    await webSocketService.SubscribeAsync(position.Symbol, stoppingToken);
                                    if (ShouldLogAudit($"RESUB:{position.Symbol}"))
                                    {
                                        _logger.LogWarning("Re-subscribed {Symbol} (Position #{PositionId}) to the WebSocket feed - it was not in the subscribed set.", position.Symbol, position.Id);
                                        await realTradeService.LogAuditAsync(position.Symbol, "WS_RESUBSCRIBED", null, null,
                                            "Symbol was missing from the live WebSocket subscription set and has been re-subscribed.", position.UserId);
                                    }
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogError(ex, "Failed to re-subscribe {Symbol} to the WebSocket feed.", position.Symbol);
                                }
                            }
                        }

                        var stalePositions = new List<RealPosition>();

                        foreach (var position in openRealPositions)
                        {
                            if (stoppingToken.IsCancellationRequested) break;

                            try
                            {
                                if (realTradeCache == null || !wsConnected ||
                                    !realTradeCache.TryGetFreshLtp(position.Symbol, LtpFreshnessWindow, out var ltp))
                                {
                                    int misses = _consecutiveStaleMisses.AddOrUpdate(position.Symbol, 1, (_, count) => count + 1);
                                    if (ShouldLogAudit($"STALE:{position.Symbol}"))
                                    {
                                        _logger.LogWarning(
                                            "LTP_UNAVAILABLE for {Symbol} (Position #{PositionId}, User {UserId}) - no fresh WebSocket tick within {Window}s (WebSocket connected: {Connected}, consecutive misses: {Misses}).",
                                            position.Symbol, position.Id, position.UserId, LtpFreshnessWindow.TotalSeconds, wsConnected, misses);
                                    }

                                    if (misses >= RestFallbackMissThreshold && brokerService != null)
                                    {
                                        stalePositions.Add(position);
                                    }
                                    continue;
                                }

                                _consecutiveStaleMisses.TryRemove(position.Symbol, out _);
                                _logger.LogDebug("LTP source=WebSocket for {Symbol}: {Ltp}", position.Symbol, ltp);
                                await realTradeService.EvaluateAndExecuteRealSellAsync(position, ltp, position.UserId);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "Error evaluating real position #{PositionId} ({Symbol}, User {UserId}); continuing with remaining positions.",
                                    position.Id, position.Symbol, position.UserId);
                            }
                        }

                        // REST fallback: a symbol stuck without a fresh WS tick for multiple cycles
                        // gets one batched /quote/ltp call covering every stale symbol this cycle -
                        // never one REST call per symbol - so this can't approach Kite's rate limit
                        // regardless of how many positions go stale at once.
                        if (stalePositions.Count > 0 && realTradeCache != null && brokerService != null)
                        {
                            await FallBackToRestLtpAsync(stalePositions, realTradeCache, realTradeService, brokerService);
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

    // Fetches one batched Zerodha REST /quote/ltp call covering every symbol that's gone stale on the
    // WebSocket feed this cycle (grouped by user, in case of multi-user support later), seeds the same
    // RAM cache the WS tick path writes to, and immediately re-evaluates the exit conditions with the
    // freshly fetched price so a dead subscription can't leave a position unmonitored indefinitely.
    private async Task FallBackToRestLtpAsync(
        List<RealPosition> stalePositions,
        IRealTradeCacheService realTradeCache,
        IAutoRealTradeService realTradeService,
        IZerodhaKiteBrokerService brokerService)
    {
        foreach (var userGroup in stalePositions.GroupBy(p => p.UserId))
        {
            int userId = userGroup.Key;
            var positions = userGroup.ToList();
            var instruments = positions.Select(p => (p.Symbol, "NSE")).Distinct().ToList();

            try
            {
                var (success, ltps, message) = await brokerService.GetLtpQuotesAsync(instruments, userId);
                if (!success || ltps == null)
                {
                    if (ShouldLogAudit($"RESTFAIL:{userId}"))
                    {
                        _logger.LogWarning("REST LTP fallback call failed for User {UserId} ({Count} stale symbols): {Message}", userId, positions.Count, message);
                        await realTradeService.LogAuditAsync("MULTIPLE", "LTP_UNAVAILABLE", null, null,
                            $"WebSocket ticks stale for {positions.Count} symbol(s) and REST /quote/ltp fallback also failed: {message}", userId);
                    }
                    continue;
                }

                foreach (var position in positions)
                {
                    if (!ltps.TryGetValue(position.Symbol, out var ltp) || ltp <= 0m)
                    {
                        if (ShouldLogAudit($"RESTMISS:{position.Symbol}"))
                        {
                            _logger.LogWarning("REST LTP fallback returned no quote for {Symbol} (Position #{PositionId}, User {UserId}).", position.Symbol, position.Id, userId);
                            await realTradeService.LogAuditAsync(position.Symbol, "LTP_UNAVAILABLE", null, null,
                                "WebSocket tick stale and REST /quote/ltp fallback did not return a price for this symbol.", userId);
                        }
                        continue;
                    }

                    // Seed the same cache the WS tick handler writes to, so this and every subsequent
                    // check this cycle (and the next, if WS recovers) sees a consistent price source.
                    realTradeCache.UpdateLiveLtp(position.Symbol, ltp);

                    if (ShouldLogAudit($"RESTOK:{position.Symbol}"))
                    {
                        _logger.LogInformation("LTP source=REST fallback for {Symbol} (Position #{PositionId}): {Ltp}", position.Symbol, position.Id, ltp);
                        await realTradeService.LogAuditAsync(position.Symbol, "LTP_REST_FALLBACK", ltp, null,
                            "WebSocket tick was stale for multiple cycles; used REST /quote/ltp fallback to keep exit monitoring live.", userId);
                    }

                    await realTradeService.EvaluateAndExecuteRealSellAsync(position, ltp, userId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error executing REST LTP fallback for User {UserId} ({Count} stale symbols).", userId, positions.Count);
            }
        }
    }

    private bool ShouldLogAudit(string key)
    {
        var now = DateTime.UtcNow;
        if (_lastAuditLogUtc.TryGetValue(key, out var last) && (now - last) < AuditLogDebounceWindow)
        {
            return false;
        }
        _lastAuditLogUtc[key] = now;
        return true;
    }
}
