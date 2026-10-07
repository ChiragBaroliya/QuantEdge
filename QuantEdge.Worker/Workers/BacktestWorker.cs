using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using QuantEdge.Infrastructure.Helpers;
using QuantEdge.Infrastructure.Services;
using QuantEdge.Infrastructure.Services.Backtest;

namespace QuantEdge.Worker.Workers;

/// <summary>
/// Runs queued backtests (Plan Phase 7) one at a time. Lives in the plain "marketdatafeed" process (like the NSE
/// bhavcopy job) because that service is always running; the replay uses only stored candles - never a Zerodha call.
/// During market hours it uses a single low-priority thread so the live feed in the same process isn't slowed down;
/// outside market hours it uses up to 4.
/// </summary>
public class BacktestWorker : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<BacktestWorker> _logger;

    public BacktestWorker(IServiceScopeFactory scopeFactory, ILogger<BacktestWorker> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);
        bool startup = true;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<IBacktestService>();
                if (startup)
                {
                    await service.FailInterruptedAsync();
                    startup = false;
                }

                var run = await service.ClaimNextAsync();
                if (run != null)
                {
                    int threads = IsMarketHours() ? 1 : Math.Clamp(Environment.ProcessorCount - 1, 1, 4);
                    _logger.LogInformation("Backtest #{Id} started ({Label}) on {Threads} thread(s).", run.Id, run.Label, threads);
                    try
                    {
                        await service.RunAsync(run, threads, stoppingToken);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        scope.ServiceProvider.GetRequiredService<IBrokerApiEventRecorder>().RecordFailure(BrokerApiSource.Job, "backtest",
                            $"Backtest #{run.Id} failed: {ex.Message}", level: "warning");
                    }
                    continue;   // look for the next queued run straight away
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Typically: backtest_runs not created yet (apply schema.sql). Retry quietly.
                _logger.LogDebug(ex, "BacktestWorker poll failed.");
            }

            await Task.Delay(PollInterval, stoppingToken);
        }
    }

    private static bool IsMarketHours()
    {
        var nowIst = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneHelper.IndianTimeZone);
        return nowIst.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)
            && nowIst.TimeOfDay >= new TimeSpan(9, 0, 0) && nowIst.TimeOfDay <= new TimeSpan(15, 45, 0);
    }
}
