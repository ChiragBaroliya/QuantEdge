using System.Collections.Generic;
using System.Threading.Tasks;
using QuantEdge.Domain.Entities;
using QuantEdge.Infrastructure.DTOs;

namespace QuantEdge.Infrastructure.Interfaces;

public interface IAutoRealTradeService
{
    Task<RealTradeSettings> GetSettingsAsync(int userId = 1);
    Task<RealTradeSettings> UpdateSettingsAsync(RealTradeSettingsUpdateDto updateDto, int userId = 1);
    Task ToggleRealTradeAsync(bool enabled, int userId = 1);
    Task<int> GetTodayRealTradeCountAsync(int userId = 1);
    Task<RealTradeDashboardDto> GetDashboardDataAsync(int userId = 1);
    Task LogAuditAsync(string symbol, string actionType, decimal? price, int? quantity, string? reason, int userId = 1);
    Task<IEnumerable<RealTradeExecutionLog>> GetTodayLogsAsync(int userId = 1, int limit = 50);

    /// <summary>
    /// Evaluates pre-trade risk conditions (Token, Capital, Daily Loss Limit, Trading Window, Max Trades)
    /// and fires a Real-Money Buy order with Zerodha if all conditions pass.
    /// </summary>
    Task<bool> EvaluateAndExecuteRealBuyAsync(string symbol, decimal entryPrice, int metConditionsCount, int userId = 1, bool isBuySignal = false);

    /// <summary>
    /// Evaluates live exit conditions (Target, Optional SL, Optional Trailing SL, Max Duration)
    /// and executes a Real-Money Market Sell order with Zerodha when triggered.
    /// </summary>
    Task<bool> EvaluateAndExecuteRealSellAsync(RealPosition position, decimal currentLtp, int userId = 1);

    /// <summary>
    /// Emergency Kill Switch: Squares off all open real positions at market price and pauses the bot.
    /// </summary>
    Task<int> SquareOffAllPositionsAsync(string reason = "Emergency Panic Kill Switch Triggered", int userId = 1);

    /// <summary>
    /// Squares off an individual real position on demand.
    /// </summary>
    Task<bool> SquareOffSinglePositionAsync(int positionId, string reason = "Manual Exit", int userId = 1);

    /// <summary>
    /// Lightweight fast endpoint handler for high-frequency (e.g. 5-second) polling of Zerodha live positions, MTM, and P&L.
    /// </summary>
    Task<RealTradeLivePositionsFastDto> GetLivePositionsFastAsync(int userId = 1);
}
