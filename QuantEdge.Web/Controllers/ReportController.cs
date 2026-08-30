using System;
using System.Net.Http;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace QuantEdge.Web.Controllers;

[Authorize]
public class ReportController : Controller
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ReportController> _logger;

    public ReportController(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<ReportController> logger)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    [HttpGet]
    public IActionResult Index()
    {
        ViewBag.ApiBaseUrl = _configuration["ApiBaseUrl"] ?? "https://localhost:44370";
        string currentUserId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "0";
        string username = User.FindFirst(ClaimTypes.Name)?.Value ?? "User";
        bool isAdmin = User.IsInRole("Admin");

        ViewBag.CurrentUserId = currentUserId;
        ViewBag.CurrentUsername = username;
        ViewBag.IsAdmin = isAdmin;

        return View();
    }

    [HttpGet("api/web/reports/performance")]
    [HttpGet("reports/performance")]
    public async Task<IActionResult> GetPerformance(
        [FromQuery] string periodType = "daily",
        [FromQuery] string tradeMode = "all",
        [FromQuery] string? userId = null,
        [FromQuery] DateTime? startDate = null,
        [FromQuery] DateTime? endDate = null,
        [FromQuery] string? symbol = null,
        [FromQuery] int periodsPage = 1,
        [FromQuery] int periodsPageSize = 10,
        [FromQuery] string periodsPnlFilter = "all",
        [FromQuery] int tradesPage = 1,
        [FromQuery] int tradesPageSize = 10,
        [FromQuery] string tradesType = "all",
        [FromQuery] string tradesPnlFilter = "all")
    {
        try
        {
            if (!User.IsInRole("Admin"))
            {
                userId = User.FindFirst(ClaimTypes.Name)?.Value ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "1";
            }

            var client = _httpClientFactory.CreateClient("QuantEdgeApi");
            string queryString = $"?periodType={Uri.EscapeDataString(periodType)}&tradeMode={Uri.EscapeDataString(tradeMode)}";
            
            if (!string.IsNullOrWhiteSpace(userId))
                queryString += $"&userId={Uri.EscapeDataString(userId)}";
            if (startDate.HasValue)
                queryString += $"&startDate={startDate.Value:yyyy-MM-dd}";
            if (endDate.HasValue)
                queryString += $"&endDate={endDate.Value:yyyy-MM-dd}";
            if (!string.IsNullOrWhiteSpace(symbol))
                queryString += $"&symbol={Uri.EscapeDataString(symbol)}";

            queryString += $"&periodsPage={periodsPage}&periodsPageSize={periodsPageSize}&periodsPnlFilter={Uri.EscapeDataString(periodsPnlFilter)}";
            queryString += $"&tradesPage={tradesPage}&tradesPageSize={tradesPageSize}&tradesType={Uri.EscapeDataString(tradesType)}&tradesPnlFilter={Uri.EscapeDataString(tradesPnlFilter)}";

            var response = await client.GetAsync($"/api/reports/performance{queryString}");
            var content = await response.Content.ReadAsStringAsync();
            Response.StatusCode = (int)response.StatusCode;
            return Content(content, "application/json");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error proxying performance report request.");
            return StatusCode(500, new { message = "Error fetching report data.", details = ex.Message });
        }
    }

    [HttpGet("api/web/reports/trades/paged")]
    [HttpGet("reports/trades/paged")]
    public async Task<IActionResult> GetTradesPaged(
        [FromQuery] string tradeMode = "all",
        [FromQuery] string? userId = null,
        [FromQuery] DateTime? startDate = null,
        [FromQuery] DateTime? endDate = null,
        [FromQuery] string? symbol = null,
        [FromQuery] string tradeType = "all",
        [FromQuery] string pnlFilter = "all",
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 10)
    {
        try
        {
            if (!User.IsInRole("Admin"))
            {
                userId = User.FindFirst(ClaimTypes.Name)?.Value ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "1";
            }

            var client = _httpClientFactory.CreateClient("QuantEdgeApi");
            string queryString = $"?tradeMode={Uri.EscapeDataString(tradeMode)}&tradeType={Uri.EscapeDataString(tradeType)}&pnlFilter={Uri.EscapeDataString(pnlFilter)}&page={page}&pageSize={pageSize}";
            
            if (!string.IsNullOrWhiteSpace(userId))
                queryString += $"&userId={Uri.EscapeDataString(userId)}";
            if (startDate.HasValue)
                queryString += $"&startDate={startDate.Value:yyyy-MM-dd}";
            if (endDate.HasValue)
                queryString += $"&endDate={endDate.Value:yyyy-MM-dd}";
            if (!string.IsNullOrWhiteSpace(symbol))
                queryString += $"&symbol={Uri.EscapeDataString(symbol)}";

            var response = await client.GetAsync($"/api/reports/trades/paged{queryString}");
            var content = await response.Content.ReadAsStringAsync();
            Response.StatusCode = (int)response.StatusCode;
            return Content(content, "application/json");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error proxying paged trades request.");
            return StatusCode(500, new { message = "Error fetching paged trades.", details = ex.Message });
        }
    }

    [HttpGet("api/web/reports/periods/paged")]
    [HttpGet("reports/periods/paged")]
    public async Task<IActionResult> GetPeriodsPaged(
        [FromQuery] string periodType = "daily",
        [FromQuery] string tradeMode = "all",
        [FromQuery] string? userId = null,
        [FromQuery] DateTime? startDate = null,
        [FromQuery] DateTime? endDate = null,
        [FromQuery] string pnlFilter = "all",
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 10)
    {
        try
        {
            if (!User.IsInRole("Admin"))
            {
                userId = User.FindFirst(ClaimTypes.Name)?.Value ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "1";
            }

            var client = _httpClientFactory.CreateClient("QuantEdgeApi");
            string queryString = $"?periodType={Uri.EscapeDataString(periodType)}&tradeMode={Uri.EscapeDataString(tradeMode)}&pnlFilter={Uri.EscapeDataString(pnlFilter)}&page={page}&pageSize={pageSize}";
            
            if (!string.IsNullOrWhiteSpace(userId))
                queryString += $"&userId={Uri.EscapeDataString(userId)}";
            if (startDate.HasValue)
                queryString += $"&startDate={startDate.Value:yyyy-MM-dd}";
            if (endDate.HasValue)
                queryString += $"&endDate={endDate.Value:yyyy-MM-dd}";

            var response = await client.GetAsync($"/api/reports/periods/paged{queryString}");
            var content = await response.Content.ReadAsStringAsync();
            Response.StatusCode = (int)response.StatusCode;
            return Content(content, "application/json");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error proxying paged periods request.");
            return StatusCode(500, new { message = "Error fetching paged periods.", details = ex.Message });
        }
    }

    [HttpGet("api/web/reports/export")]
    [HttpGet("reports/export")]
    public async Task<IActionResult> ExportCsv(
        [FromQuery] string periodType = "daily",
        [FromQuery] string tradeMode = "all",
        [FromQuery] string? userId = null,
        [FromQuery] DateTime? startDate = null,
        [FromQuery] DateTime? endDate = null,
        [FromQuery] string? symbol = null)
    {
        try
        {
            if (!User.IsInRole("Admin"))
            {
                userId = User.FindFirst(ClaimTypes.Name)?.Value ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "1";
            }

            var client = _httpClientFactory.CreateClient("QuantEdgeApi");
            string queryString = $"?periodType={Uri.EscapeDataString(periodType)}&tradeMode={Uri.EscapeDataString(tradeMode)}";
            
            if (!string.IsNullOrWhiteSpace(userId))
                queryString += $"&userId={Uri.EscapeDataString(userId)}";
            if (startDate.HasValue)
                queryString += $"&startDate={startDate.Value:yyyy-MM-dd}";
            if (endDate.HasValue)
                queryString += $"&endDate={endDate.Value:yyyy-MM-dd}";
            if (!string.IsNullOrWhiteSpace(symbol))
                queryString += $"&symbol={Uri.EscapeDataString(symbol)}";

            var response = await client.GetAsync($"/api/reports/export{queryString}");
            if (!response.IsSuccessStatusCode)
            {
                return StatusCode((int)response.StatusCode, "Failed to export report from API.");
            }

            var bytes = await response.Content.ReadAsByteArrayAsync();
            string filename = $"QuantEdge_Trading_Report_{periodType}_{DateTime.UtcNow:yyyyMMdd_HHmmss}.csv";
            return File(bytes, "text/csv", filename);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error exporting report.");
            return StatusCode(500, new { message = "Error exporting CSV.", details = ex.Message });
        }
    }
}
