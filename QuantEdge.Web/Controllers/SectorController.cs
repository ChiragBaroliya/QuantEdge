using System;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;

namespace QuantEdge.Web.Controllers;

/// <summary>
/// Sector Dashboard: Sector Overview (which sectors are strong today) -> Sector Detail (which stocks in
/// the sector have a valid setup, and why). Both pages load their data from GET /api/sectors/... in the browser.
/// </summary>
[Authorize]
public class SectorController : Controller
{
    private readonly IConfiguration _configuration;

    public SectorController(IConfiguration configuration)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
    }

    public IActionResult Index()
    {
        SetConfig();
        return View();
    }

    public IActionResult Detail(int id)
    {
        if (id <= 0) return RedirectToAction(nameof(Index));
        SetConfig();
        ViewBag.SectorId = id;
        return View();
    }

    private void SetConfig()
    {
        ViewBag.ApiBaseUrl = _configuration["ApiBaseUrl"] ?? "https://localhost:44370";
        ViewBag.UserId = int.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var uid) ? uid : 1;
    }
}
