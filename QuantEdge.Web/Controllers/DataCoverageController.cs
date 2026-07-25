using System;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace QuantEdge.Web.Controllers;

/// <summary>
/// Controller serving the Data Coverage Manager view and proxying DataCoverage API endpoints.
/// </summary>
public class DataCoverageController : Controller
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<DataCoverageController> _logger;

    public DataCoverageController(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<DataCoverageController> logger)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    [HttpGet]
    public IActionResult Index()
    {
        ViewBag.ApiBaseUrl = _configuration["ApiBaseUrl"] ?? "https://localhost:44370";
        return View();
    }

    /// <summary>
    /// Proxy PUT request to update stock coverage flags
    /// </summary>
    [HttpPut("api/datacoverage/{id}")]
    [HttpPut("datacoverage/{id}")]
    public async Task<IActionResult> UpdateCoverageFlags(int id, [FromBody] object requestBody)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("QuantEdgeApi");
            var json = System.Text.Json.JsonSerializer.Serialize(requestBody);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await client.PutAsync($"api/datacoverage/{id}", content);
            var responseString = await response.Content.ReadAsStringAsync();
            return StatusCode((int)response.StatusCode, responseString);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to proxy PUT request for stock ID {Id}", id);
            return StatusCode(500, new { message = "Unable to update stock." });
        }
    }

    /// <summary>
    /// Proxy DELETE request to delete stock record
    /// </summary>
    [HttpDelete("api/datacoverage/{id}")]
    [HttpDelete("datacoverage/{id}")]
    public async Task<IActionResult> DeleteStock(int id)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("QuantEdgeApi");
            var response = await client.DeleteAsync($"api/datacoverage/{id}");
            var responseString = await response.Content.ReadAsStringAsync();
            return StatusCode((int)response.StatusCode, responseString);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to proxy DELETE request for stock ID {Id}", id);
            return StatusCode(500, new { message = "Unable to delete stock." });
        }
    }
}
