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

    /// <summary>
    /// Counts the number of NSE trading days (weekends and Indian market holidays excluded)
    /// strictly after <paramref name="openedAtUtc"/>'s date up to and including
    /// <paramref name="nowUtc"/>'s date, both interpreted in IST. Used for holding-period exits
    /// that are meant to count trading sessions rather than raw calendar days.
    /// </summary>
    Task<int> CountTradingDaysElapsedAsync(DateTime openedAtUtc, DateTime nowUtc);
}
