using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using QuantEdge.Domain.Entities;
using QuantEdge.Infrastructure.Interfaces;
using QuantEdge.Infrastructure.Persistence.Repositories;
using QuantEdge.Infrastructure.Services;

namespace QuantEdge.Worker.Workers;

public class AutoTradeSignalScanWorker : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<AutoTradeSignalScanWorker> _logger;
    private readonly TimeSpan _scanInterval = TimeSpan.FromMinutes(15);

    public AutoTradeSignalScanWorker(
        IServiceProvider serviceProvider,
        ILogger<AutoTradeSignalScanWorker> logger)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("AutoTradeSignalScanWorker background service starting up...");

        // Startup delay
        await Task.Delay(10000, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var autoTradeService = scope.ServiceProvider.GetRequiredService<IAutoTradeService>();
                var autoTradeRepo = scope.ServiceProvider.GetRequiredService<IAutoTradeRepository>();
                
                var activeUserSettings = (await autoTradeRepo.GetActiveSettingsAsync()).ToList();

                if (activeUserSettings.Any())
                {
                    _logger.LogInformation("Executing 15-minute Auto Trade Signal Scan for {UserCount} active user(s) over ~190 stocks...", activeUserSettings.Count);
                    
                    foreach (var userSettings in activeUserSettings)
                    {
                        if (stoppingToken.IsCancellationRequested) break;
                        await RunSignalScanForUserAsync(scope.ServiceProvider, autoTradeService, userSettings, stoppingToken);
                    }
                }
                else
                {
                    _logger.LogDebug("No users currently have Auto Trade Master Switch ON. Skipping 15-minute scan cycle.");
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error occurred in AutoTradeSignalScanWorker cycle.");
            }

            await Task.Delay(_scanInterval, stoppingToken);
        }
    }

    private async Task RunSignalScanForUserAsync(
        IServiceProvider provider,
        IAutoTradeService autoTradeService,
        AutoTradeSettings settings,
        CancellationToken stoppingToken)
    {
        var stockRepo = provider.GetRequiredService<IStockMasterRepository>();
        var candleRepo = provider.GetRequiredService<IMarketCandleRepository>();

        // Dynamically fetch ~190 active stocks from stock_master database
        var activeStocks = (await stockRepo.GetActiveStocksAsync()).ToList();
        if (!activeStocks.Any())
        {
            _logger.LogWarning("No active stocks found in stock_master repository for Auto Trade Scan.");
            return;
        }

        // Fetch Nifty 50 candles for Market Filter
        var niftyCandles = (await candleRepo.GetHistoryAsync("NIFTYBEES", "1d", 60)).ToList();

        int buySignalsFound = 0;
        int executedOrdersCount = 0;

        foreach (var stock in activeStocks)
        {
            if (stoppingToken.IsCancellationRequested) break;

            try
            {
                // Fetch 1d and 60m candle history for indicator calculations
                var stockCandles1d = (await candleRepo.GetHistoryAsync(stock.Symbol, "1d", 100)).ToList();
                var stockCandles60m = (await candleRepo.GetHistoryAsync(stock.Symbol, "60m", 50)).ToList();

                if (stockCandles1d.Count < 50) continue;

                // Evaluate stock using existing 13-condition SwingDecisionEngine
                var evalResult = SwingDecisionEngine.Evaluate(stock, stockCandles1d, stockCandles60m, niftyCandles);
                if (evalResult == null || evalResult.Checklist == null) continue;

                int metCount = evalResult.Checklist.MetCount;

                if (evalResult.IsBuySignal || metCount >= settings.MinConditionsMatch)
                {
                    buySignalsFound++;
                    _logger.LogInformation("BUY Signal detected for {Symbol} for User '{UserId}' (Score: {MetCount}/13, Entry: ₹{Price:F2})",
                        stock.Symbol, settings.UserId, metCount, evalResult.EntryPrice);

                    bool executed = await autoTradeService.EvaluateAndExecuteAutoBuyAsync(
                        stock.Symbol, evalResult.EntryPrice, metCount, settings.UserId);

                    if (executed)
                    {
                        executedOrdersCount++;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error scanning symbol {Symbol} for user {UserId} during Auto Trade Scan.", stock.Symbol, settings.UserId);
            }
        }

        _logger.LogInformation("Auto Trade Scan completed for User '{UserId}'. Analyzed {Total} stocks. Found {Signals} BUY signals, Executed {Executed} orders.",
            settings.UserId, activeStocks.Count, buySignalsFound, executedOrdersCount);
    }

}
