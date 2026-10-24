using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using QuantEdge.Infrastructure.Constants;
using QuantEdge.Infrastructure.DTOs;
using QuantEdge.Infrastructure.Persistence.Repositories;

namespace QuantEdge.API.Controllers;

/// <summary>
/// Role Permissions: which Web pages each role may open (role_page_permissions + the AppPages catalog).
/// </summary>
[ApiController]
[Route("role-permissions")]
public class RolePermissionController : ControllerBase
{
    private readonly IRolePermissionRepository _repository;
    private readonly ILogger<RolePermissionController> _logger;

    public RolePermissionController(IRolePermissionRepository repository, ILogger<RolePermissionController> logger)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// GET /api/role-permissions?role=User - every catalog page with its resolved access for the role.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetMatrix([FromQuery] string role = AppPages.UserRole)
    {
        if (!TryNormalizeRole(role, out string normalizedRole))
            return BadRequest(new { message = $"Unknown role '{role}'." });

        try
        {
            bool isAdmin = AppPages.IsAdminRole(normalizedRole);
            var rows = isAdmin
                ? new List<RolePagePermissionRowDto>()
                : (await _repository.GetByRoleAsync(normalizedRole)).ToList();
            var stored = rows.ToDictionary(r => r.PageKey, r => r.IsAllowed, StringComparer.OrdinalIgnoreCase);
            var lastChange = rows.OrderByDescending(r => r.UpdatedAt).FirstOrDefault();

            return Ok(new RolePermissionMatrixDto
            {
                Role = normalizedRole,
                IsReadOnly = isAdmin,
                LastUpdatedAt = lastChange?.UpdatedAt,
                LastUpdatedBy = lastChange?.UpdatedBy,
                Pages = AppPages.All.Select(p => new RolePermissionPageDto
                {
                    Key = p.Key,
                    Name = p.Name,
                    Group = p.Group,
                    Description = p.Description,
                    Lock = p.Lock.ToString(),
                    DefaultAllowed = AppPages.Resolve(p, normalizedRole, null),
                    IsAllowed = AppPages.Resolve(p, normalizedRole, stored.TryGetValue(p.Key, out bool v) ? v : null)
                }).ToList()
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load role permissions for role {Role}.", normalizedRole);
            return StatusCode(500, new { message = "Unable to load role permissions (apply role_page_permissions from schema.sql)." });
        }
    }

    /// <summary>
    /// PUT /api/role-permissions - saves the pages a role may open. Locked pages and the Admin role cannot be changed.
    /// </summary>
    [HttpPut]
    public async Task<IActionResult> Save([FromBody] SaveRolePermissionsRequestDto request)
    {
        if (request == null || !TryNormalizeRole(request.Role, out string normalizedRole))
            return BadRequest(new { message = "A valid role is required." });

        if (AppPages.IsAdminRole(normalizedRole))
            return BadRequest(new { message = "The Admin role always has access to every page and cannot be changed." });

        var unknown = (request.AllowedPageKeys ?? new List<string>()).Where(k => AppPages.Find(k) == null).ToList();
        if (unknown.Count > 0)
            return BadRequest(new { message = $"Unknown page(s): {string.Join(", ", unknown)}." });

        var allowed = new HashSet<string>(request.AllowedPageKeys ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
        var pageAccess = AppPages.All
            .Where(p => p.Lock == PageAccessLock.None)
            .ToDictionary(p => p.Key, p => allowed.Contains(p.Key));

        try
        {
            await _repository.SaveAsync(normalizedRole, pageAccess, request.UpdatedBy);
            _logger.LogInformation("Role permissions for {Role} saved by {UpdatedBy}: {Allowed}.",
                normalizedRole, request.UpdatedBy ?? "unknown", string.Join(", ", pageAccess.Where(p => p.Value).Select(p => p.Key)));

            return Ok(new { success = true, message = $"Page access for the {normalizedRole} role saved." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save role permissions for role {Role}.", normalizedRole);
            return StatusCode(500, new { message = "Unable to save role permissions." });
        }
    }

    // app_users.role only holds Admin or User (UserController maps anything else to User).
    private static bool TryNormalizeRole(string? role, out string normalizedRole)
    {
        if (AppPages.IsAdminRole(role)) { normalizedRole = AppPages.AdminRole; return true; }
        if (string.Equals(role, AppPages.UserRole, StringComparison.OrdinalIgnoreCase)) { normalizedRole = AppPages.UserRole; return true; }
        normalizedRole = string.Empty;
        return false;
    }
}
