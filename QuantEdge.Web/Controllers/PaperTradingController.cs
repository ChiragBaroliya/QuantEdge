using System;
using QuantEdge.Infrastructure.Constants;
using QuantEdge.Web.Filters;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;

namespace QuantEdge.Web.Controllers;

// Manual (Paper) Trading is open to every signed-in user; each user trades their own manual_paper_* account.
[Authorize]
[PageAccess(AppPages.ManualTrading)]
public class PaperTradingController : Controller
{
    private readonly IConfiguration _configuration;

    public PaperTradingController(IConfiguration configuration)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
    }

    public IActionResult Index()
    {
        ViewBag.ApiBaseUrl = _configuration["ApiBaseUrl"] ?? "https://localhost:44370";
        // The page sends this as ?userId= on every /api/manualpapertrade call, so each user has their own account.
        ViewBag.UserId = int.TryParse(User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value, out var uid) ? uid : 1;
        return View();
    }
}
