using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using QuantEdge.Infrastructure.Interfaces;
using QuantEdge.Infrastructure.Persistence.Repositories;

namespace QuantEdge.API.Controllers;

[ApiController]
[Route("marketdata")]
public class MarketDataController : ControllerBase
{
    private readonly IStockMasterRepository _stockMasterRepository;
    private readonly IMarketCandleRepository _candleRepository;
    private readonly IMarketIndicatorRepository _indicatorRepository;
    private readonly ITradingSignalRepository _tradingSignalRepository;
    private readonly IInstrumentSyncService _instrumentSyncService;
    private readonly IHistoricalDataService _historicalDataService;
    private readonly ILogger<MarketDataController> _logger;
    private readonly IMarketDataCacheService? _cacheService;
    private readonly Microsoft.Extensions.DependencyInjection.IServiceScopeFactory _scopeFactory;
    private readonly Microsoft.AspNetCore.SignalR.IHubContext<QuantEdge.Infrastructure.Hubs.MarketDataHub> _hubContext;

    public MarketDataController(
        IStockMasterRepository stockMasterRepository,
        IMarketCandleRepository candleRepository,
        IMarketIndicatorRepository indicatorRepository,
        ITradingSignalRepository tradingSignalRepository,
        IInstrumentSyncService instrumentSyncService,
        IHistoricalDataService historicalDataService,
        ILogger<MarketDataController> logger,
        Microsoft.Extensions.DependencyInjection.IServiceScopeFactory scopeFactory,
        Microsoft.AspNetCore.SignalR.IHubContext<QuantEdge.Infrastructure.Hubs.MarketDataHub> hubContext,
        IMarketDataCacheService? cacheService = null)
    {
        _stockMasterRepository = stockMasterRepository ?? throw new ArgumentNullException(nameof(stockMasterRepository));
        _candleRepository = candleRepository ?? throw new ArgumentNullException(nameof(candleRepository));
        _indicatorRepository = indicatorRepository ?? throw new ArgumentNullException(nameof(indicatorRepository));
        _tradingSignalRepository = tradingSignalRepository ?? throw new ArgumentNullException(nameof(tradingSignalRepository));
        _instrumentSyncService = instrumentSyncService ?? throw new ArgumentNullException(nameof(instrumentSyncService));
        _historicalDataService = historicalDataService ?? throw new ArgumentNullException(nameof(historicalDataService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _hubContext = hubContext ?? throw new ArgumentNullException(nameof(hubContext));
        _cacheService = cacheService;
    }

    /// <summary>
    /// Gets all active stock symbols from the StockMaster database.
    /// </summary>
    [HttpGet("stocks")]
    public async Task<IActionResult> GetActiveStocks()
    {
        try
        {
            var stocks = await _stockMasterRepository.GetActiveStocksAsync();
            var list = stocks.Select(s => new { s.Symbol, s.InstrumentToken }).ToList();
            return Ok(list);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch active stock symbols.");
            return StatusCode(500, $"Internal server error: {ex.Message}");
        }
    }

    /// <summary>
    /// Gets a single stock's master details by its symbol.
    /// </summary>
    [HttpGet("stock-details/{symbol}")]
    public async Task<IActionResult> GetStockDetails(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol)) return BadRequest("Symbol parameter is required.");

        try
        {
            var stock = await _stockMasterRepository.GetBySymbolAsync(symbol);
            if (stock == null)
            {
                return NotFound($"Stock with symbol {symbol} not found.");
            }
            return Ok(stock);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch stock details for symbol {Symbol}.", symbol);
            return StatusCode(500, $"Internal server error: {ex.Message}");
        }
    }

