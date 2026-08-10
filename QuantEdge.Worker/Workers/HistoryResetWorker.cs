using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using QuantEdge.Domain.Entities;
using QuantEdge.Infrastructure.Configurations;
using QuantEdge.Infrastructure.Interfaces;
using QuantEdge.Infrastructure.Persistence.Repositories;

namespace QuantEdge.Worker.Workers;

public class HistoryResetOptions
{
    public string Symbol { get; set; } = "All";
    public string? StartDate { get; set; }
    public string? EndDate { get; set; }
    public string? Timeframe { get; set; }
}

/// <summary>
/// Background worker service that accepts command-line / configuration arguments 
/// (Symbol, StartDate, EndDate, Timeframe) to clear existing candles & indicators 
/// and fetch fresh historical data from Zerodha & recalculate technical indicators.
/// </summary>
public class HistoryResetWorker : BackgroundService
{
    private readonly IHistoricalDataService _historicalDataService;
    private readonly IIndicatorService _indicatorService;
    private readonly IStockMasterRepository _stockMasterRepository;
    private readonly IMarketCandleRepository _candleRepository;
    private readonly IMarketIndicatorRepository _indicatorRepository;
    private readonly IMarketDataCacheService? _cacheService;
    private readonly BrokerConfig _config;
    private readonly HistoryResetOptions _options;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<HistoryResetWorker> _logger;

