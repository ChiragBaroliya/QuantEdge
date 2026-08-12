using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using QuantEdge.Infrastructure.Helpers;
using QuantEdge.Infrastructure.Interfaces;
using QuantEdge.Infrastructure.Persistence.Repositories;

namespace QuantEdge.Infrastructure.Services;

/// <summary>
/// Thread-safe service validating Indian stock market hours.
/// Caches daily trading day status (weekend + holiday check) once per day in memory.
/// On day-change (midnight transition), flushes old cache and executes a single morning DB query to reload holidays.
/// </summary>
public class MarketHoursService : IMarketHoursService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MarketHoursService> _logger;
    private readonly TimeZoneInfo _indianTimeZone;

    private static readonly TimeSpan MarketOpenTime = new(9, 15, 0);   // 09:15 AM IST
    private static readonly TimeSpan MarketCloseTime = new(15, 30, 0); // 03:30 PM IST

    private HashSet<DateOnly> _holidays = new();
    private DateTime _lastHolidaysDbFetch = DateTime.MinValue;

    private DateOnly? _cachedDate;
    private bool _isTodayTradingDay;

    private readonly SemaphoreSlim _cacheLock = new(1, 1);

    public MarketHoursService(IServiceScopeFactory scopeFactory, ILogger<MarketHoursService> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _indianTimeZone = TimeZoneHelper.IndianTimeZone;
    }

    /// <inheritdoc />
    public async Task<bool> IsWithinMarketHoursAsync(DateTime? time = null)
    {
        var utcTime = time ?? DateTime.UtcNow;
        var istTime = TimeZoneInfo.ConvertTimeFromUtc(utcTime, _indianTimeZone);
        var dateOnly = DateOnly.FromDateTime(istTime);

        // Evaluate daily trading day status (weekend + holiday check) ONLY once per calendar day at new day arrival (Single Morning DB call)
        if (_cachedDate != dateOnly)
        {
            await EnsureDailyCacheInitializedAsync(dateOnly);
        }

        // If today is not a trading day (weekend or holiday), return false immediately (< 0.001 ms)
        if (!_isTodayTradingDay)
        {
            return false;
        }

        // Check if current IST time of day falls within 09:15 AM to 03:30 PM IST
        var timeOfDay = istTime.TimeOfDay;
        return timeOfDay >= MarketOpenTime && timeOfDay < MarketCloseTime;
    }

    /// <inheritdoc />
    public async Task RefreshHolidaysCacheAsync()
    {
        await _cacheLock.WaitAsync();
        try
        {
            await FetchHolidaysFromDbAsync();
            _cachedDate = null; // Invalidate daily cache so next check re-evaluates
            _logger.LogInformation("MarketHoursService: Successfully refreshed holidays cache and invalidated daily status.");
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    private async Task EnsureDailyCacheInitializedAsync(DateOnly targetDate)
    {
        await _cacheLock.WaitAsync();
        try
        {
            if (_cachedDate == targetDate) return;

            // Day change detected (Midnight transition): Flush old cache and fetch fresh holidays from DB (Single Morning DB Call)
            _logger.LogInformation("MarketHoursService: New day detected ({TargetDate}). Flushing old cache and reloading holidays from DB...", targetDate);
            await FetchHolidaysFromDbAsync();

            DayOfWeek dayOfWeek = targetDate.DayOfWeek;
            bool isWeekend = dayOfWeek == DayOfWeek.Saturday || dayOfWeek == DayOfWeek.Sunday;
            bool isHoliday = _holidays.Contains(targetDate);

            _isTodayTradingDay = !isWeekend && !isHoliday;
            _cachedDate = targetDate;

            _logger.LogInformation("MarketHoursService: Initialized daily status for {Date} ({DayOfWeek}). IsTradingDay: {IsTradingDay} (Weekend: {IsWeekend}, Holiday: {IsHoliday})",
                targetDate, dayOfWeek, _isTodayTradingDay, isWeekend, isHoliday);
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    private async Task FetchHolidaysFromDbAsync()
    {
        try
        {
            _logger.LogInformation("MarketHoursService: Fetching Indian holidays from database...");
            using var scope = _scopeFactory.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<IIndianHolidayRepository>();
            var holidaysList = await repository.GetAllHolidaysAsync();

            _holidays = holidaysList
                .Select(h => DateOnly.FromDateTime(h.HolidayDate))
                .ToHashSet();

            _lastHolidaysDbFetch = DateTime.UtcNow;
            _logger.LogInformation("MarketHoursService: Successfully loaded {Count} holidays from DB.", _holidays.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MarketHoursService: Failed to fetch Indian holidays from database.");
        }
    }
}
