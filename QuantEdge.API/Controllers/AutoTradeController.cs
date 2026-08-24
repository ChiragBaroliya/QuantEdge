using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using QuantEdge.Infrastructure.DTOs;
using QuantEdge.Infrastructure.Interfaces;

namespace QuantEdge.API.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AutoTradeController : ControllerBase
{
    private readonly IAutoTradeService _autoTradeService;

    public AutoTradeController(IAutoTradeService autoTradeService)
    {
        _autoTradeService = autoTradeService ?? throw new ArgumentNullException(nameof(autoTradeService));
    }

    private string GetCurrentUserId()
    {
        var userIdClaim = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (!string.IsNullOrWhiteSpace(userIdClaim)) return userIdClaim;

        var nameClaim = User.Identity?.Name;
        if (!string.IsNullOrWhiteSpace(nameClaim)) return nameClaim;

        return "default_user";
    }

    /// <summary>
    /// Retrieves current auto trade settings for the active logged-in user.
    /// </summary>
    [HttpGet("settings")]
    public async Task<IActionResult> GetSettings()
    {
        var settings = await _autoTradeService.GetSettingsAsync(GetCurrentUserId());
        return Ok(settings);
    }

    /// <summary>
    /// Updates user configurable auto trading parameters (Capital, Target%, SL%, Max Trades/day, Fixed Amount/trade, Min Condition Match).
    /// </summary>
    [HttpPut("settings")]
    public async Task<IActionResult> UpdateSettings([FromBody] AutoTradeSettingsUpdateDto dto)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        var updated = await _autoTradeService.UpdateSettingsAsync(dto, GetCurrentUserId());
        return Ok(updated);
    }

    /// <summary>
    /// Master toggle ON / OFF switch for Auto Paper Trading.
    /// </summary>
    [HttpPost("toggle")]
    public async Task<IActionResult> ToggleAutoTrade([FromBody] ToggleAutoTradeRequestDto dto)
    {
        await _autoTradeService.ToggleAutoTradeAsync(dto.Enabled, GetCurrentUserId());
        return Ok(new { success = true, isAutoTradeEnabled = dto.Enabled });
    }

    /// <summary>
    /// Gets full dashboard summary: status, today's counter (e.g. 3/5), open positions, unrealized P&L, logs.
    /// </summary>
    [HttpGet("dashboard")]
    public async Task<IActionResult> GetDashboard()
    {
        var dashboard = await _autoTradeService.GetDashboardDataAsync(GetCurrentUserId());
        return Ok(dashboard);
    }

    /// <summary>
    /// Fetches today's auto trade execution logs (AUTO_BUY, AUTO_SELL, SYSTEM_ALERT, etc.).
    /// </summary>
    [HttpGet("logs")]
    public async Task<IActionResult> GetLogs([FromQuery] int limit = 50)
    {
        var logs = await _autoTradeService.GetTodayLogsAsync(GetCurrentUserId(), limit);
        return Ok(logs);
    }

    /// <summary>
    /// Resets all auto paper trading data: deletes positions, orders, history, and logs for a completely fresh start.
    /// </summary>
    [HttpPost("reset")]
    public async Task<IActionResult> ResetAutoPaperTrading()
    {
        await _autoTradeService.ResetAutoPaperTradingAsync(GetCurrentUserId());
        return Ok(new { success = true, message = "Auto Paper Trading data has been reset and cleared successfully." });
    }
}

