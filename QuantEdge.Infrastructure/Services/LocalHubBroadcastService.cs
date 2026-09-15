using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using QuantEdge.Infrastructure.Hubs;
using QuantEdge.Infrastructure.Interfaces;

namespace QuantEdge.Infrastructure.Services;

/// <summary>Used inside QuantEdge.API, which actually hosts the MarketDataHub clients connect to.</summary>
public class LocalHubBroadcastService : IHubBroadcastService
{
    private readonly IHubContext<MarketDataHub> _hubContext;

    public LocalHubBroadcastService(IHubContext<MarketDataHub> hubContext)
    {
        _hubContext = hubContext;
    }

    public Task BroadcastAllAsync(string eventName, object payload) =>
        _hubContext.Clients.All.SendAsync(eventName, payload);

    public Task BroadcastGroupAsync(string groupName, string eventName, object payload) =>
        _hubContext.Clients.Group(groupName).SendAsync(eventName, payload);
}
