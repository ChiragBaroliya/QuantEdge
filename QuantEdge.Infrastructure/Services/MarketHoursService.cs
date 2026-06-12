using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using QuantEdge.Infrastructure.Interfaces;
using QuantEdge.Infrastructure.Persistence.Repositories;

namespace QuantEdge.Infrastructure.Services;

/// <summary>
/// Thread-safe service validating Indian stock market hours, caching holidays from the database
/// with a 5-minute timeout and support for immediate programmatic refresh.
/// </summary>
public class MarketHoursService : IMarketHoursService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MarketHoursService> _logger;
    private readonly TimeZoneInfo _indianTimeZone;

    private HashSet<DateOnly> _holidays = new();
    private DateTime _lastCacheUpdate = DateTime.MinValue;
    private readonly SemaphoreSlim _cacheLock = new(1, 1);

    public MarketHoursService(IServiceScopeFactory scopeFactory, ILogger<MarketHoursService> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        try
        {
            _indianTimeZone = TimeZoneInfo.FindSystemTimeZoneById("India Standard Time");
        }
        catch (TimeZoneNotFoundException)
        {
            // Fallback for Linux/macOS environments using IANA timezone identifier
            _indianTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata");
        }
    }

    public async Task<bool> IsWithinMarketHoursAsync(DateTime? time = null)
    {
        // Check cache staleness (refresh every 5 minutes if needed)
        if (DateTime.UtcNow - _lastCacheUpdate > TimeSpan.FromMinutes(5))
        {
            await RefreshHolidaysCacheAsync();
        }

        var utcTime = time ?? DateTime.UtcNow;
        var istTime = TimeZoneInfo.ConvertTimeFromUtc(utcTime, _indianTimeZone);

        // Stock market is closed on weekends
        if (istTime.DayOfWeek == DayOfWeek.Saturday || istTime.DayOfWeek == DayOfWeek.Sunday)
        {
            return false;
        }

        // Check if the current date is a configured market holiday
        var dateOnly = DateOnly.FromDateTime(istTime);
        if (_holidays.Contains(dateOnly))
        {
            return false;
        }

        // Indian Stock Market Timings:
        // Pre-Open Session: 09:00 AM - 09:15 AM
        // Trading Session: 09:15 AM - 03:30 PM
        var timeOfDay = istTime.TimeOfDay;
        var startTime = new TimeSpan(9, 0, 0);   // 09:00 AM IST
        var endTime = new TimeSpan(15, 30, 0);  // 03:30 PM IST

        return timeOfDay >= startTime && timeOfDay < endTime;
    }

    public async Task RefreshHolidaysCacheAsync()
    {
        // Avoid stampeding database requests if multiple threads trigger staleness check simultaneously
        await _cacheLock.WaitAsync();
        try
        {
            // Double-check staleness under lock
            if (DateTime.UtcNow - _lastCacheUpdate < TimeSpan.FromSeconds(10))
            {
                return;
            }

            _logger.LogInformation("Refreshing Indian holidays cache from database...");
            using var scope = _scopeFactory.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<IIndianHolidayRepository>();
            var holidaysList = await repository.GetAllHolidaysAsync();

            _holidays = holidaysList
                .Select(h => DateOnly.FromDateTime(h.HolidayDate))
                .ToHashSet();

            _lastCacheUpdate = DateTime.UtcNow;
            _logger.LogInformation("Successfully loaded {Count} holidays into memory cache.", _holidays.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to refresh Indian holidays cache.");
        }
        finally
        {
            _cacheLock.Release();
        }
    }
}
