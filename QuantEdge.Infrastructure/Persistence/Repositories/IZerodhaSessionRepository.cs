using QuantEdge.Infrastructure.Models;

namespace QuantEdge.Infrastructure.Persistence.Repositories;

/// <summary>
/// Repository contract for Zerodha session token management.
/// </summary>
public interface IZerodhaSessionRepository
{
    /// <summary>
    /// Attempts to activate the token for the given user/API key if it was created after
    /// 6:00 AM IST today. Deactivates any previously active token for the same user/key.
    /// </summary>
    /// <param name="apiKey">Zerodha API key.</param>
    /// <param name="userId">User identifier.</param>
    /// <returns>The activated access token, or <c>null</c> if no qualifying token exists.</returns>
    Task<string?> ActivateTokenIfValidAsync(string apiKey, int userId = 1);

    /// <summary>
    /// Returns the currently active Zerodha session for a specific user, or <c>null</c> if none is active.
    /// </summary>
    Task<ZerodhaSession?> GetActiveSessionAsync(int userId = 1);

    /// <summary>
    /// Returns all active Zerodha sessions across all users.
    /// </summary>
    Task<IEnumerable<ZerodhaSession>> GetAllActiveSessionsAsync();

    /// <summary>
    /// Upserts a Zerodha session for a user with Client ID and account details.
    /// </summary>
    Task UpsertSessionAsync(int userId, string apiKey, string? apiSecret, string accessToken, string? clientId = null, string? userName = null, string? userEmail = null);

    /// <summary>
    /// Updates DDPI status for a specific user.
    /// </summary>
    Task UpdateDdpiStatusAsync(int userId, bool isDdpiEnabled);
}

