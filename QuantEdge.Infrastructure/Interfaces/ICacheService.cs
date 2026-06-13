using System;
using System.Threading.Tasks;

namespace QuantEdge.Infrastructure.Interfaces;

/// <summary>
/// Generic caching interface to decouple components from specific cache implementations (like MemoryCache or Redis).
/// </summary>
public interface ICacheService
{
    /// <summary>
    /// Gets a cached item by key. Returns default if not found.
    /// </summary>
    Task<T?> GetAsync<T>(string key);

    /// <summary>
    /// Sets a cached item with an optional absolute expiration delay relative to now.
    /// </summary>
    Task SetAsync<T>(string key, T value, TimeSpan? expiration = null);

    /// <summary>
    /// Removes an item from the cache.
    /// </summary>
    Task RemoveAsync(string key);
}
