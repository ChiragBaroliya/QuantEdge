using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using QuantEdge.Domain.Entities;

namespace QuantEdge.Infrastructure.Persistence.Repositories;

/// <summary>
/// Interface representing Dapper database calls for Indian market holidays.
/// </summary>
public interface IIndianHolidayRepository
{
    Task<IEnumerable<IndianHoliday>> GetAllHolidaysAsync();
    Task InsertHolidayAsync(DateTime holidayDate, string description);
    Task DeleteHolidayAsync(int id);
    Task<bool> IsHolidayAsync(DateTime date);
}
