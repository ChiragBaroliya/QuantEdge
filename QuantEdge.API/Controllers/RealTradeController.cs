using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using QuantEdge.Infrastructure.DTOs;
using QuantEdge.Infrastructure.Interfaces;

namespace QuantEdge.API.Controllers;

[ApiController]
[Route("api/[controller]")]
[Route("[controller]")]
public class RealTradeController : ControllerBase
{
    private readonly IAutoRealTradeService _realTradeService;

    public RealTradeController(IAutoRealTradeService realTradeService)
    {
        _realTradeService = realTradeService ?? throw new ArgumentNullException(nameof(realTradeService));
    }

    private int GetCurrentUserId(int? queryUserId = null)
    {
        if (queryUserId.HasValue && queryUserId.Value > 0)
        {
            return queryUserId.Value;
        }

        var userIdClaim = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (!string.IsNullOrWhiteSpace(userIdClaim) && int.TryParse(userIdClaim, out int uid))
        {
            return uid;
        }

        return 1;
    }

    /// <summary>
    /// Retrieves current real trade settings for the active user.
    /// </summary>
    [HttpGet("settings")]
    public async Task<IActionResult> GetSettings([FromQuery] int? userId = null)
    {
        var settings = await _realTradeService.GetSettingsAsync(GetCurrentUserId(userId));
        return Ok(settings);
    }

    /// <summary>
    /// Updates real trading parameters (Capital, Target%, Optional SL%, Optional Trailing SL%, Max Trades/day, Daily Loss Limit).
    /// </summary>
    [HttpPut("settings")]
    public async Task<IActionResult> UpdateSettings([FromBody] RealTradeSettingsUpdateDto dto, [FromQuery] int? userId = null)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        try
        {
            var updated = await _realTradeService.UpdateSettingsAsync(dto, GetCurrentUserId(userId));
            return Ok(updated);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { success = false, message = ex.Message });
        }
    }

    /// <summary>
    /// Master toggle ON / OFF switch for Real Money Auto Trading.
    /// </summary>
    [HttpPost("toggle")]
    public async Task<IActionResult> ToggleRealTrade([FromBody] ToggleRealTradeRequestDto dto, [FromQuery] int? userId = null)
    {
        try
        {
            int targetUid = dto.UserId.HasValue && dto.UserId.Value > 0 ? dto.UserId.Value : GetCurrentUserId(userId);
            await _realTradeService.ToggleRealTradeAsync(dto.Enabled, targetUid);
            return Ok(new { success = true, isRealTradeEnabled = dto.Enabled, userId = targetUid });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { success = false, message = ex.Message });
        }
    }

    /// <summary>
    /// Gets full live dashboard summary: broker margin, realized/unrealized P&L, live open positions, recent orders, logs.
    /// </summary>
    [HttpGet("dashboard")]
    public async Task<IActionResult> GetDashboard([FromQuery] int? userId = null)
    {
        var dashboard = await _realTradeService.GetDashboardDataAsync(GetCurrentUserId(userId));
        return Ok(dashboard);
    }

    /// <summary>
    /// Lightweight fast endpoint for high-frequency (e.g. 5s) live positions, Zerodha MTM and P&L polling.
    /// </summary>
    [HttpGet("live-positions")]
    public async Task<IActionResult> GetLivePositionsFast([FromQuery] int? userId = null)
    {
        var liveData = await _realTradeService.GetLivePositionsFastAsync(GetCurrentUserId(userId));
        return Ok(liveData);
    }

    /// <summary>
    /// Fetches today's real trade execution logs.
    /// </summary>
    [HttpGet("logs")]
    public async Task<IActionResult> GetLogs([FromQuery] int? userId = null, [FromQuery] int limit = 50)
    {
        var logs = await _realTradeService.GetTodayLogsAsync(GetCurrentUserId(userId), limit);
        return Ok(logs);
    }

    /// <summary>
    /// Emergency Panic Kill Switch: Instantly squares off all open real positions and turns OFF live bot.
    /// </summary>
    [HttpPost("kill-switch")]
    public async Task<IActionResult> EmergencyKillSwitch([FromBody] EmergencyKillSwitchRequestDto? dto, [FromQuery] int? userId = null)
    {
        string reason = dto?.Reason ?? "Emergency Panic Kill Switch Triggered by User";
        int targetUid = dto?.UserId.HasValue == true && dto.UserId.Value > 0 ? dto.UserId.Value : GetCurrentUserId(userId);
        int closedCount = await _realTradeService.SquareOffAllPositionsAsync(reason, targetUid);
        return Ok(new { success = true, closedPositionsCount = closedCount, message = $"Kill switch activated. {closedCount} positions squared off." });
    }

    /// <summary>
    /// Squares off an individual live position on demand.
    /// </summary>
    [HttpPost("square-off")]
    public async Task<IActionResult> SquareOffPosition([FromBody] CloseRealPositionRequestDto dto, [FromQuery] int? userId = null)
    {
        if (dto.PositionId <= 0)
        {
            return BadRequest(new { success = false, message = "Valid position ID is required." });
        }

        try
        {
            int targetUid = dto.UserId.HasValue && dto.UserId.Value > 0 ? dto.UserId.Value : GetCurrentUserId(userId);
            bool success = await _realTradeService.SquareOffSinglePositionAsync(dto.PositionId, dto.Reason ?? "Manual Web Square-Off", targetUid);
            return Ok(new { success, message = success ? "Position square-off initiated." : "Failed to square off position." });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }
}
