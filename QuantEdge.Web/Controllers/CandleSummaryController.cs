using System;
using QuantEdge.Infrastructure.Constants;
using QuantEdge.Web.Filters;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;

namespace QuantEdge.Web.Controllers;

[Authorize]
[PageAccess(AppPages.CandleSummary)]
public class CandleSummaryController : Controller
{
    private readonly IConfiguration _configuration;

    public CandleSummaryController(IConfiguration configuration)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
    }

    public IActionResult Index()
    {
        ViewBag.ApiBaseUrl = _configuration["ApiBaseUrl"] ?? "https://localhost:44370";
        return View();
    }
}
