using System;

namespace QuantEdge.Domain.Entities;

/// <summary>
/// Domain entity representing a stock instrument mapping stored in the stock_master table.
/// </summary>
public class StockMaster
{
    public int Id { get; set; }
    public string Symbol { get; set; } = string.Empty;
    public int InstrumentToken { get; set; }
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
}
