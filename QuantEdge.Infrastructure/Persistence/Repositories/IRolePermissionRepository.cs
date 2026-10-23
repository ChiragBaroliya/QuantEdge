using System.Collections.Generic;
using System.Threading.Tasks;
using QuantEdge.Infrastructure.DTOs;

namespace QuantEdge.Infrastructure.Persistence.Repositories;

/// <summary>
/// Role -> page access rows (role_page_permissions) edited from the Role Permissions screen.
/// </summary>
public interface IRolePermissionRepository
{
    Task<IReadOnlyList<RolePagePermissionRowDto>> GetByRoleAsync(string role);
    Task SaveAsync(string role, IReadOnlyDictionary<string, bool> pageAccess, string? updatedBy);
}
