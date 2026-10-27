using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using QuantEdge.Infrastructure.Helpers;
using QuantEdge.Infrastructure.Interfaces;
using QuantEdge.Infrastructure.Models;
using QuantEdge.Infrastructure.Persistence.Repositories;
using QuantEdge.Infrastructure.Services;
using Xunit;

namespace QuantEdge.Tests;

/// <summary>
/// GET /user/margins/equity is shared per user for a few seconds so the 5s poll, dashboard broadcasts and scan-pass
/// capital checks don't trip Kite's rate limit (HTTP 429). The HTTP handler is a fake - nothing reaches Zerodha.
/// </summary>
public class ZerodhaKiteBrokerServiceMarginsCacheTests
{
    private const int User = 1;
    private const string MarginsJson = "{\"status\":\"success\",\"data\":{\"net\":50000.5,\"utilised\":{\"debits\":1200}}}";

    private readonly FakeKiteHandler _handler = new();
    private readonly Mock<IRealTradeCacheService> _ticks = new();
    private readonly ZerodhaKiteBrokerService _service;

    public ZerodhaKiteBrokerServiceMarginsCacheTests()
    {
        // The session check uses the real clock: a token is only valid once created after 06:00 IST today.
        var sessions = new Mock<IZerodhaSessionRepository>();
        sessions.Setup(r => r.GetActiveSessionAsync(User)).ReturnsAsync(new ZerodhaSession
        {
            UserId = User, ApiKey = "key", AccessToken = "token", IsActive = true, CreatedAt = DateTimeOffset.UtcNow
        });

        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(ZerodhaKiteBrokerService.HttpClientName)).Returns(() => new HttpClient(_handler, false));

        decimal none = 0m;
        _ticks.Setup(c => c.TryGetFreshLtp(It.IsAny<string>(), It.IsAny<TimeSpan>(), out none)).Returns(false);

        _service = new ZerodhaKiteBrokerService(sessions.Object, factory.Object, NullLogger<ZerodhaKiteBrokerService>.Instance, _ticks.Object);
    }

    private static bool BeforeTokenCutoff() =>
        TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneHelper.IndianTimeZone).Hour < 6;

    [Fact]
    public async Task ConcurrentAndRepeatedCalls_ShareOneKiteRequest()
    {
        if (BeforeTokenCutoff()) return;

        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => _service.GetEquityMarginsAsync(User)));
        var again = await _service.GetEquityMarginsAsync(User);

        Assert.Equal(1, _handler.Count("/user/margins/equity"));
        Assert.All(results.Append(again), r =>
        {
            Assert.True(r.Success);
            Assert.Equal(50000.5m, r.AvailableCash);
            Assert.Equal(1200m, r.UsedMargin);
        });
    }

    [Fact]
    public async Task CancellingAnOrder_ForcesFreshMarginRead()
    {
        if (BeforeTokenCutoff()) return;

        await _service.GetEquityMarginsAsync(User);
        await _service.CancelLiveOrderAsync("123456789", User);
        await _service.GetEquityMarginsAsync(User);

        Assert.Equal(2, _handler.Count("/user/margins/equity"));
    }

    [Fact]
    public async Task Ltp_FreshTick_IsServedWithoutRest()
    {
        decimal tick = 812.4m;
        _ticks.Setup(c => c.TryGetFreshLtp("SBIN", It.IsAny<TimeSpan>(), out tick)).Returns(true);

        var result = await _service.GetLtpQuotesAsync(new[] { ("sbin", "NSE") }, User);

        Assert.True(result.Success);
        Assert.Equal(812.4m, result.Ltps!["SBIN"]);
        Assert.Equal(0, _handler.Count("/quote/ltp"));
    }

    [Fact]
    public async Task Ltp_InsufficientPermission_StopsRestQuotes()
    {
        if (BeforeTokenCutoff()) return;
        _handler.QuoteStatus = HttpStatusCode.Forbidden;

        var first = await _service.GetLtpQuotesAsync(new[] { ("SBIN", "NSE") }, User);
        var second = await _service.GetLtpQuotesAsync(new[] { ("PNB", "NSE") }, User);

        Assert.False(first.Success);
        Assert.False(second.Success);
        Assert.Equal(1, _handler.Count("/quote/ltp"));
    }

    private sealed class FakeKiteHandler : HttpMessageHandler
    {
        private readonly List<string> _paths = new();

        public HttpStatusCode QuoteStatus { get; set; } = HttpStatusCode.OK;

        public int Count(string path)
        {
            lock (_paths) return _paths.Count(p => p == path);
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (_paths) _paths.Add(request.RequestUri!.AbsolutePath);
            await Task.Delay(20, cancellationToken); // keep the first fetch in flight while the others arrive
            if (request.RequestUri!.AbsolutePath == "/quote/ltp" && QuoteStatus == HttpStatusCode.Forbidden)
                return new HttpResponseMessage(HttpStatusCode.Forbidden)
                {
                    Content = new StringContent("{\"status\":\"error\",\"message\":\"Insufficient permission for that call.\",\"error_type\":\"PermissionException\"}")
                };
            string body = request.RequestUri!.AbsolutePath == "/user/margins/equity" ? MarginsJson : "{\"status\":\"success\",\"data\":{}}";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
        }
    }
}
