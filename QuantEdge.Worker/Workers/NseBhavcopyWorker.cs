using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using QuantEdge.Infrastructure.Helpers;
using QuantEdge.Infrastructure.Services;

namespace QuantEdge.Worker.Workers;

/// <summary>
/// Loads NSE's official end-of-day bhavcopy (Plan L.1 P9) - an NSE download, never a Zerodha call. From 18:30 IST on
/// weekdays it tries every 30 minutes until today's file is published, and also fills any of the last
/// <see cref="CatchUpDays"/> weekdays that are missing (an NSE 404 = holiday or not yet published, skipped quietly).
/// Days go oldest first and stop at the first failure: the split / bonus check compares a day with the session before it.
/// Runs in the plain "marketdatafeed" worker process only (not the per-timeframe ones) so the file is fetched once.
/// </summary>
public class NseBhavcopyWorker : BackgroundService
{
    private const int CatchUpDays = 5;
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan PublishedAfterIst = new(18, 30, 0);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<NseBhavcopyWorker> _logger;

    public NseBhavcopyWorker(IServiceScopeFactory scopeFactory, ILogger<NseBhavcopyWorker> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "NseBhavcopyWorker cycle failed.");
            }

            await Task.Delay(CheckInterval, stoppingToken);
        }
    }

    private async Task RunOnceAsync(CancellationToken stoppingToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<INseBhavcopyService>();
        var recorder = scope.ServiceProvider.GetRequiredService<IBrokerApiEventRecorder>();

        var nowIst = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneHelper.IndianTimeZone);
        for (int back = CatchUpDays; back >= 0; back--)
        {
            DateTime day = nowIst.Date.AddDays(-back);
            if (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) continue;
            if (back == 0 && nowIst.TimeOfDay < PublishedAfterIst) continue;   // today's file isn't out yet
            if (!await ProcessDayAsync(day, back == 0, nowIst, service,
                    scope.ServiceProvider.GetRequiredService<ICorporateActionService>(), recorder, _logger, stoppingToken))
            {
                break;   // retried in 30 minutes, from this same day
            }
        }

        // Market regime (Plan Phase 1): recomputed after the close on official prices, plus once at startup so the
        // 60-day history exists. DB only. A failure keeps the previous reading in force and is shown in the bell.
        bool afterClose = nowIst.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && nowIst.TimeOfDay >= PublishedAfterIst;
        if (!_regimeComputedOnce || (afterClose && _regimeComputedForIstDate != nowIst.Date))
        {
            try
            {
                var regime = scope.ServiceProvider.GetRequiredService<IMarketRegimeService>();
                var reading = await regime.ComputeAndStoreAsync();
                _regimeComputedOnce = true;
                if (afterClose) _regimeComputedForIstDate = nowIst.Date;
                if (reading != null)
                {
                    _logger.LogInformation("Market regime for {Date:yyyy-MM-dd}: {Regime} (score {Score}).", reading.TradeDate, reading.Regime, reading.Score);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Market regime computation failed.");
                recorder.RecordFailure(BrokerApiSource.Job, "market regime",
                    $"Market regime could not be computed: {ex.Message}. The previous reading stays in force; retrying in 30 min.", level: "warning");
            }
        }
    }

    private bool _regimeComputedOnce;
    private DateTime _regimeComputedForIstDate = DateTime.MinValue;

    /// <summary>
    /// Stores one day if it is missing, then runs its split / bonus check if that hasn't finished. Each failure is reported
    /// as what actually failed. False = stop here and retry this day on the next cycle.
    /// </summary>
    public static async Task<bool> ProcessDayAsync(DateTime day, bool isToday, DateTime nowIst, INseBhavcopyService service,
        ICorporateActionService corporateActions, IBrokerApiEventRecorder recorder, ILogger logger, CancellationToken stoppingToken)
    {
        if (!await service.IsLoadedAsync(day))
        {
            IReadOnlyList<NseBhavcopyRow>? rows;
            try
            {
                rows = await service.DownloadAsync(day, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "NSE bhavcopy download failed for {Day:yyyy-MM-dd}.", day);
                recorder.RecordFailure(BrokerApiSource.Nse, "bhavcopy",
                    $"NSE bhavcopy download for {day:dd-MMM-yyyy} failed: {Describe(ex)}. Daily closes stay as the last traded price; retrying in 30 min.");
                return false;
            }

            if (rows == null)
            {
                if (isToday && nowIst.TimeOfDay >= new TimeSpan(22, 0, 0))
                {
                    recorder.RecordFailure(BrokerApiSource.Nse, "bhavcopy",
                        $"NSE bhavcopy for {day:dd-MMM-yyyy} not published by 22:00 IST (holiday?). Daily closes stay as the last traded price until it loads.",
                        level: "warning");
                }
                return true;   // holiday or not out yet: nothing to compare later days against, so they can still load
            }

            BhavcopyLoadResult result;
            try
            {
                result = await service.StoreAsync(day, rows);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Saving the NSE bhavcopy failed for {Day:yyyy-MM-dd}.", day);
                recorder.RecordFailure(BrokerApiSource.Nse, "bhavcopy database",
                    $"NSE bhavcopy for {day:dd-MMM-yyyy} downloaded, but saving it to the database failed: {Describe(ex)}. Daily closes stay as the last traded price; retrying in 30 min.");
                return false;
            }

            if (result.CloseMismatches > 0)
            {
                recorder.RecordFailure(BrokerApiSource.Nse, "daily close check",
                    $"{result.CloseMismatches} stock(s) had a stored close more than 0.5% away from NSE's official close on {day:dd-MMM-yyyy} - replaced with the official values. Details on the Reconciliation page.",
                    level: "warning");
            }
        }

        try
        {
            if (await service.IsCorporateActionCheckDoneAsync(day)) return true;

            // Splits / bonuses show up as NSE's adjusted previous close - adjust our history and positions.
            await corporateActions.DetectAndApplyAsync(day);
            await service.MarkCorporateActionCheckDoneAsync(day);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Split / bonus check failed for {Day:yyyy-MM-dd}.", day);
            recorder.RecordFailure(BrokerApiSource.Nse, "corporate action",
                $"Split / bonus check for {day:dd-MMM-yyyy} failed: {Describe(ex)}. The NSE file is stored; retrying the check in 30 min.");
            return false;
        }
    }

    // Npgsql's outer message ("Exception while reading from stream") hides the reason, such as a timeout, in the inner one.
    private static string Describe(Exception ex) =>
        ex.InnerException == null ? ex.Message : $"{ex.Message} ({ex.InnerException.Message})";
}
