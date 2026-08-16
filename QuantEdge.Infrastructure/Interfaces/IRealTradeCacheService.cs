using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using QuantEdge.Domain.Entities;
using QuantEdge.Infrastructure.Models;

namespace QuantEdge.Infrastructure.Interfaces;

/// <summary>
/// Thread-safe in-memory cache manager for Auto Real Trading.
/// Ensures zero database read latency during market hours (09:15 AM - 03:30 PM).
/// </summary>
public interface IRealTradeCacheService
{
    /// <summary>
    /// Indicates whether the in-memory trading cache is currently warmed up.
    /// </summary>
    bool IsWarmedUp { get; }

    /// <summary>
    /// Preloads active user settings, active open positions, and Zerodha sessions from DB into RAM.
    /// </summary>
    Task WarmupMarketCacheAsync();

    /// <summary>
    /// Flushes the in-memory cache after market hours (post 03:30 PM IST).
    /// </summary>
    Task ReleaseMarketCacheAsync();

    /// <summary>
    /// Retrieves all active users with IsRealTradeEnabled = true from RAM.
    /// </summary>
    IReadOnlyCollection<RealTradeSettings> GetActiveUsersSettings();

    /// <summary>
    /// Gets specific user's real trade settings from RAM.
    /// </summary>
    RealTradeSettings? GetUserSettings(int userId);

    /// <summary>
    /// Updates user settings in RAM.
    /// </summary>
    void SetUserSettings(RealTradeSettings settings);

    /// <summary>
    /// Retrieves all active open real positions across all users from RAM.
    /// </summary>
    IReadOnlyCollection<RealPosition> GetAllOpenPositions();

    /// <summary>
    /// Retrieves open real positions for a specific user from RAM.
    /// </summary>
    IReadOnlyCollection<RealPosition> GetOpenPositionsForUser(int userId);

    /// <summary>
    /// Adds or updates an open position in RAM.
    /// </summary>
    void AddOrUpdatePosition(RealPosition position);

    /// <summary>
    /// Removes a position from RAM when closed.
    /// </summary>
    void RemovePosition(int positionId);

    /// <summary>
    /// Gets a user's cached Zerodha session token.
    /// </summary>
    ZerodhaSession? GetUserSession(int userId);

    /// <summary>
    /// Sets a user's Zerodha session token in RAM.
    /// </summary>
    void SetUserSession(ZerodhaSession session);
}
