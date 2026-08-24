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

public class AutoRealTradeSignalScanWorker : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<AutoRealTradeSignalScanWorker> _logger;
    private readonly TimeSpan _scanInterval = TimeSpan.FromMinutes(15);

    public AutoRealTradeSignalScanWorker(
        IServiceProvider serviceProvider,
        ILogger<AutoRealTradeSignalScanWorker> logger)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("AutoRealTradeSignalScanWorker (REAL MONEY) background service starting up...");

        // Startup delay
        await Task.Delay(12000, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var marketHoursService = scope.ServiceProvider.GetRequiredService<IMarketHoursService>();
                var realTradeCache = scope.ServiceProvider.GetService<IRealTradeCacheService>();
                bool isMarketOpen = await marketHoursService.IsWithinMarketHoursAsync();

                // 1. Warmup Cache at 09:00 AM or if not warmed up during market hours
                if (realTradeCache != null && (!realTradeCache.IsWarmedUp && isMarketOpen))
                {
                    await realTradeCache.WarmupMarketCacheAsync();
                }

                if (!isMarketOpen)
                {
                    DateTime nowIst = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneHelper.IndianTimeZone);
                    _logger.LogDebug("Outside Market Trading Window or Market Holiday ({Time} IST). Real Auto Trade Signal Scan waiting...", nowIst.ToString("HH:mm:ss"));
                }
                else
                {
                    var realTradeService = scope.ServiceProvider.GetRequiredService<IAutoRealTradeService>();
                    var activeUserSettings = realTradeCache != null && realTradeCache.IsWarmedUp
                        ? realTradeCache.GetActiveUsersSettings().ToList()
                        : (await scope.ServiceProvider.GetRequiredService<IRealTradingRepository>().GetActiveSettingsAsync()).ToList();

                    if (activeUserSettings.Any())
                    {
                        _logger.LogInformation("Executing 15-minute Single-Pass REAL MONEY Scan over active stocks for {UserCount} active user(s)...", activeUserSettings.Count);
                        await RunSinglePassScanAndExecuteAsync(scope.ServiceProvider, realTradeService, activeUserSettings, stoppingToken);
                    }
                    else
                    {
                        _logger.LogDebug("No users currently have Real Auto Trade Master Switch ON. Skipping 15-minute scan cycle.");
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error occurred in AutoRealTradeSignalScanWorker cycle.");
            }

            await Task.Delay(_scanInterval, stoppingToken);
        }
    }

    private async Task RunSinglePassScanAndExecuteAsync(
        IServiceProvider provider,
        IAutoRealTradeService realTradeService,
        List<RealTradeSettings> activeUserSettings,
        CancellationToken stoppingToken)
    {
        var stockRepo = provider.GetRequiredService<IStockMasterRepository>();
        var candleRepo = provider.GetRequiredService<IMarketCandleRepository>();

        var activeStocks = (await stockRepo.GetActiveStocksAsync()).ToList();
        if (!activeStocks.Any())
        {
            _logger.LogWarning("No active stocks found in stock_master repository for Real Auto Trade Scan.");
            return;
        }

        var niftyCandles = (await candleRepo.GetHistoryAsync("NIFTY 50", "1d", 100))
            .OrderBy(c => c.CandleTime)
            .ToList();

        if (!niftyCandles.Any())
        {
            niftyCandles = (await candleRepo.GetHistoryAsync("NIFTYBEES", "1d", 100))
                .OrderBy(c => c.CandleTime)
                .ToList();
        }

        // Single pass: collect candidate stocks
        var candidateStocks = new List<(Domain.Entities.StockMaster Stock, decimal EntryPrice, int MetCount, int Score, bool IsBuySignal)>();

        foreach (var stock in activeStocks)
        {
            if (stoppingToken.IsCancellationRequested) break;
            if (stock.Symbol == "NIFTY 50" || stock.Symbol == "NIFTYBEES") continue;

            try
            {
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

                var evalResult = SwingDecisionEngine.Evaluate(stock, stockCandles1d, stockCandles15m, stockCandles60m, niftyCandles);
                if (evalResult == null || evalResult.Checklist == null) continue;

                int metCount = evalResult.Checklist.MetCount;

                // Threshold filter: Collect candidate if confirmed Buy or >= 6 criteria
                if (evalResult.IsBuySignal || metCount >= 6)
                {
                    candidateStocks.Add((stock, evalResult.EntryPrice, metCount, evalResult.Score, evalResult.IsBuySignal));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error scanning symbol {Symbol} during Single-Pass Real Auto Trade Scan.", stock.Symbol);
            }
        }

        _logger.LogInformation("Single-Pass Scan identified {CandidateCount} candidate stocks. Distributing to {UserCount} active user(s)...",
            candidateStocks.Count, activeUserSettings.Count);

        // Distribute candidate signals to each active user based on their specific settings
        foreach (var userSettings in activeUserSettings)
        {
            if (stoppingToken.IsCancellationRequested) break;

            int executedOrdersCount = 0;
            foreach (var candidate in candidateStocks)
            {
                if (candidate.IsBuySignal || candidate.MetCount >= userSettings.MinConditionsMatch)
                {
                    bool executed = await realTradeService.EvaluateAndExecuteRealBuyAsync(
                        candidate.Stock.Symbol, candidate.EntryPrice, candidate.MetCount, userSettings.UserId, candidate.IsBuySignal);

                    if (executed)
                    {
                        executedOrdersCount++;
                        _logger.LogInformation("✅ Live BUY Executed for User {UserId}: {Symbol} @ ₹{Price:F2} (Score {Score}/100, Met {MetCount}/11)",
                            userSettings.UserId, candidate.Stock.Symbol, candidate.EntryPrice, candidate.Score, candidate.MetCount);
                    }
                }
            }
        }
    }
}
