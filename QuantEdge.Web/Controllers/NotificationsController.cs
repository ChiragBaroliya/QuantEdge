using System;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;

namespace QuantEdge.Web.Controllers;

/// <summary>
/// View All Notifications: the header bell's feed for any date range (up to 31 days). The page loads its data from
/// GET /api/notifications/history in the browser.
/// </summary>
[Authorize]
public class NotificationsController : Controller
{
    private readonly IConfiguration _configuration;

    public NotificationsController(IConfiguration configuration)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
    }

    public IActionResult Index()
    {
        ViewBag.ApiBaseUrl = _configuration["ApiBaseUrl"] ?? "https://localhost:44370";
        ViewBag.UserId = int.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var uid) ? uid : 1;
        return View();
    }
}
