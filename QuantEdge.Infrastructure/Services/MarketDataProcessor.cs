using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.SignalR;
using QuantEdge.Infrastructure.Configurations;
using QuantEdge.Infrastructure.Constants;
using QuantEdge.Infrastructure.DTOs;
using QuantEdge.Infrastructure.Interfaces;
using QuantEdge.Domain.Entities;
using QuantEdge.Infrastructure.Persistence.Repositories;
using QuantEdge.Infrastructure.Hubs;

namespace QuantEdge.Infrastructure.Services;

/// <summary>
/// Core pipeline processor implementing IMarketDataProcessor. Listens to WebSocket ticks/depths
/// and aggregates/routes them using SOLID principles.
/// Dynamically queries active symbols from the stock_master table for subscriptions.
/// </summary>
public class MarketDataProcessor : IMarketDataProcessor
{
    private readonly IWebSocketMarketDataService _webSocketService;
    private readonly ICandleBuilderService _candleBuilder;
    private readonly IMarketCandleRepository _candleRepository;
    private readonly IMarketIndicatorRepository _indicatorRepository;
    private readonly IStockMasterRepository _stockMasterRepository;
    private readonly IIndicatorService _indicatorService;
    private readonly ISignalEngineService _signalEngine;
    private readonly IMarketHoursService _marketHoursService;
    private readonly IHubContext<MarketDataHub>? _hubContext;
    private readonly BrokerConfig _config;
    private readonly ILogger<MarketDataProcessor> _logger;

    private bool _isEventHandlersWired = false;

    public bool IsConnected => _webSocketService.IsConnected;

    public MarketDataProcessor(
        IWebSocketMarketDataService webSocketService,
        ICandleBuilderService candleBuilder,
        IMarketCandleRepository candleRepository,
        IMarketIndicatorRepository indicatorRepository,
        IStockMasterRepository stockMasterRepository,
        IIndicatorService indicatorService,
        ISignalEngineService signalEngine,
        IMarketHoursService marketHoursService,
        IOptions<BrokerConfig> config,
        ILogger<MarketDataProcessor> logger,
        IHubContext<MarketDataHub>? hubContext = null)
    {
        _webSocketService = webSocketService ?? throw new ArgumentNullException(nameof(webSocketService));
        _candleBuilder = candleBuilder ?? throw new ArgumentNullException(nameof(candleBuilder));
        _candleRepository = candleRepository ?? throw new ArgumentNullException(nameof(candleRepository));
        _indicatorRepository = indicatorRepository ?? throw new ArgumentNullException(nameof(indicatorRepository));
        _stockMasterRepository = stockMasterRepository ?? throw new ArgumentNullException(nameof(stockMasterRepository));
        _indicatorService = indicatorService ?? throw new ArgumentNullException(nameof(indicatorService));
        _signalEngine = signalEngine ?? throw new ArgumentNullException(nameof(signalEngine));
        _marketHoursService = marketHoursService ?? throw new ArgumentNullException(nameof(marketHoursService));
        _hubContext = hubContext;
        _config = config?.Value ?? throw new ArgumentNullException(nameof(config));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Configures event subscriptions and starts the feed connection pipeline.
    /// </summary>
    public async Task StartProcessingAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting MarketDataProcessor core routing pipeline...");

        if (!_isEventHandlersWired)
        {
            _logger.LogInformation("Wiring MarketDataProcessor event handlers...");
            _webSocketService.OnTickReceived += ProcessIncomingTickAsync;
            _webSocketService.OnDepthReceived += ProcessIncomingDepthAsync;

            _candleBuilder.OnCandleClosed += SaveCandleToPostgresAsync;
            _candleBuilder.OnCandleUpdated += BroadcastActiveCandleAsync;

            _isEventHandlersWired = true;
        }

        // Establish connections asynchronously
        await _webSocketService.ConnectAsync(_config.WebSocketUrl, cancellationToken);

        // Dynamic subscription to all active stock symbols configured in stock_master database
        var activeStocks = await _stockMasterRepository.GetActiveStocksAsync();
        foreach (var stock in activeStocks)
        {
            _logger.LogInformation("Subscribing to active market stream for symbol: {Symbol}", stock.Symbol);
            await _webSocketService.SubscribeAsync(stock.Symbol, cancellationToken);
        }
    }

