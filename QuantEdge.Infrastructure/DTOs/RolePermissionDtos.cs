using System;
using System.Collections.Generic;

namespace QuantEdge.Infrastructure.DTOs;

/// <summary>
/// One stored row of role_page_permissions.
/// </summary>
public class RolePagePermissionRowDto
{
    public string PageKey { get; set; } = string.Empty;
    public bool IsAllowed { get; set; }
    public DateTime UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }
}

/// <summary>
/// One page on the Role Permissions screen, with its resolved access for the selected role.
/// </summary>
public class RolePermissionPageDto
{
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Group { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    /// <summary>None, AlwaysAllowed or AdminOnly.</summary>
    public string Lock { get; set; } = "None";
    public bool IsAllowed { get; set; }
    public bool DefaultAllowed { get; set; }
}

/// <summary>
/// Page access matrix for one role.
/// </summary>
public class RolePermissionMatrixDto
{
    public string Role { get; set; } = string.Empty;
    /// <summary>True for the Admin role - it always has every page and cannot be edited.</summary>
    public bool IsReadOnly { get; set; }
    public List<RolePermissionPageDto> Pages { get; set; } = new();
    public DateTime? LastUpdatedAt { get; set; }
    public string? LastUpdatedBy { get; set; }
}

/// <summary>
/// Save request: the full list of pages the role may open. Locked pages are ignored.
/// </summary>
public class SaveRolePermissionsRequestDto
{
    public string Role { get; set; } = string.Empty;
    public List<string> AllowedPageKeys { get; set; } = new();
    public string? UpdatedBy { get; set; }
}
