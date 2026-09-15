using System.Threading.Tasks;

namespace QuantEdge.Infrastructure.Interfaces;

/// <summary>
/// Sends a SignalR event to dashboard clients connected to the MarketDataHub.
/// The hub is only actually hosted inside QuantEdge.API - QuantEdge.Worker runs as a headless
/// process with no clients ever connected to a hub of its own, so a Worker-side implementation
/// must relay the broadcast over the network to the API process that owns the real hub.
/// </summary>
public interface IHubBroadcastService
{
    Task BroadcastAllAsync(string eventName, object payload);

    Task BroadcastGroupAsync(string groupName, string eventName, object payload);
}
