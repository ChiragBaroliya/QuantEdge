using KiteConnect;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using QuantEdge.Infrastructure.DTOs;
using QuantEdge.Infrastructure.Persistence.Repositories;
using System;
using System.Threading.Tasks;

namespace QuantEdge.API.Controllers;

/// <summary>
/// Zerodha OAuth for each QuantEdge user. Every user saves their own Kite Connect API key/secret (stored in
/// zerodha_sessions, never in appsettings) and logs in with it; the resulting daily token is stored on their own row.
/// </summary>
[ApiController]
[Route("zerodha")]
public class ZerodhaAuthController : ControllerBase
{
    private readonly IZerodhaSessionRepository _sessionRepository;
    private readonly ILogger<ZerodhaAuthController> _logger;
    private readonly IMemoryCache _cache;
    private readonly string _fallbackWebBaseUrl;

    // Cache key used to share the Web app's return URL across the login → callback round-trip
    private const string ReturnUrlCacheKey = "zerodha_web_return_url";

    public ZerodhaAuthController(
        IZerodhaSessionRepository sessionRepository,
        IMemoryCache cache,
        IConfiguration configuration,
        ILogger<ZerodhaAuthController> logger)
    {
        _sessionRepository = sessionRepository ?? throw new ArgumentNullException(nameof(sessionRepository));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        // Fallback URL used only if the Web project did not pass a returnUrl
        _fallbackWebBaseUrl = configuration["WebBaseUrl"] ?? "https://localhost:7031";
    }

