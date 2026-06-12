using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using QuantEdge.Infrastructure.Interfaces;

namespace QuantEdge.Worker.Workers;

/// <summary>
/// A long-running Background Service (IHostedService) that manages and runs the 
/// core real-time market data ingestion and processing pipeline.
/// </summary>
public class MarketDataFeedWorker : BackgroundService
{
    private readonly IMarketDataProcessor _processor;
    private readonly IMarketHoursService _marketHoursService;
    private readonly ILogger<MarketDataFeedWorker> _logger;

    private bool _isActiveSession = false;

    public MarketDataFeedWorker(
        IMarketDataProcessor processor, 
        IMarketHoursService marketHoursService,
        ILogger<MarketDataFeedWorker> logger)
    {
        _processor = processor ?? throw new ArgumentNullException(nameof(processor));
        _marketHoursService = marketHoursService ?? throw new ArgumentNullException(nameof(marketHoursService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Executes the background service task, running the 24x7 market hours connection loop.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MarketDataFeedWorker background service is starting up...");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                bool withinMarketHours = await _marketHoursService.IsWithinMarketHoursAsync();

                if (withinMarketHours)
                {
                    if (!_isActiveSession)
                    {
                        _logger.LogInformation("Entering Indian stock market hours. Initializing connection and subscribing to instruments...");
                        try
                        {
                            await _processor.StartProcessingAsync(stoppingToken);
                            _isActiveSession = true;
                            _logger.LogInformation("Market data processing session started successfully.");
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Failed to start market data processing. Will retry in the next cycle.");
                        }
                    }
                    else if (!_processor.IsConnected)
                    {
                        _logger.LogWarning("WebSocket connection is offline during active market hours session. Internal auto-reconnect should be in progress.");
                    }
                }
                else
                {
                    if (_isActiveSession)
                    {
                        _logger.LogInformation("Leaving Indian stock market hours. Stopping connection and live tick processing...");
                        try
                        {
                            await _processor.StopProcessingAsync(stoppingToken);
                            _isActiveSession = false;
                            _logger.LogInformation("Market data processing session stopped successfully.");
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Failed to gracefully stop market data processing.");
                            _isActiveSession = false; // Set to false to prevent repeat error logs
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An unhandled exception occurred in the MarketDataFeedWorker connection loop.");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("MarketDataFeedWorker background service connection loop has terminated.");
    }
}
