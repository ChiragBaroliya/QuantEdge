using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using QuantEdge.Domain.Entities;
using QuantEdge.Infrastructure.DTOs;
using QuantEdge.Infrastructure.Interfaces;

namespace QuantEdge.API.Controllers;

[ApiController]
[Route("api/[controller]")]
public class PaperTradingController : ControllerBase
{
    private readonly IPaperTradingService _paperTradingService;

    public PaperTradingController(IPaperTradingService paperTradingService)
    {
        _paperTradingService = paperTradingService ?? throw new ArgumentNullException(nameof(paperTradingService));
    }

    /// <summary>
    /// Retrieves current paper account balance, margin, realized PnL, and unrealized PnL summary.
    /// </summary>
    [HttpGet("account")]
    public async Task<IActionResult> GetAccount()
    {
        var portfolio = await _paperTradingService.GetPortfolioAsync("default_user");
        return Ok(portfolio);
    }

    /// <summary>
    /// Retrieves list of all active open paper positions with live ticking PnL.
    /// </summary>
    [HttpGet("positions")]
    public async Task<IActionResult> GetPositions()
    {
        var positions = await _paperTradingService.GetOpenPositionsAsync("default_user");
        return Ok(positions);
    }

    /// <summary>
    /// Retrieves active or historical paper orders.
    /// </summary>
    [HttpGet("orders")]
    public async Task<IActionResult> GetOrders([FromQuery] bool activeOnly = false)
    {
        var orders = await _paperTradingService.GetOrdersAsync("default_user", activeOnly);
        return Ok(orders);
    }

    /// <summary>
    /// Retrieves historical executed trade logs.
    /// </summary>
    [HttpGet("history")]
    public async Task<IActionResult> GetHistory([FromQuery] int limit = 50)
    {
        var history = await _paperTradingService.GetTradeHistoryAsync("default_user", limit);
        return Ok(history);
    }

    /// <summary>
    /// Retrieves paged historical executed trade logs with database-side filtering.
    /// </summary>
    [HttpGet("history/paged")]
    public async Task<IActionResult> GetPagedHistory(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 10,
        [FromQuery] string? symbol = null,
        [FromQuery] TradeSide? side = null,
        [FromQuery] DateTime? fromDate = null,
        [FromQuery] DateTime? toDate = null)
    {
        var filter = new PaperTradeHistoryFilterDto
        {
            Page = page < 1 ? 1 : page,
            PageSize = 10, // Fixed at 10 items per page
            Symbol = string.IsNullOrWhiteSpace(symbol) ? null : symbol.Trim().ToUpper(),
            Side = side,
            FromDate = fromDate,
            ToDate = toDate.HasValue ? toDate.Value.Date.AddDays(1).AddTicks(-1) : null
        };

        var result = await _paperTradingService.GetTradeHistoryPagedAsync(filter, "default_user");
        return Ok(result);
    }

    /// <summary>
    /// Places a manual market, limit, or stop-loss paper order.
    /// </summary>
    [HttpPost("order")]
    public async Task<IActionResult> PlaceOrder([FromBody] PlacePaperOrderDto dto)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        var order = await _paperTradingService.PlaceOrderAsync(dto, "default_user");
        return Ok(order);
    }

    /// <summary>
    /// Cancels a pending paper order.
    /// </summary>
    [HttpPost("order/cancel/{orderId}")]
    public async Task<IActionResult> CancelOrder(int orderId)
    {
        await _paperTradingService.CancelOrderAsync(orderId, "default_user");
        return Ok(new { success = true, message = $"Order #{orderId} cancelled successfully." });
    }

    /// <summary>
    /// Manually closes an open paper position at market price.
    /// </summary>
    [HttpPost("position/close")]
    public async Task<IActionResult> ClosePosition([FromBody] ClosePositionDto dto)
    {
        await _paperTradingService.ClosePositionAsync(dto.PositionId, dto.ExitPrice, "default_user");
        return Ok(new { success = true, message = "Position closed successfully." });
    }

    /// <summary>
    /// Resets the paper trading account balance to default initial capital (₹1,00,000).
    /// </summary>
    [HttpPost("account/reset")]
    public async Task<IActionResult> ResetAccount()
    {
        await _paperTradingService.ResetAccountAsync("default_user");
        return Ok(new { success = true, message = "Virtual account balance reset to ₹1,00,000 successfully." });
    }

    /// <summary>
    /// Toggles automatic paper trade execution on high probability AI trading signals.
    /// </summary>
    [HttpPost("settings/autotrade")]
    public async Task<IActionResult> ToggleAutoTrade([FromBody] AutoTradeStatusDto dto)
    {
        await _paperTradingService.SetAutoTradeStatusAsync(dto.Enabled);
        return Ok(new { success = true, autoTradeEnabled = dto.Enabled });
    }

    /// <summary>
    /// Retrieves full AutoTrade strategy settings and trading mode.
    /// </summary>
    [HttpGet("settings")]
    public async Task<IActionResult> GetSettings()
    {
        var settings = await _paperTradingService.GetAutoTradeSettingsAsync("default_user");
        return Ok(settings);
    }

    /// <summary>
    /// Updates AutoTrade strategy settings, timeframe, risk rules, and trading mode (Paper vs Live).
    /// </summary>
    [HttpPost("settings")]
    public async Task<IActionResult> UpdateSettings([FromBody] AutoTradeSettingsDto dto)
    {
        var updated = await _paperTradingService.UpdateAutoTradeSettingsAsync(dto, "default_user");
        return Ok(updated);
    }
}

public class AutoTradeStatusDto
{
    public bool Enabled { get; set; }
}
