using System;
using QuantEdge.Infrastructure.Constants;
using QuantEdge.Web.Filters;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;

using Microsoft.AspNetCore.Authorization;

namespace QuantEdge.Web.Controllers;

[Authorize]
[PageAccess(AppPages.SwingTrading)]
public class SwingTradingController : Controller
{
    private readonly IConfiguration _configuration;

    public SwingTradingController(IConfiguration configuration)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
    }

    public IActionResult Index()
    {
        ViewBag.ApiBaseUrl = _configuration["ApiBaseUrl"] ?? "https://localhost:44370";
        return View();
    }

    [PageAccess(AppPages.EtfList)]
    public IActionResult EtfList()
    {
        ViewBag.ApiBaseUrl = _configuration["ApiBaseUrl"] ?? "https://localhost:44370";
        return View();
    }
}
