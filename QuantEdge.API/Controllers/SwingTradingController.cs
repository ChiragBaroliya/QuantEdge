using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using QuantEdge.Infrastructure.Interfaces;

namespace QuantEdge.API.Controllers;

[ApiController]
[Route("swing")]
public class SwingTradingController : ControllerBase

{
    private readonly ISwingTradingService _swingTradingService;
    private readonly ILogger<SwingTradingController> _logger;

    public SwingTradingController(
        ISwingTradingService swingTradingService,
        ILogger<SwingTradingController> logger)
    {
        _swingTradingService = swingTradingService ?? throw new ArgumentNullException(nameof(swingTradingService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    [HttpGet("dashboard")]
    public async Task<IActionResult> GetDashboard(CancellationToken cancellationToken)
    {
        _logger.LogInformation("API GET Request: Fetching swing trading dashboard data.");
        try
        {
            var result = await _swingTradingService.GetDashboardDataAsync(cancellationToken);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to retrieve swing trading dashboard data.");
            return StatusCode(500, $"Internal server error: {ex.Message}");
        }
    }

    [HttpGet("job-status")]
    public IActionResult GetJobStatus([FromQuery] string jobType = "backfill")
    {
        var status = _swingTradingService.GetJobStatus(jobType);
        return Ok(status);
    }

    [HttpGet("slots")]
    public async Task<IActionResult> GetSlots([FromQuery] string? date, CancellationToken cancellationToken)
    {
        _logger.LogInformation("API GET Request: Fetching swing scan slots for date: {Date}", date);
        try
        {
            DateTime queryDate = DateTime.UtcNow.Date;
            if (!string.IsNullOrWhiteSpace(date) && DateTime.TryParse(date, out var parsedDate))
            {
                queryDate = parsedDate.Date;
            }

            var slots = await _swingTradingService.GetScanSlotsAsync(queryDate, cancellationToken);
            return Ok(slots);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to retrieve scan slots for date: {Date}", date);
            return StatusCode(500, $"Internal server error: {ex.Message}");
        }
    }

    [HttpGet("slot-recommendations")]
    public async Task<IActionResult> GetSlotRecommendations([FromQuery] string? date, [FromQuery] string? slot, CancellationToken cancellationToken)
    {
        _logger.LogInformation("API GET Request: Fetching swing slot recommendations for date: {Date}, slot: {Slot}", date, slot);
        try
        {
            DateTime queryDate = DateTime.UtcNow.Date;
            if (!string.IsNullOrWhiteSpace(date) && DateTime.TryParse(date, out var parsedDate))
            {
                queryDate = parsedDate.Date;
            }

            string slotLabel = slot ?? "all";
            var recommendations = await _swingTradingService.GetSlotRecommendationsAsync(queryDate, slotLabel, cancellationToken);
            return Ok(recommendations);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to retrieve slot recommendations for date: {Date}, slot: {Slot}", date, slot);
            return StatusCode(500, $"Internal server error: {ex.Message}");
        }
    }

    [HttpPost("run-intraday")]
    public IActionResult RunIntradayScan()
    {
        _logger.LogInformation("API POST Request: Triggering 30-minute Swing Trading intraday scan in background.");
        if (_swingTradingService.IsJobRunning("intraday30m"))
        {
            return Ok(new { Message = "30-minute Swing Trading scan is already running in background.", TaskStarted = false });
        }

        _swingTradingService.UpdateJobProgress("intraday30m", true, 0, "Initiating 30-minute Intraday Swing Scan...");

        _ = Task.Run(async () =>
        {
            try
            {
                await _swingTradingService.RunIntradaySlotScanAsync(null, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Background 30-minute Swing Scan failed.");
                _swingTradingService.UpdateJobProgress("intraday30m", false, 0, "30-minute scan failed.", ex.Message);
            }
        });

        return Accepted(new { Message = "30-minute Swing Trading scan initiated in background.", TaskStarted = true });
    }

    [HttpPost("run-job")]
    public IActionResult RunJob()
    {
        _logger.LogInformation("API POST Request: Triggering daily Swing Trading EOD Job in background.");
        if (_swingTradingService.IsJobRunning("eod"))
        {
            return Ok(new { Message = "Swing Trading EOD Job is already running in background.", TaskStarted = false });
        }

        _swingTradingService.UpdateJobProgress("eod", true, 0, "Initiating EOD Daily Analysis...");

        _ = Task.Run(async () =>
        {
            try
            {
                await _swingTradingService.RunEodJobAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Background EOD Job failed.");
                _swingTradingService.UpdateJobProgress("eod", false, 0, "EOD Job failed.", ex.Message);
            }
        });

        return Accepted(new { Message = "Swing Trading EOD Daily Job initiated in background.", TaskStarted = true });
    }

    [HttpPost("backfill")]
    public IActionResult Backfill()
    {
        _logger.LogInformation("API POST Request: Triggering historical backfill in background.");
        if (_swingTradingService.IsJobRunning("backfill"))
        {
            return Ok(new { Message = "Historical backtest job is already running in background.", TaskStarted = false });
        }

        _swingTradingService.UpdateJobProgress("backfill", true, 0, "Initiating historical 2-year backtest...");

        _ = Task.Run(async () =>
        {
            try
            {
                await _swingTradingService.BackfillHistoricalAnalysesAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Background historical backfill failed.");
                _swingTradingService.UpdateJobProgress("backfill", false, 0, "Backtest failed.", ex.Message);
            }
        });

        return Accepted(new { Message = "Historical backtest task started in background. Processing symbols and backtesting historical performance...", TaskStarted = true });
    }
}


