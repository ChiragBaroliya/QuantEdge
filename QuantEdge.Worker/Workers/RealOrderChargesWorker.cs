using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using QuantEdge.Infrastructure.Helpers;
using QuantEdge.Infrastructure.Services;

namespace QuantEdge.Worker.Workers;

/// <summary>
/// Once a day after the close (from 15:45 IST, weekdays), stores Zerodha's actual charges for the day's filled real
/// orders via the Kite virtual contract note - one batched Kite call per user. Checks every 15 minutes until it
/// succeeds that day; orders that already have charges are never re-sent, so extra runs make no Kite call.
/// </summary>
public class RealOrderChargesWorker : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan RunAfterIst = new(15, 45, 0);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<RealOrderChargesWorker> _logger;
    private DateTime _lastSuccessIstDate = DateTime.MinValue;

    public RealOrderChargesWorker(IServiceScopeFactory scopeFactory, ILogger<RealOrderChargesWorker> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            var nowIst = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneHelper.IndianTimeZone);
            bool weekday = nowIst.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday);

            if (weekday && nowIst.TimeOfDay >= RunAfterIst && _lastSuccessIstDate != nowIst.Date)
            {
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var service = scope.ServiceProvider.GetRequiredService<IRealOrderChargesService>();
                    int saved = await service.FetchPendingChargesAsync();
                    _logger.LogInformation("RealOrderChargesWorker: {Saved} order(s) got Zerodha's actual charges.", saved);

                    // Daily reconciliation: open real positions vs Zerodha holdings/positions (2 Kite calls per user
                    // with open positions). Mismatches go to data_quality_issues and the header bell.
                    var realRepo = scope.ServiceProvider.GetRequiredService<QuantEdge.Infrastructure.Persistence.Repositories.IRealTradingRepository>();
                    var reconciler = scope.ServiceProvider.GetRequiredService<IPositionReconciliationService>();
                    foreach (int userId in (await realRepo.GetAllOpenPositionsAsync()).Select(p => p.UserId).Distinct())
                    {
                        var check = await reconciler.CompareAsync(userId, persistIssues: true);
                        if (!check.Success)
                        {
                            scope.ServiceProvider.GetService<IBrokerApiEventRecorder>()?.RecordFailure(BrokerApiSource.Job,
                                "position reconciliation", $"Daily position check vs Zerodha could not run: {check.Message}", userId: userId);
                        }
                    }
                    _lastSuccessIstDate = nowIst.Date;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "RealOrderChargesWorker run failed; retrying in {Minutes} min.", CheckInterval.TotalMinutes);
                    using var scope = _scopeFactory.CreateScope();
                    scope.ServiceProvider.GetService<IBrokerApiEventRecorder>()?.RecordFailure(BrokerApiSource.Job,
                        "actual charges (contract note)", $"Daily actual-charges job failed: {ex.Message}. Retrying in 15 min.");
                }
            }

            await Task.Delay(CheckInterval, stoppingToken);
        }
    }
}
