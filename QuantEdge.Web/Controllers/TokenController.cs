using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using QuantEdge.Web.Models;

namespace QuantEdge.Web.Controllers;

/// <summary>
/// Controller managing the Zerodha OAuth token creation flow.
/// </summary>
public class TokenController : Controller
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<TokenController> _logger;
    private readonly string _apiBaseUrl;

    public TokenController(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<TokenController> logger)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _apiBaseUrl = configuration["ApiBaseUrl"] ?? "https://localhost:44370";
    }

    /// <summary>
    /// GET /Token — Renders the main token management dashboard.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Index()
    {
        var vm = new TokenViewModel
        {
            StatusMessage = "Checking token status...",
            StatusType = "info"
        };

        try
        {
            var client = _httpClientFactory.CreateClient("QuantEdgeApi");
            var response = await client.GetAsync("/api/zerodha/session-status");

            if (response.IsSuccessStatusCode)
            {
                string json = await response.Content.ReadAsStringAsync();
                var node = JsonNode.Parse(json);
                bool hasActiveToken = node?["hasActiveToken"]?.GetValue<bool>() ?? false;

                vm.HasActiveToken = hasActiveToken;
                vm.ApiKey = node?["apiKey"]?.ToString();
                vm.AccessTokenMasked = node?["accessTokenMasked"]?.ToString();
                vm.CreatedAtIst = node?["createdAtIst"]?.ToString();
                vm.ExpiresAtIst = node?["expiresAtIst"]?.ToString();

                if (hasActiveToken)
                {
                    vm.IsSuccess = true;
                    vm.StatusType = "success";
                    vm.StatusMessage = "Zerodha access token is ACTIVE and valid for today.";
                }
                else
                {
                    vm.IsSuccess = false;
                    vm.StatusType = "info";
                    vm.StatusMessage = "No active token for today. Click \"Create Token\" to authenticate.";
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to query Zerodha session status from API.");
            vm.StatusMessage = "Unable to connect to API server. Click \"Create Token\" to authenticate manually.";
            vm.StatusType = "warning";
        }

        // Carry forward any TempData from a previous redirect (e.g. after callback)
        if (TempData.ContainsKey("StatusMessage"))
        {
            vm.StatusMessage = TempData["StatusMessage"]?.ToString();
            vm.StatusType = TempData["StatusType"]?.ToString() ?? "info";
            vm.IsSuccess = vm.StatusType == "success";
            if (vm.IsSuccess)
            {
                vm.HasActiveToken = true;
            }
            if (TempData.ContainsKey("UserName")) vm.UserName = TempData["UserName"]?.ToString();
            if (TempData.ContainsKey("Email")) vm.Email = TempData["Email"]?.ToString();
            if (TempData.ContainsKey("AccessTokenMasked")) vm.AccessTokenMasked = TempData["AccessTokenMasked"]?.ToString();
            if (TempData.ContainsKey("ApiKey")) vm.ApiKey = TempData["ApiKey"]?.ToString();
        }

        return View(vm);
    }

    /// <summary>
    /// GET /Token/CreateToken
    /// Calls the QuantEdge API to retrieve the Zerodha login URL,
    /// then redirects the browser directly to Zerodha for OAuth login.
    /// Passes this app's own origin URL so the API knows where to redirect back.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> CreateToken()
    {
        _logger.LogInformation("Initiating Zerodha OAuth flow — fetching login URL from API...");

        try
        {
            // Build this Web app's own base URL dynamically from the current request.
            // e.g.  https://localhost:7031  or  https://localhost:44370  — whatever VS assigned.
            string webReturnUrl = $"{Request.Scheme}://{Request.Host}";

            var client = _httpClientFactory.CreateClient("QuantEdgeApi");

            // Pass our returnUrl so the API caches it and uses it in the Zerodha callback redirect
            var response = await client.GetAsync($"/api/zerodha/login-url?returnUrl={Uri.EscapeDataString(webReturnUrl)}");

            if (!response.IsSuccessStatusCode)
            {
                string errorBody = await response.Content.ReadAsStringAsync();
                _logger.LogError("Failed to retrieve login URL. Status: {Status}. Body: {Body}", response.StatusCode, errorBody);

                TempData["StatusMessage"] = $"Failed to get login URL from API. HTTP {(int)response.StatusCode}: {errorBody}";
                TempData["StatusType"] = "danger";
                return RedirectToAction(nameof(Index));
            }

            string json = await response.Content.ReadAsStringAsync();
            var loginUrlNode = JsonNode.Parse(json)?["loginUrl"]?.ToString();

            if (string.IsNullOrWhiteSpace(loginUrlNode))
            {
                _logger.LogError("API returned empty loginUrl. Response: {Json}", json);
                TempData["StatusMessage"] = "API returned an empty login URL. Check your API Key configuration.";
                TempData["StatusType"] = "danger";
                return RedirectToAction(nameof(Index));
            }

            _logger.LogInformation("Web returnUrl sent to API: {ReturnUrl}. Redirecting to Zerodha: {LoginUrl}", webReturnUrl, loginUrlNode);

            // Redirect browser to Zerodha's OAuth login page.
            // After login + 2FA, Zerodha calls: https://localhost:44369/api/zerodha/callback?request_token=XYZ
            // The API then redirects to: {webReturnUrl}/Token/Callback?success=true&...
            return Redirect(loginUrlNode);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception occurred while retrieving Zerodha login URL.");
            TempData["StatusMessage"] = $"An error occurred: {ex.Message}. Is QuantEdge.API running on {_apiBaseUrl}?";
            TempData["StatusType"] = "danger";
            return RedirectToAction(nameof(Index));
        }
    }


    /// <summary>
    /// GET /Token/Callback?success=true&message=...&apiKey=...&accessToken=...&userName=...&email=...
    /// Called by QuantEdge.API redirect after successful/failed Zerodha token exchange.
    /// Renders the result in the Web UI instead of raw JSON.
    /// </summary>
    [HttpGet]
    public IActionResult Callback(
        [FromQuery] bool success,
        [FromQuery] string? message,
        [FromQuery] string? apiKey,
        [FromQuery] string? accessToken,
        [FromQuery] string? userName,
        [FromQuery] string? email)
    {
        _logger.LogInformation("Received Zerodha callback in Web UI. Success: {Success}, User: {User}", success, userName);

        // Mask the token — show only first 8 and last 4 characters for security
        string? maskedToken = null;
        if (!string.IsNullOrWhiteSpace(accessToken) && accessToken.Length > 12)
        {
            maskedToken = accessToken[..8] + "••••••••" + accessToken[^4..];
        }
        else if (!string.IsNullOrWhiteSpace(accessToken))
        {
            maskedToken = accessToken[..Math.Min(4, accessToken.Length)] + "••••";
        }

        var vm = new TokenViewModel
        {
            IsSuccess = success,
            StatusType = success ? "success" : "danger",
            StatusMessage = message ?? (success
                ? "Zerodha Access Token has been created and saved to the database successfully!"
                : "Token creation failed. Please try again."),
            ApiKey = apiKey,
            AccessTokenMasked = maskedToken,
            UserName = userName,
            Email = email
        };

        return View(vm);
    }

    /// <summary>
    /// GET /Token/Success — Alternative success page (for programmatic/headless flow).
    /// </summary>
    [HttpGet]
    public IActionResult Success(
        [FromQuery] string? userName,
        [FromQuery] string? email,
        [FromQuery] string? apiKey,
        [FromQuery] string? message)
    {
        var vm = new TokenViewModel
        {
            IsSuccess = true,
            StatusType = "success",
            StatusMessage = message ?? "Zerodha Access Token successfully created and saved to database!",
            UserName = userName,
            Email = email,
            ApiKey = apiKey
        };
        return View("Callback", vm);
    }
}
