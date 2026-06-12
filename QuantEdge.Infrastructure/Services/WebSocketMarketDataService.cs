using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using QuantEdge.Infrastructure.DTOs;
using QuantEdge.Infrastructure.Interfaces;
using QuantEdge.Infrastructure.Models;

namespace QuantEdge.Infrastructure.Services;

/// <summary>
/// Production live WebSocket integration service implementing IWebSocketMarketDataService.
/// Integrates WebSocketConnectionManager, ReconnectPolicyService, heartbeats, and resubscriptions.
/// </summary>
public class WebSocketMarketDataService : IWebSocketMarketDataService, IDisposable
{
    public event Func<TickDataDto, Task>? OnTickReceived;
    public event Func<MarketDepthDto, Task>? OnDepthReceived;

    private readonly WebSocketConnectionManager _connectionManager;
    private readonly IReconnectPolicyService _reconnectPolicy;
    private readonly ILogger<WebSocketMarketDataService> _logger;

    private readonly ConcurrentDictionary<string, bool> _subscribedSymbols = new();
    private CancellationTokenSource? _connectionCancellationTokenSource;
    private Task? _messageLoopTask;
    private Task? _heartbeatTask;
    private int _retryCount = 0;
    private string? _lastConnectedUrl;
    private readonly SemaphoreSlim _connectionLock = new(1, 1);

    /// <summary>
    /// Gets whether the service is currently connected to the live stream.
    /// </summary>
    public bool IsConnected => _connectionManager.IsOpen;

