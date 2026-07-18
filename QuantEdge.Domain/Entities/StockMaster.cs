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
    
    // Missing Zerodha Instrument fields
    public string? ExchangeToken { get; set; }
    public string? Name { get; set; }
    public decimal? LastPrice { get; set; }
    public DateTime? Expiry { get; set; }
    public decimal? Strike { get; set; }
    public decimal? TickSize { get; set; }
    public int? LotSize { get; set; }
    public string? InstrumentType { get; set; }
    public string? Segment { get; set; }
    public string? Exchange { get; set; }
    public int? IsHistryStored { get; set; }

    public DateTime CreatedAt { get; set; }
}
