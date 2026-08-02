using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using QuantEdge.Domain.Entities;
using QuantEdge.Infrastructure.DTOs;
using QuantEdge.Infrastructure.Persistence.Repositories;
using QuantEdge.Infrastructure.Services;

namespace QuantEdge.API.Controllers;

[ApiController]
[Route("[controller]")]
public class AuthController : ControllerBase
{
    private readonly IUserRepository _userRepository;
    private readonly IPasswordHasher _passwordHasher;
    private readonly ILogger<AuthController> _logger;

    public AuthController(
        IUserRepository userRepository,
        IPasswordHasher passwordHasher,
        ILogger<AuthController> logger)
    {
        _userRepository = userRepository ?? throw new ArgumentNullException(nameof(userRepository));
        _passwordHasher = passwordHasher ?? throw new ArgumentNullException(nameof(passwordHasher));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Authenticates a user using Username and Password.
    /// </summary>
    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequestDto request)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(new AuthResultDto
            {
                Success = false,
                Message = "Invalid login payload."
            });
        }

        try
        {
            var user = await _userRepository.GetByUsernameOrEmailAsync(request.Username);
            if (user == null)
            {
                _logger.LogWarning("Failed login attempt for username '{Username}': User not found.", request.Username);
                return Unauthorized(new AuthResultDto
                {
                    Success = false,
                    Message = "Invalid Username or Password."
                });
            }

            bool isValidPassword = _passwordHasher.VerifyPassword(request.Password, user.PasswordHash);
            if (!isValidPassword)
            {
                _logger.LogWarning("Failed login attempt for user '{Username}': Invalid password.", user.Username);
                return Unauthorized(new AuthResultDto
                {
                    Success = false,
                    Message = "Invalid Username or Password."
                });
            }

            _logger.LogInformation("User '{Username}' ({Role}) logged in successfully.", user.Username, user.Role);

            return Ok(new AuthResultDto
            {
                Success = true,
                Message = "Login successful.",
                User = new UserDto
                {
                    Id = user.Id,
                    FullName = user.FullName,
                    Email = user.Email,
                    MobileNo = user.MobileNo,
                    Username = user.Username,
                    Role = user.Role,
                    CreatedAt = user.CreatedAt
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred during login for '{Username}'.", request.Username);
            return StatusCode(500, new AuthResultDto
            {
                Success = false,
                Message = "An internal server error occurred during authentication."
            });
        }
    }

    /// <summary>
    /// Registers a new user with Full Name, Username, and Password. Role is defaulted strictly to 'User'.
    /// </summary>
    [HttpPost("register")]
    public async Task<IActionResult> Register([FromBody] RegisterRequestDto request)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(new AuthResultDto
            {
                Success = false,
                Message = "Invalid registration parameters."
            });
        }

        try
        {
            // Unique Username Check
            if (await _userRepository.ExistsByUsernameAsync(request.Username))
            {
                return BadRequest(new AuthResultDto
                {
                    Success = false,
                    Message = "Username is already taken."
                });
            }

            // Optional Unique Email Check if email is provided
            if (!string.IsNullOrWhiteSpace(request.Email) && await _userRepository.ExistsByEmailAsync(request.Email))
            {
                return BadRequest(new AuthResultDto
                {
                    Success = false,
                    Message = "Email address is already registered."
                });
            }

            var newUser = new AppUser
            {
                FullName = request.FullName.Trim(),
                Email = string.IsNullOrWhiteSpace(request.Email) ? null : request.Email.Trim().ToLowerInvariant(),
                MobileNo = string.IsNullOrWhiteSpace(request.MobileNo) ? null : request.MobileNo.Trim(),
                Username = request.Username.Trim(),
                PasswordHash = _passwordHasher.HashPassword(request.Password),
                Role = "User", // Default User role for registration form
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            int userId = await _userRepository.CreateUserAsync(newUser);
            newUser.Id = userId;

            _logger.LogInformation("New user '{Username}' registered successfully with ID {UserId} and default User role.", newUser.Username, userId);

            return Ok(new AuthResultDto
            {
                Success = true,
                Message = "Registration successful. You can now log in.",
                User = new UserDto
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
            _logger.LogError(ex, "Error occurred during registration for '{Username}'.", request.Username);
            return StatusCode(500, new AuthResultDto
            {
                Success = false,
                Message = "An internal server error occurred during user registration."
            });
        }
    }

    /// <summary>
    /// Changes the password of an existing user after verifying current password.
    /// </summary>
    [HttpPost("change-password")]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequestDto request)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(new AuthResultDto
            {
                Success = false,
                Message = "Invalid password change parameters."
            });
        }

        try
        {
            var user = await _userRepository.GetByIdAsync(request.UserId);
            if (user == null)
            {
                _logger.LogWarning("Change password failed: User with ID {UserId} not found.", request.UserId);
                return NotFound(new AuthResultDto
                {
                    Success = false,
                    Message = "User not found."
                });
            }

            bool isCurrentPasswordValid = _passwordHasher.VerifyPassword(request.CurrentPassword, user.PasswordHash);
            if (!isCurrentPasswordValid)
            {
                _logger.LogWarning("Change password failed for user '{Username}': Current password incorrect.", user.Username);
                return BadRequest(new AuthResultDto
                {
                    Success = false,
                    Message = "Current password is incorrect."
                });
            }

            string newPasswordHash = _passwordHasher.HashPassword(request.NewPassword);
            bool isUpdated = await _userRepository.UpdatePasswordAsync(request.UserId, newPasswordHash);

            if (!isUpdated)
            {
                _logger.LogError("Failed to update password hash in database for user ID {UserId}.", request.UserId);
                return StatusCode(500, new AuthResultDto
                {
                    Success = false,
                    Message = "Failed to update password in database."
                });
            }

            _logger.LogInformation("Password for user '{Username}' (ID: {UserId}) changed successfully.", user.Username, request.UserId);

            return Ok(new AuthResultDto
            {
                Success = true,
                Message = "Password changed successfully."
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while changing password for user ID {UserId}.", request.UserId);
            return StatusCode(500, new AuthResultDto
            {
                Success = false,
                Message = "An internal server error occurred while changing password."
            });
        }
    }
}
