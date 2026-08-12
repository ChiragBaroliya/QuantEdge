using System;
using System.Collections.Generic;
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
                var marketHoursService = scope.ServiceProvider.GetRequiredService<IMarketHoursService>();
                bool isMarketOpen = await marketHoursService.IsWithinMarketHoursAsync();

                if (!isMarketOpen)
                {
                    DateTime nowIst = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneHelper.IndianTimeZone);
                    _logger.LogDebug("Outside Market Trading Window or Market Holiday ({Time} IST). Auto Trade Signal Scan waiting...", nowIst.ToString("HH:mm:ss"));
                }
                else
                {
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

    private static bool IsWithinTradingWindow(DateTime istTime)
    {
        if (istTime.DayOfWeek == DayOfWeek.Saturday || istTime.DayOfWeek == DayOfWeek.Sunday)
        {
            return false;
        }

        TimeSpan start = new TimeSpan(9, 15, 0); // 09:15 AM IST
        TimeSpan end = new TimeSpan(15, 30, 0);  // 03:30 PM IST

        TimeSpan nowTime = istTime.TimeOfDay;
        return nowTime >= start && nowTime <= end;
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

        // Fetch Nifty 50 candles for Market Filter (matching SwingTradingService)
        var niftyCandles = (await candleRepo.GetHistoryAsync("NIFTY 50", "1d", 100))
            .OrderBy(c => c.CandleTime)
            .ToList();

        if (!niftyCandles.Any())
        {
            niftyCandles = (await candleRepo.GetHistoryAsync("NIFTYBEES", "1d", 100))
                .OrderBy(c => c.CandleTime)
                .ToList();
        }

        int buySignalsFound = 0;
        int executedOrdersCount = 0;

        foreach (var stock in activeStocks)
        {
            if (stoppingToken.IsCancellationRequested) break;
            if (stock.Symbol == "NIFTY 50" || stock.Symbol == "NIFTYBEES") continue;

            try
            {
                // Fetch 1d, 15m, and 60m candle history for accurate multi-timeframe indicator calculations
                var stockCandles1d = (await candleRepo.GetHistoryAsync(stock.Symbol, "1d", 100))
                    .OrderBy(c => c.CandleTime)
                    .ToList();
                var stockCandles15m = (await candleRepo.GetHistoryAsync(stock.Symbol, "15m", 100))
                    .OrderBy(c => c.CandleTime)
                    .ToList();
                var stockCandles60m = (await candleRepo.GetHistoryAsync(stock.Symbol, "60m", 100))
                    .OrderBy(c => c.CandleTime)
                    .ToList();

                if (stockCandles1d.Count < 50) continue;

                // Evaluate stock using SwingDecisionEngine with all 3 timeframe candles
                var evalResult = SwingDecisionEngine.Evaluate(stock, stockCandles1d, stockCandles15m, stockCandles60m, niftyCandles);
                if (evalResult == null || evalResult.Checklist == null) continue;

                int metCount = evalResult.Checklist.MetCount;

                if (evalResult.IsBuySignal || metCount >= settings.MinConditionsMatch)
                {
                    buySignalsFound++;
                    _logger.LogInformation("BUY Signal detected for {Symbol} for User '{UserId}' (Score: {Score}/100, Met: {MetCount}/{TotalCount}, Entry: ₹{Price:F2})",
                        stock.Symbol, settings.UserId, evalResult.Score, metCount, evalResult.Checklist.TotalCount, evalResult.EntryPrice);

                    bool executed = await autoTradeService.EvaluateAndExecuteAutoBuyAsync(
                        stock.Symbol, evalResult.EntryPrice, metCount, settings.UserId, evalResult.IsBuySignal);

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
