using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Dapper;
using KiteConnect;
using QuantEdge.Infrastructure.DTOs;
using QuantEdge.Infrastructure.Interfaces;
using QuantEdge.Infrastructure.Configurations;
using QuantEdge.Infrastructure.Models;
using QuantEdge.Infrastructure.Persistence;
using QuantEdge.Infrastructure.Persistence.Repositories;

namespace QuantEdge.Infrastructure.Services;

/// <summary>
/// Production-ready Zerodha Kite WebSocket Market Data Service implementing IWebSocketMarketDataService.
/// Streams live ticks using Tech.Zerodha.KiteConnect and routes them thread-safely.
/// Dynamically loads symbol/instrument token mappings from the database.
/// </summary>
public class ZerodhaWebSocketMarketDataService : IWebSocketMarketDataService, IDisposable
{
    public event Func<TickDataDto, Task>? OnTickReceived;
    public event Func<MarketDepthDto, Task>? OnDepthReceived;

    private readonly BrokerConfig _config;
    private readonly ILogger<ZerodhaWebSocketMarketDataService> _logger;
    private readonly IDbConnectionFactory _connectionFactory;
    private readonly IStockMasterRepository _stockMasterRepository;

    private Ticker? _ticker;
    private readonly ConcurrentDictionary<string, bool> _subscribedSymbols = new();

    // Dynamic instrument maps populated from the database
    private readonly ConcurrentDictionary<string, uint> _symbolToTokenMap = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<uint, string> _tokenToSymbolMap = new();

    public bool IsConnected => _ticker != null && _ticker.IsConnected;

    public ZerodhaWebSocketMarketDataService(
        IOptions<BrokerConfig> config,
        IDbConnectionFactory connectionFactory,
        IStockMasterRepository stockMasterRepository,
        ILogger<ZerodhaWebSocketMarketDataService> logger)
    {
        _config = config?.Value ?? throw new ArgumentNullException(nameof(config));
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _stockMasterRepository = stockMasterRepository ?? throw new ArgumentNullException(nameof(stockMasterRepository));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task ConnectAsync(string connectionUrl, CancellationToken cancellationToken)
    {
        // 1. Dynamic load of active stock instruments from database
        await LoadInstrumentMappingsAsync();

        _logger.LogInformation("Resolving active Zerodha session from database...");
        string? token = null;
        try
        {
            using var conn = _connectionFactory.CreateConnection();
            token = await conn.QueryFirstOrDefaultAsync<string?>(
                "SELECT access_token FROM zerodha_sessions WHERE api_key = @ApiKey AND is_active = TRUE LIMIT 1;",
                new { ApiKey = _config.ApiKey }
            );
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to resolve active AccessToken from the database.");
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            _logger.LogError("Zerodha AccessToken is not configured or could not be found in the database. Establish session via the API login callback first!");
            throw new InvalidOperationException("Zerodha AccessToken is missing.");
        }

        _logger.LogInformation("Connecting to Zerodha Kite WebSocket with API Key: {ApiKey}", _config.ApiKey);

        try
        {
            // Initialize Kite Connect Ticker
            _ticker = new Ticker(_config.ApiKey, token);

            // Wire events
            _ticker.OnTick += OnKiteTick;
            _ticker.OnConnect += OnKiteConnect;
            _ticker.OnClose += OnKiteClose;
            _ticker.OnError += OnKiteError;
            _ticker.OnReconnect += OnKiteReconnect;

            // Enable auto reconnection
            _ticker.EnableReconnect(Interval: 5, Retries: 50);

            // Connect in background to avoid blocking
            _ticker.Connect();
            
            _logger.LogInformation("Zerodha Ticker connection initiated successfully.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize and connect Zerodha Kite Ticker.");
            throw;
        }
    }

    private async Task LoadInstrumentMappingsAsync()
    {
        _logger.LogInformation("Loading dynamic instrument mappings from stock_master database table...");
        
        try
        {
            var activeStocks = await _stockMasterRepository.GetActiveStocksAsync();
            
            _symbolToTokenMap.Clear();
            _tokenToSymbolMap.Clear();

            foreach (var stock in activeStocks)
            {
                uint token = (uint)stock.InstrumentToken;
                _symbolToTokenMap[stock.Symbol] = token;
                _tokenToSymbolMap[token] = stock.Symbol;
            }

            _logger.LogInformation("Loaded {Count} active stock instrument mappings from database.", _symbolToTokenMap.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while loading dynamic instrument mappings from stock_master.");
            throw;
        }
    }

    public Task DisconnectAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Disconnecting Zerodha Kite Ticker connection...");
        if (_ticker != null)
        {
            _ticker.Close();
            _ticker.OnTick -= OnKiteTick;
            _ticker.OnConnect -= OnKiteConnect;
            _ticker.OnClose -= OnKiteClose;
            _ticker.OnError -= OnKiteError;
            _ticker.OnReconnect -= OnKiteReconnect;
        }
        return Task.CompletedTask;
    }

    public Task SubscribeAsync(string symbol, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            throw new ArgumentException("Market symbol cannot be null or empty.", nameof(symbol));
        }

        _subscribedSymbols[symbol] = true;

        if (IsConnected && _ticker != null)
        {
            if (_symbolToTokenMap.TryGetValue(symbol, out var token))
            {
                _logger.LogInformation("Subscribing to Zerodha instrument token: {Token} for symbol: {Symbol}", token, symbol);
                _ticker.Subscribe(new uint[] { token });
                _ticker.SetMode(new uint[] { token }, "quote");
            }
            else
            {
                _logger.LogWarning("Symbol {Symbol} is not mapped to a Zerodha Instrument Token. Subscription skipped.", symbol);
            }
        }
        else
        {
            _logger.LogInformation("Symbol {Symbol} cached. Subscription will be transmitted when connection is established.", symbol);
        }

        return Task.CompletedTask;
    }

