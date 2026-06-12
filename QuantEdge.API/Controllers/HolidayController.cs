using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using QuantEdge.Infrastructure.Interfaces;
using QuantEdge.Infrastructure.Persistence.Repositories;

namespace QuantEdge.API.Controllers;

[ApiController]
[Route("api/holidays")]
public class HolidayController : ControllerBase
{
    private readonly IIndianHolidayRepository _holidayRepository;
    private readonly IMarketHoursService _marketHoursService;
    private readonly ILogger<HolidayController> _logger;

    public HolidayController(
        IIndianHolidayRepository holidayRepository,
        IMarketHoursService marketHoursService,
        ILogger<HolidayController> logger)
    {
        _holidayRepository = holidayRepository ?? throw new ArgumentNullException(nameof(holidayRepository));
        _marketHoursService = marketHoursService ?? throw new ArgumentNullException(nameof(marketHoursService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// GET /api/holidays - Retrieves all registered Indian Stock Market holidays.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        _logger.LogInformation("HTTP GET /api/holidays - Fetching all holidays.");
        try
        {
            var holidays = await _holidayRepository.GetAllHolidaysAsync();
            return Ok(holidays);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to retrieve holidays.");
            return StatusCode(500, $"Internal server error: {ex.Message}");
        }
    }

    /// <summary>
    /// POST /api/holidays - Creates or updates an Indian Stock Market holiday.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateHolidayRequest request)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.Description))
        {
            return BadRequest("Invalid holiday data. Description is required.");
        }

        _logger.LogInformation("HTTP POST /api/holidays - Creating holiday: {Date:yyyy-MM-dd} ({Desc})", request.HolidayDate, request.Description);
        try
        {
            await _holidayRepository.InsertHolidayAsync(request.HolidayDate, request.Description);
            
            // Force immediate reload of in-memory cache to sync worker and api
            await _marketHoursService.RefreshHolidaysCacheAsync();

            return Ok(new { message = "Holiday created successfully." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create holiday.");
            return StatusCode(500, $"Internal server error: {ex.Message}");
        }
    }

    /// <summary>
    /// DELETE /api/holidays/{id} - Deletes an Indian Stock Market holiday.
    /// </summary>
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        _logger.LogInformation("HTTP DELETE /api/holidays/{Id} - Deleting holiday.", id);
        try
        {
            await _holidayRepository.DeleteHolidayAsync(id);

            // Force immediate reload of in-memory cache to sync worker and api
            await _marketHoursService.RefreshHolidaysCacheAsync();

            return Ok(new { message = "Holiday deleted successfully." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete holiday.");
            return StatusCode(500, $"Internal server error: {ex.Message}");
        }
    }
}

public class CreateHolidayRequest
{
    public DateTime HolidayDate { get; set; }
    public string Description { get; set; } = string.Empty;
}
