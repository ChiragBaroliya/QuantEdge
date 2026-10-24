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
    private const int MaxSectorNameLength = 100; // sectors.name VARCHAR(100)

    private readonly IStockMasterRepository _stockMasterRepository;
    private readonly ISectorRepository _sectorRepository;
    private readonly ILogger<DataCoverageController> _logger;

    public DataCoverageController(
        IStockMasterRepository stockMasterRepository,
        ISectorRepository sectorRepository,
        ILogger<DataCoverageController> logger)
    {
        _stockMasterRepository = stockMasterRepository ?? throw new ArgumentNullException(nameof(stockMasterRepository));
        _sectorRepository = sectorRepository ?? throw new ArgumentNullException(nameof(sectorRepository));
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
        [FromQuery] string? alphabet = null,
        [FromQuery] string? sector = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25)
    {
        try
        {
            var result = await _stockMasterRepository.GetPaginatedCoverageAsync(search, status, historyFilter, alphabet, sector, page, pageSize);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch paginated stock coverage list.");
            return StatusCode(500, $"Internal server error: {ex.Message}");
        }
    }

    /// <summary>
    /// GET /datacoverage/export-excel - Downloads an Excel file (.xlsx) containing filtered stock coverage records.
    /// </summary>
    [HttpGet("export-excel")]
    public async Task<IActionResult> ExportExcel(
        [FromQuery] string? search = null,
        [FromQuery] string? status = null,
        [FromQuery] string? historyFilter = null,
        [FromQuery] string? alphabet = null,
        [FromQuery] string? sector = null)
    {
        try
        {
            var excelBytes = await _stockMasterRepository.ExportStockCoverageToExcelAsync(search, status, historyFilter, alphabet, sector);
            string fileName = $"Stock_Coverage_Export_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx";
            return File(excelBytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", fileName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate stock coverage Excel export.");
            return StatusCode(500, $"Internal server error: {ex.Message}");
        }
    }

    /// <summary>
    /// PUT /datacoverage/{id} or /api/datacoverage/{id} - Updates active status and timeframe history flags for a stock by ID.
    /// </summary>
    [HttpPut("{id}")]
    public async Task<IActionResult> UpdateCoverageFlagsPut(int id, [FromBody] UpdateStockCoverageRequest request)
    {
        if (request == null || id <= 0)
        {
            return BadRequest("Invalid stock update request.");
        }
        request.Id = id;
        return await UpdateCoverageFlags(request);
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

    /// <summary>
    /// DELETE /datacoverage/{id} - Deletes a stock master record from database tables.
    /// </summary>
    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteStock(int id)
    {
        if (id <= 0)
        {
            return BadRequest("Invalid stock ID.");
        }

        try
        {
            await _stockMasterRepository.DeleteStockAsync(id);
            return Ok(new { success = true, message = "Stock deleted successfully." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete stock with ID {Id}.", id);
            return StatusCode(500, $"Internal server error: {ex.Message}");
        }
    }

    /// <summary>
    /// POST /datacoverage/bulk-delete - Deletes multiple stock master records from database tables.
    /// </summary>
    [HttpPost("bulk-delete")]
    public async Task<IActionResult> BulkDeleteStocks([FromBody] int[] ids)
    {
        if (ids == null || ids.Length == 0)
        {
            return BadRequest("No stock IDs provided for bulk deletion.");
        }

        try
        {
            await _stockMasterRepository.BulkDeleteStocksAsync(ids);
            return Ok(new { success = true, count = ids.Length, message = $"{ids.Length} stocks deleted successfully." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to bulk delete {Count} stocks.", ids.Length);
            return StatusCode(500, $"Internal server error: {ex.Message}");
        }
    }

    /// <summary>
    /// GET /datacoverage/sectors - Active sectors with linked stock counts (sector filter and mapping popup).
    /// </summary>
    [HttpGet("sectors")]
    public async Task<IActionResult> GetSectors()
    {
        try
        {
            return Ok(await _sectorRepository.GetSectorListAsync());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch sector list.");
            return StatusCode(500, $"Internal server error: {ex.Message}");
        }
    }

    /// <summary>
    /// POST /datacoverage/sectors - Adds a new sector, or returns (and reactivates) the existing one with that name.
    /// </summary>
    [HttpPost("sectors")]
    public async Task<IActionResult> CreateSector([FromBody] CreateSectorRequest request)
    {
        string name = request?.Name?.Trim() ?? string.Empty;
        if (name.Length == 0 || name.Length > MaxSectorNameLength)
        {
            return BadRequest($"Sector name is required (max {MaxSectorNameLength} characters).");
        }

        try
        {
            return Ok(await _sectorRepository.UpsertSectorAsync(name));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create sector {Name}.", name);
            return StatusCode(500, $"Internal server error: {ex.Message}");
        }
    }

    /// <summary>
    /// PUT /datacoverage/{id}/sectors - Replaces the sectors a stock is mapped to (empty list removes all).
    /// </summary>
    [HttpPut("{id}/sectors")]
    public async Task<IActionResult> SetStockSectors(int id, [FromBody] SetStockSectorsRequest request)
    {
        if (id <= 0 || request == null)
        {
            return BadRequest("Invalid sector mapping request.");
        }

        try
        {
            await _sectorRepository.SetStockSectorsAsync(id, request.SectorIds ?? Array.Empty<int>());
            return Ok(new { success = true, message = "Stock sectors updated successfully." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update sectors for stock ID {Id}.", id);
            return StatusCode(500, $"Internal server error: {ex.Message}");
        }
    }
}


