using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using QuantEdge.Infrastructure.Helpers;
using QuantEdge.Infrastructure.Interfaces;
using QuantEdge.Infrastructure.Persistence.Repositories;
using QuantEdge.Infrastructure.Services;

namespace QuantEdge.Worker.Workers;

/// <summary>
/// Makes the daily (1d) candles of the last completed trading day final from Kite once its session has closed.
/// The 30-minute swing job's last sync is the 15:15 slot, so without this the stored daily row stays a 15:15 snapshot
/// until the next trading day's first sync - for a Friday or a pre-holiday day that's the whole weekend / holiday.
/// Works by trading day (<see cref="IMarketHoursService.GetLastCompletedTradingDayAsync"/>), not by weekday, so a
/// process started on a Saturday, Sunday or holiday still finalises the previous trading day once.
/// Runs in the plain "marketdatafeed" worker process only, next to the NSE bhavcopy load (18:30 IST), which then
/// reconciles the closes against NSE's official values.
/// </summary>
public class DailyCandleFinalizeWorker : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(30);
    // Kite's day candle settles a few minutes after the 15:30 close.
    private static readonly TimeSpan FinalizeAfterIst = new(15, 45, 0);
    private const string ReferenceSymbol = "NIFTY 50";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DailyCandleFinalizeWorker> _logger;

    private DateOnly? _finalizedTradingDay;

    public DailyCandleFinalizeWorker(IServiceScopeFactory scopeFactory, ILogger<DailyCandleFinalizeWorker> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken);

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
                _logger.LogError(ex, "DailyCandleFinalizeWorker cycle failed.");
            }

            await Task.Delay(CheckInterval, stoppingToken);
        }
    }

    private async Task RunOnceAsync(CancellationToken stoppingToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var marketHours = scope.ServiceProvider.GetRequiredService<IMarketHoursService>();

        var nowIst = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneHelper.IndianTimeZone);
        var tradingDay = await marketHours.GetLastCompletedTradingDayAsync();
        if (_finalizedTradingDay == tradingDay) return;
        if (tradingDay == DateOnly.FromDateTime(nowIst) && nowIst.TimeOfDay < FinalizeAfterIst) return;

        var stockMaster = scope.ServiceProvider.GetRequiredService<IStockMasterRepository>();
        var historicalData = scope.ServiceProvider.GetRequiredService<IHistoricalDataService>();
        var candleRepo = scope.ServiceProvider.GetRequiredService<IMarketCandleRepository>();

        var symbols = (await stockMaster.GetActiveStocksAsync()).Select(s => s.Symbol).ToList();
        if (!symbols.Contains(ReferenceSymbol)) symbols.Add(ReferenceSymbol);

        _logger.LogInformation("Finalising {Date:yyyy-MM-dd} daily candles from Kite for {Count} symbols...", tradingDay, symbols.Count);
        foreach (var symbol in symbols)
        {
            stoppingToken.ThrowIfCancellationRequested();
            try
            {
                await historicalData.SyncGapsAsync(symbol, "1d", stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Daily candle finalise failed for {Symbol} ({Date:yyyy-MM-dd}).", symbol, tradingDay);
            }
        }

        // SyncGapsAsync swallows fetch failures (e.g. no valid Zerodha token yet), so only call the day done once the
        // reference index actually has its row - otherwise retry on the next cycle.
        var last = (await candleRepo.GetHistoryAsync(ReferenceSymbol, "1d", limit: 1)).FirstOrDefault();
        if (last != null && DateOnly.FromDateTime(DayChangeCalculator.IstDate(last.CandleTime)) >= tradingDay)
        {
            _finalizedTradingDay = tradingDay;
            _logger.LogInformation("Daily candles for {Date:yyyy-MM-dd} finalised.", tradingDay);
        }
        else
        {
            _logger.LogWarning("{Symbol} has no {Date:yyyy-MM-dd} daily candle after the finalise run - retrying in {Minutes} min.",
                ReferenceSymbol, tradingDay, (int)CheckInterval.TotalMinutes);
        }
    }
}
