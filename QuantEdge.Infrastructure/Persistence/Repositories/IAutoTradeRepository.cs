using System.Collections.Generic;
using System.Threading.Tasks;
using QuantEdge.Domain.Entities;
using QuantEdge.Infrastructure.DTOs;

namespace QuantEdge.Infrastructure.Persistence.Repositories;

public interface IAutoTradeRepository
{
    Task<AutoTradeSettings> GetSettingsAsync(string userId = "default_user");
    Task<IEnumerable<AutoTradeSettings>> GetActiveSettingsAsync();
    Task<AutoTradeSettings> UpsertSettingsAsync(AutoTradeSettings settings);

    Task ToggleAutoTradeAsync(string userId, bool enabled);
    Task<int> GetTodayAutoTradeCountAsync(string userId = "default_user");
    Task LogExecutionAsync(AutoTradeExecutionLog log);
    Task<IEnumerable<AutoTradeExecutionLog>> GetTodayLogsAsync(string userId = "default_user", int limit = 50);
}
