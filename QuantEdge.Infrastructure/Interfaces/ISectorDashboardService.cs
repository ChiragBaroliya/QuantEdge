using System.Threading.Tasks;
using QuantEdge.Infrastructure.DTOs;

namespace QuantEdge.Infrastructure.Interfaces;

public interface ISectorDashboardService
{
    /// <summary>Every active sector with its strength, breadth and BUY / WATCH / NO TRADE counts. refresh = re-score now instead of using the cached minute.</summary>
    Task<SectorOverviewDto> GetOverviewAsync(int userId = 1, bool refresh = false);

    /// <summary>One sector and the final trade status of each linked stock; null when the sector doesn't exist.</summary>
    Task<SectorDetailDto?> GetSectorDetailAsync(int sectorId, int userId = 1, bool refresh = false);
}
