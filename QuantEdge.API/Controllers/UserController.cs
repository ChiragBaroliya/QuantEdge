using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using QuantEdge.Domain.Entities;
using QuantEdge.Infrastructure.DTOs;
using QuantEdge.Infrastructure.Persistence.Repositories;
using QuantEdge.Infrastructure.Services;

namespace QuantEdge.API.Controllers;

[ApiController]
[Route("api/users")]
[Route("users")]
public class UserController : ControllerBase
{
    private readonly IUserRepository _userRepository;
    private readonly IPasswordHasher _passwordHasher;
    private readonly ILogger<UserController> _logger;

    public UserController(
        IUserRepository userRepository,
        IPasswordHasher passwordHasher,
        ILogger<UserController> logger)
    {
        _userRepository = userRepository ?? throw new ArgumentNullException(nameof(userRepository));
        _passwordHasher = passwordHasher ?? throw new ArgumentNullException(nameof(passwordHasher));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// GET /api/users/summary - Returns summary KPI statistics for users.
    /// </summary>
    [HttpGet("summary")]
    public async Task<IActionResult> GetSummary()
    {
        try
        {
            var summary = await _userRepository.GetUserSummaryAsync();
            return Ok(summary);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching user summary.");
            return StatusCode(500, new { message = "An error occurred while fetching user summary." });
        }
    }

    /// <summary>
    /// GET /api/users/list - Returns paginated user list based on search string, role filter, page number, and page size.
    /// </summary>
    [HttpGet("list")]
    public async Task<IActionResult> GetPaginatedList(
        [FromQuery] string? search = null,
        [FromQuery] string? roleFilter = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25)
    {
        try
        {
            var result = await _userRepository.GetPaginatedUsersAsync(search, roleFilter, page, pageSize);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching paginated user list.");
            return StatusCode(500, new { message = "An error occurred while fetching user list." });
        }
    }

    /// <summary>
    /// GET /api/users/{id} - Returns single user profile by ID.
    /// </summary>
    [HttpGet("{id}")]
    public async Task<IActionResult> GetById(int id)
    {
        if (id <= 0)
            return BadRequest(new { message = "Invalid user ID." });

        try
        {
            var user = await _userRepository.GetByIdAsync(id);
            if (user == null)
                return NotFound(new { message = "User not found." });

            return Ok(new UserDto
            {
                Id = user.Id,
                FullName = user.FullName,
                Email = user.Email,
                MobileNo = user.MobileNo,
                Username = user.Username,
                Role = user.Role,
                CreatedAt = user.CreatedAt
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching user with ID {UserId}.", id);
            return StatusCode(500, new { message = "An error occurred while fetching user details." });
        }
    }

    /// <summary>
    /// POST /api/users - Creates a new user by Admin.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> CreateUser([FromBody] CreateUserRequestDto request)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(new { message = "Invalid parameters provided for user creation." });
        }

        try
        {
            // Unique Username check
            if (await _userRepository.ExistsByUsernameAsync(request.Username))
            {
                return BadRequest(new { message = $"Username '{request.Username}' is already taken." });
            }

            // Unique Email check if provided
            if (!string.IsNullOrWhiteSpace(request.Email) && await _userRepository.ExistsByEmailAsync(request.Email))
            {
                return BadRequest(new { message = $"Email address '{request.Email}' is already registered." });
            }

            string targetRole = string.Equals(request.Role, "Admin", StringComparison.OrdinalIgnoreCase) ? "Admin" : "User";

            var newUser = new AppUser
            {
                FullName = request.FullName.Trim(),
                Username = request.Username.Trim(),
                Email = string.IsNullOrWhiteSpace(request.Email) ? null : request.Email.Trim().ToLowerInvariant(),
                MobileNo = string.IsNullOrWhiteSpace(request.MobileNo) ? null : request.MobileNo.Trim(),
                PasswordHash = _passwordHasher.HashPassword(request.Password),
                Role = targetRole,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            int newUserId = await _userRepository.CreateUserAsync(newUser);
            newUser.Id = newUserId;

            _logger.LogInformation("Admin created new user '{Username}' (ID: {UserId}, Role: {Role}).", newUser.Username, newUserId, newUser.Role);

            return Ok(new
            {
                success = true,
                message = "User created successfully.",
                user = new UserDto
                {
                    Id = newUser.Id,
                    FullName = newUser.FullName,
                    Email = newUser.Email,
                    MobileNo = newUser.MobileNo,
                    Username = newUser.Username,
                    Role = newUser.Role,
                    CreatedAt = newUser.CreatedAt
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating user '{Username}'.", request.Username);
            return StatusCode(500, new { message = "An error occurred while creating the user." });
        }
    }

    /// <summary>
    /// PUT /api/users/{id} - Updates an existing user's details and role by Admin.
    /// </summary>
    [HttpPut("{id}")]
    public async Task<IActionResult> UpdateUser(int id, [FromBody] UpdateUserRequestDto request)
    {
        if (id <= 0 || request == null)
        {
            return BadRequest(new { message = "Invalid update request." });
        }

        request.Id = id;
        if (!ModelState.IsValid)
        {
            return BadRequest(new { message = "Invalid parameters provided for user update." });
        }

        try
        {
            var existingUser = await _userRepository.GetByIdAsync(id);
            if (existingUser == null)
            {
                return NotFound(new { message = "User not found." });
            }

            // Check email uniqueness if modified
            if (!string.IsNullOrWhiteSpace(request.Email) && await _userRepository.ExistsByEmailAsync(request.Email, id))
            {
                return BadRequest(new { message = $"Email address '{request.Email}' is already used by another account." });
            }

            string targetRole = string.Equals(request.Role, "Admin", StringComparison.OrdinalIgnoreCase) ? "Admin" : "User";

            // Safety check: Prevent changing sole Admin to regular User
            if (string.Equals(existingUser.Role, "Admin", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(targetRole, "User", StringComparison.OrdinalIgnoreCase))
            {
                int adminCount = await _userRepository.GetAdminCountAsync();
                if (adminCount <= 1)
                {
                    return BadRequest(new { message = "Cannot demote the only remaining Admin user in the system." });
                }
            }

            existingUser.FullName = request.FullName.Trim();
            existingUser.Email = string.IsNullOrWhiteSpace(request.Email) ? null : request.Email.Trim().ToLowerInvariant();
            existingUser.MobileNo = string.IsNullOrWhiteSpace(request.MobileNo) ? null : request.MobileNo.Trim();
            existingUser.Role = targetRole;

            bool updated = await _userRepository.UpdateUserAsync(existingUser);
            if (!updated)
            {
                return StatusCode(500, new { message = "Failed to update user in database." });
            }

            _logger.LogInformation("Admin updated user ID {UserId} ('{Username}').", id, existingUser.Username);

            return Ok(new
            {
                success = true,
                message = "User updated successfully.",
                user = new UserDto
                {
                    Id = existingUser.Id,
                    FullName = existingUser.FullName,
                    Email = existingUser.Email,
                    MobileNo = existingUser.MobileNo,
                    Username = existingUser.Username,
                    Role = existingUser.Role,
                    CreatedAt = existingUser.CreatedAt
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating user ID {UserId}.", id);
            return StatusCode(500, new { message = "An error occurred while updating the user." });
        }
    }

    /// <summary>
    /// DELETE /api/users/{id} - Deletes a user account by Admin.
    /// </summary>
    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteUser(int id)
    {
        if (id <= 0)
        {
            return BadRequest(new { message = "Invalid user ID." });
        }

        try
        {
            var user = await _userRepository.GetByIdAsync(id);
            if (user == null)
            {
                return NotFound(new { message = "User not found." });
            }

            // Safety check: Prevent deleting sole Admin
            if (string.Equals(user.Role, "Admin", StringComparison.OrdinalIgnoreCase))
            {
                int adminCount = await _userRepository.GetAdminCountAsync();
                if (adminCount <= 1)
                {
                    return BadRequest(new { message = "Cannot delete the only remaining Admin user in the system." });
                }
            }

            bool deleted = await _userRepository.DeleteUserAsync(id);
            if (!deleted)
            {
                return StatusCode(500, new { message = "Failed to delete user from database." });
            }

            _logger.LogInformation("Admin deleted user ID {UserId} ('{Username}').", id, user.Username);

            return Ok(new { success = true, message = $"User '{user.Username}' deleted successfully." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting user ID {UserId}.", id);
            return StatusCode(500, new { message = "An error occurred while deleting the user." });
        }
    }

    /// <summary>
    /// POST /api/users/{id}/reset-password - Resets password for a user account by Admin.
    /// </summary>
    [HttpPost("{id}/reset-password")]
    public async Task<IActionResult> ResetPassword(int id, [FromBody] ResetUserPasswordRequestDto request)
    {
        if (id <= 0 || request == null)
        {
            return BadRequest(new { message = "Invalid password reset request." });
        }

        request.UserId = id;
        if (!ModelState.IsValid)
        {
            return BadRequest(new { message = "Password must be at least 6 characters." });
        }

        try
        {
            var user = await _userRepository.GetByIdAsync(id);
            if (user == null)
            {
                return NotFound(new { message = "User not found." });
            }

            string newHash = _passwordHasher.HashPassword(request.NewPassword);
            bool updated = await _userRepository.UpdatePasswordAsync(id, newHash);

            if (!updated)
            {
                return StatusCode(500, new { message = "Failed to reset password in database." });
            }

            _logger.LogInformation("Admin reset password for user ID {UserId} ('{Username}').", id, user.Username);

            return Ok(new { success = true, message = $"Password for '{user.Username}' reset successfully." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error resetting password for user ID {UserId}.", id);
            return StatusCode(500, new { message = "An error occurred while resetting password." });
        }
    }
}
