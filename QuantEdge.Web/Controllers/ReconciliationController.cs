using System;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;

namespace QuantEdge.Web.Controllers;

/// <summary>
/// Reconciliation: our open real positions vs Zerodha, and the daily data-quality checks. The page loads its data
/// from GET /api/reconciliation/... in the browser.
/// </summary>
[Authorize]
public class ReconciliationController : Controller
{
    private readonly IConfiguration _configuration;

    public ReconciliationController(IConfiguration configuration)
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
