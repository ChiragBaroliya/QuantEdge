using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using QuantEdge.Infrastructure.Constants;
using QuantEdge.Infrastructure.DTOs;
using QuantEdge.Infrastructure.Interfaces;

namespace QuantEdge.Web.Services;

/// <summary>
/// Answers "can this signed-in user open page X?" for the sidebar and [PageAccess] controllers.
/// Reads GET /api/role-permissions once per role and caches it, so saving on the Role Permissions
/// screen takes effect without the user signing in again.
/// </summary>
public interface IPagePermissionService
{
    Task<IReadOnlySet<string>> GetAllowedPagesAsync(ClaimsPrincipal user);
    Task<bool> CanAccessAsync(ClaimsPrincipal user, string pageKey);
    Task InvalidateAsync(string role);
}

public class PagePermissionService : IPagePermissionService
{
    private const string CacheKeyPrefix = "role_page_permissions_";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(2);
    // When the API is down, fall back to catalog defaults but retry soon.
    private static readonly TimeSpan FallbackCacheDuration = TimeSpan.FromSeconds(30);

    private static readonly IReadOnlySet<string> AllPages =
        new HashSet<string>(AppPages.All.Select(p => p.Key), StringComparer.OrdinalIgnoreCase);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ICacheService _cacheService;
    private readonly ILogger<PagePermissionService> _logger;

    public PagePermissionService(
        IHttpClientFactory httpClientFactory,
        ICacheService cacheService,
        ILogger<PagePermissionService> logger)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _cacheService = cacheService ?? throw new ArgumentNullException(nameof(cacheService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<IReadOnlySet<string>> GetAllowedPagesAsync(ClaimsPrincipal user)
    {
        if (user?.Identity?.IsAuthenticated != true)
            return new HashSet<string>();

        if (user.IsInRole(AppPages.AdminRole))
            return AllPages;

        // Only Admin and User exist; any other role claim gets the User role's access.
        return await GetAllowedPagesForRoleAsync(AppPages.UserRole);
    }

    public async Task<bool> CanAccessAsync(ClaimsPrincipal user, string pageKey) =>
        (await GetAllowedPagesAsync(user)).Contains(pageKey);

    public Task InvalidateAsync(string role) =>
        AppPages.IsAdminRole(role) ? Task.CompletedTask : _cacheService.RemoveAsync(CacheKeyPrefix + AppPages.UserRole);

    private async Task<IReadOnlySet<string>> GetAllowedPagesForRoleAsync(string role)
    {
        string cacheKey = CacheKeyPrefix + role;
        var cached = await _cacheService.GetAsync<HashSet<string>>(cacheKey);
        if (cached != null)
            return cached;

        try
        {
            var client = _httpClientFactory.CreateClient("QuantEdgeApi");
            var matrix = await client.GetFromJsonAsync<RolePermissionMatrixDto>(
                $"/api/role-permissions?role={Uri.EscapeDataString(role)}");

            if (matrix != null)
            {
                var allowed = new HashSet<string>(
                    matrix.Pages.Where(p => p.IsAllowed).Select(p => p.Key), StringComparer.OrdinalIgnoreCase);
                await _cacheService.SetAsync(cacheKey, allowed, CacheDuration);
                return allowed;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load page permissions for role {Role} - using catalog defaults.", role);
        }

        var defaults = new HashSet<string>(
            AppPages.All.Where(p => AppPages.Resolve(p, role, null)).Select(p => p.Key), StringComparer.OrdinalIgnoreCase);
        await _cacheService.SetAsync(cacheKey, defaults, FallbackCacheDuration);
        return defaults;
    }
}
