namespace QuantEdge.Infrastructure.DTOs;

/// <summary>
/// DTO representing an ETF row returned by sp_get_paginated_etf_list.
/// </summary>
public class EtfListItemDto
{
    public int Id { get; set; }
    public string Symbol { get; set; } = string.Empty;
    public string? Name { get; set; }
    public string? Exchange { get; set; }
    public decimal? LastPrice { get; set; }
    public bool IsActive { get; set; }
    public int TotalRecords { get; set; }
}
