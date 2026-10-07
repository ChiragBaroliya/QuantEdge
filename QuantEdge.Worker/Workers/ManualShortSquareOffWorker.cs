using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using QuantEdge.Infrastructure.Interfaces;

namespace QuantEdge.Worker.Workers;

/// <summary>
/// Manual Short Selling is intraday only, like a broker's MIS square-off: once a minute, buys back every open
/// manual paper short past its user's Short Square-off Time (default 15:15 IST), plus any short left from an
/// earlier day (a missed run). Reads only the manual_paper_* tables and stored 1-minute closes - no Zerodha
/// call - and never touches Auto Paper / Auto Real positions.
/// </summary>
public class ManualShortSquareOffWorker : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(1);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ManualShortSquareOffWorker> _logger;

    public ManualShortSquareOffWorker(IServiceScopeFactory scopeFactory, ILogger<ManualShortSquareOffWorker> logger)
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
                int closed = await service.SquareOffDueShortsAsync();
                if (closed > 0)
                {
                    _logger.LogInformation("ManualShortSquareOffWorker: {Closed} manual short(s) bought back (auto square-off).", closed);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ManualShortSquareOffWorker run failed; retrying in {Seconds}s.", CheckInterval.TotalSeconds);
            }

            await Task.Delay(CheckInterval, stoppingToken);
        }
    }
}
