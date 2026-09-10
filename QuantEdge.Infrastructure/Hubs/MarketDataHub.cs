using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;

namespace QuantEdge.Infrastructure.Hubs;

/// <summary>
/// SignalR Hub for streaming real-time market data ticks, candles, and trading signals.
/// Clients join groups based on symbol and timeframe (e.g. NIFTY_1m).
/// </summary>
public class MarketDataHub : Hub
{
    /// <summary>
    /// Adds client connection to the specified symbol and timeframe streaming group.
    /// </summary>
    public async Task Subscribe(string symbol, string timeframe)
    {
        if (string.IsNullOrWhiteSpace(symbol) || string.IsNullOrWhiteSpace(timeframe)) return;

        string groupName = GetGroupName(symbol, timeframe);
        await Groups.AddToGroupAsync(Context.ConnectionId, groupName);
    }

    /// <summary>
    /// Removes client connection from the specified symbol and timeframe streaming group.
    /// </summary>
    public async Task Unsubscribe(string symbol, string timeframe)
    {
        if (string.IsNullOrWhiteSpace(symbol) || string.IsNullOrWhiteSpace(timeframe)) return;

        string groupName = GetGroupName(symbol, timeframe);
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, groupName);
    }

    /// <summary>
    /// Adds client connection to the Swing Trading Dashboard live streaming group.
    /// </summary>
    public async Task SubscribeSwingDashboard()
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, "SwingDashboard");
    }

    /// <summary>
    /// Removes client connection from the Swing Trading Dashboard live streaming group.
    /// </summary>
    public async Task UnsubscribeSwingDashboard()
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, "SwingDashboard");
    }

    /// <summary>
    /// Adds client connection to its own user group, so user-scoped events (e.g. real-trade holding
    /// monitoring/sell updates) can be sent only to that user instead of broadcasting to every client.
    /// </summary>
    public async Task JoinUserGroup(string userId)
    {
        if (string.IsNullOrWhiteSpace(userId)) return;

        await Groups.AddToGroupAsync(Context.ConnectionId, GetUserGroupName(userId));
    }

    /// <summary>
    /// Removes client connection from its user group.
    /// </summary>
    public async Task LeaveUserGroup(string userId)
    {
        if (string.IsNullOrWhiteSpace(userId)) return;

        await Groups.RemoveFromGroupAsync(Context.ConnectionId, GetUserGroupName(userId));
    }

    private static string GetGroupName(string symbol, string timeframe)
    {
        return $"{symbol.ToUpper().Trim()}_{timeframe.ToLower().Trim()}";
    }

    private static string GetUserGroupName(string userId)
    {
        return $"user-{userId.Trim()}";
    }
}
