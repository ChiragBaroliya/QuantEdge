using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using QuantEdge.Domain.Entities;
using QuantEdge.Infrastructure.Helpers;
using QuantEdge.Infrastructure.Persistence.Repositories;
using QuantEdge.Infrastructure.Services;
using Xunit;

namespace QuantEdge.Tests;

public class ZerodhaTokenWindowTests
{
    private static DateTime Ist(int day, int hour, int minute = 0) => new(2026, 10, day, hour, minute, 0);

    [Theory]
    [InlineData(9, 8, 50, 9, 14, 0, true)]    // today's 08:50 login, checked at 14:00
    [InlineData(8, 8, 50, 9, 14, 0, false)]   // yesterday's token after today's 06:00 expiry - the "invalid token" bug
    [InlineData(8, 20, 0, 9, 2, 0, true)]     // last night's token is still valid before 06:00
    [InlineData(8, 5, 0, 9, 2, 0, false)]     // created before yesterday's 06:00 - expired
    [InlineData(9, 6, 0, 9, 6, 0, true)]      // created exactly at the 06:00 reset
    public void TokenIsValidUntilTheNextSixAmIst(int cDay, int cHour, int cMin, int nDay, int nHour, int nMin, bool valid)
    {
        Assert.Equal(valid, ZerodhaHistoricalDataService.IsTokenFromCurrentSession(Ist(cDay, cHour, cMin), Ist(nDay, nHour, nMin)));
    }
}

public class NotificationHistoryTests
{
    private readonly Mock<IRealTradingRepository> _realRepo = new();
    private readonly Mock<IBrokerApiEventRepository> _brokerRepo = new();
    private readonly Mock<ISwingSlotRecommendationRepository> _slotRepo = new();
    private readonly Mock<IMarketCandleRepository> _candleRepo = new();

    private NotificationService CreateService() => new(
        _realRepo.Object, _slotRepo.Object, _candleRepo.Object, Mock.Of<ISwingStrategySettingsRepository>(),
        NullLogger<NotificationService>.Instance, cacheService: null, brokerApiEventRepository: _brokerRepo.Object);

    private static DateTime TodayIst => TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneHelper.IndianTimeZone).Date;
    private static DateTime IstToUtc(DateTime ist) => TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(ist, DateTimeKind.Unspecified), TimeZoneHelper.IndianTimeZone);

    [Fact]
    public async Task History_ReturnsItemsInsideTheRange_NewestFirst_AndHidesOtherUsersEvents()
    {
        var yesterdayNoon = IstToUtc(TodayIst.AddDays(-1).AddHours(12));
        var tenDaysAgo = IstToUtc(TodayIst.AddDays(-10).AddHours(12));
        _realRepo.Setup(r => r.GetLogsSinceAsync(1, It.IsAny<DateTime>(), It.IsAny<int>())).ReturnsAsync(new[]
        {
            new RealTradeExecutionLog { Id = 1, UserId = 1, Symbol = "TCS", ActionType = "REAL_BUY", Reason = "bought", ExecutedAt = yesterdayNoon },
            new RealTradeExecutionLog { Id = 2, UserId = 1, Symbol = "TCS", ActionType = "REAL_SIGNAL_SKIPPED", Reason = "noise", ExecutedAt = yesterdayNoon },
            new RealTradeExecutionLog { Id = 3, UserId = 1, Symbol = "INFY", ActionType = "REAL_SELL", Reason = "old", ExecutedAt = tenDaysAgo }
        });
        _brokerRepo.Setup(r => r.GetSinceAsync(It.IsAny<DateTime>(), It.IsAny<int>())).ReturnsAsync(new List<BrokerApiEvent>
        {
            new() { Id = 10, OccurredAt = yesterdayNoon.AddMinutes(5), Source = BrokerApiSource.Session, Operation = "historical sync", Level = "warning", Message = "token rejected", RepeatCount = 1 },
            new() { Id = 11, OccurredAt = yesterdayNoon, Source = BrokerApiSource.Rest, Operation = "GET /orders", Level = "error", Message = "other user", UserId = 2, RepeatCount = 1 }
        });

        var result = await CreateService().GetHistoryAsync(1, TodayIst.AddDays(-6), TodayIst);

        Assert.Equal(TodayIst.AddDays(-6), result.FromIst);
        Assert.Equal(new[] { "zapi-10", "rt-1" }, result.Items.Select(i => i.Id)); // newest first; noise, out-of-range and user 2 dropped
        Assert.Equal("Zerodha call skipped", result.Items[0].Title);
        Assert.False(result.Truncated);
    }

    [Fact]
    public async Task History_ClampsTheRangeTo31DaysAndNeverPastToday()
    {
        _realRepo.Setup(r => r.GetLogsSinceAsync(It.IsAny<int>(), It.IsAny<DateTime>(), It.IsAny<int>())).ReturnsAsync(Array.Empty<RealTradeExecutionLog>());
        _brokerRepo.Setup(r => r.GetSinceAsync(It.IsAny<DateTime>(), It.IsAny<int>())).ReturnsAsync(new List<BrokerApiEvent>());

        var result = await CreateService().GetHistoryAsync(1, TodayIst.AddDays(-200), TodayIst.AddDays(5));

        Assert.Equal(TodayIst, result.ToIst);
        Assert.Equal(TodayIst.AddDays(-(NotificationService.MaxHistoryDays - 1)), result.FromIst);
    }
}
