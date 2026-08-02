using System;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;

namespace QuantEdge.Web.Controllers;

public class AutoTradingController : Controller
{
    private readonly IConfiguration _configuration;

    public AutoTradingController(IConfiguration configuration)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
    }

    public IActionResult Index()
    {
        ViewBag.ApiBaseUrl = _configuration["ApiBaseUrl"] ?? "https://localhost:44370";
        return View();
    }
}
