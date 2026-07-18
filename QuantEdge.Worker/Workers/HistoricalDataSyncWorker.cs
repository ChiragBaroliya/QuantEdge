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
            var stocksToSync = activeStocks.Where(s => s.IsHistryStored == 0).ToList();

            _logger.LogInformation("HistoricalDataSyncWorker is executing gap check and backfill for {Count} symbols where IsHistryStored = 0...", stocksToSync.Count);

            foreach (var stock in stocksToSync)
            {
                if (stoppingToken.IsCancellationRequested)
                    break;

                bool syncSuccess = true;
                foreach (var timeframe in _config.Timeframes)
                {
                    if (stoppingToken.IsCancellationRequested)
                        break;

                    try
                    {
                        await _historicalDataService.SyncGapsAsync(stock.Symbol, timeframe, stoppingToken);
                    }
                    catch (Exception ex)
                    {
                        syncSuccess = false;
                        _logger.LogError(ex, "Failed to complete gap sync for symbol {Symbol} ({Timeframe}). Skipping to next configuration.", stock.Symbol, timeframe);
                    }
                }

                if (syncSuccess && !stoppingToken.IsCancellationRequested)
                {
                    try
                    {
                        await _stockMasterRepository.UpdateHistoryStoredAsync(stock.Id, 1);
                        _logger.LogInformation("Successfully backfilled and updated IsHistryStored to 1 for symbol {Symbol}.", stock.Symbol);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to update IsHistryStored to 1 for symbol {Symbol}.", stock.Symbol);
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
}