    public HistoryResetWorker(
        IHistoricalDataService historicalDataService,
        IIndicatorService indicatorService,
        IStockMasterRepository stockMasterRepository,
        IMarketCandleRepository candleRepository,
        IMarketIndicatorRepository indicatorRepository,
        IOptions<BrokerConfig> config,
        IOptions<HistoryResetOptions> options,
        IHostApplicationLifetime lifetime,
        ILogger<HistoryResetWorker> logger,
        IMarketDataCacheService? cacheService = null)
    {
        _historicalDataService = historicalDataService ?? throw new ArgumentNullException(nameof(historicalDataService));
        _indicatorService = indicatorService ?? throw new ArgumentNullException(nameof(indicatorService));
        _stockMasterRepository = stockMasterRepository ?? throw new ArgumentNullException(nameof(stockMasterRepository));
        _candleRepository = candleRepository ?? throw new ArgumentNullException(nameof(candleRepository));
        _indicatorRepository = indicatorRepository ?? throw new ArgumentNullException(nameof(indicatorRepository));
        _config = config?.Value ?? throw new ArgumentNullException(nameof(config));
        _options = options?.Value ?? new HistoryResetOptions();
        _lifetime = lifetime ?? throw new ArgumentNullException(nameof(lifetime));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _cacheService = cacheService;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("HistoryResetWorker background task starting up...");

        try
        {
            await Task.Delay(1000, stoppingToken);

            // 1. Resolve Target Stock(s)
            string rawSymbol = (_options.Symbol ?? "All").Trim();
            bool isAllStocks = string.IsNullOrWhiteSpace(rawSymbol) ||
                               rawSymbol.Equals("all", StringComparison.OrdinalIgnoreCase) ||
                               rawSymbol.Equals("all stocks", StringComparison.OrdinalIgnoreCase) ||
                               rawSymbol.Equals("all_stocks", StringComparison.OrdinalIgnoreCase) ||
                               rawSymbol.Equals("null", StringComparison.OrdinalIgnoreCase);

            List<StockMaster> targetStocks;
            if (!isAllStocks)
            {
                var symbols = rawSymbol.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                targetStocks = new List<StockMaster>();
                foreach (var sym in symbols)
                {
                    var stock = await _stockMasterRepository.GetBySymbolAsync(sym);
                    if (stock != null)
                    {
                        targetStocks.Add(stock);
                    }
                    else
                    {
                        _logger.LogWarning("Symbol '{Symbol}' not found in StockMaster table.", sym);
                    }
                }

                if (!targetStocks.Any())
                {
                    _logger.LogError("None of the specified symbols [{Symbols}] were found in StockMaster database table. Aborting job.", rawSymbol);
                    _lifetime.StopApplication();
                    return;
                }
            }
            else
            {
                targetStocks = (await _stockMasterRepository.GetActiveStocksAsync()).ToList();
                if (!targetStocks.Any())
                {
                    targetStocks = (await _stockMasterRepository.GetAllAsync()).ToList();
                }
            }

            // 2. Resolve Date Bounds (Default: Today dd/MM/yyyy)
            DateTime startDate = ParseDate(_options.StartDate) ?? DateTime.UtcNow.Date;
            DateTime endDate = ParseDate(_options.EndDate) ?? DateTime.UtcNow.Date;

            if (startDate > endDate)
            {
                _logger.LogError("Invalid date range: StartDate ({Start:dd/MM/yyyy}) cannot be later than EndDate ({End:dd/MM/yyyy}).", startDate, endDate);
                _lifetime.StopApplication();
                return;
            }

            var indianTimeZone = TimeZoneInfo.FindSystemTimeZoneById("India Standard Time");
            DateTime startIst = startDate.Date.Add(new TimeSpan(9, 15, 0));
            DateTime endIst = endDate.Date.Add(new TimeSpan(15, 30, 0));

            DateTime startUtc = TimeZoneInfo.ConvertTimeToUtc(startIst, indianTimeZone);
            DateTime endUtc = TimeZoneInfo.ConvertTimeToUtc(endIst, indianTimeZone);

            // 3. Resolve Target Timeframes
            string rawTf = (_options.Timeframe ?? "").Trim().ToLower();
            List<string> targetTimeframes;

            if (string.IsNullOrWhiteSpace(rawTf) || rawTf.Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                targetTimeframes = new List<string> { "1m", "5m", "15m", "60m", "1d" };
            }
            else
            {
                targetTimeframes = rawTf.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(tf => new[] { "1m", "3m", "5m", "15m", "30m", "60m", "1d" }.Contains(tf))
                    .ToList();

                if (!targetTimeframes.Any())
                {
                    targetTimeframes = new List<string> { "1m", "5m", "15m", "60m", "1d" };
                }
            }

            _logger.LogInformation("================================================================================");
            _logger.LogInformation(" History Reset Worker Executing Job:");
            _logger.LogInformation(" - Target Symbol(s) : {Symbol} ({Count} stock(s))", isAllStocks ? "ALL Active Stocks" : rawSymbol.ToUpper(), targetStocks.Count);
            _logger.LogInformation(" - Start Date       : {StartDate:dd/MM/yyyy}", startDate);
            _logger.LogInformation(" - End Date         : {EndDate:dd/MM/yyyy}", endDate);
            _logger.LogInformation(" - Timeframe(s)     : [{Timeframes}]", string.Join(", ", targetTimeframes));
            _logger.LogInformation("================================================================================");

            foreach (var tf in targetTimeframes)
            {
                if (stoppingToken.IsCancellationRequested) break;

                _logger.LogInformation("Clearing existing candles & indicators for timeframe {Timeframe} from {Start:dd/MM/yyyy} to {End:dd/MM/yyyy}...", tf, startDate, endDate);

                // Bulk clear for all stocks, or single clear for specific stock
                if (isAllStocks)
                {
                    await _candleRepository.DeleteHistoryRangeAsync(null, tf, startUtc, endUtc);
                    await _indicatorRepository.DeleteIndicatorsRangeAsync(null, tf, startUtc, endUtc);
                    _cacheService?.ClearCache(null, tf);
                }
                else
                {
                    foreach (var stock in targetStocks)
                    {
                        await _candleRepository.DeleteHistoryRangeAsync(stock.Symbol, tf, startUtc, endUtc);
                        await _indicatorRepository.DeleteIndicatorsRangeAsync(stock.Symbol, tf, startUtc, endUtc);
                        _cacheService?.ClearCache(stock.Symbol, tf);
                    }
                }

                int total = targetStocks.Count;
                int processed = 0;

                var semaphore = new SemaphoreSlim(5);
                var syncTasks = targetStocks.Select(async stock =>
                {
                    await semaphore.WaitAsync(stoppingToken);
                    try
                    {
                        await _historicalDataService.FetchHistoricalCandlesAsync(stock.Symbol, tf, startUtc, endUtc, stoppingToken);
                        await _indicatorService.BackfillHistoricalIndicatorsAsync(stock.Symbol, tf, startUtc, endUtc);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to reset history for {Symbol} ({Timeframe}).", stock.Symbol, tf);
                    }
                    finally
                    {
                        _cacheService?.ClearCache(stock.Symbol, tf);
                        semaphore.Release();
                        int current = Interlocked.Increment(ref processed);
                        double pct = Math.Round((double)current / total * 100, 1);
                        _logger.LogInformation("Progress ({Timeframe}): [{Processed}/{Total}] {Symbol} ({Percent}%) completed.", tf, current, total, stock.Symbol, pct);
                    }
                });

                await Task.WhenAll(syncTasks);
                _logger.LogInformation("Completed history reset for timeframe {Timeframe}.", tf);
            }

            _logger.LogInformation("Successfully completed history reset job for all target stocks and timeframes.");
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("HistoryResetWorker task was cancelled.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception occurred in HistoryResetWorker.");
        }
        finally
        {
            _lifetime.StopApplication();
        }
    }

    private static DateTime? ParseDate(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;

        string cleanInput = input.Trim();
        string[] formats = new[] { 
            "dd/MM/yyyy", 
            "d/M/yyyy", 
            "dd-MM-yyyy", 
            "d-M-yyyy", 
            "yyyy-MM-dd", 
            "yyyy/MM/dd", 
            "MM/dd/yyyy" 
        };

        if (DateTime.TryParseExact(cleanInput, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime parsedDate))
        {
            return parsedDate;
        }

        if (DateTime.TryParse(cleanInput, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime fallbackDate))
        {
            return fallbackDate;
        }

        return null;
    }
}
