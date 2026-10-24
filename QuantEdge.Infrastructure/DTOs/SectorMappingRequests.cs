namespace QuantEdge.Infrastructure.DTOs;

/// <summary>Body for POST /datacoverage/sectors (Data Coverage Manager "Add new sector").</summary>
public class CreateSectorRequest
{
    public string Name { get; set; } = string.Empty;
}

/// <summary>Body for PUT /datacoverage/{id}/sectors: the stock's full set of sector ids (empty clears it).</summary>
public class SetStockSectorsRequest
{
    public int[] SectorIds { get; set; } = System.Array.Empty<int>();
}
