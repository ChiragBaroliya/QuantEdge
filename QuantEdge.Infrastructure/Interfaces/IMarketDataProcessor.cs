using System.Threading;
using System.Threading.Tasks;
using QuantEdge.Infrastructure.DTOs;

namespace QuantEdge.Infrastructure.Interfaces;

/// <summary>
/// Core orchestrator that processes, filters, and dispatches incoming tick data and market depth updates.
/// </summary>
public interface IMarketDataProcessor
{
    /// <summary>
    /// Gets whether the processor is currently connected to the live data stream.
    /// </summary>
    bool IsConnected { get; }

    /// <summary>
    /// Starts the core processing pipeline, subscribing to stream feeds.
    /// </summary>
    Task StartProcessingAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Gracefully stops the core processing pipeline and disconnects subscriptions.
    /// </summary>
    Task StopProcessingAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Processes and routes an incoming market tick.
    /// </summary>
    Task ProcessIncomingTickAsync(TickDataDto tick);

    /// <summary>
    /// Processes and routes an incoming market depth/order book state update.
    /// </summary>
    Task ProcessIncomingDepthAsync(MarketDepthDto depth);
}
