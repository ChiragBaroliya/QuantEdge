using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using QuantEdge.Infrastructure.DTOs;

namespace QuantEdge.Infrastructure.Persistence.Repositories;

/// <summary>
/// Repository interface for querying aggregated trading investment reports, performance metrics, and paginated records.
/// </summary>
public interface ITradingReportRepository
{
    Task<IReadOnlyList<TradingReportTradeDto>> GetTradesAsync(ReportFilterDto filter, CancellationToken cancellationToken = default);
    Task<PagedResult<TradingReportTradeDto>> GetTradesPagedAsync(ReportTradesFilterDto filter, CancellationToken cancellationToken = default);
    Task<TradingReportSummaryDto> GetSummaryAsync(ReportFilterDto filter, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TradingReportPeriodDto>> GetPeriodicBreakdownAsync(ReportFilterDto filter, CancellationToken cancellationToken = default);
    Task<PagedResult<TradingReportPeriodDto>> GetPeriodicBreakdownPagedAsync(ReportPeriodsFilterDto filter, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TradingReportEquityPointDto>> GetEquityCurveAsync(ReportFilterDto filter, CancellationToken cancellationToken = default);
}