    /// <summary>
    /// Checks if a valid Zerodha session token exists for today (created after 6:00 AM IST cutoff).
    /// </summary>
    [HttpGet("session-status")]
    public async Task<IActionResult> GetSessionStatus([FromQuery] int userId = 1)
    {
        try
        {
            var activeSession = await _sessionRepository.GetActiveSessionAsync(userId);
            if (activeSession == null || string.IsNullOrWhiteSpace(activeSession.ApiKey))
            {
                return Ok(new
                {
                    hasActiveToken = false,
                    hasCredentials = false,
                    userId = userId,
                    message = "Zerodha API Key / Secret not saved yet. Save your Kite Connect credentials first."
                });
            }

            // Attempt activation for today's token, then re-read the session
            if (!string.IsNullOrWhiteSpace(activeSession.AccessToken) && !activeSession.IsActive)
            {
                await _sessionRepository.ActivateTokenIfValidAsync(userId);
                activeSession = await _sessionRepository.GetActiveSessionAsync(userId) ?? activeSession;
            }

            TimeZoneInfo indianTimeZone;
            try
            {
                indianTimeZone = TimeZoneInfo.FindSystemTimeZoneById("India Standard Time");
            }
            catch (TimeZoneNotFoundException)
            {
                indianTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata");
            }

            var nowIst = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, indianTimeZone);

            // Cutoff logic: 6:00 AM IST today (or yesterday 6:00 AM if before 6:00 AM IST).
            // Kept as DateTimeOffset (with an explicit IST offset) throughout - comparing it
            // against a Kind-Unspecified DateTime relies on the implicit DateTime->DateTimeOffset
            // conversion, which stamps the DateTime with the host machine's LOCAL timezone offset
            // rather than IST, silently shifting the cutoff on a UTC-hosted server.
            DateTimeOffset todayCutoffIst = nowIst.TimeOfDay < TimeSpan.FromHours(6)
                ? new DateTimeOffset(nowIst.Date, nowIst.Offset).AddDays(-1).AddHours(6)
                : new DateTimeOffset(nowIst.Date, nowIst.Offset).AddHours(6);

            DateTimeOffset nextExpiryIst = todayCutoffIst.AddDays(1);

            bool hasApiSecret = !string.IsNullOrWhiteSpace(activeSession.ApiSecret);
            string? accessToken = activeSession.AccessToken;
            if (!string.IsNullOrWhiteSpace(accessToken))
            {
                var createdAtIst = TimeZoneInfo.ConvertTime(activeSession.CreatedAt, indianTimeZone);
                if (createdAtIst >= todayCutoffIst)
                {
                    string maskedToken = accessToken.Length > 12
                        ? accessToken[..8] + "••••••••" + accessToken[^4..]
                        : accessToken[..Math.Min(4, accessToken.Length)] + "••••";

                    return Ok(new
                    {
                        hasActiveToken = true,
                        hasCredentials = true,
                        hasApiSecret = hasApiSecret,
                        userId = activeSession.UserId,
                        clientId = activeSession.ClientId ?? "N/A",
                        userName = activeSession.UserName,
                        userEmail = activeSession.UserEmail,
                        isDdpiEnabled = activeSession.IsDdpiEnabled,
                        apiKey = activeSession.ApiKey,
                        accessTokenMasked = maskedToken,
                        createdAtIst = createdAtIst.ToString("dd-MMM-yyyy hh:mm:ss tt"),
                        expiresAtIst = nextExpiryIst.ToString("dd-MMM-yyyy hh:mm:ss tt"),
                        message = "Valid Zerodha access token active for today."
                    });
                }
            }

            return Ok(new
            {
                hasActiveToken = false,
                hasCredentials = true,
                hasApiSecret = hasApiSecret,
                userId = userId,
                isDdpiEnabled = activeSession.IsDdpiEnabled,
                apiKey = activeSession.ApiKey,
                expiresAtIst = nextExpiryIst.ToString("dd-MMM-yyyy hh:mm:ss tt"),
                message = "No active session for today. Token expired or not created."
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking Zerodha session status.");
            return StatusCode(500, new { hasActiveToken = false, message = ex.Message });
        }
    }

    /// <summary>
    /// Updates DDPI status for a specific user.
    /// </summary>
    [HttpPost("ddpi-status")]
    public async Task<IActionResult> UpdateDdpiStatus([FromBody] UpdateDdpiDto request)
    {
        try
        {
            int targetUserId = request.UserId > 0 ? request.UserId : 1;
            await _sessionRepository.UpdateDdpiStatusAsync(targetUserId, request.IsDdpiEnabled);
            _logger.LogInformation("Updated DDPI status for User {UserId} to {Status}", targetUserId, request.IsDdpiEnabled);
            return Ok(new { success = true, userId = targetUserId, isDdpiEnabled = request.IsDdpiEnabled });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update DDPI status.");
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }

    /// <summary>
    /// Saves the user's own Kite Connect API key and secret (from developers.kite.trade). A blank secret keeps the
    /// stored one; a new API key clears the user's token, so they connect Zerodha again.
    /// </summary>
    [HttpPost("credentials")]
    public async Task<IActionResult> SaveCredentials([FromBody] SaveZerodhaCredentialsDto request)
    {
        try
        {
            int targetUserId = request.UserId > 0 ? request.UserId : 1;
            string apiKey = (request.ApiKey ?? string.Empty).Trim();
            string? apiSecret = string.IsNullOrWhiteSpace(request.ApiSecret) ? null : request.ApiSecret.Trim();

            string? keyError = ValidateCredential(apiKey, "API Key", 6, 50);
            if (keyError != null)
            {
                return BadRequest(new { success = false, message = keyError });
            }

            if (apiSecret != null)
            {
                string? secretError = ValidateCredential(apiSecret, "API Secret", 8, 100);
                if (secretError != null)
                {
                    return BadRequest(new { success = false, message = secretError });
                }
                if (apiSecret == apiKey)
                {
                    return BadRequest(new { success = false, message = "API Secret must be different from the API Key." });
                }
            }

            var existing = await _sessionRepository.GetActiveSessionAsync(targetUserId);
            bool keyChanged = !string.IsNullOrWhiteSpace(existing?.ApiKey) && existing.ApiKey != apiKey;
            if (apiSecret == null && (keyChanged || string.IsNullOrWhiteSpace(existing?.ApiSecret)))
            {
                // A new API Key is a different Kite app, so the stored secret no longer matches it
                return BadRequest(new
                {
                    success = false,
                    message = keyChanged ? "Enter the API Secret of the new Kite Connect app." : "API Secret is required."
                });
            }

            await _sessionRepository.SaveCredentialsAsync(targetUserId, apiKey, apiSecret);
            _logger.LogInformation("Saved Zerodha API credentials for User {UserId} (API Key: {ApiKey}).", targetUserId, apiKey);
            return Ok(new { success = true, userId = targetUserId, apiKey });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save Zerodha API credentials.");
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }

    /// <summary>
    /// Kite Connect API keys and secrets are plain letters/digits. Returns an error message, or <c>null</c> when valid.
    /// Same rules as the Token page (token-manager.js).
    /// </summary>
    private static string? ValidateCredential(string value, string label, int minLength, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return $"{label} is required.";
        if (value.Any(char.IsWhiteSpace)) return $"{label} must not contain spaces.";
        if (!value.All(char.IsAsciiLetterOrDigit)) return $"{label} can contain only letters and numbers.";
        if (value.Length < minLength || value.Length > maxLength)
            return $"{label} must be {minLength}–{maxLength} characters (you entered {value.Length}).";
        return null;
    }

    /// <summary>
    /// Generates the Zerodha OAuth login URL with the user's own API key.
    /// Redirects the user to: https://kite.zerodha.com/connect/login?v=3&api_key=XYZ&state=USER_ID
    /// </summary>
    [HttpGet("login-url")]
    public async Task<IActionResult> GetLoginUrl([FromQuery] string? returnUrl = null, [FromQuery] int userId = 1)
    {
        try
        {
            var session = await _sessionRepository.GetActiveSessionAsync(userId);
            string? apiKey = session?.ApiKey;
            if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(session?.ApiSecret))
            {
                _logger.LogWarning("Zerodha login requested for User {UserId} without saved API credentials.", userId);
                return BadRequest("Zerodha API Key / Secret not saved for this user. Save them on the Token page first.");
            }

            if (!string.IsNullOrWhiteSpace(returnUrl))
            {
                _cache.Set(ReturnUrlCacheKeyFor(userId), returnUrl, TimeSpan.FromMinutes(10));
                _logger.LogInformation("Saved Web returnUrl to MemoryCache: {ReturnUrl}", returnUrl);
            }

            // Generate Zerodha OAuth login URL. Kite only echoes back what is in redirect_params (a URL-encoded query string),
            // so the userId travels as redirect_params=state=<userId> and arrives on the callback as ?state=<userId>.
            string redirectParams = $"state={userId}";
            string loginUrl = $"https://kite.zerodha.com/connect/login?v=3&api_key={Uri.EscapeDataString(apiKey)}&redirect_params={Uri.EscapeDataString(redirectParams)}";

            _logger.LogInformation("Generated Zerodha login URL for User {UserId} with API Key: {ApiKey}", userId, apiKey);
            return Ok(new { loginUrl });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate Zerodha login URL.");
            return StatusCode(500, $"Internal server error: {ex.Message}");
        }
    }

    /// <summary>
    /// Callback endpoint receiving the request_token from Zerodha, exchanging it for an access_token,
    /// and persisting it securely for the specific user.
    /// </summary>
    [HttpGet("callback")]
    public async Task<IActionResult> Callback([FromQuery(Name = "request_token")] string requestToken,
        [FromQuery(Name = "action")] string? action = null,
        [FromQuery(Name = "type")] string? type = null,
        [FromQuery(Name = "status")] string? status = null,
        [FromQuery(Name = "state")] string? state = null)
    {
        if (string.IsNullOrWhiteSpace(requestToken))
        {
            return BadRequest("Missing required query parameter 'request_token'.");
        }

        // Without the state we can't tell whose Kite app issued the request_token; never guess (defaulting to user 1 would
        // exchange it with the admin's key/secret and fail, or worse, overwrite the admin's session).
        if (string.IsNullOrWhiteSpace(state) || !int.TryParse(state, out int userId) || userId <= 0)
        {
            _logger.LogWarning("Zerodha callback received without a valid state (state='{State}').", state);
            string missingStateUrl = $"{ResolveWebReturnUrl(0)}?connected=false&message={Uri.EscapeDataString("Token exchange failed: Zerodha did not return the user state. Please click Create Token again.")}";
            return Redirect(missingStateUrl);
        }

        _logger.LogInformation("Received request_token from Zerodha for User {UserId}. Initiating token exchange...", userId);

        try
        {
            // The request_token was issued for this user's own Kite app, so it is exchanged with that app's key/secret
            var credentials = await _sessionRepository.GetActiveSessionAsync(userId);
            string? apiKey = credentials?.ApiKey;
            string? apiSecret = credentials?.ApiSecret;
            if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(apiSecret))
            {
                throw new InvalidOperationException($"Zerodha API Key / Secret not saved for user {userId}.");
            }

            // Initialize Kite client and exchange request_token for access_token
            var kite = new Kite(apiKey);
            User userSession = kite.GenerateSession(requestToken, apiSecret);
            string accessToken = userSession.AccessToken;

            if (string.IsNullOrWhiteSpace(accessToken))
            {
                throw new InvalidOperationException("Zerodha returned an empty access token.");
            }

            _logger.LogInformation("Successfully exchanged request_token for User {UserId} (Client ID: {ClientId}, Name: {UserName}). Storing access token...", 
                userId, userSession.UserId, userSession.UserName);

            // 1. Store in PostgreSQL database for specific user
            await _sessionRepository.UpsertSessionAsync(
                userId,
                apiKey,
                apiSecret,
                accessToken,
                clientId: userSession.UserId,
                userName: userSession.UserName,
                userEmail: userSession.Email
            );

            // 2. Immediately activate token in DB if valid for today
            await _sessionRepository.ActivateTokenIfValidAsync(userId);

            _logger.LogInformation("Zerodha Access Token successfully stored for User {UserId} (Client: {ClientId}).", userId, userSession.UserId);

            // 3. Back to the Web page the login started from
            string targetUrl = ResolveWebReturnUrl(userId);
            _logger.LogInformation("Redirecting to Web UI at: {TargetUrl}", targetUrl);

            var redirectUrl = $"{targetUrl}?connected=true&message={Uri.EscapeDataString("⚡ Zerodha Account Connected Successfully! Daily Access Token is now ACTIVE.")}";
            return Redirect(redirectUrl);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred during Zerodha token callback exchange.");

            string targetUrl = ResolveWebReturnUrl(userId);
            var errorUrl = $"{targetUrl}?connected=false&message={Uri.EscapeDataString($"Token exchange failed: {ex.Message}")}";
            return Redirect(errorUrl);
        }
    }

    // Per user, so two users logging in at the same time are each sent back to their own page
    private static string ReturnUrlCacheKeyFor(int userId) => $"{ReturnUrlCacheKey}_{userId}";

    /// <summary>
    /// The Web page to return to after the Zerodha login: the returnUrl the login-url call cached for this user
    /// (Token page or Real Trading page), else WebBaseUrl from appsettings. A bare base URL goes to /Token, which every role can open.
    /// </summary>
    private string ResolveWebReturnUrl(int userId)
    {
        string webUrl = _cache.TryGetValue(ReturnUrlCacheKeyFor(userId), out string? cached) && !string.IsNullOrWhiteSpace(cached)
            ? cached
            : _fallbackWebBaseUrl;

        string targetUrl = webUrl.TrimEnd('/');
        if (Uri.TryCreate(targetUrl, UriKind.Absolute, out var uri) && uri.AbsolutePath.Trim('/').Length == 0)
        {
            targetUrl += "/Token";
        }
        return targetUrl;
    }
}
