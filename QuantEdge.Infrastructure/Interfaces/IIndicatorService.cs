using System.Threading.Tasks;

namespace QuantEdge.Infrastructure.Interfaces;

/// <summary>
/// Service contract responsible for technical indicator calculation and persistence.
/// </summary>
public interface IIndicatorService
{
    /// <summary>
    /// Calculates and persists technical indicators for the latest candle in the database.
    /// </summary>
    Task CalculateAndSaveLatestIndicatorAsync(string symbol, string timeframe);

    /// <summary>
    /// Recalculates and overwrites indicators for all stored history (up to a limit) for backfilling.
    /// </summary>
    Task BackfillHistoricalIndicatorsAsync(string symbol, string timeframe);
}
