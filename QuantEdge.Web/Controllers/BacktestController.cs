using System;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;

namespace QuantEdge.Web.Controllers;

/// <summary>
/// Backtest (Plan Phase 7): queue a replay of the live strategy on stored candles and read its results. The page talks to
/// /api/backtest/... in the browser; the run itself happens in the worker.
/// </summary>
[Authorize]
public class BacktestController : Controller
{
    private readonly IConfiguration _configuration;

    public BacktestController(IConfiguration configuration)
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
