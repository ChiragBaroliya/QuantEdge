using System;

namespace QuantEdge.Domain.Entities;

/// <summary>
/// Domain entity representing an Indian stock market holiday.
/// </summary>
public class IndianHoliday
{
    public int Id { get; set; }
    public DateTime HolidayDate { get; set; }
    public string Description { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}
