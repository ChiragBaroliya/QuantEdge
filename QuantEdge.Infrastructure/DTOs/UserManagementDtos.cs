using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace QuantEdge.Infrastructure.DTOs;

/// <summary>
/// DTO for requesting paginated user list with filters.
/// </summary>
public class UserManagementFilterDto
{
    public string? Search { get; set; }
    public string? RoleFilter { get; set; }
    public int PageNumber { get; set; } = 1;
    public int PageSize { get; set; } = 25;
}

/// <summary>
/// Paginated result container for User Management table.
/// </summary>
public class PaginatedUserResultDto
{
    public IEnumerable<UserDto> Items { get; set; } = new List<UserDto>();
    public int TotalCount { get; set; }
    public int PageNumber { get; set; }
    public int PageSize { get; set; }
    public int TotalPages => PageSize > 0 ? (int)Math.Ceiling((double)TotalCount / PageSize) : 0;
}

/// <summary>
/// Summary KPI counts for User Management dashboard.
/// </summary>
public class UserSummaryDto
{
    public int TotalUsers { get; set; }
    public int AdminCount { get; set; }
    public int UserCount { get; set; }
}

/// <summary>
/// Request DTO for creating a new user by Admin.
/// </summary>
public class CreateUserRequestDto
{
    [Required(ErrorMessage = "Full Name is required.")]
    [StringLength(150, ErrorMessage = "Full Name cannot exceed 150 characters.")]
    [Display(Name = "Full Name")]
    public string FullName { get; set; } = string.Empty;

    [Required(ErrorMessage = "Username is required.")]
    [MinLength(3, ErrorMessage = "Username must be at least 3 characters.")]
    [StringLength(100, ErrorMessage = "Username cannot exceed 100 characters.")]
    [RegularExpression(@"^[a-zA-Z0-9_\-\.]+$", ErrorMessage = "Username can only contain letters, numbers, dots, hyphens, and underscores.")]
    [Display(Name = "Username")]
    public string Username { get; set; } = string.Empty;

    [EmailAddress(ErrorMessage = "Invalid Email Address format.")]
    [StringLength(255, ErrorMessage = "Email cannot exceed 255 characters.")]
    [Display(Name = "Email")]
    public string? Email { get; set; }

    [StringLength(20, ErrorMessage = "Mobile Number cannot exceed 20 characters.")]
    [Display(Name = "Mobile No")]
    public string? MobileNo { get; set; }

    [Required(ErrorMessage = "Password is required.")]
    [MinLength(6, ErrorMessage = "Password must be at least 6 characters.")]
    [DataType(DataType.Password)]
    public string Password { get; set; } = string.Empty;

    [Required(ErrorMessage = "Role is required.")]
    [Display(Name = "Role")]
    public string Role { get; set; } = "User"; // "Admin" or "User"
}

/// <summary>
/// Request DTO for updating existing user details by Admin.
/// </summary>
public class UpdateUserRequestDto
{
    [Required(ErrorMessage = "User ID is required.")]
    public int Id { get; set; }

    [Required(ErrorMessage = "Full Name is required.")]
    [StringLength(150, ErrorMessage = "Full Name cannot exceed 150 characters.")]
    [Display(Name = "Full Name")]
    public string FullName { get; set; } = string.Empty;

    [EmailAddress(ErrorMessage = "Invalid Email Address format.")]
    [StringLength(255, ErrorMessage = "Email cannot exceed 255 characters.")]
    [Display(Name = "Email")]
    public string? Email { get; set; }

    [StringLength(20, ErrorMessage = "Mobile Number cannot exceed 20 characters.")]
    [Display(Name = "Mobile No")]
    public string? MobileNo { get; set; }

    [Required(ErrorMessage = "Role is required.")]
    [Display(Name = "Role")]
    public string Role { get; set; } = "User"; // "Admin" or "User"
}

/// <summary>
/// Request DTO for resetting user password by Admin.
/// </summary>
public class ResetUserPasswordRequestDto
{
    [Required(ErrorMessage = "User ID is required.")]
    public int UserId { get; set; }

    [Required(ErrorMessage = "New Password is required.")]
    [MinLength(6, ErrorMessage = "New Password must be at least 6 characters.")]
    [DataType(DataType.Password)]
    [Display(Name = "New Password")]
    public string NewPassword { get; set; } = string.Empty;
}
