using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using QuantEdge.Infrastructure.DTOs;
using QuantEdge.Infrastructure.Hubs;

namespace QuantEdge.API.Controllers;

/// <summary>
/// Internal relay endpoint used only by QuantEdge.Worker (a headless process with no dashboard
/// clients of its own connected to any hub) to forward a SignalR broadcast to the clients actually
/// connected to this process's MarketDataHub. Not part of the public API surface - protected by a
/// shared secret configured identically in both processes' appsettings (RealtimeBroadcast:SharedSecret).
/// </summary>
[ApiController]
[Route("internal/broadcast")]
[ApiExplorerSettings(IgnoreApi = true)]
public class InternalBroadcastController : ControllerBase
{
    private readonly IHubContext<MarketDataHub> _hubContext;
    private readonly IConfiguration _configuration;
    private readonly ILogger<InternalBroadcastController> _logger;

    public InternalBroadcastController(
        IHubContext<MarketDataHub> hubContext,
        IConfiguration configuration,
        ILogger<InternalBroadcastController> logger)
    {
        _hubContext = hubContext ?? throw new ArgumentNullException(nameof(hubContext));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    [HttpPost]
    public async Task<IActionResult> Broadcast([FromBody] HubBroadcastRequestDto request)
    {
        string? expectedSecret = _configuration["RealtimeBroadcast:SharedSecret"];
        if (string.IsNullOrEmpty(expectedSecret))
        {
            _logger.LogError("RealtimeBroadcast:SharedSecret is not configured - rejecting internal broadcast relay request.");
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        if (!Request.Headers.TryGetValue("X-Internal-Broadcast-Secret", out var providedSecret) ||
            providedSecret != expectedSecret)
        {
            _logger.LogWarning("Rejected internal broadcast relay request with missing/invalid shared secret from {RemoteIp}.",
                HttpContext.Connection.RemoteIpAddress);
            return Unauthorized();
        }

        if (request == null || string.IsNullOrWhiteSpace(request.EventName))
        {
            return BadRequest("EventName is required.");
        }

        if (!string.IsNullOrWhiteSpace(request.GroupName))
        {
            await _hubContext.Clients.Group(request.GroupName).SendAsync(request.EventName, request.Payload);
        }
        else
        {
            await _hubContext.Clients.All.SendAsync(request.EventName, request.Payload);
        }

        return Ok();
    }
}
