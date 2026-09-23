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
    /// Evaluates candidate signal from scan job and places an auto paper buy order if every entry gate
    /// passes - the same gates and entry levels as Auto Real Trading (<see cref="Services.SwingTradeRules"/>).
    /// </summary>
    Task<bool> EvaluateAndExecuteAutoBuyAsync(string symbol, decimal entryPrice, int metConditionsCount, string userId = "default_user", bool isBuySignal = false,
        decimal? engineStopLoss = null, decimal? engineTarget = null, decimal? dailyAtr = null);

    /// <summary>
    /// Evaluates exit conditions via the shared swing exit policy (<see cref="Services.SwingTradeRules"/>)
    /// and executes the auto paper sell when triggered.
    /// </summary>
    Task<bool> EvaluateAndExecuteAutoSellAsync(PaperPosition position, decimal currentLtp, string userId = "default_user");

    /// <summary>
    /// Completely clears and resets all paper trading positions, orders, trade history, and execution logs for a fresh start.
    /// </summary>
    Task ResetAutoPaperTradingAsync(string userId = "default_user");
}
