using QuantEdge.Infrastructure.Models;

namespace QuantEdge.Infrastructure.Persistence.Repositories;

/// <summary>
/// Repository contract for Zerodha session token management.
/// </summary>
public interface IZerodhaSessionRepository
{
    /// <summary>
    /// Attempts to activate the user's token if it was created after 6:00 AM IST today.
    /// </summary>
    /// <param name="userId">User identifier.</param>
    /// <returns>The activated access token, or <c>null</c> if no qualifying token exists.</returns>
    Task<string?> ActivateTokenIfValidAsync(int userId);

    /// <summary>
    /// Returns the Zerodha session row of a specific user (API credentials plus the latest token, which may be
    /// missing or stale), or <c>null</c> if the user has not saved Zerodha credentials.
    /// </summary>
    Task<ZerodhaSession?> GetActiveSessionAsync(int userId = 1);

    /// <summary>
    /// Returns all active Zerodha sessions across all users.
    /// </summary>
    Task<IEnumerable<ZerodhaSession>> GetAllActiveSessionsAsync();

    /// <summary>
    /// Returns the session system-wide market data runs on (an Admin user's active session), or <c>null</c>.
    /// </summary>
    Task<ZerodhaSession?> GetMarketDataSessionAsync();

    /// <summary>
    /// Saves a user's own Kite Connect API key and secret. A <c>null</c> secret keeps the stored one; a changed
    /// API key clears the user's token so they connect Zerodha again.
    /// </summary>
    Task SaveCredentialsAsync(int userId, string apiKey, string? apiSecret);

    /// <summary>
    /// Upserts a Zerodha session for a user with Client ID and account details.
    /// </summary>
    Task UpsertSessionAsync(int userId, string apiKey, string? apiSecret, string accessToken, string? clientId = null, string? userName = null, string? userEmail = null);

    /// <summary>
    /// Updates DDPI status for a specific user.
    /// </summary>
    Task UpdateDdpiStatusAsync(int userId, bool isDdpiEnabled);
}

