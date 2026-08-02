using System.Collections.Generic;
using System.Threading.Tasks;
using QuantEdge.Domain.Entities;
using QuantEdge.Infrastructure.DTOs;

namespace QuantEdge.Infrastructure.Interfaces;

public interface IAutoTradeService
{
    Task<AutoTradeSettings> GetSettingsAsync(string userId = "default_user");
    Task<AutoTradeSettings> UpdateSettingsAsync(AutoTradeSettingsUpdateDto updateDto, string userId = "default_user");
    Task ToggleAutoTradeAsync(bool enabled, string userId = "default_user");
    Task<int> GetTodayAutoTradeCountAsync(string userId = "default_user");
    Task<AutoTradeDashboardDto> GetDashboardDataAsync(string userId = "default_user");
    Task LogAuditAsync(string symbol, string actionType, decimal? price, int? quantity, string? reason, string userId = "default_user");
    Task<IEnumerable<AutoTradeExecutionLog>> GetTodayLogsAsync(string userId = "default_user", int limit = 50);

    /// <summary>
    /// Evaluates candidate signal from scan job and places auto paper buy order if all 8 validation checks pass.
    /// </summary>
    Task<bool> EvaluateAndExecuteAutoBuyAsync(string symbol, decimal entryPrice, int metConditionsCount, string userId = "default_user");

    /// <summary>
    /// Evaluates real-time price against position Target %, Stop Loss %, or Max Duration and executes auto sell order if hit.
    /// </summary>
    Task<bool> EvaluateAndExecuteAutoSellAsync(PaperPosition position, decimal currentLtp, string userId = "default_user");
}
