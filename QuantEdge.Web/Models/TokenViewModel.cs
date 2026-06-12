namespace QuantEdge.Web.Models;

/// <summary>
/// View model for the Token management dashboard.
/// </summary>
public class TokenViewModel
{
    /// <summary>
    /// Status message to display (success, error, or informational).
    /// </summary>
    public string? StatusMessage { get; set; }

    /// <summary>
    /// CSS class for the status alert (success, danger, info).
    /// </summary>
    public string StatusType { get; set; } = "info";

    /// <summary>
    /// Whether the last token creation was successful.
    /// </summary>
    public bool IsSuccess { get; set; }

    /// <summary>
    /// The Zerodha API key currently configured.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// The masked access token (for display only).
    /// </summary>
    public string? AccessTokenMasked { get; set; }

    /// <summary>
    /// User name returned from Zerodha session exchange.
    /// </summary>
    public string? UserName { get; set; }

    /// <summary>
    /// Email returned from Zerodha session exchange.
    /// </summary>
    public string? Email { get; set; }
}