    public WebSocketMarketDataService(
        WebSocketConnectionManager connectionManager,
        IReconnectPolicyService reconnectPolicy,
        ILogger<WebSocketMarketDataService> logger)
    {
        _connectionManager = connectionManager ?? throw new ArgumentNullException(nameof(connectionManager));
        _reconnectPolicy = reconnectPolicy ?? throw new ArgumentNullException(nameof(reconnectPolicy));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Connects to the live WebSocket feed URL. Spins up background receiver loop and heartbeat ping tasks.
    /// </summary>
    public async Task ConnectAsync(string connectionUrl, CancellationToken cancellationToken)
    {
        await _connectionLock.WaitAsync(cancellationToken);
        try
        {
            _lastConnectedUrl = connectionUrl;
            
            // Cancel any prior connection scope
            _connectionCancellationTokenSource?.Cancel();
            _connectionCancellationTokenSource = new CancellationTokenSource();

            _logger.LogInformation("Attempting to connect to live market WebSocket feed: {WebSocketUrl}", connectionUrl);
            await _connectionManager.ConnectAsync(connectionUrl, cancellationToken);
            
            // Connection succeeded: Reset retry counters
            _retryCount = 0;
            _reconnectPolicy.Reset();

            var token = _connectionCancellationTokenSource.Token;

            // Spin up thread-safe background consumer loops
            _messageLoopTask = Task.Run(() => RunMessageLoopAsync(token), token);
            _heartbeatTask = Task.Run(() => RunHeartbeatLoopAsync(token), token);

            // Re-subscribe to symbols automatically on reconnection (highly robust)
            await ResubscribeAllAsync(token);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect to live WebSocket feed: {WebSocketUrl}. Triggering automatic background reconnect policy.", connectionUrl);
            
            // Trigger automatic reconnect loop on a background thread to prevent blocking caller thread
            _ = Task.Run(() => ReconnectAsync(CancellationToken.None), CancellationToken.None);
            throw;
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    /// <summary>
    /// Gracefully disconnects from the live WebSocket feed, halting background consumer loops and heartbeats.
    /// </summary>
    public async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        await _connectionLock.WaitAsync(cancellationToken);
        try
        {
            _logger.LogInformation("Disconnecting from live market WebSocket feed...");
            _connectionCancellationTokenSource?.Cancel();

            try
            {
                await _connectionManager.DisconnectAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error occurred during closure of WebSocket connection.");
            }

            // Graceful wait for background threads to unwind
            if (_messageLoopTask != null)
            {
                await Task.WhenAny(_messageLoopTask, Task.Delay(1000, cancellationToken));
            }
            if (_heartbeatTask != null)
            {
                await Task.WhenAny(_heartbeatTask, Task.Delay(1000, cancellationToken));
            }
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    /// <summary>
    /// Subscribes to live feed updates for a symbol. Thread-safely records the symbol to restore it in case of reconnection.
    /// </summary>
    public async Task SubscribeAsync(string symbol, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            throw new ArgumentException("Market symbol cannot be null or empty.", nameof(symbol));
        }

        // Store subscription symbol to support automatic session restore/re-subscribe
        _subscribedSymbols[symbol] = true;

        if (IsConnected)
        {
            _logger.LogInformation("Subscribing to market symbol feed: {Symbol}", symbol);

            // Structure subscription payload preparing for modern broker APIs (like Grow API / Groww Broker)
            var payload = new
            {
                action = "subscribe",
                symbol = symbol,
                timestamp = DateTime.UtcNow
            };

            var json = JsonSerializer.Serialize(payload);
            await _connectionManager.SendAsync(json, cancellationToken);
        }
        else
        {
            _logger.LogInformation("Symbol {Symbol} registered in local cache. Subscription will be transmitted automatically once connection goes online.", symbol);
        }
    }

    private async Task ResubscribeAllAsync(CancellationToken cancellationToken)
    {
        if (_subscribedSymbols.IsEmpty)
        {
            return;
        }

        _logger.LogInformation("Re-establishing subscriptions for cached symbols: {Symbols}", string.Join(", ", _subscribedSymbols.Keys));
        foreach (var symbol in _subscribedSymbols.Keys)
        {
            try
            {
                await SubscribeAsync(symbol, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to restore symbol subscription for: {Symbol} during reconnection loop.", symbol);
            }
        }
    }

    /// <summary>
    /// Processes parsed string messages, extracts and auto-maps Tick DTOs or Depth spreads, and fires appropriate events.
    /// </summary>
    public async Task ProcessMessageAsync(string message, CancellationToken cancellationToken)
    {
        _logger.LogTrace("WebSocket payload received: {Message}", message);

        try
        {
            using var document = JsonDocument.Parse(message);
            var root = document.RootElement;

            // Handle Keep-Alive Pong messages from server
            if (root.TryGetProperty("type", out var typeProp) && typeProp.GetString() == "pong")
            {
                _logger.LogDebug("Received keep-alive PONG frame from remote WebSocket.");
                return;
            }

            // Ingestion schema flexible check: supports Grow API style tick formats (ltp/Ltp/LTP)
            if (root.TryGetProperty("ltp", out _) || root.TryGetProperty("price", out _) || root.TryGetProperty("LTP", out _))
            {
                var symbol = root.TryGetProperty("symbol", out var sym) ? sym.GetString() ?? "" : "";
                
                decimal ltp = 0;
                if (root.TryGetProperty("ltp", out var ltpProp))
                {
                    ltp = ltpProp.GetDecimal();
                }
                else if (root.TryGetProperty("price", out var pProp))
                {
                    ltp = pProp.GetDecimal();
                }
                else if (root.TryGetProperty("LTP", out var ltpProp2))
                {
                    ltp = ltpProp2.GetDecimal();
                }

                long volume = root.TryGetProperty("volume", out var volProp) ? volProp.GetInt64() : 0;
                var timestamp = root.TryGetProperty("timestamp", out var tsProp) && tsProp.TryGetDateTime(out var dt) ? dt : DateTime.UtcNow;

                var tickDto = new TickDataDto(symbol, ltp, volume, timestamp);

                if (OnTickReceived != null)
                {
                    await OnTickReceived.Invoke(tickDto);
                }
            }
            // Parse Order Book spread snapshots
            else if (root.TryGetProperty("bids", out _) || root.TryGetProperty("asks", out _))
            {
                var symbol = root.TryGetProperty("symbol", out var sym) ? sym.GetString() ?? "" : "";
                var timestamp = root.TryGetProperty("timestamp", out var tsProp) && tsProp.TryGetDateTime(out var dt) ? dt : DateTime.UtcNow;

                var bids = new List<DepthLevel>();
                var asks = new List<DepthLevel>();

                if (root.TryGetProperty("bids", out var bidsProp) && bidsProp.ValueKind == JsonValueKind.Array)
                {
                    foreach (var bidItem in bidsProp.EnumerateArray())
                    {
                        var price = bidItem.GetProperty("price").GetDecimal();
                        var quantity = bidItem.GetProperty("quantity").GetInt64();
                        bids.Add(new DepthLevel(price, quantity));
                    }
                }

                if (root.TryGetProperty("asks", out var asksProp) && asksProp.ValueKind == JsonValueKind.Array)
                {
                    foreach (var askItem in asksProp.EnumerateArray())
                    {
                        var price = askItem.GetProperty("price").GetDecimal();
                        var quantity = askItem.GetProperty("quantity").GetInt64();
                        asks.Add(new DepthLevel(price, quantity));
                    }
                }

                var depthDto = new MarketDepthDto(symbol, timestamp, bids, asks);

                if (OnDepthReceived != null)
                {
                    await OnDepthReceived.Invoke(depthDto);
                }
            }
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to parse JSON packet over WebSocket stream: {Message}", message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing message parsing event triggers.");
        }
    }

    /// <summary>
    /// Executes automatic background connection restore according to ReconnectPolicyService.
    /// </summary>
    public async Task ReconnectAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(_lastConnectedUrl))
        {
            _logger.LogWarning("Auto-reconnection aborted. No connection history exists.");
            return;
        }

        await _connectionLock.WaitAsync(cancellationToken);
        try
        {
            if (IsConnected)
            {
                _logger.LogInformation("Auto-reconnection aborted. WebSocket connection is already established and online.");
                return;
            }

            _retryCount++;
            var nextDelay = _reconnectPolicy.GetNextDelay(_retryCount);

            _logger.LogWarning("WebSocket link severed. Scheduling reconnection attempt #{Attempt} in {DelaySeconds:F2} seconds...", _retryCount, nextDelay.TotalSeconds);
            await Task.Delay(nextDelay, cancellationToken);

            _connectionCancellationTokenSource?.Cancel();
            _connectionCancellationTokenSource = new CancellationTokenSource();
            var token = _connectionCancellationTokenSource.Token;

            _logger.LogInformation("Reconnecting WebSocket stream connection (attempt #{Attempt})...", _retryCount);
            await _connectionManager.ConnectAsync(_lastConnectedUrl, token);

            // Reconnected successfully!
            _retryCount = 0;
            _reconnectPolicy.Reset();

            // Restart background receiver and heartbeat routines
            _messageLoopTask = Task.Run(() => RunMessageLoopAsync(token), token);
            _heartbeatTask = Task.Run(() => RunHeartbeatLoopAsync(token), token);

            // Restore symbols subscription list
            await ResubscribeAllAsync(token);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Reconnection attempt #{Attempt} failed. Retrying in background...", _retryCount);
            
            // Queue next retry attempt
            _ = Task.Run(() => ReconnectAsync(CancellationToken.None), CancellationToken.None);
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    private async Task RunMessageLoopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting WebSocket background receiver queue stream...");
        try
        {
            // Consuming IAsyncEnumerable async stream yielded by connection manager
            await foreach (var message in _connectionManager.ReceiveMessagesAsync(cancellationToken))
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                await ProcessMessageAsync(message, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("WebSocket background receiver stream cancelled gracefully.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fatal crash occurred inside background WebSocket receiver pipeline.");
        }
        finally
        {
            _logger.LogInformation("WebSocket background receiver pipeline terminated.");
            
            // Trigger automatic reconnect if connection went offline unexpectedly and not cancelled
            if (!cancellationToken.IsCancellationRequested && !IsConnected)
            {
                _ = Task.Run(() => ReconnectAsync(CancellationToken.None), CancellationToken.None);
            }
        }
    }

    private async Task RunHeartbeatLoopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting background heartbeat worker...");
        var pingInterval = TimeSpan.FromSeconds(25);

        try
        {
            while (!cancellationToken.IsCancellationRequested && IsConnected)
            {
                await Task.Delay(pingInterval, cancellationToken);
                if (IsConnected)
                {
                    _logger.LogDebug("Sending WebSocket ping keepalive frame...");
                    var pingFrame = JsonSerializer.Serialize(new { type = "ping", action = "ping", timestamp = DateTime.UtcNow });
                    await _connectionManager.SendAsync(pingFrame, cancellationToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Background heartbeat worker stopped gracefully.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Heartbeat ping write operation encountered an error.");
        }
        finally
        {
            _logger.LogInformation("Background heartbeat worker terminated.");
        }
    }

    /// <summary>
    /// Disposes resources, cancelling active worker tasks.
    /// </summary>
    public void Dispose()
    {
        _connectionCancellationTokenSource?.Cancel();
        _connectionCancellationTokenSource?.Dispose();
        _connectionLock.Dispose();
        GC.SuppressFinalize(this);
    }
}
