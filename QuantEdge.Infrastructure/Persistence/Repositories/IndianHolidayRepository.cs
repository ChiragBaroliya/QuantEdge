using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Dapper;
using QuantEdge.Domain.Entities;

namespace QuantEdge.Infrastructure.Persistence.Repositories;

/// <summary>
/// Dapper implementation of IIndianHolidayRepository using PostgreSQL stored functions and procedures.
/// </summary>
public class IndianHolidayRepository : IIndianHolidayRepository
{
    private readonly IDbConnectionFactory _connectionFactory;

    public IndianHolidayRepository(IDbConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
    }

    public async Task<IEnumerable<IndianHoliday>> GetAllHolidaysAsync()
    {
        using var connection = _connectionFactory.CreateConnection();
        return await connection.QueryAsync<IndianHoliday>(
            "SELECT * FROM sp_get_indian_holidays();"
        );
    }

    public async Task InsertHolidayAsync(DateTime holidayDate, string description)
    {
        using var connection = _connectionFactory.CreateConnection();
        await connection.ExecuteAsync(
            "CALL sp_insert_indian_holiday(@p_holiday_date::date, @p_description::varchar);",
            new { p_holiday_date = holidayDate.Date, p_description = description }
        );
    }

    public async Task DeleteHolidayAsync(int id)
    {
        using var connection = _connectionFactory.CreateConnection();
        await connection.ExecuteAsync(
            "CALL sp_delete_indian_holiday(@p_id::integer);",
            new { p_id = id }
        );
    }

    public async Task<bool> IsHolidayAsync(DateTime date)
    {
        using var connection = _connectionFactory.CreateConnection();
        return await connection.ExecuteScalarAsync<bool>(
            "SELECT sp_is_indian_holiday(@p_date::date);",
            new { p_date = date.Date }
        );
    }
}
