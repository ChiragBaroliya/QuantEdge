using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using QuantEdge.Infrastructure.DTOs;

namespace QuantEdge.Web.Controllers;

public class AccountController : Controller
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<AccountController> _logger;

    public AccountController(
        IHttpClientFactory httpClientFactory,
        ILogger<AccountController> logger)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Displays Login View.
    /// </summary>
    [HttpGet]
    [AllowAnonymous]
    public IActionResult Login(string? returnUrl = null)
    {
        if (User.Identity != null && User.Identity.IsAuthenticated)
        {
            return RedirectToAction("Index", "Home");
        }

        ViewData["ReturnUrl"] = returnUrl;
        return View(new LoginRequestDto());
    }

    /// <summary>
    /// Processes user login by calling QuantEdge.API via HttpClient.
    /// </summary>
    [HttpPost]
    [AllowAnonymous]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Login(LoginRequestDto model, string? returnUrl = null)
    {
        ViewData["ReturnUrl"] = returnUrl;

        if (!ModelState.IsValid)
        {
            return View(model);
        }

        try
        {
            var client = _httpClientFactory.CreateClient("QuantEdgeApi");
            var response = await client.PostAsJsonAsync("/api/auth/login", model);

            AuthResultDto? result = null;
            if (response.Content != null)
            {
                result = await response.Content.ReadFromJsonAsync<AuthResultDto>();
            }

            if (!response.IsSuccessStatusCode || result == null || !result.Success || result.User == null)
            {
                string errorMsg = result?.Message ?? "Invalid Username or Password.";
                _logger.LogWarning("Web login failed via API for user '{Username}': {Reason}", model.Username, errorMsg);
                ModelState.AddModelError(string.Empty, errorMsg);
                return View(model);
            }

            var user = result.User;
            var claims = new List<Claim>
            {
                new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
                new Claim(ClaimTypes.Name, user.Username),
                new Claim(ClaimTypes.GivenName, user.FullName),
                new Claim(ClaimTypes.Email, user.Email ?? string.Empty),
                new Claim(ClaimTypes.Role, user.Role),
                new Claim("MobileNo", user.MobileNo ?? string.Empty)
            };

            var claimsIdentity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
            var authProperties = new AuthenticationProperties
            {
                IsPersistent = model.RememberMe,
                ExpiresUtc = model.RememberMe 
                    ? DateTimeOffset.UtcNow.AddDays(365) 
                    : DateTimeOffset.UtcNow.AddHours(8)
            };

            await HttpContext.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                new ClaimsPrincipal(claimsIdentity),
                authProperties);

            _logger.LogInformation("Web user '{Username}' ({Role}) logged in successfully via API.", user.Username, user.Role);

            if (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl))
            {
                return Redirect(returnUrl);
            }

            return RedirectToAction("Index", "Home");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing login via API for '{Username}'.", model.Username);
            ModelState.AddModelError(string.Empty, "Unable to connect to Authentication API service. Please try again.");
            return View(model);
        }
    }

    /// <summary>
    /// Displays Registration View.
    /// </summary>
    [HttpGet]
    [AllowAnonymous]
    public IActionResult Register()
    {
        if (User.Identity != null && User.Identity.IsAuthenticated)
        {
            return RedirectToAction("Index", "Home");
        }

        return View(new RegisterRequestDto());
    }

    /// <summary>
    /// Processes user registration by calling QuantEdge.API via HttpClient.
    /// Default role assigned is strictly 'User'.
    /// </summary>
    [HttpPost]
    [AllowAnonymous]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Register(RegisterRequestDto model)
    {
        if (!ModelState.IsValid)
        {
            return View(model);
        }

        try
        {
            var client = _httpClientFactory.CreateClient("QuantEdgeApi");
            var response = await client.PostAsJsonAsync("/api/auth/register", model);

            AuthResultDto? result = null;
            if (response.Content != null)
            {
                result = await response.Content.ReadFromJsonAsync<AuthResultDto>();
            }

            if (!response.IsSuccessStatusCode || result == null || !result.Success)
            {
                string errorMsg = result?.Message ?? "Registration failed. Please check your parameters.";
                _logger.LogWarning("Web registration failed via API for user '{Username}': {Reason}", model.Username, errorMsg);

                if (errorMsg.Contains("Username", StringComparison.OrdinalIgnoreCase))
                {
                    ModelState.AddModelError("Username", errorMsg);
                }
                else if (errorMsg.Contains("Email", StringComparison.OrdinalIgnoreCase))
                {
                    ModelState.AddModelError("Email", errorMsg);
                }
                else
                {
                    ModelState.AddModelError(string.Empty, errorMsg);
                }

                return View(model);
            }

            _logger.LogInformation("New user '{Username}' successfully registered via API.", model.Username);

            TempData["SuccessMessage"] = "Registration successful! You can now log in with your credentials.";
            return RedirectToAction("Login");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error registering user via API for '{Username}'.", model.Username);
            ModelState.AddModelError(string.Empty, "Unable to connect to Authentication API service. Please try again.");
            return View(model);
        }
    }

    /// <summary>
    /// Handles user logout.
    /// </summary>
    [HttpPost]
    [Authorize]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        _logger.LogInformation("User logged out.");
        return RedirectToAction("Login");
    }

    /// <summary>
    /// Displays Change Password View.
    /// </summary>
    [HttpGet]
    [Authorize]
    public IActionResult ChangePassword()
    {
        return View(new ChangePasswordDto());
    }

    /// <summary>
    /// Processes password change via API request.
    /// </summary>
    [HttpPost]
    [Authorize]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ChangePassword(ChangePasswordDto model)
    {
        if (!ModelState.IsValid)
        {
            return View(model);
        }

        try
        {
            string userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "0";
            if (!int.TryParse(userIdClaim, out int userId) || userId <= 0)
            {
                _logger.LogWarning("Change password failed: Invalid user claim ID '{ClaimValue}'.", userIdClaim);
                ModelState.AddModelError(string.Empty, "Unable to identify current user session. Please sign in again.");
                return View(model);
            }

            var request = new ChangePasswordRequestDto
            {
                UserId = userId,
                CurrentPassword = model.CurrentPassword,
                NewPassword = model.NewPassword
            };

            var client = _httpClientFactory.CreateClient("QuantEdgeApi");
            var response = await client.PostAsJsonAsync("/api/auth/change-password", request);

            AuthResultDto? result = null;
            if (response.Content != null)
            {
                result = await response.Content.ReadFromJsonAsync<AuthResultDto>();
            }

            if (!response.IsSuccessStatusCode || result == null || !result.Success)
            {
                string errorMsg = result?.Message ?? "Failed to change password. Please verify your current password.";
                _logger.LogWarning("Change password failed via API for user ID {UserId}: {Reason}", userId, errorMsg);

                if (errorMsg.Contains("Current password", StringComparison.OrdinalIgnoreCase))
                {
                    ModelState.AddModelError("CurrentPassword", errorMsg);
                }
                else
                {
                    ModelState.AddModelError(string.Empty, errorMsg);
                }

                return View(model);
            }

            _logger.LogInformation("User ID {UserId} changed password successfully via API.", userId);
            TempData["SuccessMessage"] = "Your password has been changed successfully!";
            return RedirectToAction("Index", "Home");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error changing password via API.");
            ModelState.AddModelError(string.Empty, "Unable to connect to Authentication API service. Please try again.");
            return View(model);
        }
    }

    /// <summary>
    /// Displays Access Denied page for unauthorized access attempts.
    /// </summary>
    [HttpGet]
    [AllowAnonymous]
    public IActionResult AccessDenied()
    {
        return View();
    }
}
