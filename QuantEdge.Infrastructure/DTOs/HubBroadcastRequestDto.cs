using System.Text.Json;

namespace QuantEdge.Infrastructure.DTOs;

/// <summary>Payload posted by QuantEdge.Worker to QuantEdge.API's internal relay endpoint to
/// forward a SignalR broadcast to dashboard clients connected to the API-hosted hub.</summary>
public class HubBroadcastRequestDto
{
    public string EventName { get; set; } = string.Empty;

    public string? GroupName { get; set; }

    public JsonElement Payload { get; set; }
}
