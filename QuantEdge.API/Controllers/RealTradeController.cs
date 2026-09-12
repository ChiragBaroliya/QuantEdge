using System;
using System.Linq;
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
    /// Closed/filled real trade history - entry price, exit price, realized P&L, exit reason.
    /// Recorded on every buy/sell fill but was previously never surfaced anywhere.
    /// </summary>
    [HttpGet("trade-history")]
    public async Task<IActionResult> GetTradeHistory([FromQuery] int? userId = null, [FromQuery] int limit = 100)
    {
        var history = await _realTradeService.GetTradeHistoryAsync(GetCurrentUserId(userId), limit);
        return Ok(history);
    }

    /// <summary>
    /// Manual one-off Real Trade BUY (e.g. triggered from the Swing Trading dashboard for a specific signal),
    /// bypassing the 15-minute auto-scan cycle. Still routed through the same risk checks as automatic
    /// execution (master switch, token validity, market hours, daily trade/loss limits, duplicate position, capital).
    /// </summary>
    [HttpPost("manual-buy")]
    public async Task<IActionResult> ManualBuy([FromBody] ManualRealBuyRequestDto dto, [FromQuery] int? userId = null)
    {
        if (dto == null || string.IsNullOrWhiteSpace(dto.Symbol) || dto.EntryPrice <= 0)
        {
            return BadRequest(new { success = false, message = "A valid Symbol and EntryPrice are required." });
        }

        int targetUid = dto.UserId.HasValue && dto.UserId.Value > 0 ? dto.UserId.Value : GetCurrentUserId(userId);

        bool executed = await _realTradeService.EvaluateAndExecuteRealBuyAsync(
            dto.Symbol, dto.EntryPrice, dto.MetConditionsCount, targetUid, isBuySignal: true);

        if (executed)
        {
            return Ok(new { success = true, message = $"Real BUY order submitted for {dto.Symbol.ToUpper().Trim()}." });
        }

        var recentLogs = await _realTradeService.GetTodayLogsAsync(targetUid, 20);
        var latestForSymbol = recentLogs
            .Where(l => string.Equals(l.Symbol, dto.Symbol.Trim(), StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(l => l.ExecutedAt)
            .FirstOrDefault();

        return Ok(new
        {
            success = false,
            message = latestForSymbol?.Reason ?? "Real BUY was not executed. Ensure Real Trade master switch is ON and check the Auto Real Trade logs."
        });
    }

    /// <summary>
    /// Enrolls an existing Zerodha Holding into the bot's monitoring pipeline for a target-price auto-sell.
    /// No BUY order is placed since the shares are already held; it simply registers a real_positions row
    /// that the existing position monitor watches, exactly like any bot-bought or manual-buy position.
    /// </summary>
    [HttpPost("holdings/enable-monitoring")]
    public async Task<IActionResult> EnableHoldingMonitoring([FromBody] EnableHoldingMonitoringRequestDto dto, [FromQuery] int? userId = null)
    {
        if (dto == null || string.IsNullOrWhiteSpace(dto.Symbol) || dto.Quantity <= 0 || dto.AveragePrice <= 0 || dto.TargetPrice <= 0)
        {
            return BadRequest(new { success = false, message = "A valid Symbol, Quantity, AveragePrice, and TargetPrice are required." });
        }

        int targetUid = dto.UserId.HasValue && dto.UserId.Value > 0 ? dto.UserId.Value : GetCurrentUserId(userId);

        var (success, message) = await _realTradeService.EnableHoldingMonitoringAsync(
            dto.Symbol, dto.Quantity, dto.AveragePrice, dto.TargetPrice, targetUid);

        return Ok(new { success, message });
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

    /// <summary>
    /// Manual SELL for any stock currently held/positioned at Zerodha (Live Positions or Holdings),
    /// including one the bot isn't tracking as a real_positions row.
    /// </summary>
    [HttpPost("manual-sell")]
    public async Task<IActionResult> ManualSell([FromBody] ManualSellRequestDto dto, [FromQuery] int? userId = null)
    {
        if (dto == null || string.IsNullOrWhiteSpace(dto.Symbol) || dto.Quantity <= 0 || dto.CurrentPrice <= 0)
        {
            return BadRequest(new { success = false, message = "A valid Symbol, Quantity, and CurrentPrice are required." });
        }

        try
        {
            int targetUid = dto.UserId.HasValue && dto.UserId.Value > 0 ? dto.UserId.Value : GetCurrentUserId(userId);
            var (success, message) = await _realTradeService.ManualSellAsync(
                dto.Symbol, dto.Quantity, dto.CurrentPrice, dto.Product, dto.EntryPriceHint, dto.Reason ?? "Manual Web Sell", targetUid);

            return Ok(new { success, message });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }

    /// <summary>
    /// Force re-verifies one order's status directly against Zerodha and corrects our records if they've
    /// drifted from the broker's truth - e.g. an order shown FILLED here that Zerodha's own Orders page
    /// still shows resting OPEN (unfilled).
    /// </summary>
    [HttpPost("resync-order")]
    public async Task<IActionResult> ResyncOrderStatus([FromBody] ResyncOrderStatusRequestDto dto, [FromQuery] int? userId = null)
    {
        if (dto == null || dto.OrderId <= 0)
        {
            return BadRequest(new { success = false, message = "A valid OrderId is required." });
        }

        try
        {
            int targetUid = dto.UserId.HasValue && dto.UserId.Value > 0 ? dto.UserId.Value : GetCurrentUserId(userId);
            var (success, message) = await _realTradeService.ResyncOrderStatusAsync(dto.OrderId, targetUid);
            return Ok(new { success, message });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }

    /// <summary>
    /// Resyncs every recent order against Zerodha in one pass - what "Sync Now" calls so it actually
    /// re-verifies order status with the broker, the same check the per-order 🔄 Resync button runs.
    /// </summary>
    [HttpPost("resync-recent-orders")]
    public async Task<IActionResult> ResyncRecentOrders([FromQuery] int? userId = null)
    {
        try
        {
            int targetUid = GetCurrentUserId(userId);
            var (success, message) = await _realTradeService.ResyncRecentOrdersAsync(targetUid);
            return Ok(new { success, message });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }
}
