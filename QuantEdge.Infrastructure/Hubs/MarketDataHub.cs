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

    private static string GetGroupName(string symbol, string timeframe)
    {
        return $"{symbol.ToUpper().Trim()}_{timeframe.ToLower().Trim()}";
    }
}
