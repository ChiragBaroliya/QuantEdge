using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using QuantEdge.Domain.Entities;
using QuantEdge.Infrastructure.DTOs;
using QuantEdge.Infrastructure.Helpers;
using QuantEdge.Infrastructure.Interfaces;

namespace QuantEdge.API.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ManualPaperTradeController : ControllerBase
{
    private readonly IManualPaperTradeService _manualPaperTradeService;

    public ManualPaperTradeController(IManualPaperTradeService manualPaperTradeService)
    {
        _manualPaperTradeService = manualPaperTradeService ?? throw new ArgumentNullException(nameof(manualPaperTradeService));
    }

    // Same numeric user id resolution as RealTradeController (defaults to user 1).
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
    /// Retrieves Manual Paper Trading settings (manual_paper_trade_settings).
    /// </summary>
    [HttpGet("settings")]
    public async Task<IActionResult> GetSettings()
    {
        var settings = await _manualPaperTradeService.GetSettingsAsync(GetCurrentUserId());
        return Ok(settings);
    }

    /// <summary>
    /// Updates Manual Paper Trading settings.
    /// </summary>
    [HttpPut("settings")]
    public async Task<IActionResult> UpdateSettings([FromBody] ManualPaperTradeSettingsUpdateDto dto)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        var updated = await _manualPaperTradeService.UpdateSettingsAsync(dto, GetCurrentUserId());
        return Ok(updated);
    }

    /// <summary>
    /// Manual Paper Trading ON / OFF switch.
    /// </summary>
    [HttpPost("toggle")]
    public async Task<IActionResult> Toggle([FromBody] ToggleManualPaperTradeRequestDto dto)
    {
        await _manualPaperTradeService.ToggleManualTradeAsync(dto.Enabled, GetCurrentUserId());
        return Ok(new { success = true, isManualTradeEnabled = dto.Enabled });
    }

    /// <summary>
    /// Today's Manual Paper Trading execution logs (manual_paper_trade_execution_logs) and trade count.
    /// </summary>
    [HttpGet("logs")]
    public async Task<IActionResult> GetLogs([FromQuery] int limit = 50)
    {
        int userId = GetCurrentUserId();
        var logs = await _manualPaperTradeService.GetTodayLogsAsync(userId, limit);
        int todayCount = await _manualPaperTradeService.GetTodayTradeCountAsync(userId);
        return Ok(new { todayTradeCount = todayCount, logs });
    }

    /// <summary>
    /// Order ticket price - the latest stored 1-minute candle close from Postgres (market_candles_1m).
    /// Manual Trading never asks Zerodha for a live price.
    /// </summary>
    [HttpGet("quote")]
    public async Task<IActionResult> GetQuote([FromQuery] string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return BadRequest(new { success = false, message = "Symbol is required." });
        }

        var price = await _manualPaperTradeService.GetQuoteAsync(symbol.Trim().ToUpper());
        if (price == null || price.Ltp <= 0m)
        {
            return Ok(new { success = false, symbol = symbol.Trim().ToUpper(), message = "No stored price for this symbol." });
        }

        return Ok(new { success = true, symbol = price.Symbol, ltp = price.Ltp, asOfUtc = price.PriceTime });
    }

    /// <summary>
    /// Manual Trading page stat cards - Manual paper equity, margin, unrealized / realized P&amp;L
    /// (fn_get_manual_paper_dashboard).
    /// </summary>
    [HttpGet("dashboard")]
    public async Task<IActionResult> GetDashboard()
    {
        return Ok(await _manualPaperTradeService.GetDashboardAsync(GetCurrentUserId()));
    }

    /// <summary>
    /// Manual Trading page, Live Open Positions - OPEN Manual paper positions only
    /// (fn_get_manual_paper_open_positions).
    /// </summary>
    [HttpGet("positions")]
    public async Task<IActionResult> GetPositions()
    {
        return Ok(await _manualPaperTradeService.GetOpenPositionsAsync(GetCurrentUserId()));
    }

    /// <summary>
    /// Reset Capital on the Manual Trading page - clears ONLY manual orders, positions, trade history and
    /// logs (fn_reset_manual_paper_trading). Settings are kept; Auto Paper / Auto Real data is not touched.
    /// </summary>
    [HttpPost("reset")]
    public async Task<IActionResult> Reset()
    {
        await _manualPaperTradeService.ResetAsync(GetCurrentUserId());
        return Ok(new { success = true, message = "Manual Paper Trading has been reset. Manual Capital is available again in full." });
    }

    /// <summary>
    /// Edit button on Live Open Positions - updates Stop Loss, Trailing SL % and Target of an OPEN
    /// manual paper position (fn_update_manual_paper_position_levels).
    /// </summary>
    [HttpPut("position/{positionId:int}/levels")]
    public async Task<IActionResult> UpdatePositionLevels(int positionId, [FromBody] UpdateManualPositionLevelsDto dto)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        var (success, message) = await _manualPaperTradeService.UpdatePositionLevelsAsync(
            positionId, dto.StopLoss, dto.TrailingSlPct, dto.TakeProfit, GetCurrentUserId());
        return Ok(new { success, message });
    }

    /// <summary>
    /// Close button on Live Open Positions - sells an OPEN long / buys back an OPEN short at the latest price.
    /// </summary>
    [HttpPost("position/close/{positionId:int}")]
    public async Task<IActionResult> ClosePosition(int positionId)
    {
        var (success, message) = await _manualPaperTradeService.ClosePositionAsync(positionId, GetCurrentUserId());
        return Ok(new { success, message });
    }

    /// <summary>
    /// Manual Trading page, Active / Pending Orders tab - paged Manual paper orders with
    /// Symbol / Side / Status / Date filters (fn_get_manual_paper_orders_paged).
    /// </summary>
    [HttpGet("orders")]
    public async Task<IActionResult> GetOrders(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 10,
        [FromQuery] string? symbol = null,
        [FromQuery] TradeSide? side = null,
        [FromQuery] PaperOrderStatus? status = null,
        [FromQuery] DateTime? fromDate = null,
        [FromQuery] DateTime? toDate = null)
    {
        var filter = new ManualPaperOrderFilterDto
        {
            Page = page < 1 ? 1 : page,
            PageSize = Math.Clamp(pageSize, 1, 100),
            Symbol = string.IsNullOrWhiteSpace(symbol) ? null : symbol.Trim().ToUpper(),
            Side = side,
            Status = status,
            FromDate = ToIstDayStartUtc(fromDate),
            ToDate = ToIstDayEndUtc(toDate)
        };

        return Ok(await _manualPaperTradeService.GetOrdersPagedAsync(filter, GetCurrentUserId()));
    }

    /// <summary>
    /// Manual Trading page, Execution History tab - paged Manual paper trade history with
    /// Symbol / Side / Date filters (fn_get_manual_paper_trade_history_paged).
    /// </summary>
    [HttpGet("history")]
    public async Task<IActionResult> GetHistory(
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
            PageSize = Math.Clamp(pageSize, 1, 100),
            Symbol = string.IsNullOrWhiteSpace(symbol) ? null : symbol.Trim().ToUpper(),
            Side = side,
            FromDate = ToIstDayStartUtc(fromDate),
            ToDate = ToIstDayEndUtc(toDate)
        };

        return Ok(await _manualPaperTradeService.GetTradeHistoryPagedAsync(filter, GetCurrentUserId()));
    }

    // The date pickers send calendar dates; treat them as IST days so "From / To" cover the full
    // Indian trading day (00:00:00 - 23:59:59 IST).
    private static DateTime? ToIstDayStartUtc(DateTime? date) =>
        date.HasValue
            ? TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(date.Value.Date, DateTimeKind.Unspecified), TimeZoneHelper.IndianTimeZone)
            : null;

    private static DateTime? ToIstDayEndUtc(DateTime? date) =>
        date.HasValue
            ? TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(date.Value.Date.AddDays(1).AddTicks(-1), DateTimeKind.Unspecified), TimeZoneHelper.IndianTimeZone)
            : null;

    /// <summary>
    /// Manual one-off Paper BUY from the Manual Trading page - the paper counterpart of
    /// RealTradeController.ManualBuy, with the same risk checks and the user's own Quantity,
    /// Stop Loss % and Trailing Stop Loss %.
    /// </summary>
    [HttpPost("buy")]
    public async Task<IActionResult> Buy([FromBody] ManualPaperBuyRequestDto dto)
    {
        if (dto == null || string.IsNullOrWhiteSpace(dto.Symbol) || dto.EntryPrice <= 0)
        {
            return BadRequest(new { success = false, message = "A valid Symbol and EntryPrice are required." });
        }

        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        var (success, message) = await _manualPaperTradeService.ExecuteManualBuyAsync(
            dto.Symbol, dto.EntryPrice, dto.Quantity, dto.StopLossPct, dto.TrailingSlPct, GetCurrentUserId());

        return Ok(new { success, message });
    }

    /// <summary>
    /// Manual Paper SHORT SELL from the Manual Trading page - sell first, buy back later (Close = Buy to Cover).
    /// Same request and risk checks as Buy, plus the short entry cut-off. Intraday only: an uncovered short is
    /// bought back automatically at the Short Square-off Time.
    /// </summary>
    [HttpPost("short")]
    public async Task<IActionResult> Short([FromBody] ManualPaperBuyRequestDto dto)
    {
        if (dto == null || string.IsNullOrWhiteSpace(dto.Symbol) || dto.EntryPrice <= 0)
        {
            return BadRequest(new { success = false, message = "A valid Symbol and EntryPrice are required." });
        }

        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        var (success, message) = await _manualPaperTradeService.ExecuteManualShortAsync(
            dto.Symbol, dto.EntryPrice, dto.Quantity, dto.StopLossPct, dto.TrailingSlPct, GetCurrentUserId());

        return Ok(new { success, message });
    }
}
