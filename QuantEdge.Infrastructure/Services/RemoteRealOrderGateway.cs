using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using QuantEdge.Infrastructure.DTOs;
using QuantEdge.Infrastructure.Interfaces;

namespace QuantEdge.Infrastructure.Services;

/// <summary>
/// Used inside QuantEdge.API when it is deployed on a server whose IP is NOT registered with Zerodha
/// (RealOrderExecution:Mode = Remote) - forwards the order to QuantEdge.Worker's internal endpoint
/// (InternalRealOrderEndpointWorker), which places it from the Worker server's registered static IP.
/// Never retries: an order request is not idempotent, so a lost response must surface as "status unknown"
/// rather than risk placing the same order twice.
/// </summary>
public class RemoteRealOrderGateway : IRealOrderGateway
{
    public const string HttpClientName = "RealOrderExecutionRelay";
    public const string SecretHeaderName = "X-Internal-Order-Secret";

    private const string UnknownOutcomeHint = "Check the Real Orders Book before retrying.";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<RemoteRealOrderGateway> _logger;

    public RemoteRealOrderGateway(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<RemoteRealOrderGateway> logger)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task<RealOrderExecutionResultDto> ManualBuyAsync(ManualRealBuyRequestDto dto) => SendAsync("manual-buy", dto, dto.Symbol);

    public Task<RealOrderExecutionResultDto> ManualSellAsync(ManualSellRequestDto dto) => SendAsync("manual-sell", dto, dto.Symbol);

    public Task<RealOrderExecutionResultDto> ManualBuyBackAsync(ManualSellRequestDto dto) => SendAsync("manual-buy-back", dto, dto.Symbol);

    public Task<RealOrderExecutionResultDto> SquareOffPositionAsync(CloseRealPositionRequestDto dto) => SendAsync("square-off", dto, $"position #{dto.PositionId}");

    public Task<RealOrderExecutionResultDto> SquareOffAllAsync(EmergencyKillSwitchRequestDto dto) => SendAsync("kill-switch", dto, "all positions");

    private async Task<RealOrderExecutionResultDto> SendAsync(string action, object dto, string subject)
    {
        string? workerBaseUrl = _configuration["RealOrderExecution:WorkerBaseUrl"];
        string? sharedSecret = _configuration["RealOrderExecution:SharedSecret"];
        if (string.IsNullOrWhiteSpace(workerBaseUrl) || string.IsNullOrWhiteSpace(sharedSecret))
        {
            _logger.LogError("RealOrderExecution:WorkerBaseUrl/SharedSecret not configured - cannot forward '{Action}' for {Subject} to the Worker.", action, subject);
            return Fail("Order NOT placed: the order execution service (Worker) is not configured on the API server.");
        }

        try
        {
            var client = _httpClientFactory.CreateClient(HttpClientName);
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{workerBaseUrl.TrimEnd('/')}/internal/real-orders/{action}")
            {
                Content = JsonContent.Create(dto, dto.GetType())
            };
            request.Headers.Add(SecretHeaderName, sharedSecret);

            using var response = await client.SendAsync(request);

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.ServiceUnavailable or HttpStatusCode.NotFound)
            {
                // Rejected before reaching the trade service - nothing was sent to Zerodha.
                _logger.LogError("Worker order endpoint rejected '{Action}' for {Subject} with {StatusCode} - check RealOrderExecution config on both servers.",
                    action, subject, response.StatusCode);
                return Fail($"Order NOT placed: the order execution service (Worker) rejected the request ({(int)response.StatusCode}).");
            }

            RealOrderExecutionResultDto? result = null;
            try
            {
                result = await response.Content.ReadFromJsonAsync<RealOrderExecutionResultDto>();
            }
            catch (JsonException)
            {
                // Falls through to the unknown-outcome result below.
            }

            if (result == null)
            {
                _logger.LogError("Worker order endpoint returned {StatusCode} with no readable result for '{Action}' ({Subject}).", response.StatusCode, action, subject);
                return Fail($"The order execution service returned an unexpected response ({(int)response.StatusCode}). {UnknownOutcomeHint}");
            }

            return result;
        }
        catch (TaskCanceledException ex)
        {
            // The Worker may still be placing (or have placed) the order - never report this as "not placed".
            _logger.LogError(ex, "Timed out waiting for the Worker to execute '{Action}' for {Subject}.", action, subject);
            return Fail($"No response from the order execution service in time - the order may still have been placed. {UnknownOutcomeHint}");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Could not reach the Worker order endpoint for '{Action}' ({Subject}).", action, subject);
            return Fail($"Could not reach the order execution service (Worker): {ex.Message}. {UnknownOutcomeHint}");
        }
    }

    private static RealOrderExecutionResultDto Fail(string message) => new() { Success = false, Message = message };
}
