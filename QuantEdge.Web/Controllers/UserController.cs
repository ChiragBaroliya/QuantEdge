using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using QuantEdge.Infrastructure.DTOs;

namespace QuantEdge.Web.Controllers;

/// <summary>
/// Web Controller serving User Management View and proxying API endpoints.
/// Strictly accessible by Admin role users.
/// </summary>
[Authorize(Roles = "Admin")]
public class UserController : Controller
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<UserController> _logger;

    public UserController(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<UserController> logger)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Displays User Management View.
    /// </summary>
    [HttpGet]
    public IActionResult Index()
    {
        ViewBag.ApiBaseUrl = _configuration["ApiBaseUrl"] ?? "https://localhost:44370";
        string currentUserId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "0";
        ViewBag.CurrentUserId = currentUserId;
        return View();
    }

    /// <summary>
    /// Proxy GET request for User Summary KPI metrics.
    /// </summary>
    [HttpGet("api/web/users/summary")]
    [HttpGet("user/summary")]
    public async Task<IActionResult> GetSummary()
    {
        try
        {
            var client = _httpClientFactory.CreateClient("QuantEdgeApi");
            var response = await client.GetAsync("/api/users/summary");
            var content = await response.Content.ReadAsStringAsync();
            return StatusCode((int)response.StatusCode, content);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to proxy user summary request.");
            return StatusCode(500, new { message = "Unable to fetch user summary metrics." });
        }
    }

    /// <summary>
    /// Proxy GET request for paginated user list.
    /// </summary>
    [HttpGet("api/web/users/list")]
    [HttpGet("user/list")]
    public async Task<IActionResult> GetPaginatedList(
        [FromQuery] string? search = null,
        [FromQuery] string? roleFilter = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("QuantEdgeApi");
            var url = $"/api/users/list?search={Uri.EscapeDataString(search ?? "")}&roleFilter={Uri.EscapeDataString(roleFilter ?? "")}&page={page}&pageSize={pageSize}";
            var response = await client.GetAsync(url);
            var content = await response.Content.ReadAsStringAsync();
            return StatusCode((int)response.StatusCode, content);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to proxy paginated user list request.");
            return StatusCode(500, new { message = "Unable to fetch user list." });
        }
    }

    /// <summary>
    /// Proxy POST request to create a new user.
    /// </summary>
    [HttpPost("api/web/users")]
    [HttpPost("user/create")]
    public async Task<IActionResult> CreateUser([FromBody] CreateUserRequestDto request)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("QuantEdgeApi");
            var response = await client.PostAsJsonAsync("/api/users", request);
            var content = await response.Content.ReadAsStringAsync();
            return StatusCode((int)response.StatusCode, content);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to proxy user creation request.");
            return StatusCode(500, new { message = "Unable to create user." });
        }
    }

    /// <summary>
    /// Proxy PUT request to update user details.
    /// </summary>
    [HttpPut("api/web/users/{id}")]
    [HttpPut("user/update/{id}")]
    public async Task<IActionResult> UpdateUser(int id, [FromBody] UpdateUserRequestDto request)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("QuantEdgeApi");
            var response = await client.PutAsJsonAsync($"/api/users/{id}", request);
            var content = await response.Content.ReadAsStringAsync();
            return StatusCode((int)response.StatusCode, content);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to proxy user update request for ID {UserId}.", id);
            return StatusCode(500, new { message = "Unable to update user." });
        }
    }

    /// <summary>
    /// Proxy DELETE request to delete a user.
    /// </summary>
    [HttpDelete("api/web/users/{id}")]
    [HttpDelete("user/delete/{id}")]
    public async Task<IActionResult> DeleteUser(int id)
    {
        try
        {
            // Extra safety check in Web Layer: Prevent deleting self
            string currentUserId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "0";
            if (int.TryParse(currentUserId, out int loggedInId) && loggedInId == id)
            {
                return BadRequest(new { message = "You cannot delete your own active user account." });
            }

            var client = _httpClientFactory.CreateClient("QuantEdgeApi");
            var response = await client.DeleteAsync($"/api/users/{id}");
            var content = await response.Content.ReadAsStringAsync();
            return StatusCode((int)response.StatusCode, content);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to proxy user deletion request for ID {UserId}.", id);
            return StatusCode(500, new { message = "Unable to delete user." });
        }
    }

    /// <summary>
    /// Proxy POST request to reset user password.
    /// </summary>
    [HttpPost("api/web/users/{id}/reset-password")]
    [HttpPost("user/reset-password/{id}")]
    public async Task<IActionResult> ResetPassword(int id, [FromBody] ResetUserPasswordRequestDto request)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("QuantEdgeApi");
            var response = await client.PostAsJsonAsync($"/api/users/{id}/reset-password", request);
            var content = await response.Content.ReadAsStringAsync();
            return StatusCode((int)response.StatusCode, content);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to proxy password reset request for ID {UserId}.", id);
            return StatusCode(500, new { message = "Unable to reset password." });
        }
    }
}
