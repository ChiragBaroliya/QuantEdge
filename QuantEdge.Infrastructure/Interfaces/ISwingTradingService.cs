using System.Threading;
using System.Threading.Tasks;
using QuantEdge.Infrastructure.DTOs;

namespace QuantEdge.Infrastructure.Interfaces;

public interface ISwingTradingService
{
    Task<SwingTradingDashboardDto> GetDashboardDataAsync(CancellationToken cancellationToken);
    Task RunEodJobAsync(CancellationToken cancellationToken);
    Task BackfillHistoricalAnalysesAsync(CancellationToken cancellationToken);
    SwingJobStatusDto GetJobStatus(string jobType);
    bool IsJobRunning(string jobType);
    void UpdateJobProgress(string jobType, bool running, int progress, string message, string? error = null);
}

