using System.Collections.Generic;

namespace QuantEdge.Infrastructure.DTOs;

/// <summary>
/// Paginated result wrapper for the Swing Trading ETF List screen.
/// </summary>
public class PaginatedEtfListResult
{
    public IEnumerable<EtfListItemDto> Items { get; set; } = new List<EtfListItemDto>();
    public int TotalCount { get; set; }
    public int PageNumber { get; set; }
    public int PageSize { get; set; }
    public int TotalPages => PageSize > 0 ? (int)System.Math.Ceiling((double)TotalCount / PageSize) : 0;
}
