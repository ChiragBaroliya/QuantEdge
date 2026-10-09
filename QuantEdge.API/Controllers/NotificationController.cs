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

    /// <summary>
    /// View All Notifications page: every notification between two IST dates (yyyy-MM-dd, inclusive; default the last
    /// 7 days). The range is capped at 31 days and never goes past today; the response says which range was used.
    /// </summary>
    [HttpGet("history")]
    public async Task<IActionResult> GetHistory([FromQuery] int userId = 1, [FromQuery] DateTime? from = null, [FromQuery] DateTime? to = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            DateTime todayIst = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, QuantEdge.Infrastructure.Helpers.TimeZoneHelper.IndianTimeZone).Date;
            DateTime toIst = DateTime.SpecifyKind((to ?? todayIst).Date, DateTimeKind.Unspecified);
            DateTime fromIst = DateTime.SpecifyKind((from ?? toIst.AddDays(-6)).Date, DateTimeKind.Unspecified);

            var result = await _notificationService.GetHistoryAsync(userId > 0 ? userId : 1, fromIst, toIst, cancellationToken);
            return Ok(result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return StatusCode(499);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load notification history for user {UserId}.", userId);
            return StatusCode(500, $"Internal server error: {ex.Message}");
        }
    }
}
