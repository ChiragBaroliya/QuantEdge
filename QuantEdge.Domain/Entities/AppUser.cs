using System;

namespace QuantEdge.Domain.Entities;

/// <summary>
/// Domain entity representing an application user in QuantEdge.
/// </summary>
public class AppUser
{
    public int Id { get; set; }
    public string FullName { get; set; } = string.Empty;
    public string? Email { get; set; } // Optional/hidden for now, future enablement
    public string? MobileNo { get; set; } // Optional/hidden for now, future enablement
    public string Username { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string Role { get; set; } = "User"; // "Admin" or "User"
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
