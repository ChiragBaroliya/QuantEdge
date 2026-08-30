using System.Threading;
using System.Threading.Tasks;
using QuantEdge.Infrastructure.DTOs;

namespace QuantEdge.Infrastructure.Interfaces;

/// <summary>
/// Service interface for generating performance analytics, trading investment reports, and paged data streams.
/// </summary>
public interface ITradingReportService
{
    Task<TradingReportResponseDto> GetPerformanceReportAsync(ReportFilterDto filter, CancellationToken cancellationToken = default);
    Task<PagedResult<TradingReportTradeDto>> GetTradesPagedAsync(ReportTradesFilterDto filter, CancellationToken cancellationToken = default);
    Task<PagedResult<TradingReportPeriodDto>> GetPeriodsPagedAsync(ReportPeriodsFilterDto filter, CancellationToken cancellationToken = default);
    Task<byte[]> ExportReportCsvAsync(ReportFilterDto filter, CancellationToken cancellationToken = default);
}
