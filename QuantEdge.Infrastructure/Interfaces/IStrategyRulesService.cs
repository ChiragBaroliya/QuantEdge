using System.Collections.Generic;
using System.Threading.Tasks;
using QuantEdge.Infrastructure.DTOs;

namespace QuantEdge.Infrastructure.Interfaces;

public interface IStrategyRulesService
{
    Task<StrategyRulesDto> GetAsync();

    /// <summary>Validates and saves. On failure nothing is saved and every problem is listed.</summary>
    Task<(bool Success, IReadOnlyList<string> Errors, StrategyRulesDto? Saved)> SaveAsync(StrategyRulesSaveDto dto);
}
