using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using QuantEdge.Infrastructure.Interfaces;

namespace QuantEdge.API.Controllers;

/// <summary>
/// Sector Dashboard: sector strength and the BUY / WATCH / NO TRADE status of every stock in a sector,
/// from the same SwingDecisionEngine evaluation the auto-trading bot runs.
/// </summary>
[ApiController]
[Route("sectors")]
public class SectorController : ControllerBase
{
    private readonly ISectorDashboardService _sectorService;
    private readonly ILogger<SectorController> _logger;

    public SectorController(ISectorDashboardService sectorService, ILogger<SectorController> logger)
    {
        _sectorService = sectorService ?? throw new ArgumentNullException(nameof(sectorService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    [HttpGet("overview")]
    public async Task<IActionResult> GetOverview([FromQuery] int userId = 1, [FromQuery] bool refresh = false)
    {
        try
        {
            return Ok(await _sectorService.GetOverviewAsync(NormalizeUser(userId), refresh));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to build the sector overview for user {UserId}.", userId);
            return StatusCode(500, $"An error occurred while building the sector overview: {ex.Message}");
        }
    }

    [HttpGet("{sectorId:int}")]
    public async Task<IActionResult> GetSectorDetail(int sectorId, [FromQuery] int userId = 1, [FromQuery] bool refresh = false)
    {
        try
        {
            var detail = await _sectorService.GetSectorDetailAsync(sectorId, NormalizeUser(userId), refresh);
            return detail == null ? NotFound($"Sector {sectorId} was not found or is inactive.") : Ok(detail);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to build sector {SectorId} for user {UserId}.", sectorId, userId);
            return StatusCode(500, $"An error occurred while building the sector detail: {ex.Message}");
        }
    }

    private static int NormalizeUser(int userId) => userId > 0 ? userId : 1;
}
