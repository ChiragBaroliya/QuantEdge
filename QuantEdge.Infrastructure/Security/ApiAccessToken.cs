using System;
using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;

namespace QuantEdge.Infrastructure.Security;

/// <summary>Who an API request is from, once its token has been checked.</summary>
public sealed record ApiCaller(int UserId, string Role)
{
    public bool MayActForAnyUser => Role is ApiAccessToken.RoleAdmin or ApiAccessToken.RoleService;
}

/// <summary>
/// Short-lived signed token that proves an API request comes from the Web app: the logged-in user's browser, or the Web
/// server itself. Format: base64url("userId|role|expiryUnixSeconds") + "." + base64url(HMAC-SHA256). The Web app and the
/// API share the signing key (ApiAuth:SigningKey), which never goes in a tracked settings file on a server.
/// </summary>
public static class ApiAccessToken
{
    public const string SigningKeySetting = "ApiAuth:SigningKey";
    public const string EnforceSetting = "ApiAuth:Enforce";
    public const string RoleAdmin = "Admin";
    public const string RoleService = "Service";
    public const int MinSigningKeyLength = 32;

    public static readonly TimeSpan UserLifetime = TimeSpan.FromHours(12);
    public static readonly TimeSpan ServiceLifetime = TimeSpan.FromMinutes(5);

    /// <summary>The configured signing key, or null when it is missing or too short to be safe.</summary>
    public static string? SigningKey(IConfiguration configuration)
    {
        string? key = configuration[SigningKeySetting];
        return !string.IsNullOrWhiteSpace(key) && key.Length >= MinSigningKeyLength ? key : null;
    }

    public static string Create(string signingKey, int userId, string role, TimeSpan lifetime, DateTime? nowUtc = null)
    {
        long expiry = new DateTimeOffset(nowUtc ?? DateTime.UtcNow, TimeSpan.Zero).Add(lifetime).ToUnixTimeSeconds();
        string payload = Encode(Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{userId}|{role}|{expiry}")));
        return $"{payload}.{Encode(Sign(signingKey, payload))}";
    }

    public static bool TryValidate(string? token, string signingKey, DateTime nowUtc, out ApiCaller? caller)
    {
        caller = null;
        if (string.IsNullOrWhiteSpace(token)) return false;

        string[] parts = token.Split('.');
        if (parts.Length != 2) return false;

        try
        {
            if (!CryptographicOperations.FixedTimeEquals(Decode(parts[1]), Sign(signingKey, parts[0]))) return false;

            string[] fields = Encoding.UTF8.GetString(Decode(parts[0])).Split('|');
            if (fields.Length != 3
                || !int.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int userId)
                || !long.TryParse(fields[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out long expiry))
            {
                return false;
            }

            if (new DateTimeOffset(nowUtc, TimeSpan.Zero).ToUnixTimeSeconds() >= expiry) return false;

            caller = new ApiCaller(userId, fields[1]);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>A token for this logged-in user; empty when the user isn't logged in or no signing key is configured.</summary>
    public static string ForUser(IConfiguration configuration, ClaimsPrincipal user)
    {
        string? key = SigningKey(configuration);
        if (key == null || user.Identity?.IsAuthenticated != true) return string.Empty;
        if (!int.TryParse(user.FindFirst(ClaimTypes.NameIdentifier)?.Value, out int userId)) return string.Empty;
        return Create(key, userId, user.FindFirst(ClaimTypes.Role)?.Value ?? "User", UserLifetime);
    }

    /// <summary>A token for the Web server's own calls to the API; empty when no signing key is configured.</summary>
    public static string ForService(IConfiguration configuration)
    {
        string? key = SigningKey(configuration);
        return key == null ? string.Empty : Create(key, 0, RoleService, ServiceLifetime);
    }

    private static byte[] Sign(string signingKey, string payload) =>
        HMACSHA256.HashData(Encoding.UTF8.GetBytes(signingKey), Encoding.ASCII.GetBytes(payload));

    private static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Decode(string text)
    {
        string s = text.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }
}
