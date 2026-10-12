using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using QuantEdge.Infrastructure.Interfaces;

namespace QuantEdge.Worker.Workers;

/// <summary>
/// Manual Paper Limit Orders: every few seconds, fills each Pending manual limit order once a stored 1-minute
/// candle placed after it has traded through its price, and expires the ones still Pending at the trading window
/// end (shorts: the short entry cut-off). Reads only the manual_paper_* tables and market_candles_1m - no Zerodha
/// call - and never touches Auto Paper / Auto Real orders.
/// </summary>
public class ManualLimitOrderWorker : BackgroundService
{
    // Candles are stored once a minute; checking more often only shortens the wait after a candle lands.
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(15);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ManualLimitOrderWorker> _logger;

    public ManualLimitOrderWorker(IServiceScopeFactory scopeFactory, ILogger<ManualLimitOrderWorker> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<IManualPaperTradeService>();
                var (filled, cancelled) = await service.ProcessPendingLimitOrdersAsync();
                if (filled > 0 || cancelled > 0)
                {
                    _logger.LogInformation("ManualLimitOrderWorker: {Filled} manual limit order(s) filled, {Cancelled} cancelled/expired.", filled, cancelled);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ManualLimitOrderWorker run failed; retrying in {Seconds}s.", CheckInterval.TotalSeconds);
            }

            await Task.Delay(CheckInterval, stoppingToken);
        }
    }
}
