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
    /// Recalculates and overwrites indicators for historical candles within an optional date range for backfilling.
    /// </summary>
    Task BackfillHistoricalIndicatorsAsync(string symbol, string timeframe, System.DateTime? fromTime = null, System.DateTime? toTime = null);
}
