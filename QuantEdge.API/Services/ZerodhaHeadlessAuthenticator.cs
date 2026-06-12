using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using OtpNet;

namespace QuantEdge.API.Services;

/// <summary>
/// Headless/Programmatic authenticator for Zerodha Kite.
/// Simulates browser interactive login flow using raw HTTP requests and TOTP generation.
/// </summary>
public class ZerodhaHeadlessAuthenticator
{
    private readonly string _userId;
    private readonly string _password;
    private readonly string _totpSecret;
    private readonly string _apiKey;

    public ZerodhaHeadlessAuthenticator(string userId, string password, string totpSecret, string apiKey)
    {
        _userId = userId ?? throw new ArgumentNullException(nameof(userId));
        _password = password ?? throw new ArgumentNullException(nameof(password));
        _totpSecret = totpSecret ?? throw new ArgumentNullException(nameof(totpSecret));
        _apiKey = apiKey ?? throw new ArgumentNullException(nameof(apiKey));
    }

    /// <summary>
    /// Executes the full headless authentication pipeline and returns the request token.
    /// </summary>
    public async Task<string> FetchRequestTokenAsync()
    {
        var cookieContainer = new CookieContainer();
        using var handler = new HttpClientHandler { CookieContainer = cookieContainer, AllowAutoRedirect = false };
        using var client = new HttpClient(handler);
        
        client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

        // --- Step 1: POST to /api/login ---
        var loginUrl = "https://kite.zerodha.com/api/login";
        var loginContent = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            { "user_id", _userId },
            { "password", _password }
        });

        var loginResponse = await client.PostAsync(loginUrl, loginContent);
        var loginJson = await loginResponse.Content.ReadAsStringAsync();
        
        var loginData = JsonNode.Parse(loginJson);
        if (loginData?["status"]?.ToString() != "success")
        {
            throw new InvalidOperationException($"Step 1 Login Failed: {loginData?["message"]}");
        }

        string? requestId = loginData["data"]?["request_id"]?.ToString();
        if (string.IsNullOrEmpty(requestId))
        {
            throw new InvalidOperationException("Step 1 Failed: request_id was not returned.");
        }

        // --- Step 2: Generate TOTP & POST to /api/twofa ---
        string totpCode = GenerateTotpCode(_totpSecret);
        
        var twofaUrl = "https://kite.zerodha.com/api/twofa";
        var twofaContent = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            { "user_id", _userId },
            { "request_id", requestId },
            { "twofa_value", totpCode },
            { "twofa_type", "totp" }
        });

        var twofaResponse = await client.PostAsync(twofaUrl, twofaContent);
        var twofaJson = await twofaResponse.Content.ReadAsStringAsync();
        
        var twofaData = JsonNode.Parse(twofaJson);
        if (twofaData?["status"]?.ToString() != "success")
        {
            throw new InvalidOperationException($"Step 2 Two-Factor Authentication Failed: {twofaData?["message"]}");
        }

        // --- Step 3: GET the developer connection URL on kite.trade to initiate OAuth session ---
        var initiateUrl = $"https://kite.trade/connect/login?api_key={_apiKey}&v=3";
        var initiateResponse = await client.GetAsync(initiateUrl);

        if (initiateResponse.StatusCode != HttpStatusCode.Redirect && initiateResponse.StatusCode != HttpStatusCode.MovedPermanently)
        {
            throw new InvalidOperationException($"Step 3 Initiate Failed: Expected 302 redirect from kite.trade, received {initiateResponse.StatusCode}");
        }

        var authorizedLoginUrl = initiateResponse.Headers.Location?.ToString();
        if (string.IsNullOrEmpty(authorizedLoginUrl))
        {
            throw new InvalidOperationException("Step 3 Initiate Failed: Location header was missing in redirect response from kite.trade.");
        }

        // --- Step 3b: GET the authorized login URL on kite.zerodha.com carrying the OAuth session cookies and sess_id ---
        var connectResponse = await client.GetAsync(authorizedLoginUrl);

        if (connectResponse.StatusCode != HttpStatusCode.Redirect && connectResponse.StatusCode != HttpStatusCode.MovedPermanently)
        {
            var loc = connectResponse.Headers.Location?.ToString();
            throw new InvalidOperationException($"Step 3 Connect Failed: Expected 302 redirect to callback, received {connectResponse.StatusCode}. Location: {loc}");
        }

        // Extract redirect URI from "Location" header
        var redirectUri = connectResponse.Headers.Location?.ToString();
        if (string.IsNullOrEmpty(redirectUri))
        {
            throw new InvalidOperationException("Step 3 Connect Failed: Location header was missing in redirect response from kite.zerodha.com.");
        }

        // --- Step 4: Extract request_token from Redirect URL ---
        var match = Regex.Match(redirectUri, @"request_token=([^&]+)");
        if (!match.Success)
        {
            throw new InvalidOperationException($"Step 4 Failed: Could not extract request_token from URL: {redirectUri}");
        }

        return match.Groups[1].Value;
    }

    /// <summary>
    /// Generates a standard RFC 6238 6-digit TOTP code from a base32 encoded secret.
    /// </summary>
    private string GenerateTotpCode(string base32Secret)
    {
        byte[] secretBytes = Base32Encoding.ToBytes(base32Secret.Trim().Replace(" ", ""));
        var totp = new Totp(secretBytes);
        return totp.ComputeTotp();
    }
}
