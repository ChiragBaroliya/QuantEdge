using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using QuantEdge.Domain.Entities;
using QuantEdge.Infrastructure.Interfaces;
using QuantEdge.Infrastructure.Models;
using QuantEdge.Infrastructure.Persistence.Repositories;

namespace QuantEdge.Infrastructure.Services;

/// <summary>
/// Thread-safe in-memory cache manager for Auto Real Trading.
/// Ensures zero database read latency during market hours (09:15 AM - 03:30 PM).
/// </summary>
public class RealTradeCacheService : IRealTradeCacheService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<RealTradeCacheService> _logger;

    private readonly ConcurrentDictionary<int, RealTradeSettings> _userSettingsCache = new();
    private readonly ConcurrentDictionary<int, RealPosition> _openPositionsCache = new();
    private readonly ConcurrentDictionary<int, ZerodhaSession> _userSessionsCache = new();

    private bool _isWarmedUp = false;
    public bool IsWarmedUp => _isWarmedUp;

    public RealTradeCacheService(
        IServiceProvider serviceProvider,
        ILogger<RealTradeCacheService> logger)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task WarmupMarketCacheAsync()
    {
        try
        {
            _logger.LogInformation("🔥 Pre-Market Warmup: Loading Active Users, Open Positions & Zerodha Sessions into In-Memory RAM...");

            using var scope = _serviceProvider.CreateScope();
            var realRepo = scope.ServiceProvider.GetRequiredService<IRealTradingRepository>();
            var sessionRepo = scope.ServiceProvider.GetRequiredService<IZerodhaSessionRepository>();

            // 1. Load active user settings
            var activeSettings = await realRepo.GetActiveSettingsAsync();
            _userSettingsCache.Clear();
            foreach (var s in activeSettings)
            {
                _userSettingsCache[s.UserId] = s;
            }

            // 2. Load open real positions across all users
            var openPositions = await realRepo.GetAllOpenPositionsAsync();
            _openPositionsCache.Clear();
            foreach (var pos in openPositions)
            {
                _openPositionsCache[pos.Id] = pos;
            }

            // 3. Load active Zerodha sessions
            var activeSessions = await sessionRepo.GetAllActiveSessionsAsync();
            _userSessionsCache.Clear();
            foreach (var sess in activeSessions)
            {
                _userSessionsCache[sess.UserId] = sess;
            }

            _isWarmedUp = true;
            _logger.LogInformation("✅ Pre-Market Warmup complete: Cached {UserCount} active user(s), {PosCount} open position(s), and {SessionCount} active broker session(s) in RAM.",
                _userSettingsCache.Count, _openPositionsCache.Count, _userSessionsCache.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Error during Pre-Market Warmup in RealTradeCacheService.");
        }
    }

    public Task ReleaseMarketCacheAsync()
    {
        _logger.LogInformation("🧹 Market Close: Releasing In-Memory Auto Real Trading Cache...");
        _userSettingsCache.Clear();
        _openPositionsCache.Clear();
        _userSessionsCache.Clear();
        _isWarmedUp = false;
        _logger.LogInformation("✅ In-Memory Auto Real Trading Cache released successfully.");
        return Task.CompletedTask;
    }

    public IReadOnlyCollection<RealTradeSettings> GetActiveUsersSettings()
    {
        return _userSettingsCache.Values
            .Where(s => s.IsRealTradeEnabled)
            .ToList();
    }

    public RealTradeSettings? GetUserSettings(int userId)
    {
        if (_userSettingsCache.TryGetValue(userId, out var settings))
        {
            return settings;
        }
        return null;
    }

    public void SetUserSettings(RealTradeSettings settings)
    {
        if (settings != null)
        {
            _userSettingsCache[settings.UserId] = settings;
        }
    }

    public IReadOnlyCollection<RealPosition> GetAllOpenPositions()
    {
        return _openPositionsCache.Values
            .Where(p => p.Status == PositionStatus.OPEN)
            .ToList();
    }

    public IReadOnlyCollection<RealPosition> GetOpenPositionsForUser(int userId)
    {
        return _openPositionsCache.Values
            .Where(p => p.UserId == userId && p.Status == PositionStatus.OPEN)
            .ToList();
    }

    public void AddOrUpdatePosition(RealPosition position)
    {
        if (position != null)
        {
            if (position.Status == PositionStatus.OPEN)
            {
                _openPositionsCache[position.Id] = position;
            }
            else
            {
                _openPositionsCache.TryRemove(position.Id, out _);
            }
        }
    }

    public void RemovePosition(int positionId)
    {
        _openPositionsCache.TryRemove(positionId, out _);
    }

    public ZerodhaSession? GetUserSession(int userId)
    {
        if (_userSessionsCache.TryGetValue(userId, out var session))
        {
            return session;
        }
        return null;
    }

    public void SetUserSession(ZerodhaSession session)
    {
        if (session != null)
        {
            _userSessionsCache[session.UserId] = session;
        }
    }
}
