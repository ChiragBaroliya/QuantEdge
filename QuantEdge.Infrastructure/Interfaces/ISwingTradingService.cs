using System.Threading;
using System.Threading.Tasks;
using QuantEdge.Infrastructure.DTOs;

namespace QuantEdge.Infrastructure.Interfaces;

public interface ISwingTradingService
{
    Task<SwingTradingDashboardDto> GetDashboardDataAsync(CancellationToken cancellationToken);
    Task RunEodJobAsync(CancellationToken cancellationToken);
    Task BackfillHistoricalAnalysesAsync(CancellationToken cancellationToken);
}
