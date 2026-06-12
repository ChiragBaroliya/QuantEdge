namespace QuantEdge.Infrastructure.Models;

/// <summary>
/// Represents a Zerodha OAuth session stored in the zerodha_sessions table.
/// </summary>
public record ZerodhaSession
{
    /// <summary>Zerodha API key — primary key.</summary>
    public string ApiKey { get; init; } = string.Empty;

    /// <summary>The OAuth access token.</summary>
    public string AccessToken { get; init; } = string.Empty;

    /// <summary>
    /// Whether this session is currently the active token.
    /// Only one session per api_key can be active at a time.
    /// Tokens are activated by <c>ActiveZerodhaTokenWorker</c> after verifying they
    /// were created after 6:00 AM IST on the current trading day.
    /// </summary>
    public bool IsActive { get; init; }

    /// <summary>UTC timestamp when this token was created / last refreshed.</summary>
    public DateTimeOffset CreatedAt { get; init; }
}
