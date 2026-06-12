using QuantEdge.Infrastructure.Models;

namespace QuantEdge.Infrastructure.Persistence.Repositories;

/// <summary>
/// Repository contract for Zerodha session token management.
/// </summary>
public interface IZerodhaSessionRepository
{
    /// <summary>
    /// Attempts to activate the token for the given API key if it was created after
    /// 6:00 AM IST today. Deactivates any previously active token for the same key.
    /// </summary>
    /// <param name="apiKey">Zerodha API key.</param>
    /// <returns>The activated access token, or <c>null</c> if no qualifying token exists.</returns>
    Task<string?> ActivateTokenIfValidAsync(string apiKey);

    /// <summary>
    /// Returns the currently active Zerodha session, or <c>null</c> if none is active.
    /// </summary>
    Task<ZerodhaSession?> GetActiveSessionAsync();
}