    /// <summary>
    /// Gracefully stops the core processing pipeline and disconnects subscriptions.
    /// </summary>
    public async Task StopProcessingAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping MarketDataProcessor core routing pipeline connection...");
        await _webSocketService.DisconnectAsync(cancellationToken);
    }

    /// <summary>
    /// Processes incoming tick data and forwards it to the thread-safe candle builder.
    /// </summary>
    public async Task ProcessIncomingTickAsync(TickDataDto tick)
    {
        if (!await _marketHoursService.IsWithinMarketHoursAsync(tick.Timestamp))
        {
            _logger.LogTrace("Discarding tick for {Symbol} because it is outside market hours.", tick.Symbol);
            return;
        }

        try
        {
            _logger.LogTrace("Orchestrating tick: {Symbol} @ {LTP} | Vol: {Volume}", tick.Symbol, tick.LTP, tick.Volume);
            
            // Forward tick data to aggregate candlesticks
            await _candleBuilder.ProcessTickAsync(tick);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing incoming tick for symbol {Symbol}.", tick.Symbol);
        }
    }

    /// <summary>
    /// Ingests real-time market depth / order book levels.
    /// </summary>
    public async Task ProcessIncomingDepthAsync(MarketDepthDto depth)
    {
        if (!await _marketHoursService.IsWithinMarketHoursAsync(depth.Timestamp))
        {
            _logger.LogTrace("Discarding market depth update for {Symbol} because it is outside market hours.", depth.Symbol);
            return;
        }

        try
        {
            _logger.LogTrace("Orchestrating market depth update for: {Symbol} at {Time}", depth.Symbol, depth.Timestamp);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing incoming market depth for symbol {Symbol}.", depth.Symbol);
        }
    }

    private async Task BroadcastActiveCandleAsync(CandleDto candleDto)
    {
        try
        {
            string timeframeStr = candleDto.Interval.TotalMinutes >= 1
                ? $"{(int)candleDto.Interval.TotalMinutes}m"
                : $"{(int)candleDto.Interval.TotalSeconds}s";

            string groupName = $"{candleDto.Symbol.ToUpper().Trim()}_{timeframeStr.ToLower().Trim()}";

            // Broadcast real-time (unfinished) active candle details to subscribers
            if (_hubContext != null)
            {
                await _hubContext.Clients.Group(groupName).SendAsync("ReceiveActiveCandle", new
                {
                    time = new DateTimeOffset(candleDto.Timestamp).ToUnixTimeMilliseconds(),
                    open = candleDto.Open,
                    high = candleDto.High,
                    low = candleDto.Low,
                    close = candleDto.Close,
                    volume = candleDto.Volume
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to broadcast active candle tick update for symbol {Symbol}.", candleDto.Symbol);
        }
    }

    private async Task SaveCandleToPostgresAsync(CandleDto candleDto)
    {
        try
        {
            _logger.LogInformation("Persisting closed candle to PostgreSQL: {Symbol} {Interval} @ {Close}", candleDto.Symbol, candleDto.Interval, candleDto.Close);

            string timeframeStr = candleDto.Interval.TotalMinutes >= 1
                ? $"{(int)candleDto.Interval.TotalMinutes}m"
                : $"{(int)candleDto.Interval.TotalSeconds}s";

            int deterministicId = GenerateDeterministicIntId(candleDto.Symbol, timeframeStr, candleDto.Timestamp);

            var marketCandle = new MarketCandle
            {
                Id = deterministicId,
                Symbol = candleDto.Symbol,
                Timeframe = timeframeStr,
                Open = candleDto.Open,
                High = candleDto.High,
                Low = candleDto.Low,
                Close = candleDto.Close,
                Volume = candleDto.Volume,
                CandleTime = candleDto.Timestamp.ToUniversalTime(),
                CreatedAt = DateTime.UtcNow
            };

            await _candleRepository.InsertAsync(marketCandle);
            _logger.LogInformation("Successfully saved closed candle to PostgreSQL for {Symbol} ({Timeframe})", candleDto.Symbol, timeframeStr);

            // 1. Compute and persist technical indicators for this new candle
            await _indicatorService.CalculateAndSaveLatestIndicatorAsync(candleDto.Symbol, timeframeStr);

            // 2. Evaluate signals based on newly updated indicators
            var signal = await _signalEngine.EvaluateSignalAsync(candleDto.Symbol, timeframeStr, CancellationToken.None);

            // 3. Load latest indicators for the closed candle
            var indicators = (await _indicatorRepository.GetHistoryAsync(candleDto.Symbol, timeframeStr, limit: 1)).FirstOrDefault();

            // 4. Broadcast the completed candle + indicators + signals to clients
            string groupName = $"{candleDto.Symbol.ToUpper().Trim()}_{timeframeStr.ToLower().Trim()}";
            if (_hubContext != null)
            {
                await _hubContext.Clients.Group(groupName).SendAsync("ReceiveClosedCandle", new
                {
                    time = new DateTimeOffset(candleDto.Timestamp).ToUnixTimeMilliseconds(),
                    open = candleDto.Open,
                    high = candleDto.High,
                    low = candleDto.Low,
                    close = candleDto.Close,
                    volume = candleDto.Volume,
                    rsi = indicators?.RSI,
                    ema20 = indicators?.EMA20,
                    ema50 = indicators?.EMA50,
                    macd = indicators?.MACD,
                    signalLine = indicators?.SignalLine,
                    vwap = indicators?.VWAP,
                    signalType = signal.SignalType,
                    signalScore = signal.Score,
                    signalStrength = signal.Strength,
                    signalReason = signal.Reason
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process closed candle, indicators, or signal for symbol {Symbol}.", candleDto.Symbol);
        }
    }

    private static int GenerateDeterministicIntId(string symbol, string timeframe, DateTime candleTime)
    {
        string input = $"{symbol}_{timeframe}_{candleTime:yyyyMMddHHmmss}";
        uint hash = 2166136261;
        foreach (char c in input)
        {
            hash = (hash ^ c) * 16777619;
        }
        return (int)hash;
    }
}
