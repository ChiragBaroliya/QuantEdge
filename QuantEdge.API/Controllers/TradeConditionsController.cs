using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using QuantEdge.Domain.Entities;
using QuantEdge.Infrastructure.DTOs;
using QuantEdge.Infrastructure.Interfaces;

namespace QuantEdge.API.Controllers;

/// <summary>
/// Trade Conditions page: which of the 11 BUY / SHORT conditions the Auto Paper / Auto Real bots apply for a user.
/// The user is resolved exactly as each mode's own controller does it (AutoTradeController / RealTradeController),
/// so the page and the bot read the same profile.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class TradeConditionsController : ControllerBase
{
    private readonly ITradeConditionService _service;

    public TradeConditionsController(ITradeConditionService service)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
    }

    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] string mode, [FromQuery] int? userId = null)
    {
        if (!TryResolve(mode, userId, out var tradeMode, out var userRef, out var error)) return BadRequest(new { message = error });
        return Ok(await _service.GetModeAsync(tradeMode, userRef));
    }

    [HttpPut]
    public async Task<IActionResult> Save([FromQuery] string mode, [FromBody] TradeConditionSaveDto dto, [FromQuery] int? userId = null)
    {
        if (!TryResolve(mode, userId, out var tradeMode, out var userRef, out var error)) return BadRequest(new { message = error });
        var (success, saveError, saved) = await _service.SaveModeAsync(tradeMode, userRef, dto);
        return success ? Ok(saved) : BadRequest(new { message = saveError });
    }

    [HttpPost("reset")]
    public async Task<IActionResult> Reset([FromQuery] string mode, [FromQuery] int? userId = null)
    {
        if (!TryResolve(mode, userId, out var tradeMode, out var userRef, out var error)) return BadRequest(new { message = error });
        return Ok(await _service.ResetModeAsync(tradeMode, userRef));
    }

    private bool TryResolve(string? mode, int? userId, out string tradeMode, out string userRef, out string? error)
    {
        tradeMode = (mode ?? string.Empty).Trim().ToUpperInvariant();
        userRef = string.Empty;
        error = null;

        if (tradeMode == TradeConditionProfile.ModeAutoReal)
        {
            userRef = GetRealUserId(userId).ToString();
            return true;
        }
        if (tradeMode == TradeConditionProfile.ModeAutoPaper)
        {
            userRef = GetAutoPaperUserId();
            return true;
        }

        error = "mode must be AUTO_PAPER or AUTO_REAL.";
        return false;
    }

    // Same as RealTradeController.GetCurrentUserId.
    private int GetRealUserId(int? queryUserId)
    {
        if (queryUserId.HasValue && queryUserId.Value > 0) return queryUserId.Value;
        var userIdClaim = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        return !string.IsNullOrWhiteSpace(userIdClaim) && int.TryParse(userIdClaim, out int uid) ? uid : 1;
    }

    // Same as AutoTradeController.GetCurrentUserId.
    private string GetAutoPaperUserId()
    {
        var userIdClaim = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (!string.IsNullOrWhiteSpace(userIdClaim)) return userIdClaim;

        var nameClaim = User.Identity?.Name;
        if (!string.IsNullOrWhiteSpace(nameClaim)) return nameClaim;

        return "default_user";
    }
}
