using System.Collections.Generic;

namespace QuantEdge.Infrastructure.DTOs;

/// <summary>
/// Container DTO for returning paginated items and total counts to the API/UI.
/// </summary>
public class PaginatedCoverageResult
{
    public IEnumerable<StockCoverageDto> Items { get; set; } = new List<StockCoverageDto>();
    public int TotalCount { get; set; }
    public int PageNumber { get; set; }
    public int PageSize { get; set; }
    public int TotalPages => PageSize > 0 ? (int)System.Math.Ceiling((double)TotalCount / PageSize) : 0;
}
