using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Dapper;
using QuantEdge.Domain.Entities;

using System.Linq;
using QuantEdge.Infrastructure.Interfaces;

namespace QuantEdge.Infrastructure.Persistence.Repositories;

/// <summary>
/// Dapper implementation of IIndianHolidayRepository using PostgreSQL stored functions and procedures.
/// Uses Memory Cache for high performance holiday checks.
/// </summary>
public class IndianHolidayRepository : IIndianHolidayRepository
{
    private readonly IDbConnectionFactory _connectionFactory;
    private readonly ICacheService? _cacheService;

    public IndianHolidayRepository(IDbConnectionFactory connectionFactory, ICacheService? cacheService = null)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _cacheService = cacheService;
    }

    public async Task<IEnumerable<IndianHoliday>> GetAllHolidaysAsync()
    {
        string cacheKey = "indian_holidays_all";
        if (_cacheService != null)
        {
            var cached = await _cacheService.GetAsync<IEnumerable<IndianHoliday>>(cacheKey);
            if (cached != null) return cached;
        }

        using var connection = _connectionFactory.CreateConnection();
        var holidays = (await connection.QueryAsync<IndianHoliday>(
            "SELECT * FROM sp_get_indian_holidays();"
        )).ToList();

        if (_cacheService != null && holidays.Any())
        {
            await _cacheService.SetAsync(cacheKey, holidays, TimeSpan.FromHours(1));
        }

        return holidays;
    }

    public async Task InsertHolidayAsync(DateTime holidayDate, string description)
    {
        using var connection = _connectionFactory.CreateConnection();
        await connection.ExecuteAsync(
            "CALL sp_insert_indian_holiday(@p_holiday_date::date, @p_description::varchar);",
            new { p_holiday_date = holidayDate.Date, p_description = description }
        );

        if (_cacheService != null)
        {
            await _cacheService.RemoveAsync("indian_holidays_all");
        }
    }

    public async Task DeleteHolidayAsync(int id)
    {
        using var connection = _connectionFactory.CreateConnection();
        await connection.ExecuteAsync(
            "CALL sp_delete_indian_holiday(@p_id::integer);",
            new { p_id = id }
        );

        if (_cacheService != null)
        {
            await _cacheService.RemoveAsync("indian_holidays_all");
        }
    }

    public async Task<bool> IsHolidayAsync(DateTime date)
    {
        var holidays = await GetAllHolidaysAsync();
        if (holidays != null && holidays.Any())
        {
            return holidays.Any(h => h.HolidayDate.Date == date.Date);
        }

        using var connection = _connectionFactory.CreateConnection();
        return await connection.ExecuteScalarAsync<bool>(
            "SELECT sp_is_indian_holiday(@p_date::date);",
            new { p_date = date.Date }
        );
    }
}
