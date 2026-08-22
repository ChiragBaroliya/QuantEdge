using Dapper;
using KiteConnect;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using QuantEdge.API.Services;
using QuantEdge.Infrastructure.Configurations;
using QuantEdge.Infrastructure.DTOs;
using QuantEdge.Infrastructure.Persistence;
using QuantEdge.Infrastructure.Persistence.Repositories;
using System;
using System.Data;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using static KiteConnect.Constants.GTT;
using static System.Collections.Specialized.BitVector32;

namespace QuantEdge.API.Controllers;

[ApiController]
[Route("zerodha")]
public class ZerodhaAuthController : ControllerBase
{
    private readonly BrokerConfig _config;
    private readonly IDbConnectionFactory _connectionFactory;
    private readonly IZerodhaSessionRepository _sessionRepository;
    private readonly ILogger<ZerodhaAuthController> _logger;
    private readonly IMemoryCache _cache;
    private readonly string _fallbackWebBaseUrl;

    // Cache key used to share the Web app's return URL across the login → callback round-trip
    private const string ReturnUrlCacheKey = "zerodha_web_return_url";

    public ZerodhaAuthController(
        IOptions<BrokerConfig> config,
        IDbConnectionFactory connectionFactory,
        IZerodhaSessionRepository sessionRepository,
        IMemoryCache cache,
        IConfiguration configuration,
        ILogger<ZerodhaAuthController> logger)
    {
        _config = config?.Value ?? throw new ArgumentNullException(nameof(config));
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
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
            if (string.IsNullOrWhiteSpace(_config.ApiKey))
            {
                return Ok(new { hasActiveToken = false, message = "ApiKey is not configured." });
            }

            // 1. Attempt activation for today's token
            await _sessionRepository.ActivateTokenIfValidAsync(_config.ApiKey, userId);

            // 2. Fetch current active session for this user
            var activeSession = await _sessionRepository.GetActiveSessionAsync(userId);

            TimeZoneInfo indianTimeZone;
            try
            {
                indianTimeZone = TimeZoneInfo.FindSystemTimeZoneById("India Standard Time");
            }
            catch (TimeZoneNotFoundException)
            {
                indianTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata");
            }

            var nowIst = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, indianTimeZone);

            // Cutoff logic: 6:00 AM IST today (or yesterday 6:00 AM if before 6:00 AM IST)
            DateTime todayCutoffIst = nowIst.TimeOfDay < TimeSpan.FromHours(6)
                ? nowIst.Date.AddDays(-1).AddHours(6)
                : nowIst.Date.AddHours(6);

            DateTime nextExpiryIst = todayCutoffIst.AddDays(1);

            if (activeSession != null)
            {
                var createdAtIst = TimeZoneInfo.ConvertTime(activeSession.CreatedAt, indianTimeZone);
                if (createdAtIst >= todayCutoffIst)
                {
                    string maskedToken = activeSession.AccessToken.Length > 12
                        ? activeSession.AccessToken[..8] + "••••••••" + activeSession.AccessToken[^4..]
                        : activeSession.AccessToken[..Math.Min(4, activeSession.AccessToken.Length)] + "••••";

                    return Ok(new
                    {
                        hasActiveToken = true,
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
                userId = userId,
                isDdpiEnabled = activeSession?.IsDdpiEnabled ?? false,
                apiKey = _config.ApiKey,
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
    /// Generates the Zerodha OAuth login URL.
    /// Redirects the user to: https://kite.zerodha.com/connect/login?v=3&api_key=XYZ&state=USER_ID
    /// </summary>
    [HttpGet("login-url")]
    public IActionResult GetLoginUrl([FromQuery] string? returnUrl = null, [FromQuery] int userId = 1)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_config.ApiKey))
            {
                _logger.LogError("ApiKey is not configured in BrokerConfig.");
                return BadRequest("ApiKey is missing in BrokerConfig.");
            }

            if (!string.IsNullOrWhiteSpace(returnUrl))
            {
                _cache.Set(ReturnUrlCacheKey, returnUrl, TimeSpan.FromMinutes(10));
                _logger.LogInformation("Saved Web returnUrl to MemoryCache: {ReturnUrl}", returnUrl);
            }

            // Generate Zerodha OAuth login URL with state = userId
            string statePayload = userId.ToString();
            string loginUrl = $"https://kite.zerodha.com/connect/login?v=3&api_key={Uri.EscapeDataString(_config.ApiKey)}&state={Uri.EscapeDataString(statePayload)}";

            _logger.LogInformation("Generated Zerodha login URL for User {UserId} with API Key: {ApiKey}", userId, _config.ApiKey);
            return Ok(new { loginUrl });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate Zerodha login URL.");
            return StatusCode(500, $"Internal server error: {ex.Message}");
        }
    }

