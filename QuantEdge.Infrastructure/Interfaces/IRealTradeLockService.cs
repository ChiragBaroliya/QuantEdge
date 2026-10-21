using System;
using System.Threading;
using System.Threading.Tasks;

namespace QuantEdge.Infrastructure.Interfaces;

/// <summary>
/// Cross-process lock for real-money order placement. The in-process SemaphoreSlim guards in
/// AutoRealTradeService only cover one process; this one is shared by every API / Worker process
/// on the same database, so two instances can never both place the same real order.
/// </summary>
public interface IRealTradeLockService
{
    /// <summary>
    /// Tries to take <paramref name="key"/> without waiting. Returns a handle that releases it on
    /// dispose, or null when another holder (any process) already has it.
    /// </summary>
    Task<IAsyncDisposable?> TryAcquireAsync(string key, CancellationToken cancellationToken = default);
}
