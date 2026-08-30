using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using QuantEdge.Infrastructure.DTOs;
using QuantEdge.Infrastructure.Interfaces;

namespace QuantEdge.API.Controllers;

[ApiController]
[Route("api/reports")]
[Route("reports")]
public class ReportController : ControllerBase
{
    private readonly ITradingReportService _reportService;
    private readonly ILogger<ReportController> _logger;

    public ReportController(
        ITradingReportService reportService,
        ILogger<ReportController> logger)
    {
        _reportService = reportService ?? throw new ArgumentNullException(nameof(reportService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// GET /api/reports/performance
    /// Returns aggregated investment turnover, net PnL, ROI %, win rate, and initial paged tables.
    /// </summary>
    [HttpGet("performance")]
    public async Task<IActionResult> GetPerformanceReport(
        [FromQuery] string periodType = "daily",
        [FromQuery] string tradeMode = "all",
        [FromQuery] string? userId = null,
        [FromQuery] DateTime? startDate = null,
        [FromQuery] DateTime? endDate = null,
        [FromQuery] string? symbol = null,
        [FromQuery] int periodsPage = 1,
        [FromQuery] int periodsPageSize = 10,
        [FromQuery] string periodsPnlFilter = "all",
        [FromQuery] int tradesPage = 1,
        [FromQuery] int tradesPageSize = 10,
        [FromQuery] string tradesType = "all",
        [FromQuery] string tradesPnlFilter = "all",
        CancellationToken cancellationToken = default)
    {
        try
        {
            var filter = new ReportFilterDto(
                PeriodType: periodType,
                TradeMode: tradeMode,
                UserId: userId,
                StartDate: startDate,
                EndDate: endDate,
                Symbol: symbol,
                PeriodsPage: periodsPage,
                PeriodsPageSize: periodsPageSize,
                PeriodsPnlFilter: periodsPnlFilter,
                TradesPage: tradesPage,
                TradesPageSize: tradesPageSize,
                TradesType: tradesType,
                TradesPnlFilter: tradesPnlFilter
            );

            var report = await _reportService.GetPerformanceReportAsync(filter, cancellationToken);
            return Ok(report);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate performance report.");
            return StatusCode(500, new { message = "Error generating trading performance report.", details = ex.Message });
        }
    }

    /// <summary>
    /// GET /api/reports/trades/paged
    /// Returns paginated closed trade records with database-side filtering.
    /// </summary>
    [HttpGet("trades/paged")]
    public async Task<IActionResult> GetTradesPaged(
        [FromQuery] string tradeMode = "all",
        [FromQuery] string? userId = null,
        [FromQuery] DateTime? startDate = null,
        [FromQuery] DateTime? endDate = null,
        [FromQuery] string? symbol = null,
        [FromQuery] string tradeType = "all",
        [FromQuery] string pnlFilter = "all",
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 10,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var filter = new ReportTradesFilterDto(
                TradeMode: tradeMode,
                UserId: userId,
                StartDate: startDate,
                EndDate: endDate,
                Symbol: symbol,
                TradeType: tradeType,
                PnlFilter: pnlFilter,
                Page: page,
                PageSize: pageSize
            );

            var pagedTrades = await _reportService.GetTradesPagedAsync(filter, cancellationToken);
            return Ok(pagedTrades);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to retrieve paged trade records.");
            return StatusCode(500, new { message = "Error fetching paged trades.", details = ex.Message });
        }
    }

    /// <summary>
    /// GET /api/reports/periods/paged
    /// Returns paginated periodic breakdown records with filtering.
    /// </summary>
    [HttpGet("periods/paged")]
    public async Task<IActionResult> GetPeriodsPaged(
        [FromQuery] string periodType = "daily",
        [FromQuery] string tradeMode = "all",
        [FromQuery] string? userId = null,
        [FromQuery] DateTime? startDate = null,
        [FromQuery] DateTime? endDate = null,
        [FromQuery] string pnlFilter = "all",
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 10,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var filter = new ReportPeriodsFilterDto(
                PeriodType: periodType,
                TradeMode: tradeMode,
                UserId: userId,
                StartDate: startDate,
                EndDate: endDate,
                PnlFilter: pnlFilter,
                Page: page,
                PageSize: pageSize
            );

            var pagedPeriods = await _reportService.GetPeriodsPagedAsync(filter, cancellationToken);
            return Ok(pagedPeriods);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to retrieve paged period records.");
            return StatusCode(500, new { message = "Error fetching paged periods.", details = ex.Message });
        }
    }

    /// <summary>
    /// GET /api/reports/export
    /// Exports the filtered trade records and executive summary as a CSV file.
    /// </summary>
    [HttpGet("export")]
    public async Task<IActionResult> ExportReportCsv(
        [FromQuery] string periodType = "daily",
        [FromQuery] string tradeMode = "all",
        [FromQuery] string? userId = null,
        [FromQuery] DateTime? startDate = null,
        [FromQuery] DateTime? endDate = null,
        [FromQuery] string? symbol = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var filter = new ReportFilterDto(
                PeriodType: periodType,
                TradeMode: tradeMode,
                UserId: userId,
                StartDate: startDate,
                EndDate: endDate,
                Symbol: symbol
            );

            var fileBytes = await _reportService.ExportReportCsvAsync(filter, cancellationToken);
            string filename = $"QuantEdge_Trading_Report_{periodType}_{DateTime.UtcNow:yyyyMMdd_HHmmss}.csv";
            return File(fileBytes, "text/csv", filename);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to export performance report to CSV.");
            return StatusCode(500, new { message = "Error exporting trading report.", details = ex.Message });
        }
    }
}
