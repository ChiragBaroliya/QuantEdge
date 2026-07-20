using System;

namespace QuantEdge.Infrastructure.DTOs;

/// <summary>
/// DTO representing stock detail and candle coverage status returned by sp_get_paginated_stock_coverage.
/// </summary>
public class StockCoverageDto
{
    public int Id { get; set; }
    public string Symbol { get; set; } = string.Empty;
    public string? Name { get; set; }
    public string? Exchange { get; set; }
    public int InstrumentToken { get; set; }
    public bool IsActive { get; set; }
    public int? IsHistryStored1m { get; set; }
    public int? IsHistryStored5m { get; set; }
    public int? IsHistryStored15m { get; set; }
    public int? IsHistryStored60m { get; set; }
    public int? IsHistryStored1d { get; set; }
    public DateTime CreatedAt { get; set; }
    public long Count1d { get; set; }
    public long Count60m { get; set; }
    public DateTime? LastCandleDate { get; set; }
    public int TotalRecords { get; set; }
}
