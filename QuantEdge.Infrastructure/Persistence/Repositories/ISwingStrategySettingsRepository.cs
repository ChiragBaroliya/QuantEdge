using System.Threading.Tasks;
using QuantEdge.Domain.Entities;

namespace QuantEdge.Infrastructure.Persistence.Repositories;

public interface ISwingStrategySettingsRepository
{
    Task<SwingStrategySettings> GetSettingsAsync();
    Task<SwingStrategySettings> UpdateSettingsAsync(SwingStrategySettings settings);

    /// <summary>Saves the rule numbers, score thresholds and gate mode in one step. Other settings are left alone.</summary>
    Task<SwingStrategySettings> UpdateRulesAsync(SwingRuleParameters rules, int buyScoreThreshold, int watchScoreThreshold, string marketGateMode);
}
