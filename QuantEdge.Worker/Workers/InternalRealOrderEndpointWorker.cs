using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using QuantEdge.Infrastructure.DTOs;
using QuantEdge.Infrastructure.Interfaces;
using QuantEdge.Infrastructure.Services;
using Serilog;

namespace QuantEdge.Worker.Workers;

/// <summary>
/// Internal order endpoint for QuantEdge.API's RemoteRealOrderGateway. Zerodha only accepts orders from the
/// static IPs registered on the Kite app - this Worker server's - so when the API runs on another server it
/// forwards user-triggered Real Trade orders (Manual Buy / Sell / Buy Back, Square-Off, Kill Switch) here,
/// and they are placed from this process exactly as the API would have placed them itself.
/// Hosts a minimal Kestrel listener on RealOrderExecution:ListenUrl, protected by RealOrderExecution:SharedSecret
/// (configured identically on the API). Not public - firewall the port to the API server's IP only.
/// Running it in the autotrade process also means manual exits share the in-process per-symbol trade locks
/// with the automatic position monitor.
/// </summary>
public class InternalRealOrderEndpointWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<InternalRealOrderEndpointWorker> _logger;

    public InternalRealOrderEndpointWorker(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<InternalRealOrderEndpointWorker> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        string? listenUrl = _configuration["RealOrderExecution:ListenUrl"];
        string? sharedSecret = _configuration["RealOrderExecution:SharedSecret"];
        if (string.IsNullOrWhiteSpace(listenUrl) || string.IsNullOrWhiteSpace(sharedSecret))
        {
            _logger.LogWarning("RealOrderExecution:ListenUrl/SharedSecret not configured - internal real order endpoint not started.");
            return;
        }

        byte[] expectedSecret = Encoding.UTF8.GetBytes(sharedSecret);

        try
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls(listenUrl);
            builder.Logging.ClearProviders();
            builder.Services.AddSerilog();

            var app = builder.Build();

            var orders = app.MapGroup("/internal/real-orders").AddEndpointFilter(async (context, next) =>
            {
                string? provided = context.HttpContext.Request.Headers[RemoteRealOrderGateway.SecretHeaderName];
                if (provided == null || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(provided), expectedSecret))
                {
                    _logger.LogWarning("Rejected internal real order request with missing/invalid shared secret from {RemoteIp}.",
                        context.HttpContext.Connection.RemoteIpAddress);
                    return Results.Unauthorized();
                }

                return await next(context);
            });

            orders.MapPost("/manual-buy", (ManualRealBuyRequestDto dto) =>
                ExecuteAsync("manual-buy", dto.Symbol, dto.UserId, gateway => gateway.ManualBuyAsync(dto)));
            orders.MapPost("/manual-sell", (ManualSellRequestDto dto) =>
                ExecuteAsync("manual-sell", dto.Symbol, dto.UserId, gateway => gateway.ManualSellAsync(dto)));
            orders.MapPost("/manual-buy-back", (ManualSellRequestDto dto) =>
                ExecuteAsync("manual-buy-back", dto.Symbol, dto.UserId, gateway => gateway.ManualBuyBackAsync(dto)));
            orders.MapPost("/square-off", (CloseRealPositionRequestDto dto) =>
                ExecuteAsync("square-off", $"position #{dto.PositionId}", dto.UserId, gateway => gateway.SquareOffPositionAsync(dto)));
            orders.MapPost("/kill-switch", (EmergencyKillSwitchRequestDto dto) =>
                ExecuteAsync("kill-switch", "all positions", dto.UserId, gateway => gateway.SquareOffAllAsync(dto)));

            _logger.LogInformation("Internal real order endpoint listening on {ListenUrl}.", listenUrl);
            await app.RunAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            // Never take the rest of the autotrade process (auto scan / position monitor) down with it.
            _logger.LogError(ex, "Internal real order endpoint failed to start or crashed on {ListenUrl} - API-forwarded orders will fail until the Worker is restarted.", listenUrl);
        }
    }

    private async Task<IResult> ExecuteAsync(string action, string subject, int? userId, Func<IRealOrderGateway, Task<RealOrderExecutionResultDto>> execute)
    {
        _logger.LogInformation("Executing API-forwarded real order '{Action}' for {Subject} (User {UserId}).", action, subject, userId);
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var gateway = scope.ServiceProvider.GetRequiredService<IRealOrderGateway>();
            var result = await execute(gateway);

            _logger.LogInformation("API-forwarded real order '{Action}' for {Subject} (User {UserId}) finished: Success={Success}, {Message}",
                action, subject, userId, result.Success, result.Message);
            return Results.Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "API-forwarded real order '{Action}' for {Subject} (User {UserId}) threw.", action, subject, userId);
            return Results.Json(new RealOrderExecutionResultDto { Success = false, Message = ex.Message }, statusCode: StatusCodes.Status500InternalServerError);
        }
    }
}
