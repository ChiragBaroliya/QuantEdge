using System;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;

namespace QuantEdge.Web.Controllers;

/// <summary>
/// Controller serving the Data Coverage Manager view.
/// </summary>
public class DataCoverageController : Controller
{
    private readonly IConfiguration _configuration;

    public DataCoverageController(IConfiguration configuration)
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
