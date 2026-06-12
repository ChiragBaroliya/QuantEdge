using System;
using System.Threading.Tasks;

namespace QuantEdge.Infrastructure.Interfaces;

/// <summary>
/// Service responsible for validating if a given timestamp falls within 
/// Indian Stock Market hours (pre-open + normal trading, excluding weekends and holidays).
/// </summary>
public interface IMarketHoursService
{
    /// <summary>
    /// Checks if the provided timestamp (or current time if null) is within active market trading hours.
    /// </summary>
    Task<bool> IsWithinMarketHoursAsync(DateTime? time = null);

    /// <summary>
    /// Force-refreshes the internal cache of market holidays from the database.
    /// </summary>
    Task RefreshHolidaysCacheAsync();
}
