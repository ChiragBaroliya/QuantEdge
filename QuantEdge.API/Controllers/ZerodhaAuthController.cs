using Dapper;
using KiteConnect;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using QuantEdge.API.Services;
using QuantEdge.Infrastructure.Configurations;
using QuantEdge.Infrastructure.Persistence;
using System;
using System.Data;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using static KiteConnect.Constants.GTT;
using static System.Collections.Specialized.BitVector32;

namespace QuantEdge.API.Controllers;

[ApiController]
[Route("api/zerodha")]
public class ZerodhaAuthController : ControllerBase
{
    private readonly BrokerConfig _config;
    private readonly IDbConnectionFactory _connectionFactory;
    private readonly ILogger<ZerodhaAuthController> _logger;
    private readonly IMemoryCache _cache;
    private readonly string _fallbackWebBaseUrl;

    // Cache key used to share the Web app's return URL across the login → callback round-trip
    private const string ReturnUrlCacheKey = "zerodha_web_return_url";

    public ZerodhaAuthController(
        IOptions<BrokerConfig> config,
        IDbConnectionFactory connectionFactory,
        IMemoryCache cache,
        IConfiguration configuration,
        ILogger<ZerodhaAuthController> logger)
    {
        _config = config?.Value ?? throw new ArgumentNullException(nameof(config));
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        // Fallback URL used only if the Web project did not pass a returnUrl
        _fallbackWebBaseUrl = configuration["WebBaseUrl"] ?? "https://localhost:7031";
    }

    /// <summary>
    /// Generates and returns the official Zerodha login URL.
    /// Accepts an optional <paramref name="returnUrl"/> (the Web app's origin) so the Callback
    /// knows which port to redirect back to — no hardcoding required.
    /// </summary>
    [HttpGet("login-url")]
    public IActionResult GetLoginUrl([FromQuery] string? returnUrl)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_config.ApiKey))
            {
                return BadRequest("Zerodha ApiKey is not configured.");
            }

            // Cache the caller's return URL for up to 10 minutes so Callback can use it.
            // Only the scheme+host+port is needed (e.g. "https://localhost:7031").
            if (!string.IsNullOrWhiteSpace(returnUrl))
            {
                _cache.Set(ReturnUrlCacheKey, returnUrl.TrimEnd('/'), TimeSpan.FromMinutes(10));
                _logger.LogInformation("Cached Web returnUrl: {ReturnUrl}", returnUrl);
            }

            var kite = new Kite(_config.ApiKey);
            string loginUrl = kite.GetLoginURL();

            _logger.LogInformation("Generated Zerodha login URL for API Key: {ApiKey}", _config.ApiKey);
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
    /// and persisting it securely.
    /// </summary>
    [HttpGet("callback")]
    public async Task<IActionResult> Callback([FromQuery(Name = "request_token")] string requestToken,
        [FromQuery(Name = "action")] string action,
        [FromQuery(Name = "type")] string type,
        [FromQuery(Name = "status")] string status)
    {
        if (string.IsNullOrWhiteSpace(requestToken))
        {
            return BadRequest("Missing required query parameter 'request_token'.");
        }

        _logger.LogInformation("Received request_token from Zerodha. Initiating token exchange...");

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

            _logger.LogInformation("Successfully exchanged request_token. Storing access token...");

            // 1. Store in PostgreSQL database using Stored Procedure
            var callbackParams = new DynamicParameters();
            callbackParams.Add("p_api_key", _config.ApiKey);
            callbackParams.Add("p_access_token", accessToken);

            using (var conn = _connectionFactory.CreateConnection())
            {
                await conn.ExecuteAsync(
                    "sp_upsert_zerodha_session",
                    callbackParams,
                    commandType: CommandType.StoredProcedure
                );
            }

            // 2. Persist to appsettings.json dynamically in the API & Worker folders
            UpdateAppsettingsInAllPaths(accessToken);

            _logger.LogInformation("Zerodha Access Token successfully stored and configuration updated.");

            // 3. Resolve the Web UI base URL:
            //    - First priority: URL cached during login-url call (dynamic, works on any port)
            //    - Fallback: WebBaseUrl from appsettings.json
            string webBase = _cache.TryGetValue(ReturnUrlCacheKey, out string? cached) && !string.IsNullOrWhiteSpace(cached)
                ? cached
                : _fallbackWebBaseUrl;

            _logger.LogInformation("Redirecting to Web UI at: {WebBase}/Token/Callback", webBase);

            var redirectUrl = $"{webBase}/Token/Callback" +
                $"?success=true" +
                $"&message={Uri.EscapeDataString("Authentication successful! Zerodha Access Token has been created and saved to database.")}" +
                $"&apiKey={Uri.EscapeDataString(_config.ApiKey)}" +
                $"&accessToken={Uri.EscapeDataString(accessToken)}" +
                $"&userName={Uri.EscapeDataString(userSession.UserName ?? string.Empty)}" +
                $"&email={Uri.EscapeDataString(userSession.Email ?? string.Empty)}";

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