    /// <summary>
    /// Gets all stock instruments (active and inactive) from the StockMaster database.
    /// Supports an optional query parameter `sync=true` to manually trigger an immediate update from Zerodha.
    /// </summary>
    [HttpGet("instruments")]
    public async Task<IActionResult> GetAllInstruments([FromQuery] bool sync = false)
    {
        try
        {
            if (sync)
            {
                _logger.LogInformation("HTTP Request: Triggering manual sync of Zerodha instruments...");
                await _instrumentSyncService.SyncInstrumentsAsync(HttpContext.RequestAborted);
                _logger.LogInformation("HTTP Request: Manual sync completed successfully.");
            }

            var instruments = await _stockMasterRepository.GetAllAsync();
            return Ok(instruments);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch all stock instruments.");
            return StatusCode(500, $"Internal server error: {ex.Message}");
        }
    }

    /// <summary>
    /// Returns combined candles, indicators, and signals for the given symbol and timeframe.
    /// Supports optional `before` parameter (Unix timestamp in milliseconds) for historical pagination.
    /// </summary>
    [HttpGet("chart-data")]
    public async Task<IActionResult> GetChartData(
        [FromQuery] string symbol, 
        [FromQuery] string timeframe, 
        [FromQuery] int limit = 500,
        [FromQuery] long? before = null)
    {
        if (string.IsNullOrWhiteSpace(symbol)) return BadRequest("Symbol parameter is required.");
        if (string.IsNullOrWhiteSpace(timeframe)) return BadRequest("Timeframe parameter is required.");

        _logger.LogInformation("HTTP Request: Fetching combined chart data for symbol {Symbol} ({Timeframe}, Limit: {Limit}, Before: {Before})", symbol, timeframe, limit, before);

        try
        {
            DateTime? beforeDateTime = before.HasValue 
                ? DateTimeOffset.FromUnixTimeMilliseconds(before.Value).UtcDateTime 
                : null;

            IEnumerable<QuantEdge.Domain.Entities.MarketCandle> rawCandles;
            IEnumerable<QuantEdge.Domain.Entities.MarketIndicator> rawIndicators;
            IEnumerable<QuantEdge.Domain.Entities.TradingSignal> rawSignals;

            if (!beforeDateTime.HasValue && _cacheService != null)
            {
                // Serve active chart dataset directly from In-Memory RAM Cache (< 1ms microsecond speed)
                var candlesTask = _cacheService.GetRecentCandlesAsync(symbol, timeframe, limit);
                var indicatorsTask = _cacheService.GetRecentIndicatorsAsync(symbol, timeframe, limit);
                var signalsTask = _tradingSignalRepository.GetRecentSignalsAsync(limit);

                await Task.WhenAll(candlesTask, indicatorsTask, signalsTask);

                rawCandles = candlesTask.Result;
                rawIndicators = indicatorsTask.Result;
                rawSignals = signalsTask.Result;
            }
            else
            {
                // Fallback to PostgreSQL for deep historical pagination
                var candlesTask = _candleRepository.GetHistoryAsync(symbol, timeframe, limit, beforeDateTime);
                var indicatorsTask = _indicatorRepository.GetHistoryAsync(symbol, timeframe, limit, beforeDateTime);
                var signalsTask = _tradingSignalRepository.GetRecentSignalsAsync(limit);

                await Task.WhenAll(candlesTask, indicatorsTask, signalsTask);

                rawCandles = candlesTask.Result;
                rawIndicators = indicatorsTask.Result;
                rawSignals = signalsTask.Result;
            }

            // Deduplicate and order candles chronologically (oldest first for Lightweight Charts)
            var candles = rawCandles
                .GroupBy(c => c.CandleTime)
                .Select(g => g.First())
                .OrderBy(c => c.CandleTime)
                .ToList();

            var indicators = rawIndicators
                .GroupBy(i => i.CandleTime.ToString("yyyy-MM-dd HH:mm:ssZ"))
                .ToDictionary(g => g.Key, g => g.First());

            var signalsByTimeSec = rawSignals
                .Where(s => s.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase))
                .GroupBy(s => (s.CandleTime.Kind == DateTimeKind.Utc ? new DateTimeOffset(s.CandleTime) : new DateTimeOffset(DateTime.SpecifyKind(s.CandleTime, DateTimeKind.Utc))).ToUnixTimeSeconds())
                .ToDictionary(g => g.Key, g => g.First());

            // Match and build chart DTO list ordered chronologically (oldest first for Lightweight Charts)
            var chartData = candles.Select(c =>
            {
                DateTime utcTime = c.CandleTime.Kind == DateTimeKind.Utc 
                    ? c.CandleTime 
                    : DateTime.SpecifyKind(c.CandleTime, DateTimeKind.Utc);

                long timeMs = new DateTimeOffset(utcTime).ToUnixTimeMilliseconds();
                long timeSec = timeMs / 1000;
                string key = c.CandleTime.ToString("yyyy-MM-dd HH:mm:ssZ");
                
                indicators.TryGetValue(key, out var ind);
                signalsByTimeSec.TryGetValue(timeSec, out var sig);

                return new
                {
                    time = timeMs,
                    open = c.Open,
                    high = c.High,
                    low = c.Low,
                    close = c.Close,
                    volume = c.Volume,
                    rsi = ind?.RSI,
                    ema20 = ind?.EMA20,
                    ema50 = ind?.EMA50,
                    macd = ind?.MACD,
                    signalLine = ind?.SignalLine,
                    vwap = ind?.VWAP,
                    signalType = sig?.SignalType,
                    signalScore = sig?.SignalStrength,
                    signalReason = sig?.Reason
                };
            })
            .OrderBy(d => d.time)
            .ToList();

            return Ok(chartData);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to compile chart data for symbol {Symbol}.", symbol);
            return StatusCode(500, $"Internal server error: {ex.Message}");
        }
    }

    /// <summary>
    /// Computes day-wise Profit/Loss percentage for the given symbol across the current trading week
    /// (Monday through today, IST). Daily P/L% = ((Close - PreviousTradingDayClose) / PreviousTradingDayClose) * 100.
    /// The first trading day shown in the week uses the close of the most recent trading day before Monday as its baseline.
    /// Reuses the same daily candle source (live cache with DB fallback) as the main price chart.
    /// </summary>
    [HttpGet("weekly-pnl")]
    public async Task<IActionResult> GetWeeklyPnl([FromQuery] string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol)) return BadRequest("Symbol parameter is required.");

        try
        {
            var indianTz = QuantEdge.Infrastructure.Helpers.TimeZoneHelper.IndianTimeZone;
            DateTime nowIst = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, indianTz);
            DateTime todayIst = nowIst.Date;
            int daysSinceMonday = ((int)todayIst.DayOfWeek - (int)DayOfWeek.Monday + 7) % 7;
            DateTime mondayIst = todayIst.AddDays(-daysSinceMonday);

            // Reuse the same live-cache-first / DB-fallback daily candle source as GetChartData above.
            var rawCandles = await GetRecentCandlesForSymbolAsync(symbol, "1d", limit: 15);

            // Group by IST trading date (candle_time is stored UTC) in case of any duplicate entries per day.
            var dailyCloses = rawCandles
                .Select(c => new
                {
                    Date = TimeZoneInfo.ConvertTimeFromUtc(
                        c.CandleTime.Kind == DateTimeKind.Utc ? c.CandleTime : DateTime.SpecifyKind(c.CandleTime, DateTimeKind.Utc),
                        indianTz).Date,
                    c.Close
                })
                .GroupBy(x => x.Date)
                .Select(g => new { Date = g.Key, Close = g.Last().Close })
                .OrderBy(x => x.Date)
                .ToList();

            // The "1d" candle for today is only finalized at end-of-day, so during an ongoing trading session
            // it won't exist yet. Fall back to the latest available intraday candle close for today (the same
            // live-updated source that drives the real-time price shown elsewhere on the dashboard) so today's
            // bar reflects the current in-progress price instead of being omitted until market close.
            if (!dailyCloses.Any(d => d.Date == todayIst))
            {
                decimal? todaysLiveClose = await GetLatestIntradayCloseForDateAsync(symbol, todayIst, indianTz);
                if (todaysLiveClose.HasValue && todaysLiveClose.Value > 0m)
                {
                    dailyCloses.Add(new { Date = todayIst, Close = todaysLiveClose.Value });
                    dailyCloses = dailyCloses.OrderBy(x => x.Date).ToList();
                }
            }

            var weekDays = dailyCloses.Where(d => d.Date >= mondayIst && d.Date <= todayIst).ToList();

            var days = new List<object>();
            for (int i = 0; i < weekDays.Count; i++)
            {
                decimal? prevClose = i == 0
                    ? dailyCloses.Where(d => d.Date < mondayIst)
                        .OrderByDescending(d => d.Date)
                        .Select(d => (decimal?)d.Close)
                        .FirstOrDefault()
                    : weekDays[i - 1].Close;

                // Skip days where a valid baseline can't be determined rather than fabricating a value.
                if (!prevClose.HasValue || prevClose.Value <= 0m) continue;

                decimal pnlPercent = Math.Round(((weekDays[i].Close - prevClose.Value) / prevClose.Value) * 100m, 2);

                days.Add(new
                {
                    date = weekDays[i].Date.ToString("yyyy-MM-dd"),
                    day = weekDays[i].Date.ToString("ddd"),
                    close = weekDays[i].Close,
                    previousClose = prevClose.Value,
                    pnlPercent
                });
            }

            return Ok(new
            {
                symbol = symbol.ToUpper(),
                weekStart = mondayIst.ToString("yyyy-MM-dd"),
                weekEnd = todayIst.ToString("yyyy-MM-dd"),
                hasData = days.Count > 0,
                message = days.Count > 0 ? null : "Insufficient historical data to compute the current week's P/L for this stock.",
                days
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to compute weekly P/L for symbol {Symbol}.", symbol);
            return StatusCode(500, $"Internal server error: {ex.Message}");
        }
    }

    /// <summary>
    /// Fetches recent candles for a symbol/timeframe using the same live-cache-first, DB-fallback
    /// pattern as GetChartData, so callers don't duplicate that source-selection logic.
    /// </summary>
    private async Task<List<QuantEdge.Domain.Entities.MarketCandle>> GetRecentCandlesForSymbolAsync(string symbol, string timeframe, int limit)
    {
        if (_cacheService != null)
        {
            return await _cacheService.GetRecentCandlesAsync(symbol, timeframe, limit);
        }

        return (await _candleRepository.GetHistoryAsync(symbol, timeframe, limit))
            .OrderBy(c => c.CandleTime)
            .ToList();
    }

    /// <summary>
    /// Finds the latest available intraday close for a symbol on a given IST trading date, trying
    /// progressively coarser timeframes. Used as a fallback for "today" when the daily ("1d") candle
    /// hasn't been finalized yet (it's only written at end-of-day) - this is the same live-updated
    /// intraday data that already drives the real-time price shown elsewhere on the dashboard.
    /// </summary>
    private async Task<decimal?> GetLatestIntradayCloseForDateAsync(string symbol, DateTime targetDateIst, TimeZoneInfo indianTz)
    {
        foreach (var timeframe in new[] { "1m", "5m", "15m", "60m" })
        {
            var candles = await GetRecentCandlesForSymbolAsync(symbol, timeframe, limit: 400);
            var lastForDate = candles
                .Where(c =>
                {
                    DateTime utc = c.CandleTime.Kind == DateTimeKind.Utc ? c.CandleTime : DateTime.SpecifyKind(c.CandleTime, DateTimeKind.Utc);
                    return TimeZoneInfo.ConvertTimeFromUtc(utc, indianTz).Date == targetDateIst;
                })
                .OrderBy(c => c.CandleTime)
                .LastOrDefault();

            if (lastForDate != null) return lastForDate.Close;
        }

        return null;
    }

    /// <summary>
    /// Deletes all history for today for a specific symbol and timeframe.
    /// </summary>
    [HttpDelete("history/today/{symbol}")]
    public async Task<IActionResult> DeleteTodayHistory(string symbol, [FromQuery] string timeframe)
    {
        if (string.IsNullOrWhiteSpace(symbol)) return BadRequest("Symbol parameter is required.");
        if (string.IsNullOrWhiteSpace(timeframe)) return BadRequest("Timeframe parameter is required.");
        try
        {
            await _candleRepository.DeleteTodayHistoryAsync(symbol, timeframe);
            await _indicatorRepository.DeleteTodayIndicatorsAsync(symbol, timeframe);
            return Ok(new { message = $"Successfully deleted today's history and indicators for {symbol} ({timeframe})." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete today's history for {Symbol} ({Timeframe}).", symbol, timeframe);
            return StatusCode(500, $"Internal server error: {ex.Message}");
        }
    }

    /// <summary>
    /// Purges candles and indicators matching created_at date across all timeframes and updates stock coverage flags.
    /// </summary>
    [HttpPost("history/purge-by-date")]
    public async Task<IActionResult> PurgeHistoryByDate(
        [FromQuery] DateTime date,
        [FromQuery] string? symbol = null)
    {
        try
        {
            var (deletedCandles, deletedIndicators, affectedStocks) = await _candleRepository.PurgeHistoryByDateAsync(date, symbol);
            
            if (_cacheService != null)
            {
                _cacheService.ClearCache(symbol);
            }

            string symbolText = string.IsNullOrWhiteSpace(symbol) ? "ALL stocks" : $"symbol {symbol}";
            return Ok(new
            {
                success = true,
                message = $"Successfully purged history created on {date:yyyy-MM-dd} for {symbolText}.",
                deletedCandles,
                deletedIndicators,
                affectedStocks
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to purge history data for date {Date}.", date);
            return StatusCode(500, $"Internal server error: {ex.Message}");
        }
    }

    /// <summary>
    /// Fetches historical data from Zerodha for today for a specific symbol and timeframe.
    /// </summary>
    [HttpPost("history/today/{symbol}")]
    public async Task<IActionResult> CreateTodayHistory(string symbol, [FromQuery] string timeframe)
    {
        if (string.IsNullOrWhiteSpace(symbol)) return BadRequest("Symbol parameter is required.");
        if (string.IsNullOrWhiteSpace(timeframe)) return BadRequest("Timeframe parameter is required.");
        try
        {
            DateTime fromTime = DateTime.UtcNow.Date; // Start of today (UTC)
            DateTime toTime = DateTime.UtcNow;

            // Fetch from Zerodha using the service
            await _historicalDataService.FetchHistoricalCandlesAsync(symbol, timeframe, fromTime, toTime, CancellationToken.None);

            return Ok(new { message = $"Successfully fetched today's history from Zerodha for {symbol} ({timeframe})." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch today's history from Zerodha for {Symbol} ({Timeframe}).", symbol, timeframe);
            return StatusCode(500, $"Internal server error: {ex.Message}");
        }
    }

    /// <summary>
    /// Resets history for a date range (fromDate to toDate) for active stocks for a specific timeframe in the background.
    /// Clears existing candle & indicator records for the date range and fetches/inserts updated records.
    /// </summary>
    [HttpPost("history/reset")]
    public IActionResult ResetHistoryRange(
        [FromQuery] string timeframe,
        [FromQuery] DateTime? fromDate = null,
        [FromQuery] DateTime? toDate = null,
        [FromQuery] string? symbol = null)
    {
        if (string.IsNullOrWhiteSpace(timeframe)) return BadRequest("Timeframe parameter is required.");

        var indianTz = QuantEdge.Infrastructure.Helpers.TimeZoneHelper.IndianTimeZone;
        DateTime sDate = fromDate?.Date ?? DateTime.UtcNow.Date;
        DateTime eDate = toDate?.Date ?? DateTime.UtcNow.Date;

        DateTime startIst = sDate.Add(new TimeSpan(9, 15, 0));
        DateTime endIst = eDate.Add(new TimeSpan(15, 30, 0));

        DateTime startUtc = TimeZoneInfo.ConvertTimeToUtc(startIst, indianTz);
        DateTime endUtc = TimeZoneInfo.ConvertTimeToUtc(endIst, indianTz);

        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var stockRepo = scope.ServiceProvider.GetRequiredService<IStockMasterRepository>();
                var candleRepo = scope.ServiceProvider.GetRequiredService<IMarketCandleRepository>();
                var indicatorRepo = scope.ServiceProvider.GetRequiredService<IMarketIndicatorRepository>();

                string cleanSymbol = symbol?.Trim() ?? "";
                bool isAllStocks = string.IsNullOrWhiteSpace(cleanSymbol) ||
                                   cleanSymbol.Equals("all", StringComparison.OrdinalIgnoreCase) ||
                                   cleanSymbol.Equals("all stocks", StringComparison.OrdinalIgnoreCase) ||
                                   cleanSymbol.Equals("all_stocks", StringComparison.OrdinalIgnoreCase) ||
                                   cleanSymbol.Equals("null", StringComparison.OrdinalIgnoreCase) ||
                                   cleanSymbol.Equals("undefined", StringComparison.OrdinalIgnoreCase);

                List<QuantEdge.Domain.Entities.StockMaster> targetStocks;
                if (!isAllStocks)
                {
                    var singleStock = await stockRepo.GetBySymbolAsync(cleanSymbol);
                    targetStocks = singleStock != null ? new List<QuantEdge.Domain.Entities.StockMaster> { singleStock } : new List<QuantEdge.Domain.Entities.StockMaster>();
                }
                else
                {
                    targetStocks = (await stockRepo.GetActiveStocksAsync()).ToList();
                    if (!targetStocks.Any())
                    {
                        targetStocks = (await stockRepo.GetAllAsync()).ToList();
                    }
                }

                int total = targetStocks.Count;
                if (total == 0)
                {
                    await _hubContext.Clients.All.SendAsync("SyncComplete", new { message = "No active stocks found to sync." });
                    return;
                }

                int processed = 0;

                await _hubContext.Clients.All.SendAsync("SyncProgress", new { 
                    message = $"Starting reset for {total} stocks ({timeframe}) from {startUtc:yyyy-MM-dd} to {endUtc:yyyy-MM-dd}...", 
                    progress = 0 
                });

                // If symbol is not specified or specifies ALL STOCKS, perform a single bulk delete for candles & indicators for maximum performance
                if (isAllStocks)
                {
                    await candleRepo.DeleteHistoryRangeAsync(null, timeframe, startUtc, endUtc);
                    await indicatorRepo.DeleteIndicatorsRangeAsync(null, timeframe, startUtc, endUtc);
                    _cacheService?.ClearCache(null, timeframe);
                }

                // Process stocks concurrently with SemaphoreSlim to limit max parallelism (5 concurrent workers)
                var semaphore = new SemaphoreSlim(5);
                var syncTasks = targetStocks.Select(async stock =>
                {
                    await semaphore.WaitAsync();
                    try
                    {
                        using var innerScope = _scopeFactory.CreateScope();
                        var innerCandleRepo = innerScope.ServiceProvider.GetRequiredService<IMarketCandleRepository>();
                        var innerIndicatorRepo = innerScope.ServiceProvider.GetRequiredService<IMarketIndicatorRepository>();
                        var innerHistService = innerScope.ServiceProvider.GetRequiredService<IHistoricalDataService>();
                        var innerIndService = innerScope.ServiceProvider.GetRequiredService<IIndicatorService>();

                        // Clear records & RAM cache for specific stock if filtering by a single stock symbol
                        if (!isAllStocks)
                        {
                            await innerCandleRepo.DeleteHistoryRangeAsync(stock.Symbol, timeframe, startUtc, endUtc);
                            await innerIndicatorRepo.DeleteIndicatorsRangeAsync(stock.Symbol, timeframe, startUtc, endUtc);
                            _cacheService?.ClearCache(stock.Symbol, timeframe);
                        }

                        // Fetch fresh historical candles and backfill technical indicators for the specified date range
                        await innerHistService.FetchHistoricalCandlesAsync(stock.Symbol, timeframe, startUtc, endUtc, CancellationToken.None);
                        await innerIndService.BackfillHistoricalIndicatorsAsync(stock.Symbol, timeframe, startUtc, endUtc);
                    }
                    catch (Exception innerEx)
                    {
                        _logger.LogError(innerEx, "Failed to fetch history from Zerodha for {Symbol} ({Timeframe}) range {Start} to {End}.", stock.Symbol, timeframe, startUtc, endUtc);
                    }
                    finally
                    {
                        _cacheService?.ClearCache(stock.Symbol, timeframe);
                        semaphore.Release();
                        int currentCount = Interlocked.Increment(ref processed);
                        double pct = Math.Round((double)currentCount / total * 100, 1);
                        await _hubContext.Clients.All.SendAsync("SyncProgress", new { 
                            message = $"Synced {stock.Symbol} ({currentCount}/{total})", 
                            progress = pct 
                        });
                    }
                });

                await Task.WhenAll(syncTasks);

                await _hubContext.Clients.All.SendAsync("SyncComplete", new { 
                    message = $"Successfully synced history for {total} stocks ({timeframe}) from {startUtc:yyyy-MM-dd} to {endUtc:yyyy-MM-dd}." 
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to sync history for active stocks ({Timeframe}) in background.", timeframe);
                await _hubContext.Clients.All.SendAsync("SyncError", new { message = "Error during bulk sync: " + ex.Message });
            }
        });

        return Accepted(new { message = $"Background sync task started for timeframe {timeframe} ({startUtc:yyyy-MM-dd} to {endUtc:yyyy-MM-dd})." });
    }

    /// <summary>
    /// Resets all history for today for all active stocks for a specific timeframe in the background.
    /// </summary>
    [HttpPost("history/today/all/reset")]
    public IActionResult ResetTodayHistoryAll(
        [FromQuery] string timeframe,
        [FromQuery] DateTime? fromDate = null,
        [FromQuery] DateTime? toDate = null,
        [FromQuery] string? symbol = null)
    {
        return ResetHistoryRange(timeframe, fromDate, toDate, symbol);
    }

    /// <summary>
    /// Gets real-time memory usage statistics and Market Data Cache metrics.
    /// </summary>
    [HttpGet("memory-stats")]
    public IActionResult GetMemoryStats()
    {
        try
        {
            if (_cacheService != null)
            {
                var stats = _cacheService.GetMemoryMetrics();
                return Ok(stats);
            }

            long workingSetBytes = System.Diagnostics.Process.GetCurrentProcess().WorkingSet64;
            long gcHeapBytes = GC.GetTotalMemory(false);

            return Ok(new QuantEdge.Infrastructure.DTOs.CacheMemoryMetricsDto
            {
                ProcessWorkingSetMB = Math.Round(workingSetBytes / (1024.0 * 1024.0), 2),
                GcTotalMemoryMB = Math.Round(gcHeapBytes / (1024.0 * 1024.0), 2),
                Gen0Collections = GC.CollectionCount(0),
                Gen1Collections = GC.CollectionCount(1),
                Gen2Collections = GC.CollectionCount(2),
                TotalCachedSymbols = 0,
                TotalCachedCandles = 0,
                TotalCachedIndicators = 0,
                EstimatedCacheMemoryMB = 0
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to retrieve memory usage statistics.");
            return StatusCode(500, $"Internal server error: {ex.Message}");
        }
    }
}
