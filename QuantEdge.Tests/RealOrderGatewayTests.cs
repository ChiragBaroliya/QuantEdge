using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using QuantEdge.Infrastructure.DTOs;
using QuantEdge.Infrastructure.Interfaces;
using QuantEdge.Infrastructure.Services;
using QuantEdge.Worker.Workers;
using Xunit;

namespace QuantEdge.Tests;

/// <summary>
/// API -> Worker order forwarding (split-server deployment). Round-trips through the real Worker endpoint on a
/// loopback port; the trade service behind it is a Moq mock, so no order can ever reach Zerodha from these tests.
/// </summary>
public class RealOrderGatewayTests : IAsyncLifetime
{
    private const string Secret = "test-secret";

    private readonly Mock<IAutoRealTradeService> _tradeService = new(MockBehavior.Strict);
    private readonly int _port = FreePort();
    private ServiceProvider _workerServices = null!;
    private InternalRealOrderEndpointWorker _endpoint = null!;

    public async Task InitializeAsync()
    {
        var workerConfig = Config(("RealOrderExecution:ListenUrl", $"http://127.0.0.1:{_port}"), ("RealOrderExecution:SharedSecret", Secret));

        var services = new ServiceCollection();
        services.AddSingleton(_tradeService.Object);
        services.AddSingleton<IRealOrderGateway, LocalRealOrderGateway>();
        _workerServices = services.BuildServiceProvider();

        _endpoint = new InternalRealOrderEndpointWorker(
            _workerServices.GetRequiredService<IServiceScopeFactory>(), workerConfig, NullLogger<InternalRealOrderEndpointWorker>.Instance);
        await _endpoint.StartAsync(CancellationToken.None);
        await WaitUntilListeningAsync(_port);
    }

    public async Task DisposeAsync()
    {
        await _endpoint.StopAsync(CancellationToken.None);
        _endpoint.Dispose();
        await _workerServices.DisposeAsync();
    }

    [Fact]
    public async Task BuyBack_IsPlacedByTheWorker_WithTheApiResolvedUserAndReason()
    {
        _tradeService
            .Setup(s => s.ManualBuyBackAsync("SBIN", 10, 800m, "MIS", 820m, "Manual Web Buy Back", 7))
            .ReturnsAsync((true, "BUY BACK order placed for SBIN."));

        var result = await Gateway().ManualBuyBackAsync(new ManualSellRequestDto
        {
            Symbol = "SBIN", Quantity = 10, CurrentPrice = 800m, Product = "MIS", EntryPriceHint = 820m,
            Reason = "Manual Web Buy Back", UserId = 7
        });

        Assert.True(result.Success);
        Assert.Equal("BUY BACK order placed for SBIN.", result.Message);
        _tradeService.VerifyAll();
    }

    [Fact]
    public async Task ManualBuy_NotExecuted_ReturnsNoMessage_SoTheApiFallsBackToTheAuditLog()
    {
        _tradeService
            .Setup(s => s.EvaluateAndExecuteRealBuyAsync("INFY", 1500m, 3, 2, true, null, null, true, 5, 2m, 1m, null))
            .ReturnsAsync(false);

        var result = await Gateway().ManualBuyAsync(new ManualRealBuyRequestDto
        {
            Symbol = "INFY", EntryPrice = 1500m, MetConditionsCount = 3, Quantity = 5, StopLossPct = 2m, TrailingSlPct = 1m, UserId = 2
        });

        Assert.False(result.Success);
        Assert.Null(result.Message);
    }

    [Fact]
    public async Task KillSwitch_ReturnsTheClosedPositionCount()
    {
        _tradeService.Setup(s => s.SquareOffAllPositionsAsync("panic", 1)).ReturnsAsync(3);

        var result = await Gateway().SquareOffAllAsync(new EmergencyKillSwitchRequestDto { Reason = "panic", UserId = 1 });

        Assert.True(result.Success);
        Assert.Equal(3, result.ClosedPositionsCount);
    }

    [Fact]
    public async Task WrongSecret_IsRejected_AndNoOrderIsPlaced()
    {
        // Strict mock: any call into the trade service would throw and fail the test.
        var result = await Gateway(secret: "wrong").ManualSellAsync(new ManualSellRequestDto
        {
            Symbol = "SBIN", Quantity = 1, CurrentPrice = 800m, Reason = "x", UserId = 1
        });

        Assert.False(result.Success);
        Assert.StartsWith("Order NOT placed", result.Message);
    }

    [Fact]
    public async Task WorkerThrowing_IsReportedAsAFailedResult()
    {
        _tradeService
            .Setup(s => s.SquareOffSinglePositionAsync(42, "Manual Web Square-Off", 1))
            .ThrowsAsync(new InvalidOperationException("Zerodha session is not active."));

        var result = await Gateway().SquareOffPositionAsync(new CloseRealPositionRequestDto { PositionId = 42, Reason = "Manual Web Square-Off", UserId = 1 });

        Assert.False(result.Success);
        Assert.Equal("Zerodha session is not active.", result.Message);
    }

    [Fact]
    public async Task UnreachableWorker_TellsTheUserToCheckTheOrdersBook()
    {
        var result = await Gateway(baseUrl: $"http://127.0.0.1:{FreePort()}").ManualBuyBackAsync(new ManualSellRequestDto
        {
            Symbol = "SBIN", Quantity = 1, CurrentPrice = 800m, Reason = "x", UserId = 1
        });

        Assert.False(result.Success);
        Assert.Contains("Check the Real Orders Book", result.Message);
    }

    [Fact]
    public async Task MissingConfig_DoesNotSendAnything()
    {
        var gateway = new RemoteRealOrderGateway(new SimpleHttpClientFactory(), Config(), NullLogger<RemoteRealOrderGateway>.Instance);

        var result = await gateway.ManualBuyBackAsync(new ManualSellRequestDto { Symbol = "SBIN", Quantity = 1, CurrentPrice = 800m, UserId = 1 });

        Assert.False(result.Success);
        Assert.StartsWith("Order NOT placed", result.Message);
    }

    private RemoteRealOrderGateway Gateway(string secret = Secret, string? baseUrl = null) =>
        new(new SimpleHttpClientFactory(),
            Config(("RealOrderExecution:WorkerBaseUrl", baseUrl ?? $"http://127.0.0.1:{_port}"), ("RealOrderExecution:SharedSecret", secret)),
            NullLogger<RemoteRealOrderGateway>.Instance);

    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value))).Build();

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task WaitUntilListeningAsync(int port)
    {
        for (int i = 0; i < 100; i++)
        {
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, port);
                return;
            }
            catch (SocketException)
            {
                await Task.Delay(50);
            }
        }

        throw new TimeoutException($"Internal real order endpoint did not start listening on port {port}.");
    }

    private sealed class SimpleHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
