using System;

namespace QuantEdge.Infrastructure.Interfaces;

/// <summary>
/// Service contract responsible for defining the automatic reconnection backoff policy.
/// </summary>
public interface IReconnectPolicyService
{
    /// <summary>
    /// Calculates the next reconnection delay based on the current retry count, using exponential backoff and jitter.
    /// </summary>
    TimeSpan GetNextDelay(int retryCount);

    /// <summary>
    /// Resets the reconnection policy metrics if applicable.
    /// </summary>
    void Reset();
}
