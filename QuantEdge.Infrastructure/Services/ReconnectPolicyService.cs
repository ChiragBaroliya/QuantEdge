using System;
using QuantEdge.Infrastructure.Interfaces;

namespace QuantEdge.Infrastructure.Services;

/// <summary>
/// Thread-safe implementation of IReconnectPolicyService calculating exponential backoff delays with random jitter.
/// </summary>
public class ReconnectPolicyService : IReconnectPolicyService
{
    private readonly TimeSpan _baseDelay = TimeSpan.FromSeconds(2);
    private readonly TimeSpan _maxDelay = TimeSpan.FromMinutes(2);
    private readonly double _multiplier = 2.0;

    /// <summary>
    /// Calculates exponential delay: BaseDelay * Multiplier^retryCount + random jitter (capped at MaxDelay).
    /// </summary>
    public TimeSpan GetNextDelay(int retryCount)
    {
        if (retryCount <= 0)
        {
            return _baseDelay;
        }

        // Exponential backoff calculation
        double delaySeconds = _baseDelay.TotalSeconds * Math.Pow(_multiplier, Math.Min(retryCount, 8));

        // Incorporate random jitter (up to 30% of calculated backoff duration) to avoid synchronized connection attempts
        double jitter = Random.Shared.NextDouble() * 0.3 * delaySeconds;
        delaySeconds += jitter;

        var nextDelay = TimeSpan.FromSeconds(delaySeconds);
        return nextDelay > _maxDelay ? _maxDelay : nextDelay;
    }

    /// <summary>
    /// Resets the reconnection policy backoff state.
    /// </summary>
    public void Reset()
    {
        // Stateless calculation does not require explicit local field resets.
    }
}
