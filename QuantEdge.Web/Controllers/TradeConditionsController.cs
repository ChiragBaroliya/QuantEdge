using System;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using QuantEdge.Infrastructure.Constants;
using QuantEdge.Web.Filters;

namespace QuantEdge.Web.Controllers;

/// <summary>
/// Trade Conditions screen: pick which of the 11 BUY / SHORT conditions the Auto Paper and Auto Real bots apply.
/// The page reads and saves through the API (/api/tradeconditions).
/// </summary>
[Authorize]
[PageAccess(AppPages.TradeConditions)]
public class TradeConditionsController : Controller
{
    private readonly IConfiguration _configuration;

    public TradeConditionsController(IConfiguration configuration)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
    }

    [HttpGet]
    public IActionResult Index()
    {
        ViewBag.ApiBaseUrl = _configuration["ApiBaseUrl"] ?? "https://localhost:44370";
        return View();
    }
}
