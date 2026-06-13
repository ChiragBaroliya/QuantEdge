using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using QuantEdge.Infrastructure.Configurations;
using QuantEdge.Infrastructure.Interfaces;

namespace QuantEdge.Worker.Workers;

/// <summary>
/// A background hosted service that syncs Zerodha instruments to the database.
/// Runs immediately on startup if configured via RunInstrumentsSyncImmediately,
/// and executes on a recurring schedule every Monday morning at 8:00 AM IST.
/// </summary>
public class InstrumentSyncWorker : BackgroundService
{
    private readonly IInstrumentSyncService _syncService;
    private readonly BrokerConfig _config;
    private readonly ILogger<InstrumentSyncWorker> _logger;

    public InstrumentSyncWorker(
        IInstrumentSyncService syncService,
        IOptions<BrokerConfig> config,
        ILogger<InstrumentSyncWorker> logger)
    {
        _syncService = syncService ?? throw new ArgumentNullException(nameof(syncService));
        _config = config?.Value ?? throw new ArgumentNullException(nameof(config));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("InstrumentSyncWorker started.");

        // Brief delay on startup so DatabaseInitializer has time to run
        await Task.Delay(4000, stoppingToken);

        // 1. Immediate sync for testing if configured
        if (_config.RunInstrumentsSyncImmediately)
        {
            _logger.LogInformation("RunInstrumentsSyncImmediately flag is TRUE. Running instrument sync immediately...");
            try
            {
                await _syncService.SyncInstrumentsAsync(stoppingToken);
                _logger.LogInformation("Immediate instrument sync completed successfully.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to complete immediate instrument sync.");
            }
        }
        else
        {
            _logger.LogInformation("RunInstrumentsSyncImmediately flag is FALSE. Skipping startup sync.");
        }

        // 2. Schedule loop (runs every Monday morning at 9:05 AM IST)
        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = GetDelayUntilNextMonday905AM();
            _logger.LogInformation("Next scheduled instrument sync in {Hours:F1} hour(s) (at Monday 9:05 AM IST).", delay.TotalHours);

            try
            {
                await Task.Delay(delay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            _logger.LogInformation("Scheduled Monday 9:05 AM IST sync triggered. Syncing instruments...");
            try
            {
                await _syncService.SyncInstrumentsAsync(stoppingToken);
                _logger.LogInformation("Scheduled instrument sync completed successfully.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to complete scheduled instrument sync.");
            }
        }

        _logger.LogInformation("InstrumentSyncWorker has stopped.");
    }

    private TimeSpan GetDelayUntilNextMonday905AM()
    {
        var now = DateTime.UtcNow;
        TimeZoneInfo indianTimeZone;
        try
        {
            indianTimeZone = TimeZoneInfo.FindSystemTimeZoneById("India Standard Time");
        }
        catch (TimeZoneNotFoundException)
        {
            indianTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata");
        }

        var nowIst = TimeZoneInfo.ConvertTimeFromUtc(now, indianTimeZone);

        // Find next Monday 9:05 AM
        var nextMonday = nowIst.Date;
        int daysUntilMonday = ((int)DayOfWeek.Monday - (int)nowIst.DayOfWeek + 7) % 7;
        
        // If it's already Monday past 9:05 AM, target the following Monday
        bool isAlreadyMondayPast905 = daysUntilMonday == 0 && (nowIst.Hour > 9 || (nowIst.Hour == 9 && nowIst.Minute >= 5));
        if (isAlreadyMondayPast905)
        {
            daysUntilMonday = 7;
        }
        
        nextMonday = nextMonday.AddDays(daysUntilMonday).AddHours(9).AddMinutes(5);
        var delay = nextMonday - nowIst;
        
        // Ensure delay is not negative (e.g. clock adjustments)
        return delay < TimeSpan.Zero ? TimeSpan.FromMinutes(1) : delay;
    }
}
