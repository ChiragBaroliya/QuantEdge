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

/// <summary>An active sector with its linked stock count (Data Coverage Manager sector filter / mapping popup).</summary>
public class SectorListItem
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int StockCount { get; set; }
}

/// <summary>Sectors and their linked stocks for the Sector Dashboard and the Data Coverage Manager.</summary>
public interface ISectorRepository
{
    Task<IReadOnlyList<SectorStockRow>> GetSectorStocksAsync();

    Task<IReadOnlyList<SectorListItem>> GetSectorListAsync();

    /// <summary>Returns the sector with this name (case-insensitive, reactivated if switched off) or creates it.</summary>
    Task<SectorListItem> UpsertSectorAsync(string name);

    /// <summary>Makes <paramref name="sectorIds"/> the stock's full set of active sectors.</summary>
    Task SetStockSectorsAsync(int stockId, IEnumerable<int> sectorIds);
}
