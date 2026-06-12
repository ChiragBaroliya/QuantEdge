using System;
using System.Threading;
using System.Threading.Tasks;
using QuantEdge.Infrastructure.DTOs;

namespace QuantEdge.Infrastructure.Interfaces;

/// <summary>
/// Service responsible for managing connections and subscriptions to market data stream sources (e.g., WebSockets).
/// </summary>
public interface IWebSocketMarketDataService
{
    /// <summary>
    /// Event triggered when a new market tick is received.
    /// </summary>
    event Func<TickDataDto, Task>? OnTickReceived;

    /// <summary>
    /// Event triggered when a new order book depth state is received.
    /// </summary>
    event Func<MarketDepthDto, Task>? OnDepthReceived;

    /// <summary>
    /// Gets whether the service is currently connected to the stream.
    /// </summary>
    bool IsConnected { get; }

    /// <summary>
    /// Connects to the market data feed source asynchronously.
    /// </summary>
    Task ConnectAsync(string connectionUrl, CancellationToken cancellationToken);

    /// <summary>
    /// Disconnects from the market data feed source asynchronously.
    /// </summary>
    Task DisconnectAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Subscribes to a specific market symbol feed asynchronously.
    /// </summary>
    Task SubscribeAsync(string symbol, CancellationToken cancellationToken);
}
