using System;
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
/// A background hosted worker that executes on application startup to identify gaps 
/// in local historical candlesticks and backfills them from the configured broker.
/// Dynamically queries active symbols from the stock_master table.
/// </summary>
public class HistoricalDataSyncWorker : BackgroundService
{
    private readonly IHistoricalDataService _historicalDataService;
    private readonly IStockMasterRepository _stockMasterRepository;
    private readonly BrokerConfig _config;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<HistoricalDataSyncWorker> _logger;

    public HistoricalDataSyncWorker(
        IHistoricalDataService historicalDataService, 
        IStockMasterRepository stockMasterRepository,
        IOptions<BrokerConfig> config,
        IHostApplicationLifetime lifetime,
        ILogger<HistoricalDataSyncWorker> _loggerLocal)
    {
        _historicalDataService = historicalDataService ?? throw new ArgumentNullException(nameof(historicalDataService));
        _stockMasterRepository = stockMasterRepository ?? throw new ArgumentNullException(nameof(stockMasterRepository));
        _config = config?.Value ?? throw new ArgumentNullException(nameof(config));
        _lifetime = lifetime ?? throw new ArgumentNullException(nameof(lifetime));
        _logger = _loggerLocal ?? throw new ArgumentNullException(nameof(_loggerLocal));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("HistoricalDataSyncWorker background service is starting up...");

        try
        {
            // Delay briefly on startup to ensure database initialization and auth flow have completed
            await Task.Delay(3000, stoppingToken);

            _logger.LogInformation("HistoricalDataSyncWorker is retrieving active symbols from StockMaster...");
            var activeStocks = await _stockMasterRepository.GetActiveStocksAsync();
            var stocksToSync = activeStocks.Where(stock => 
                _config.Timeframes != null && _config.Timeframes.Any(tf => (GetHistoryStoredStatus(stock, tf) ?? 0) == 0)
            ).ToList();

            _logger.LogInformation("HistoricalDataSyncWorker is executing gap check and backfill for {Count} symbols where at least one target timeframe history is missing...", stocksToSync.Count);

            foreach (var stock in stocksToSync)
            {
                if (stoppingToken.IsCancellationRequested)
                    break;

                foreach (var timeframe in _config.Timeframes ?? Array.Empty<string>())
                {
                    if (stoppingToken.IsCancellationRequested)
                        break;

                    // Skip timeframe sync if history for this timeframe is already stored for this symbol
                    if ((GetHistoryStoredStatus(stock, timeframe) ?? 0) == 1)
                    {
                        _logger.LogInformation("History for timeframe {Timeframe} is already stored for symbol {Symbol}. Skipping.", timeframe, stock.Symbol);
                        continue;
                    }

                    try
                    {
                        await _historicalDataService.SyncGapsAsync(stock.Symbol, timeframe, stoppingToken);

                        await _stockMasterRepository.UpdateHistoryStoredAsync(stock.Id, timeframe, 1);
                        _logger.LogInformation("Successfully backfilled and updated history stored to 1 for symbol {Symbol} ({Timeframe}).", stock.Symbol, timeframe);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to complete gap sync for symbol {Symbol} ({Timeframe}). Skipping to next configuration.", stock.Symbol, timeframe);
                    }
                }
            }

            _logger.LogInformation("HistoricalDataSyncWorker has finished the initial startup gap check and backfill.");
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("HistoricalDataSyncWorker backfill was cancelled.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "An unhandled exception occurred in HistoricalDataSyncWorker execution.");
        }
        finally
        {
            _logger.LogInformation("Stopping application hosted services as history sync is completed.");
            _lifetime.StopApplication();
        }
    }

    private static int? GetHistoryStoredStatus(StockMaster stock, string timeframe)
    {
        return timeframe.ToLower() switch
        {
            "1m" => stock.IsHistryStored1m,
            "5m" => stock.IsHistryStored5m,
            "15m" => stock.IsHistryStored15m,
            "60m" => stock.IsHistryStored60m,
            "1d" => stock.IsHistryStored1d,
            _ => null
        };
    }
}
