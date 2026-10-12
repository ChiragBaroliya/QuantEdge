using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using QuantEdge.Infrastructure.Interfaces;

namespace QuantEdge.Worker.Workers;

/// <summary>
/// Manual Paper Auto Exit: every few seconds, checks each OPEN manual paper position that has Auto Exit ON against the
/// completed 1-minute candles stored in market_candles_1m, and closes it (SELL a long / BUY TO COVER a short) when its
/// Target, Stop Loss or trailed stop was hit. Stored candles only - no WebSocket / live feed, no Zerodha call - and
/// only the manual_paper_* tables; Auto Paper / Auto Real positions are never touched.
/// </summary>
public class ManualAutoExitWorker : BackgroundService
{
    // A candle is final once its minute has passed; checking more often only shortens the wait after it lands.
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(15);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ManualAutoExitWorker> _logger;

    public ManualAutoExitWorker(IServiceScopeFactory scopeFactory, ILogger<ManualAutoExitWorker> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<IManualPaperTradeService>();
                int closed = await service.ProcessAutoExitsAsync();
                if (closed > 0)
                {
                    _logger.LogInformation("ManualAutoExitWorker: {Closed} manual position(s) closed at Target / Stop Loss / Trailing SL.", closed);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ManualAutoExitWorker run failed; retrying in {Seconds}s.", CheckInterval.TotalSeconds);
            }

            await Task.Delay(CheckInterval, stoppingToken);
        }
    }
}
