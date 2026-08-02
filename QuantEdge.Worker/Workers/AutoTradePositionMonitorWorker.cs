using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using QuantEdge.Domain.Entities;
using QuantEdge.Infrastructure.DTOs;
using QuantEdge.Infrastructure.Interfaces;

namespace QuantEdge.Worker.Workers;

public class AutoTradePositionMonitorWorker : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<AutoTradePositionMonitorWorker> _logger;
    private readonly TimeSpan _fallbackInterval = TimeSpan.FromSeconds(30);

    public AutoTradePositionMonitorWorker(
        IServiceProvider serviceProvider,
        ILogger<AutoTradePositionMonitorWorker> logger)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("AutoTradePositionMonitorWorker background service starting up...");

        await Task.Delay(5000, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var autoTradeService = scope.ServiceProvider.GetRequiredService<IAutoTradeService>();
                var paperService = scope.ServiceProvider.GetRequiredService<IPaperTradingService>();
                var wsService = scope.ServiceProvider.GetService<IWebSocketMarketDataService>();

                // Fetch OPEN Auto Positions
                var openAutoPositions = (await paperService.GetOpenPositionsAsync("default_user"))
                    .Where(p => p.TradeType == TradeType.Auto)
                    .ToList();

                if (openAutoPositions.Any())
                {
                    bool isWsConnected = wsService != null && wsService.IsConnected;

                    if (isWsConnected)
                    {
                        // WebSocket Connected Mode: Process real-time tick monitoring
                        foreach (var position in openAutoPositions)
                        {
                            if (stoppingToken.IsCancellationRequested) break;

                            decimal ltp = position.CurrentPrice;
                            if (ltp > 0m)
                            {
                                await autoTradeService.EvaluateAndExecuteAutoSellAsync(position, ltp, "default_user");
                            }
                        }
                    }
                    else
                    {
                        // REST Polling Fallback Mode: Poll current LTP from matching engine or API
                        _logger.LogWarning("Zerodha WebSocket is disconnected. Running REST Polling Fallback monitor for {Count} open auto positions...", openAutoPositions.Count);

                        foreach (var position in openAutoPositions)
                        {
                            if (stoppingToken.IsCancellationRequested) break;

                            decimal ltp = position.CurrentPrice;
                            if (ltp <= 0m) ltp = position.AverageEntryPrice;

                            await autoTradeService.EvaluateAndExecuteAutoSellAsync(position, ltp, "default_user");
                        }

                        // Try reconnecting WebSocket if disconnected
                        try
                        {
                            if (wsService != null && !wsService.IsConnected)
                            {
                                _logger.LogInformation("Attempting Zerodha WebSocket reconnection...");
                                await wsService.ConnectAsync("", stoppingToken);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Zerodha WebSocket reconnection attempt failed or token expired.");
                            
                            // Check if Access Token expired
                            if (ex.Message.Contains("AccessToken") || ex.Message.Contains("expired") || ex.Message.Contains("Unauthorized"))
                            {
                                _logger.LogError("Zerodha Access Token has EXPIRED! Stopping Auto Trading and notifying user.");
                                await autoTradeService.ToggleAutoTradeAsync(false, "default_user");
                                await autoTradeService.LogAuditAsync("ZERODHA", "TOKEN_EXPIRED", null, null,
                                    "Zerodha Access Token expired. Auto Trading stopped automatically. Please re-authenticate.", "default_user");
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
                _logger.LogError(ex, "Error occurred in AutoTradePositionMonitorWorker loop.");
            }

            await Task.Delay(_fallbackInterval, stoppingToken);
        }
    }
}
