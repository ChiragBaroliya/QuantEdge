using System.Threading;
using System.Threading.Tasks;
using QuantEdge.Infrastructure.DTOs;

namespace QuantEdge.Infrastructure.Interfaces;

/// <summary>
/// Service interface for evaluating real-time and historical technical indicators
/// to generate BUY/SELL recommendations.
/// </summary>
public interface ISignalEngineService
{
    /// <summary>
    /// Queries the latest market indicators and candles, runs evaluation, 
    /// persists any generated signals, and returns detailed scoring outputs.
    /// </summary>
    Task<SignalEvaluationResult> EvaluateSignalAsync(string symbol, string timeframe, CancellationToken cancellationToken);
}
