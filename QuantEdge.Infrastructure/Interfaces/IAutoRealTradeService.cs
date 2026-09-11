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
    /// Polls Zerodha for every real order still recorded as Open (broker-accepted but unconfirmed) across
    /// all users, and finalizes it once the broker confirms the real outcome (FILLED closes the position
    /// and records realized P&amp;L; REJECTED/CANCELLED clears the order and leaves the position open for retry).
    /// Called periodically by the position monitor worker - never assume a placed order is filled.
    /// </summary>
    Task ReconcilePendingRealOrdersAsync();

    /// <summary>
    /// Force re-verifies one order's status directly against Zerodha and corrects our records if they've
    /// drifted from the broker's truth (e.g. an order recorded Filled/Rejected here that Zerodha still
    /// shows resting OPEN). Triggerable on demand for a single order from the Real Orders Book, unlike
    /// <see cref="ReconcilePendingRealOrdersAsync"/> which only scans currently-Open orders each cycle.
    /// </summary>
    Task<(bool Success, string Message)> ResyncOrderStatusAsync(int orderId, int userId = 1);

    /// <summary>
    /// Resyncs every recent order for a user against Zerodha in one pass - what "Sync Now" runs so it
    /// actually re-verifies order status with the broker, not just re-reads QuantEdge's own records.
    /// </summary>
    Task<(bool Success, string Message)> ResyncRecentOrdersAsync(int userId = 1);

    /// <summary>
    /// Squares off an individual real position on demand.
    /// </summary>
    Task<bool> SquareOffSinglePositionAsync(int positionId, string reason = "Manual Exit", int userId = 1);

    /// <summary>
    /// Lightweight fast endpoint handler for high-frequency (e.g. 5-second) polling of Zerodha live positions, MTM, and P&L.
    /// </summary>
    Task<RealTradeLivePositionsFastDto> GetLivePositionsFastAsync(int userId = 1);

    /// <summary>
    /// Turns an existing Zerodha Holding into a monitored real_positions row (no BUY order is placed,
    /// since the shares are already held). Once created, the existing position monitor watches it for
    /// the target price and auto-sells through the same pipeline as any other real position.
    /// </summary>
    Task<(bool Success, string Message)> EnableHoldingMonitoringAsync(string symbol, int quantity, decimal averagePrice, decimal targetPrice, int userId = 1);

    /// <summary>
    /// Manual SELL for any stock currently held/positioned at Zerodha. If the symbol is already a
    /// bot-tracked open position, this delegates to <see cref="SquareOffSinglePositionAsync"/> so
    /// P&amp;L and trade history stay consistent; otherwise it sells directly against the broker
    /// (same broker-confirmed fill semantics as every other real order - never assumes Filled).
    /// </summary>
    Task<(bool Success, string Message)> ManualSellAsync(string symbol, int quantity, decimal currentPrice, string? product, decimal? entryPriceHint, string reason, int userId = 1);
}
