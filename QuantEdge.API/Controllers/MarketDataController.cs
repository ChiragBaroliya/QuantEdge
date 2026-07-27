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

            DateTime todayStartUtc = QuantEdge.Infrastructure.Services.MarketDataCacheService.GetTodayStartUtc();

            if (beforeDateTime.HasValue)
            {
                // Historical Pagination: Fetch directly from PostgreSQL DB for candles strictly before 'beforeDateTime'
                var candlesTask = _candleRepository.GetHistoryAsync(symbol, timeframe, limit, beforeDateTime);
                var indicatorsTask = _indicatorRepository.GetHistoryAsync(symbol, timeframe, limit, beforeDateTime);
                var signalsTask = _tradingSignalRepository.GetRecentSignalsAsync(limit);

                await Task.WhenAll(candlesTask, indicatorsTask, signalsTask);

                rawCandles = candlesTask.Result.Where(c => c.CandleTime < todayStartUtc || c.CandleTime < beforeDateTime);
                rawIndicators = indicatorsTask.Result.Where(i => i.CandleTime < todayStartUtc || i.CandleTime < beforeDateTime);
                rawSignals = signalsTask.Result;
            }
            else
            {
                // Initial Chart Load: Hybrid Strategy
                // 1. Fetch Today's Candles & Indicators from In-Memory Cache (< 1ms microsecond speed)
                Task<List<QuantEdge.Domain.Entities.MarketCandle>> todayCandlesTask = _cacheService != null 
                    ? _cacheService.GetTodayCandlesAsync(symbol, timeframe)
                    : Task.FromResult(new List<QuantEdge.Domain.Entities.MarketCandle>());

                Task<List<QuantEdge.Domain.Entities.MarketIndicator>> todayIndicatorsTask = _cacheService != null
                    ? _cacheService.GetTodayIndicatorsAsync(symbol, timeframe)
                    : Task.FromResult(new List<QuantEdge.Domain.Entities.MarketIndicator>());

                // 2. Fetch Previous Historical Candles & Indicators directly from PostgreSQL DB (< todayStartUtc)
                Task<IEnumerable<QuantEdge.Domain.Entities.MarketCandle>> dbHistoryCandlesTask = 
                    _candleRepository.GetHistoryAsync(symbol, timeframe, limit, beforeTime: todayStartUtc);

                Task<IEnumerable<QuantEdge.Domain.Entities.MarketIndicator>> dbHistoryIndicatorsTask = 
                    _indicatorRepository.GetHistoryAsync(symbol, timeframe, limit, beforeTime: todayStartUtc);

                Task<IEnumerable<QuantEdge.Domain.Entities.TradingSignal>> signalsTask = 
                    _tradingSignalRepository.GetRecentSignalsAsync(limit);

                await Task.WhenAll(todayCandlesTask, todayIndicatorsTask, dbHistoryCandlesTask, dbHistoryIndicatorsTask, signalsTask);

                // Combine Historical DB + Today Memory Cache
                rawCandles = dbHistoryCandlesTask.Result.Concat(todayCandlesTask.Result);
                rawIndicators = dbHistoryIndicatorsTask.Result.Concat(todayIndicatorsTask.Result);
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

            // Compute dynamic fallback indicators for candles lacking database indicator entries
            var closes = candles.Select(c => c.Close).ToList();
            var ema20List = QuantEdge.Infrastructure.Services.IndicatorCalculator.CalculateEma(closes, 20);
            var ema50List = QuantEdge.Infrastructure.Services.IndicatorCalculator.CalculateEma(closes, 50);
            var rsiList = QuantEdge.Infrastructure.Services.IndicatorCalculator.CalculateRsi(closes, 14);
            var (macdList, signalList) = QuantEdge.Infrastructure.Services.IndicatorCalculator.CalculateMacd(closes);

            // Match and build chart DTO list ordered chronologically (oldest first for Lightweight Charts)
            var chartData = candles.Select((c, i) =>
            {
                DateTime utcTime = c.CandleTime.Kind == DateTimeKind.Utc 
                    ? c.CandleTime 
                    : DateTime.SpecifyKind(c.CandleTime, DateTimeKind.Utc);

                long timeMs = new DateTimeOffset(utcTime).ToUnixTimeMilliseconds();
                long timeSec = timeMs / 1000;
                string key = c.CandleTime.ToString("yyyy-MM-dd HH:mm:ssZ");
                
                indicators.TryGetValue(key, out var ind);
                signalsByTimeSec.TryGetValue(timeSec, out var sig);

                decimal rsiVal = ind != null && ind.RSI != 0 ? ind.RSI : rsiList[i];
                decimal ema20Val = ind != null && ind.EMA20 != 0 ? ind.EMA20 : ema20List[i];
                decimal ema50Val = ind != null && ind.EMA50 != 0 ? ind.EMA50 : ema50List[i];
                decimal macdVal = ind != null && ind.MACD != 0 ? ind.MACD : macdList[i];
                decimal signalVal = ind != null && ind.SignalLine != 0 ? ind.SignalLine : signalList[i];
                decimal vwapVal = ind != null && ind.VWAP != 0 ? ind.VWAP : c.Close;

                return new
                {
                    time = timeMs,
                    open = c.Open,
                    high = c.High,
                    low = c.Low,
                    close = c.Close,
                    volume = c.Volume,
                    rsi = Math.Round(rsiVal, 2),
                    ema20 = Math.Round(ema20Val, 2),
                    ema50 = Math.Round(ema50Val, 2),
                    macd = Math.Round(macdVal, 2),
                    signalLine = Math.Round(signalVal, 2),
                    vwap = Math.Round(vwapVal, 2),
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

        DateTime startUtc = fromDate.HasValue 
            ? DateTime.SpecifyKind(fromDate.Value.Date, DateTimeKind.Utc) 
            : DateTime.UtcNow.Date;
        
        DateTime endUtc = toDate.HasValue 
            ? DateTime.SpecifyKind(toDate.Value.Date.AddDays(1).AddTicks(-1), DateTimeKind.Utc) 
            : DateTime.UtcNow;

        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var stockRepo = scope.ServiceProvider.GetRequiredService<IStockMasterRepository>();
                var candleRepo = scope.ServiceProvider.GetRequiredService<IMarketCandleRepository>();
                var indicatorRepo = scope.ServiceProvider.GetRequiredService<IMarketIndicatorRepository>();
                var historicalDataService = scope.ServiceProvider.GetRequiredService<IHistoricalDataService>();
                var indicatorService = scope.ServiceProvider.GetRequiredService<IIndicatorService>();

                List<QuantEdge.Domain.Entities.StockMaster> targetStocks;
                if (!string.IsNullOrWhiteSpace(symbol))
                {
                    var singleStock = await stockRepo.GetBySymbolAsync(symbol);
                    targetStocks = singleStock != null ? new List<QuantEdge.Domain.Entities.StockMaster> { singleStock } : new List<QuantEdge.Domain.Entities.StockMaster>();
                }
                else
                {
                    targetStocks = (await stockRepo.GetActiveStocksAsync()).ToList();
                }

                int total = targetStocks.Count;
                int processed = 0;

                await _hubContext.Clients.All.SendAsync("SyncProgress", new { 
                    message = $"Starting reset for {total} stocks ({timeframe}) from {startUtc:yyyy-MM-dd} to {endUtc:yyyy-MM-dd}...", 
                    progress = 0 
                });

                foreach (var stock in targetStocks)
                {
                    // 1. Clear records in range
                    await candleRepo.DeleteHistoryRangeAsync(stock.Symbol, timeframe, startUtc, endUtc);
                    await indicatorRepo.DeleteIndicatorsRangeAsync(stock.Symbol, timeframe, startUtc, endUtc);

                    // 2. Fetch & Insert new records
                    try
                    {
                        await historicalDataService.FetchHistoricalCandlesAsync(stock.Symbol, timeframe, startUtc, endUtc, CancellationToken.None);
                        await indicatorService.BackfillHistoricalIndicatorsAsync(stock.Symbol, timeframe);
                    }
                    catch (Exception innerEx)
                    {
                        _logger.LogError(innerEx, "Failed to fetch history from Zerodha for {Symbol} ({Timeframe}) range {Start} to {End}.", stock.Symbol, timeframe, startUtc, endUtc);
                    }

                    processed++;
                    double pct = Math.Round((double)processed / total * 100, 1);
                    await _hubContext.Clients.All.SendAsync("SyncProgress", new { 
                        message = $"Synced {stock.Symbol} ({processed}/{total})", 
                        progress = pct 
                    });
                }

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
