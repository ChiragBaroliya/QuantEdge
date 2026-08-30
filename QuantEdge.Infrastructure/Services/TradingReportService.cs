using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using QuantEdge.Infrastructure.DTOs;
using QuantEdge.Infrastructure.Interfaces;
using QuantEdge.Infrastructure.Persistence.Repositories;

namespace QuantEdge.Infrastructure.Services;

/// <summary>
/// Service implementing performance analysis, multi-timeframe periodic aggregations, paginated streams, and report exports.
/// </summary>
public class TradingReportService : ITradingReportService
{
    private readonly ITradingReportRepository _reportRepository;
    private readonly ICacheService? _cacheService;
    private readonly ILogger<TradingReportService> _logger;

    public TradingReportService(
        ITradingReportRepository reportRepository,
        ILogger<TradingReportService> logger,
        ICacheService? cacheService = null)
    {
        _reportRepository = reportRepository ?? throw new ArgumentNullException(nameof(reportRepository));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _cacheService = cacheService;
    }

    public async Task<TradingReportResponseDto> GetPerformanceReportAsync(ReportFilterDto filter, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Generating Trading Performance Report: Period={Period}, Mode={Mode}, User={User}, Range={Start:yyyy-MM-dd} to {End:yyyy-MM-dd}",
            filter.PeriodType, filter.TradeMode, filter.UserId ?? "all", filter.StartDate, filter.EndDate);

        string cacheKey = $"report_overview_{filter.PeriodType}_{filter.TradeMode}_{filter.UserId ?? "all"}_{filter.StartDate:yyyyMMdd}_{filter.EndDate:yyyyMMdd}_{filter.Symbol ?? "all"}_p{filter.PeriodsPage}_t{filter.TradesPage}";
        
        if (_cacheService != null)
        {
            var cached = await _cacheService.GetAsync<TradingReportResponseDto>(cacheKey);
            if (cached != null)
            {
                return cached;
            }
        }

        var summaryTask = _reportRepository.GetSummaryAsync(filter, cancellationToken);
        var equityTask = _reportRepository.GetEquityCurveAsync(filter, cancellationToken);
        
        var periodsFilter = new ReportPeriodsFilterDto(
            PeriodType: filter.PeriodType,
            TradeMode: filter.TradeMode,
            UserId: filter.UserId,
            StartDate: filter.StartDate,
            EndDate: filter.EndDate,
            PnlFilter: filter.PeriodsPnlFilter,
            Page: filter.PeriodsPage,
            PageSize: filter.PeriodsPageSize
        );
        var periodsTask = _reportRepository.GetPeriodicBreakdownPagedAsync(periodsFilter, cancellationToken);

        var tradesFilter = new ReportTradesFilterDto(
            TradeMode: filter.TradeMode,
            UserId: filter.UserId,
            StartDate: filter.StartDate,
            EndDate: filter.EndDate,
            Symbol: filter.Symbol,
            TradeType: filter.TradesType,
            PnlFilter: filter.TradesPnlFilter,
            Page: filter.TradesPage,
            PageSize: filter.TradesPageSize
        );
        var tradesTask = _reportRepository.GetTradesPagedAsync(tradesFilter, cancellationToken);

        await Task.WhenAll(summaryTask, equityTask, periodsTask, tradesTask);

        var response = new TradingReportResponseDto(
            Filter: filter,
            Summary: await summaryTask,
            Periods: await periodsTask,
            EquityCurve: await equityTask,
            RecentTrades: await tradesTask
        );

        if (_cacheService != null)
        {
            await _cacheService.SetAsync(cacheKey, response, TimeSpan.FromSeconds(20));
        }

        return response;
    }

    public async Task<PagedResult<TradingReportTradeDto>> GetTradesPagedAsync(ReportTradesFilterDto filter, CancellationToken cancellationToken = default)
    {
        return await _reportRepository.GetTradesPagedAsync(filter, cancellationToken);
    }

    public async Task<PagedResult<TradingReportPeriodDto>> GetPeriodsPagedAsync(ReportPeriodsFilterDto filter, CancellationToken cancellationToken = default)
    {
        return await _reportRepository.GetPeriodicBreakdownPagedAsync(filter, cancellationToken);
    }

    public async Task<byte[]> ExportReportCsvAsync(ReportFilterDto filter, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Exporting Trading Performance Report to CSV...");
        var trades = await _reportRepository.GetTradesAsync(filter, cancellationToken);
        var summary = await _reportRepository.GetSummaryAsync(filter, cancellationToken);

        var sb = new StringBuilder();
        sb.AppendLine("QUANTEDGE TRADING PERFORMANCE REPORT");
        sb.AppendLine($"Generated At,{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine($"Filter Mode,{filter.TradeMode}");
        sb.AppendLine($"Period Type,{filter.PeriodType}");
        sb.AppendLine($"Date Range,{(filter.StartDate.HasValue ? filter.StartDate.Value.ToString("yyyy-MM-dd") : "All")} to {(filter.EndDate.HasValue ? filter.EndDate.Value.ToString("yyyy-MM-dd") : "All")}");
        sb.AppendLine();
        sb.AppendLine("EXECUTIVE SUMMARY");
        sb.AppendLine($"Total Invested Capital (INR),{summary.TotalInvestedCapital:F2}");
        sb.AppendLine($"Net Realized P&L (INR),{summary.NetRealizedPnl:F2}");
        sb.AppendLine($"Total ROI (%),{summary.TotalRoiPct:F2}%");
        sb.AppendLine($"Total Trades,{summary.TotalTrades}");
        sb.AppendLine($"Winning Trades,{summary.WinningTrades}");
        sb.AppendLine($"Losing Trades,{summary.LosingTrades}");
        sb.AppendLine($"Win Rate (%),{summary.WinRatePct:F2}%");
        sb.AppendLine($"Profit Factor,{summary.ProfitFactor:F2}");
        sb.AppendLine();
        sb.AppendLine("DETAILED TRADE LOG");
        sb.AppendLine("Trade ID,Execution Date,Symbol,Mode,Side,Quantity,Entry Price (INR),Exit Price (INR),Invested Capital (INR),Realized P&L (INR),Return (%),Trade Type,Exit Reason,User");

        foreach (var t in trades)
        {
            sb.AppendLine($"{t.Id},{t.ExecutedAt:yyyy-MM-dd HH:mm:ss},{t.Symbol},{t.Mode},{t.Side},{t.Quantity},{t.EntryPrice:F2},{t.ExecutedPrice:F2},{t.InvestedAmount:F2},{t.RealizedPnl:F2},{t.ReturnPct:F2}%,{t.TradeType},\"{t.ExitReason}\",{t.Username}");
        }

        return Encoding.UTF8.GetBytes(sb.ToString());
    }
}
