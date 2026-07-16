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

    [HttpPost("run-job")]
    public async Task<IActionResult> RunJob(CancellationToken cancellationToken)
    {
        _logger.LogInformation("API POST Request: Triggering daily Swing Trading EOD Job manually.");
        try
        {
            await _swingTradingService.RunEodJobAsync(cancellationToken);
            return Ok(new { Message = "Swing Trading EOD Daily Job completed successfully!" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed manually executing Swing Trading EOD Job.");
            return StatusCode(500, $"Internal server error: {ex.Message}");
        }
    }

    [HttpPost("backfill")]
    public async Task<IActionResult> Backfill(CancellationToken cancellationToken)
    {
        _logger.LogInformation("API POST Request: Triggering historical backfill/backtest.");
        try
        {
            await _swingTradingService.BackfillHistoricalAnalysesAsync(cancellationToken);
            return Ok(new { Message = "Historical daily analyses backfilled successfully!" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed executing historical backfill.");
            return StatusCode(500, $"Internal server error: {ex.Message}");
        }
    }
}
