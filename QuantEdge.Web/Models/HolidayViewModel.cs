using System;

namespace QuantEdge.Web.Models;

/// <summary>
/// View model representing an Indian market holiday in the Web UI.
/// </summary>
public class HolidayViewModel
{
    public int Id { get; set; }
    public DateTime HolidayDate { get; set; }
    public string Description { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}
