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

    private int GetCurrentUserId()
    {
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
    public async Task<IActionResult> GetSettings()
    {
        var settings = await _realTradeService.GetSettingsAsync(GetCurrentUserId());
        return Ok(settings);
    }

    /// <summary>
    /// Updates real trading parameters (Capital, Target%, Optional SL%, Optional Trailing SL%, Max Trades/day, Daily Loss Limit).
    /// </summary>
    [HttpPut("settings")]
    public async Task<IActionResult> UpdateSettings([FromBody] RealTradeSettingsUpdateDto dto)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        try
        {
            var updated = await _realTradeService.UpdateSettingsAsync(dto, GetCurrentUserId());
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
    public async Task<IActionResult> ToggleRealTrade([FromBody] ToggleRealTradeRequestDto dto)
    {
        try
        {
            await _realTradeService.ToggleRealTradeAsync(dto.Enabled, GetCurrentUserId());
            return Ok(new { success = true, isRealTradeEnabled = dto.Enabled });
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
    public async Task<IActionResult> GetDashboard()
    {
        var dashboard = await _realTradeService.GetDashboardDataAsync(GetCurrentUserId());
        return Ok(dashboard);
    }

    /// <summary>
    /// Lightweight fast endpoint for high-frequency (e.g. 5s) live positions, Zerodha MTM and P&L polling.
    /// </summary>
    [HttpGet("live-positions")]
    public async Task<IActionResult> GetLivePositionsFast()
    {
        var liveData = await _realTradeService.GetLivePositionsFastAsync(GetCurrentUserId());
        return Ok(liveData);
    }

    /// <summary>
    /// Fetches today's real trade execution logs.
    /// </summary>
    [HttpGet("logs")]
    public async Task<IActionResult> GetLogs([FromQuery] int limit = 50)
    {
        var logs = await _realTradeService.GetTodayLogsAsync(GetCurrentUserId(), limit);
        return Ok(logs);
    }

    /// <summary>
    /// Emergency Panic Kill Switch: Instantly squares off all open real positions and turns OFF live bot.
    /// </summary>
    [HttpPost("kill-switch")]
    public async Task<IActionResult> EmergencyKillSwitch([FromBody] EmergencyKillSwitchRequestDto? dto)
    {
        string reason = dto?.Reason ?? "Emergency Panic Kill Switch Triggered by User";
        int closedCount = await _realTradeService.SquareOffAllPositionsAsync(reason, GetCurrentUserId());
        return Ok(new { success = true, closedPositionsCount = closedCount, message = $"Kill switch activated. {closedCount} positions squared off." });
    }

    /// <summary>
    /// Squares off an individual live position on demand.
    /// </summary>
    [HttpPost("square-off")]
    public async Task<IActionResult> SquareOffPosition([FromBody] CloseRealPositionRequestDto dto)
    {
        if (dto.PositionId <= 0) return BadRequest(new { success = false, message = "Invalid position ID" });

        string reason = dto.Reason ?? "Manual 1-Click Square-off";
        bool result = await _realTradeService.SquareOffSinglePositionAsync(dto.PositionId, reason, GetCurrentUserId());
        return Ok(new { success = result, message = result ? "Position squared off successfully" : "Failed to square off position" });
    }
}
