using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using QuantEdge.Infrastructure.Constants;
using QuantEdge.Infrastructure.DTOs;
using QuantEdge.Web.Services;

namespace QuantEdge.Web.Controllers;

/// <summary>
/// Role Permissions screen: Admin decides which pages each role can open. Proxies /api/role-permissions.
/// Strictly Admin-only (the page is AdminOnly in AppPages, so it can never be granted to another role).
/// </summary>
[Authorize(Roles = AppPages.AdminRole)]
public class RolePermissionController : Controller
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IPagePermissionService _pagePermissions;
    private readonly ILogger<RolePermissionController> _logger;

    public RolePermissionController(
        IHttpClientFactory httpClientFactory,
        IPagePermissionService pagePermissions,
        ILogger<RolePermissionController> logger)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _pagePermissions = pagePermissions ?? throw new ArgumentNullException(nameof(pagePermissions));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    [HttpGet]
    public IActionResult Index()
    {
        return View();
    }

    /// <summary>
    /// GET /rolepermission/matrix?role=User
    /// </summary>
    [HttpGet("rolepermission/matrix")]
    public async Task<IActionResult> GetMatrix([FromQuery] string role = AppPages.UserRole)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("QuantEdgeApi");
            var response = await client.GetAsync($"/api/role-permissions?role={Uri.EscapeDataString(role ?? AppPages.UserRole)}");
            var content = await response.Content.ReadAsStringAsync();
            return StatusCode((int)response.StatusCode, content);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to proxy role permission matrix request for role {Role}.", role);
            return StatusCode(500, new { message = "Unable to load role permissions." });
        }
    }

    /// <summary>
    /// PUT /rolepermission/save
    /// </summary>
    [HttpPut("rolepermission/save")]
    public async Task<IActionResult> Save([FromBody] SaveRolePermissionsRequestDto request)
    {
        if (request == null)
            return BadRequest(new { message = "Invalid save request." });

        try
        {
            request.UpdatedBy = User.Identity?.Name;

            var client = _httpClientFactory.CreateClient("QuantEdgeApi");
            var response = await client.PutAsJsonAsync("/api/role-permissions", request);
            var content = await response.Content.ReadAsStringAsync();

            // Sidebar and page checks pick up the change on the next request.
            if (response.IsSuccessStatusCode)
                await _pagePermissions.InvalidateAsync(request.Role);

            return StatusCode((int)response.StatusCode, content);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to proxy role permission save for role {Role}.", request.Role);
            return StatusCode(500, new { message = "Unable to save role permissions." });
        }
    }
}
