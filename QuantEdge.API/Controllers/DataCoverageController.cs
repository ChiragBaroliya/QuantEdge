using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using QuantEdge.Infrastructure.DTOs;
using QuantEdge.Infrastructure.Persistence.Repositories;

namespace QuantEdge.API.Controllers;

[ApiController]
[Route("datacoverage")]
public class DataCoverageController : ControllerBase
{
    private readonly IStockMasterRepository _stockMasterRepository;
    private readonly ILogger<DataCoverageController> _logger;

    public DataCoverageController(
        IStockMasterRepository stockMasterRepository,
        ILogger<DataCoverageController> logger)
    {
        _stockMasterRepository = stockMasterRepository ?? throw new ArgumentNullException(nameof(stockMasterRepository));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// GET /datacoverage/summary - Gets overall data coverage summary counts.
    /// </summary>
    [HttpGet("summary")]
    public async Task<IActionResult> GetSummary()
    {
        try
        {
            var summary = await _stockMasterRepository.GetCoverageSummaryAsync();
            return Ok(summary);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch data coverage summary.");
            return StatusCode(500, $"Internal server error: {ex.Message}");
        }
    }

    /// <summary>
    /// GET /datacoverage/list - Gets paginated stock coverage data matching search and filter options.
    /// </summary>
    [HttpGet("list")]
    public async Task<IActionResult> GetPaginatedList(
        [FromQuery] string? search = null,
        [FromQuery] string? status = null,
        [FromQuery] string? historyFilter = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25)
    {
        try
        {
            var result = await _stockMasterRepository.GetPaginatedCoverageAsync(search, status, historyFilter, page, pageSize);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch paginated stock coverage list.");
            return StatusCode(500, $"Internal server error: {ex.Message}");
        }
    }

    /// <summary>
    /// POST /datacoverage/update - Updates active status and timeframe history flags for a stock.
    /// </summary>
    [HttpPost("update")]
    public async Task<IActionResult> UpdateCoverageFlags([FromBody] UpdateStockCoverageRequest request)
    {
        if (request == null || request.Id <= 0)
        {
            return BadRequest("Invalid stock update request.");
        }

        try
        {
            await _stockMasterRepository.UpdateStockCoverageFlagsAsync(request);
            return Ok(new { success = true, message = "Stock coverage flags updated successfully." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update stock coverage flags for stock ID {Id}.", request.Id);
            return StatusCode(500, $"Internal server error: {ex.Message}");
        }
    }
}
