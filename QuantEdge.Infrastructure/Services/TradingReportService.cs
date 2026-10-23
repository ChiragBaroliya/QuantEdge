using System;
using System.Collections.Generic;
using System.Globalization;
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

        // Every field that changes the response is in the key, so a changed filter never gets another filter's tables.
        string cacheKey = $"report_overview_{filter.PeriodType}_{filter.TradeMode}_{filter.UserId ?? "all"}_{filter.StartDate:yyyyMMdd}_{filter.EndDate:yyyyMMdd}_{filter.Symbol ?? "all"}"
            + $"_p{filter.PeriodsPage}x{filter.PeriodsPageSize}_{filter.PeriodsPnlFilter}"
            + $"_t{filter.TradesPage}x{filter.TradesPageSize}_{filter.TradesType}_{filter.TradesPnlFilter}";

        if (_cacheService != null)
        {
            var cached = await _cacheService.GetAsync<TradingReportResponseDto>(cacheKey);
            if (cached != null)
            {
                return cached;
            }
        }

        // Summary, equity curve and periods come from one load of the trades, so they always agree with each other.
        var allTradesTask = _reportRepository.GetTradesAsync(filter, cancellationToken);

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

        await Task.WhenAll(allTradesTask, tradesTask);

        var allTrades = await allTradesTask;
        var periods = TradingReportCalculator.Periods(allTrades, filter.PeriodType);

        var response = new TradingReportResponseDto(
            Filter: filter,
            Summary: TradingReportCalculator.Summary(allTrades),
            Periods: TradingReportCalculator.PagePeriods(periods, filter.PeriodsPnlFilter, filter.PeriodsPage, filter.PeriodsPageSize),
            EquityCurve: TradingReportCalculator.EquityCurve(allTrades),
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
        var summary = TradingReportCalculator.Summary(trades);
        var inv = CultureInfo.InvariantCulture;

        var sb = new StringBuilder();
        sb.AppendLine("QUANTEDGE TRADING PERFORMANCE REPORT");
        sb.AppendLine($"Generated At,{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine($"Filter Mode,{Csv(filter.TradeMode)}");
        sb.AppendLine($"Period Type,{Csv(filter.PeriodType)}");
        sb.AppendLine($"Symbol,{Csv(string.IsNullOrWhiteSpace(filter.Symbol) ? "All" : filter.Symbol)}");
        sb.AppendLine($"Date Range (IST),{(filter.StartDate.HasValue ? filter.StartDate.Value.ToString("yyyy-MM-dd") : "All")} to {(filter.EndDate.HasValue ? filter.EndDate.Value.ToString("yyyy-MM-dd") : "All")}");
        sb.AppendLine();
        sb.AppendLine("EXECUTIVE SUMMARY");
        sb.AppendLine(string.Create(inv, $"Turnover - Total Invested (INR),{summary.TotalInvestedCapital:F2}"));
        sb.AppendLine(string.Create(inv, $"Peak Capital Deployed (INR),{summary.PeakCapitalDeployed:F2}"));
        sb.AppendLine(string.Create(inv, $"Gross Realized P&L (INR),{summary.GrossRealizedPnl:F2}"));
        sb.AppendLine(string.Create(inv, $"Charges (INR),{summary.TotalCharges:F2}"));
        sb.AppendLine(string.Create(inv, $"Net Realized P&L after charges (INR),{summary.NetRealizedPnl:F2}"));
        sb.AppendLine(string.Create(inv, $"ROI on Peak Capital (%),{summary.TotalRoiPct:F2}"));
        sb.AppendLine(string.Create(inv, $"Max Drawdown (%),{summary.MaxDrawdownPct:F2}"));
        sb.AppendLine($"Total Trades,{summary.TotalTrades}");
        sb.AppendLine($"Winning Trades,{summary.WinningTrades}");
        sb.AppendLine($"Losing Trades,{summary.LosingTrades}");
        sb.AppendLine(string.Create(inv, $"Win Rate (%),{summary.WinRatePct:F2}"));
        sb.AppendLine(string.Create(inv, $"Profit Factor,{summary.ProfitFactor:F2}"));
        sb.AppendLine();
        sb.AppendLine("DETAILED TRADE LOG");
        sb.AppendLine("Trade ID,Execution Date (UTC),Symbol,Mode,Side,Quantity,Entry Price (INR),Exit Price (INR),Invested Capital (INR),Gross P&L (INR),Charges (INR),Net P&L (INR),Charge Rates,Charge Source,Net Return (%),Trade Type,Exit Reason,User");

        foreach (var t in trades)
        {
            sb.AppendLine(string.Create(inv,
                $"{t.Id},{t.ExecutedAt:yyyy-MM-dd HH:mm:ss},{Csv(t.Symbol)},{Csv(t.Mode)},{t.Side},{t.Quantity},{t.EntryPrice:F2},{t.ExecutedPrice:F2},{t.InvestedAmount:F2},{t.RealizedPnl:F2},{t.Charges:F2},{t.NetPnl:F2},{t.ChargeProduct},{t.ChargeSource},{t.ReturnPct:F2},{Csv(t.TradeType)},{Csv(t.ExitReason)},{Csv(t.Username)}"));
        }

        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    // RFC 4180 text field: quoted, with embedded quotes doubled, so commas/quotes in values can't shift columns.
    private static string Csv(string? value) => "\"" + (value ?? string.Empty).Replace("\"", "\"\"") + "\"";
}
