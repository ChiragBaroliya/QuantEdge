namespace QuantEdge.Infrastructure.Models;

/// <summary>
/// Represents a Zerodha OAuth session stored in the zerodha_sessions table.
/// </summary>
public record ZerodhaSession
{
    /// <summary>User ID who owns this Zerodha session.</summary>
    public int UserId { get; init; } = 1;

    /// <summary>Zerodha Client ID / User ID (e.g. CJC294).</summary>
    public string? ClientId { get; init; }

    /// <summary>Zerodha Account Holder Name (e.g. Neel A Patel).</summary>
    public string? UserName { get; init; }

    /// <summary>Zerodha registered email address.</summary>
    public string? UserEmail { get; init; }

    /// <summary>Zerodha API key.</summary>
    public string ApiKey { get; init; } = string.Empty;

    /// <summary>Zerodha API secret (optional, stored for automated token flows).</summary>
    public string? ApiSecret { get; init; }

    /// <summary>The OAuth access token.</summary>
    public string AccessToken { get; init; } = string.Empty;

    /// <summary>
    /// Whether this session is currently the active token.
    /// Tokens are activated after verifying they were created after 6:00 AM IST on the current trading day.
    /// </summary>
    public bool IsActive { get; init; }

    /// <summary>
    /// Whether Demat Debit and Pledge Instruction (DDPI) is completed in Zerodha Demat Console.
    /// When true, automated CNC Sell orders execute without CDSL TPIN or OTP.
    /// </summary>
    public bool IsDdpiEnabled { get; init; }

    /// <summary>UTC timestamp when this token was created / last refreshed.</summary>
    public DateTimeOffset CreatedAt { get; init; }
}

