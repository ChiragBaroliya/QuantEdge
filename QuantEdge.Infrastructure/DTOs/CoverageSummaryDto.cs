namespace QuantEdge.Infrastructure.DTOs;

/// <summary>
/// DTO representing overall database stats returned by sp_get_data_coverage_summary.
/// </summary>
public class CoverageSummaryDto
{
    public int TotalStocks { get; set; }
    public int ActiveCount { get; set; }
    public int InactiveCount { get; set; }
    public int HistoryMissingCount { get; set; }
}
