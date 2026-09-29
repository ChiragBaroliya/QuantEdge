using System.Collections.Generic;
using System.Threading.Tasks;

namespace QuantEdge.Infrastructure.Persistence.Repositories;

/// <summary>Per-user favorite symbols for the Signal Dashboard symbol dropdown.</summary>
public interface IFavoriteSymbolRepository
{
    Task<IReadOnlyList<string>> GetFavoritesAsync(int userId);
    Task AddFavoriteAsync(int userId, string symbol);
    Task RemoveFavoriteAsync(int userId, string symbol);
}
