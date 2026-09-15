using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using QuantEdge.Infrastructure.Interfaces;

namespace QuantEdge.Infrastructure.Services;

/// <summary>
/// Used inside QuantEdge.Worker, which has no hub of its own - relays the broadcast over HTTP to
/// QuantEdge.API's internal endpoint, which forwards it to the dashboard clients it actually hosts.
/// Never throws: a failed/unreachable relay should never break the trading flow that triggered it,
/// so failures are logged and swallowed.
/// </summary>
public class RemoteHubBroadcastService : IHubBroadcastService
{
    public const string HttpClientName = "HubBroadcastRelay";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<RemoteHubBroadcastService> _logger;

    public RemoteHubBroadcastService(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<RemoteHubBroadcastService> logger)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task BroadcastAllAsync(string eventName, object payload) => SendAsync(eventName, null, payload);

    public Task BroadcastGroupAsync(string groupName, string eventName, object payload) => SendAsync(eventName, groupName, payload);

    private async Task SendAsync(string eventName, string? groupName, object payload)
    {
        try
        {
            string? apiBaseUrl = _configuration["RealtimeBroadcast:ApiBaseUrl"];
            string? sharedSecret = _configuration["RealtimeBroadcast:SharedSecret"];
            if (string.IsNullOrWhiteSpace(apiBaseUrl) || string.IsNullOrWhiteSpace(sharedSecret))
            {
                _logger.LogWarning(
                    "RealtimeBroadcast:ApiBaseUrl/SharedSecret not configured - skipping relay of hub event '{EventName}'.",
                    eventName);
                return;
            }

            var client = _httpClientFactory.CreateClient(HttpClientName);
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{apiBaseUrl.TrimEnd('/')}/internal/broadcast")
            {
                Content = JsonContent.Create(new { eventName, groupName, payload })
            };
            request.Headers.Add("X-Internal-Broadcast-Secret", sharedSecret);

            using var response = await client.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Hub broadcast relay for '{EventName}' returned {StatusCode}.", eventName, response.StatusCode);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to relay hub broadcast '{EventName}' to API.", eventName);
        }
    }
}
