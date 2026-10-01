using System.Collections.Generic;
using System.Threading.Tasks;

namespace QuantEdge.Infrastructure.Persistence.Repositories;

/// <summary>One sector-stock link. StockId/Symbol are null for a sector that has no linked stocks yet.</summary>
public class SectorStockRow
{
    public int SectorId { get; set; }
    public string SectorName { get; set; } = string.Empty;
    public int? StockId { get; set; }
    public string? Symbol { get; set; }
    public string? StockName { get; set; }
}

/// <summary>Sectors and their linked stocks for the Sector Dashboard.</summary>
public interface ISectorRepository
{
    Task<IReadOnlyList<SectorStockRow>> GetSectorStocksAsync();
}
