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
        decimal? engineStopLoss = null, decimal? engineTarget = null, decimal? dailyAtr = null, decimal? riskPct = null);

    /// <summary>
    /// Auto Short Selling (paper): places a simulated paper SHORT SELL after the same entry gates as
    /// <see cref="EvaluateAndExecuteAutoBuyAsync"/>, plus the Auto Short switch (OFF by default) and the short entry
    /// cut-off. Levels are mirrored (Stop Loss above the entry, Target below). Never places a broker order.
    /// </summary>
    Task<bool> EvaluateAndExecuteAutoShortAsync(string symbol, decimal entryPrice, int metConditionsCount, string userId = "default_user", bool isSellSignal = false,
        decimal? engineStopLoss = null, decimal? engineTarget = null, decimal? dailyAtr = null, decimal? riskPct = null);

    /// <summary>
    /// Evaluates exit conditions via the shared swing exit policy (<see cref="Services.SwingTradeRules"/>)
    /// and executes the auto paper sell when triggered. An open short is bought back instead (intraday short exit
    /// policy, incl. the auto square-off).
    /// </summary>
    Task<bool> EvaluateAndExecuteAutoSellAsync(PaperPosition position, decimal currentLtp, string userId = "default_user");

    /// <summary>
    /// Completely clears and resets all paper trading positions, orders, trade history, and execution logs for a fresh start.
    /// </summary>
    Task ResetAutoPaperTradingAsync(string userId = "default_user");
}
