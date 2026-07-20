namespace QuantEdge.Infrastructure.DTOs;

/// <summary>
/// Request DTO for updating stock active status and history stored flags via sp_update_stock_coverage_flags.
/// </summary>
public class UpdateStockCoverageRequest
{
    public int Id { get; set; }
    public bool IsActive { get; set; }
    public int? IsHistryStored1m { get; set; }
    public int? IsHistryStored5m { get; set; }
    public int? IsHistryStored15m { get; set; }
    public int? IsHistryStored60m { get; set; }
    public int? IsHistryStored1d { get; set; }
}
