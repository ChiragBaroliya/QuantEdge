using System.Threading.Tasks;
using QuantEdge.Domain.Entities;

namespace QuantEdge.Infrastructure.Persistence.Repositories;

public interface ISwingStrategySettingsRepository
{
    Task<SwingStrategySettings> GetSettingsAsync();
    Task<SwingStrategySettings> UpdateSettingsAsync(SwingStrategySettings settings);
}
