using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using QuantEdge.Web.Models;

namespace QuantEdge.Web.Controllers;

/// <summary>
/// MVC Controller for rendering the Indian Stock Market Holiday dashboard UI.
/// </summary>
public class HolidayController : Controller
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<HolidayController> _logger;

    public HolidayController(IHttpClientFactory httpClientFactory, ILogger<HolidayController> logger)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// GET /Holiday - Renders the dashboard showing list of holidays and add form.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Index()
    {
        var holidays = new List<HolidayViewModel>();
        try
        {
            var client = _httpClientFactory.CreateClient("QuantEdgeApi");
            var response = await client.GetFromJsonAsync<List<HolidayViewModel>>("/api/holidays");
            if (response != null)
            {
                holidays = response;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to retrieve holidays in Web UI.");
            ViewBag.ErrorMessage = $"Failed to fetch holidays from API: {ex.Message}";
        }

        // Retrieve validation or transaction results from post-redirect
        if (TempData.ContainsKey("SuccessMessage"))
        {
            ViewBag.SuccessMessage = TempData["SuccessMessage"]?.ToString();
        }
        if (TempData.ContainsKey("ErrorMessage"))
        {
            ViewBag.ErrorMessage = TempData["ErrorMessage"]?.ToString();
        }

        return View(holidays);
    }

    /// <summary>
    /// POST /Holiday/Create - Adds a new holiday via the API.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Create(DateTime holidayDate, string description)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            TempData["ErrorMessage"] = "Description is required.";
            return RedirectToAction(nameof(Index));
        }

        try
        {
            var client = _httpClientFactory.CreateClient("QuantEdgeApi");
            var payload = new { HolidayDate = holidayDate, Description = description };
            var response = await client.PostAsJsonAsync("/api/holidays", payload);
            
            if (response.IsSuccessStatusCode)
            {
                TempData["SuccessMessage"] = "Holiday added successfully!";
            }
            else
            {
                var content = await response.Content.ReadAsStringAsync();
                TempData["ErrorMessage"] = $"Failed to add holiday: {content}";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create holiday via Web UI.");
            TempData["ErrorMessage"] = $"An error occurred: {ex.Message}";
        }

        return RedirectToAction(nameof(Index));
    }

    /// <summary>
    /// POST /Holiday/Delete - Deletes a holiday by ID via the API.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Delete(int id)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("QuantEdgeApi");
            var response = await client.DeleteAsync($"/api/holidays/{id}");
            
            if (response.IsSuccessStatusCode)
            {
                TempData["SuccessMessage"] = "Holiday deleted successfully.";
            }
            else
            {
                var content = await response.Content.ReadAsStringAsync();
                TempData["ErrorMessage"] = $"Failed to delete holiday: {content}";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete holiday via Web UI.");
            TempData["ErrorMessage"] = $"An error occurred: {ex.Message}";
        }

        return RedirectToAction(nameof(Index));
    }
}
