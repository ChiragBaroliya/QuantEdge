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
        Microsoft.AspNetCore.SignalR.IHubContext<QuantEdge.Infrastructure.Hubs.MarketDataHub> hubContext)
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
    /// </summary>
    [HttpGet("chart-data")]
    public async Task<IActionResult> GetChartData([FromQuery] string symbol, [FromQuery] string timeframe, [FromQuery] int limit = 150)
    {
        if (string.IsNullOrWhiteSpace(symbol)) return BadRequest("Symbol parameter is required.");
        if (string.IsNullOrWhiteSpace(timeframe)) return BadRequest("Timeframe parameter is required.");

        _logger.LogInformation("HTTP Request: Fetching combined chart data for symbol {Symbol} ({Timeframe}, Limit: {Limit})", symbol, timeframe, limit);

        try
        {
            // Fetch candles and indicators from DB (ordered by candle_time DESC in repos)
            var candlesTask = _candleRepository.GetHistoryAsync(symbol, timeframe, limit);
            var indicatorsTask = _indicatorRepository.GetHistoryAsync(symbol, timeframe, limit);
            var signalsTask = _tradingSignalRepository.GetRecentSignalsAsync(limit);

            await Task.WhenAll(candlesTask, indicatorsTask, signalsTask);

            var candles = candlesTask.Result.ToList();
            var indicators = indicatorsTask.Result.ToDictionary(i => i.CandleTime.ToString("yyyy-MM-dd HH:mm:ssZ"));
            var signals = signalsTask.Result.Where(s => s.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase))
                .ToDictionary(s => s.CandleTime.ToString("yyyy-MM-dd HH:mm:ssZ"));

            // Match and build chart DTO list ordered chronologically (oldest first for Lightweight Charts)
            var chartData = candles.Select(c =>
            {
                string key = c.CandleTime.ToString("yyyy-MM-dd HH:mm:ssZ");
                
                indicators.TryGetValue(key, out var ind);
                signals.TryGetValue(key, out var sig);

                return new
                {
                    time = new DateTimeOffset(c.CandleTime).ToUnixTimeMilliseconds(),
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
                    signalType = sig?.SignalType, // BUY, SELL, HOLD
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
    /// Resets all history for today for all active stocks for a specific timeframe in the background.
    /// This deletes existing data and fetches new data sequentially.
    /// </summary>
    [HttpPost("history/today/all/reset")]
    public IActionResult ResetTodayHistoryAll([FromQuery] string timeframe)
    {
        if (string.IsNullOrWhiteSpace(timeframe)) return BadRequest("Timeframe parameter is required.");
        
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var stockRepo = scope.ServiceProvider.GetRequiredService<IStockMasterRepository>();
                var candleRepo = scope.ServiceProvider.GetRequiredService<IMarketCandleRepository>();
                var indicatorRepo = scope.ServiceProvider.GetRequiredService<IMarketIndicatorRepository>();
                var historicalDataService = scope.ServiceProvider.GetRequiredService<IHistoricalDataService>();

                var activeStocks = (await stockRepo.GetActiveStocksAsync()).ToList();
                DateTime fromTime = DateTime.UtcNow.Date; // Start of today (UTC)
                DateTime toTime = DateTime.UtcNow;
                int total = activeStocks.Count;
                int processed = 0;

                await _hubContext.Clients.All.SendAsync("SyncProgress", new { 
                    message = $"Starting sync for {total} stocks ({timeframe})...", 
                    progress = 0 
                });

                foreach (var stock in activeStocks)
                {
                    // 1. Delete
                    await candleRepo.DeleteTodayHistoryAsync(stock.Symbol, timeframe);
                    await indicatorRepo.DeleteTodayIndicatorsAsync(stock.Symbol, timeframe);

                    // 2. Fetch
                    try
                    {
                        await historicalDataService.FetchHistoricalCandlesAsync(stock.Symbol, timeframe, fromTime, toTime, CancellationToken.None);
                    }
                    catch (Exception innerEx)
                    {
                        _logger.LogError(innerEx, "Failed to fetch today's history from Zerodha for {Symbol} ({Timeframe}).", stock.Symbol, timeframe);
                    }

                    processed++;
                    double pct = Math.Round((double)processed / total * 100, 1);
                    await _hubContext.Clients.All.SendAsync("SyncProgress", new { 
                        message = $"Synced {stock.Symbol} ({processed}/{total})", 
                        progress = pct 
                    });
                }

                await _hubContext.Clients.All.SendAsync("SyncComplete", new { 
                    message = $"Successfully synced history for all {total} stocks ({timeframe})." 
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to sync today's history for all active stocks ({Timeframe}) in background.", timeframe);
                await _hubContext.Clients.All.SendAsync("SyncError", new { message = "Error during bulk sync: " + ex.Message });
            }
        });

        return Accepted(new { message = $"Background sync task started for timeframe {timeframe}." });
    }
}