    private void OnKiteTick(Tick tick)
    {
        _logger.LogDebug("Received Zerodha Kite tick for token: {Token} | LTP: {Ltp}", tick.InstrumentToken, tick.LastPrice);

        if (OnTickReceived != null)
        {
            string symbol = _tokenToSymbolMap.TryGetValue(tick.InstrumentToken, out var sym) ? sym : tick.InstrumentToken.ToString();
            
            var tickDto = new TickDataDto(
                Symbol: symbol,
                LTP: tick.LastPrice,
                Volume: (long)tick.Volume,
                Timestamp: tick.Timestamp ?? DateTime.UtcNow
            );

            // Execute the event handler asynchronously
            _ = Task.Run(async () =>
            {
                try
                {
                    await OnTickReceived.Invoke(tickDto);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error dispatching OnTickReceived event.");
                }
            });
        }

        // Fire depth event if full mode details are available
        if (OnDepthReceived != null && tick.Bids != null && tick.Offers != null)
        {
            string symbol = _tokenToSymbolMap.TryGetValue(tick.InstrumentToken, out var sym) ? sym : tick.InstrumentToken.ToString();

            var bids = tick.Bids.Select(b => new DepthLevel(b.Price, b.Quantity)).ToList();
            var offers = tick.Offers.Select(a => new DepthLevel(a.Price, a.Quantity)).ToList();

            var depthDto = new MarketDepthDto(
                Symbol: symbol,
                Timestamp: tick.Timestamp ?? DateTime.UtcNow,
                Bids: bids,
                Asks: offers
            );

            _ = Task.Run(async () =>
            {
                try
                {
                    await OnDepthReceived.Invoke(depthDto);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error dispatching OnDepthReceived event.");
                }
            });
        }
    }

    private void OnKiteConnect()
    {
        _logger.LogInformation("Zerodha Kite Ticker WebSocket connection opened.");
        
        // Resubscribe to cached symbols
        if (!_subscribedSymbols.IsEmpty && _ticker != null)
        {
            var tokensToSubscribe = new List<uint>();
            foreach (var symbol in _subscribedSymbols.Keys)
            {
                if (_symbolToTokenMap.TryGetValue(symbol, out var token))
                {
                    tokensToSubscribe.Add(token);
                }
            }

            if (tokensToSubscribe.Any())
            {
                _logger.LogInformation("Re-subscribing to cached Zerodha instrument tokens: {Tokens}", string.Join(", ", tokensToSubscribe));
                _ticker.Subscribe(tokensToSubscribe.ToArray());
                _ticker.SetMode(tokensToSubscribe.ToArray(), "quote");
            }
        }
    }

    private void OnKiteClose()
    {
        _logger.LogWarning("Zerodha Kite Ticker WebSocket connection closed.");
    }

    private void OnKiteError(string message)
    {
        _logger.LogError("Zerodha Kite Ticker WebSocket error occurred: {Message}", message);
    }

    private void OnKiteReconnect()
    {
        _logger.LogInformation("Zerodha Kite Ticker WebSocket attempting to reconnect...");
    }

    public void Dispose()
    {
        if (_ticker != null)
        {
            _ticker.Close();
        }
        GC.SuppressFinalize(this);
    }
}
