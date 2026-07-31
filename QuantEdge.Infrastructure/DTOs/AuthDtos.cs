using System;
using System.ComponentModel.DataAnnotations;

namespace QuantEdge.Infrastructure.DTOs;

/// <summary>
/// DTO for user login using Username and Password.
/// </summary>
public class LoginRequestDto
{
    [Required(ErrorMessage = "Username is required.")]
    [Display(Name = "Username")]
    public string Username { get; set; } = string.Empty;

    [Required(ErrorMessage = "Password is required.")]
    [DataType(DataType.Password)]
    public string Password { get; set; } = string.Empty;

    [Display(Name = "Remember Me")]
    public bool RememberMe { get; set; } = false;
}

/// <summary>
/// DTO for user registration (Full Name, UserName, Password, Confirm Password).
/// Email and MobileNo are optional/hidden for future enablement.
/// </summary>
public class RegisterRequestDto
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

    [Required(ErrorMessage = "Password is required.")]
    [MinLength(6, ErrorMessage = "Password must be at least 6 characters.")]
    [DataType(DataType.Password)]
    public string Password { get; set; } = string.Empty;

    [Required(ErrorMessage = "Confirm Password is required.")]
    [DataType(DataType.Password)]
    [Compare("Password", ErrorMessage = "Password and Confirm Password do not match.")]
    [Display(Name = "Confirm Password")]
    public string ConfirmPassword { get; set; } = string.Empty;

    // Optional fields for future enablement
    public string? Email { get; set; }
    public string? MobileNo { get; set; }
}

/// <summary>
/// DTO representing authenticated user details.
/// </summary>
public class UserDto
{
    public int Id { get; set; }
    public string FullName { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string? MobileNo { get; set; }
    public string Username { get; set; } = string.Empty;
    public string Role { get; set; } = "User";
    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// Result DTO for authentication operations.
/// </summary>
public class AuthResultDto
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public UserDto? User { get; set; }
}

/// <summary>
/// DTO for changing user password from Web UI.
/// </summary>
public class ChangePasswordDto
{
    [Required(ErrorMessage = "Current Password is required.")]
    [DataType(DataType.Password)]
    [Display(Name = "Current Password")]
    public string CurrentPassword { get; set; } = string.Empty;

    [Required(ErrorMessage = "New Password is required.")]
    [MinLength(6, ErrorMessage = "New Password must be at least 6 characters.")]
    [DataType(DataType.Password)]
    [Display(Name = "New Password")]
    public string NewPassword { get; set; } = string.Empty;

    [Required(ErrorMessage = "Confirm New Password is required.")]
    [DataType(DataType.Password)]
    [Compare("NewPassword", ErrorMessage = "New Password and Confirm New Password do not match.")]
    [Display(Name = "Confirm New Password")]
    public string ConfirmNewPassword { get; set; } = string.Empty;
}

/// <summary>
/// DTO for changing user password via API request payload.
/// </summary>
public class ChangePasswordRequestDto
{
    [Required(ErrorMessage = "User ID is required.")]
    public int UserId { get; set; }

    [Required(ErrorMessage = "Current Password is required.")]
    public string CurrentPassword { get; set; } = string.Empty;

    [Required(ErrorMessage = "New Password is required.")]
    [MinLength(6, ErrorMessage = "New Password must be at least 6 characters.")]
    public string NewPassword { get; set; } = string.Empty;
}
