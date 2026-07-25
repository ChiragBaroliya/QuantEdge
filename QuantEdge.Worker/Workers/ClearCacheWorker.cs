using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using QuantEdge.Infrastructure.Interfaces;

namespace QuantEdge.Worker.Workers;

/// <summary>
/// A background hosted service that executes on-demand to purge all in-memory cached entries
/// and stops automatically upon completion. Useful for testing and manual cache resets.
/// </summary>
public class ClearCacheWorker : BackgroundService
{
    private readonly ICacheService _cacheService;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<ClearCacheWorker> _logger;

    public ClearCacheWorker(
        ICacheService cacheService,
        IHostApplicationLifetime lifetime,
        ILogger<ClearCacheWorker> logger)
    {
        _cacheService = cacheService ?? throw new ArgumentNullException(nameof(cacheService));
        _lifetime = lifetime ?? throw new ArgumentNullException(nameof(lifetime));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ClearCacheWorker job starting up...");

        try
        {
            await Task.Delay(1000, stoppingToken);

            _logger.LogInformation("Clearing all in-memory cached entries (StockMaster metadata, Zerodha active sessions)...");
            await _cacheService.ClearAllAsync();

            _logger.LogInformation("Memory cache cleared successfully.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to clear memory cache.");
        }
        finally
        {
            _logger.LogInformation("Stopping application as cache clear job is completed.");
            _lifetime.StopApplication();
        }
    }
}