    /// <summary>
    /// Programmatically triggers the full headless login flow to retrieve and persist a new access token.
    /// </summary>
    [HttpPost("headless-login")]
    public async Task<IActionResult> HeadlessLogin()
    {
        _logger.LogInformation("Initiating programmatic headless login flow...");

        try
        {
            if (string.IsNullOrWhiteSpace(_config.ApiKey) || string.IsNullOrWhiteSpace(_config.ApiSecret))
            {
                return BadRequest("Zerodha ApiKey or ApiSecret is not configured.");
            }

            if (string.IsNullOrWhiteSpace(_config.UserId) || string.IsNullOrWhiteSpace(_config.Password) || string.IsNullOrWhiteSpace(_config.TotpSecret))
            {
                return BadRequest("Zerodha UserId, Password, or TotpSecret is not configured in appsettings.json.");
            }

            // Step 1: Run the headless login flow to fetch request token
            var authenticator = new ZerodhaHeadlessAuthenticator(
                _config.UserId,
                _config.Password,
                _config.TotpSecret,
                _config.ApiKey
            );

            string requestToken = await authenticator.FetchRequestTokenAsync();
            _logger.LogInformation("Programmatically obtained request_token: {RequestToken}", requestToken);

            // Step 2: Exchange request_token for access_token
            var kite = new Kite(_config.ApiKey);
            User userSession = kite.GenerateSession(requestToken, _config.ApiSecret);
            string accessToken = userSession.AccessToken;

            if (string.IsNullOrWhiteSpace(accessToken))
            {
                throw new InvalidOperationException("Zerodha returned an empty access token.");
            }

            _logger.LogInformation("Successfully programmatically exchanged request_token. Storing access token...");

            // 3. Store in PostgreSQL database using Stored Procedure
            var parameters = new DynamicParameters();
            parameters.Add("p_api_key", _config.ApiKey);
            parameters.Add("p_access_token", accessToken);

            using (var conn = _connectionFactory.CreateConnection())
            {
                await conn.ExecuteAsync(
                    "sp_upsert_zerodha_session",
                    parameters,
                    commandType: CommandType.StoredProcedure
                );
            }

            // Immediately activate token in DB if valid for today
            await _sessionRepository.ActivateTokenIfValidAsync(_config.ApiKey);

            // 4. Persist to appsettings.json dynamically in the API & Worker folders
            UpdateAppsettingsInAllPaths(accessToken);

            _logger.LogInformation("Zerodha Access Token successfully stored and configuration updated via headless login.");

            return Ok(new
            {
                Message = "Headless login successful! Zerodha Access Token has been created and configured.",
                ApiKey = _config.ApiKey,
                AccessToken = accessToken,
                UserName = userSession.UserName,
                Email = userSession.Email
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred during Zerodha programmatic headless login.");
            return StatusCode(500, $"Failed to perform programmatic headless login: {ex.Message}");
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

        int userId = 1;
        if (!string.IsNullOrWhiteSpace(state) && int.TryParse(state, out int parsedUid))
        {
            userId = parsedUid;
        }

        _logger.LogInformation("Received request_token from Zerodha for User {UserId}. Initiating token exchange...", userId);

        try
        {
            if (string.IsNullOrWhiteSpace(_config.ApiKey) || string.IsNullOrWhiteSpace(_config.ApiSecret))
            {
                return BadRequest("Zerodha ApiKey or ApiSecret is not configured.");
            }

            // Initialize Kite client and exchange request_token for access_token
            var kite = new Kite(_config.ApiKey);
            User userSession = kite.GenerateSession(requestToken, _config.ApiSecret);
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
                _config.ApiKey, 
                _config.ApiSecret, 
                accessToken,
                clientId: userSession.UserId,
                userName: userSession.UserName,
                userEmail: userSession.Email
            );

            // 2. Immediately activate token in DB if valid for today
            await _sessionRepository.ActivateTokenIfValidAsync(_config.ApiKey, userId);

            // 3. Persist to appsettings.json dynamically
            UpdateAppsettingsInAllPaths(accessToken);

            _logger.LogInformation("Zerodha Access Token successfully stored for User {UserId} (Client: {ClientId}).", userId, userSession.UserId);

            // 4. Resolve the Web UI base URL:
            //    - First priority: URL cached during login-url call (dynamic, works on any port)
            //    - Fallback: WebBaseUrl from appsettings.json
            string webBase = _cache.TryGetValue(ReturnUrlCacheKey, out string? cached) && !string.IsNullOrWhiteSpace(cached)
                ? cached
                : _fallbackWebBaseUrl;

            _logger.LogInformation("Redirecting to Web UI at: {WebBase}/RealTrading", webBase);

            var redirectUrl = $"{webBase}/RealTrading?connected=true&message={Uri.EscapeDataString("⚡ Zerodha Account Connected Successfully! Daily Access Token is now ACTIVE.")}";
            return Redirect(redirectUrl);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred during Zerodha token callback exchange.");

            string webBase = _cache.TryGetValue(ReturnUrlCacheKey, out string? cached) && !string.IsNullOrWhiteSpace(cached)
                ? cached
                : _fallbackWebBaseUrl;

            var errorUrl = $"{webBase}/Token/Callback" +
                $"?success=false" +
                $"&message={Uri.EscapeDataString($"Token exchange failed: {ex.Message}")}";

            return Redirect(errorUrl);
        }
    }

    private void UpdateAppsettingsInAllPaths(string accessToken)
    {
        // Try resolving various directories to find and update config files
        string currentDir = Directory.GetCurrentDirectory();
        
        string[] appsettingsPaths = new[]
        {
            // API current directory files
            Path.Combine(currentDir, "appsettings.json"),
            Path.Combine(currentDir, "appsettings.Development.json"),
            // API output directory files
            Path.Combine(AppContext.BaseDirectory, "appsettings.json"),
            Path.Combine(AppContext.BaseDirectory, "appsettings.Development.json"),
            // Worker project source files
            Path.Combine(currentDir, "..", "QuantEdge.Worker", "appsettings.json"),
            Path.Combine(currentDir, "..", "QuantEdge.Worker", "appsettings.Development.json"),
            // Worker output directories (if they are compiled nearby)
            Path.Combine(currentDir, "..", "QuantEdge.Worker", "bin", "Debug", "net10.0", "appsettings.json"),
            Path.Combine(currentDir, "..", "QuantEdge.Worker", "bin", "Debug", "net10.0", "appsettings.Development.json")
        };

        foreach (var path in appsettingsPaths)
        {
            try
            {
                string fullPath = Path.GetFullPath(path);
                if (System.IO.File.Exists(fullPath))
                {
                    _logger.LogInformation("Updating config file at: {Path}", fullPath);
                    string json = System.IO.File.ReadAllText(fullPath);
                    var rootNode = JsonNode.Parse(json);
                    
                    if (rootNode != null)
                    {
                        var brokerConfig = rootNode["MarketDataSettings"]?["BrokerConfig"];
                        if (brokerConfig != null)
                        {
                            brokerConfig["AccessToken"] = accessToken;
                            var options = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
                            System.IO.File.WriteAllText(fullPath, rootNode.ToJsonString(options));
                            _logger.LogInformation("Successfully updated: {Path}", fullPath);
                        }
                    }
                }

            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to update appsettings JSON file at {Path}", path);
            }
        }
    }
}
