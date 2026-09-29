using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using QuantEdge.Infrastructure.Interfaces;

namespace QuantEdge.API.Controllers;

[ApiController]
[Route("notifications")]
public class NotificationController : ControllerBase
{
    private readonly INotificationService _notificationService;
    private readonly ILogger<NotificationController> _logger;

    public NotificationController(INotificationService notificationService, ILogger<NotificationController> logger)
    {
        _notificationService = notificationService ?? throw new ArgumentNullException(nameof(notificationService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Today's (IST) notifications for the header bell: real trade events, swing BUY slots and
    /// NIFTY market filter flips. Nothing from a previous day is returned.
    /// </summary>
    [HttpGet("today")]
    public async Task<IActionResult> GetToday([FromQuery] int userId = 1, CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await _notificationService.GetTodayAsync(userId > 0 ? userId : 1, cancellationToken);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load today's notifications for user {UserId}.", userId);
            return StatusCode(500, $"Internal server error: {ex.Message}");
        }
    }
}
