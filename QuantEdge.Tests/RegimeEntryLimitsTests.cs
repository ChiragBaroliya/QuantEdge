using QuantEdge.Infrastructure.Services;
using Xunit;

namespace QuantEdge.Tests;

public class RegimeEntryLimitsTests
{
    // Weekdays only, with Wed 28 Oct 2026 as a market holiday.
    private static Task<bool> IsTradingDay(DateOnly d) =>
        Task.FromResult(d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && d != new DateOnly(2026, 10, 28));

    private static Task<bool> IsStale(int readingDay, int nowDay) =>
        RegimeGate.IsReadingStaleAsync(new DateTime(2026, 10, readingDay), new DateTime(2026, 10, nowDay, 10, 0, 0), IsTradingDay);

    [Theory]
    [InlineData(23, 26, false)] // Fri reading on Mon: nothing missed
    [InlineData(22, 26, false)] // Thu reading on Mon: Fri missed - one failed update is tolerated
    [InlineData(21, 26, true)]  // Wed reading on Mon: Thu and Fri missed
    [InlineData(27, 29, false)] // Tue reading on Thu: Wed was a holiday
    [InlineData(26, 29, false)] // Mon reading on Thu: only Tue missed, Wed was a holiday
    [InlineData(26, 30, true)]  // Mon reading on Fri: Tue and Thu missed
    [InlineData(30, 30, false)] // today's own reading
    public async Task ReadingIsStale_OnlyAfterMoreThanOneMissedSession(int readingDay, int nowDay, bool expected)
    {
        Assert.Equal(expected, await IsStale(readingDay, nowDay));
    }

    [Fact]
    public async Task LongOutage_IsStale()
    {
        Assert.True(await RegimeGate.IsReadingStaleAsync(new DateTime(2025, 1, 1), new DateTime(2026, 10, 30), IsTradingDay));
    }

    [Theory]
    [InlineData(10, 100, 0, 10000, 30, 10)]    // ₹3,000 limit, nothing open: 10 shares fit
    [InlineData(10, 100, 2500, 10000, 30, 5)]  // ₹500 of room left: reduced to 5
    [InlineData(10, 100, 3000, 10000, 30, 0)]  // limit reached
    [InlineData(10, 100, 9000, 10000, 30, 0)]  // already over the limit: never negative
    public void Exposure_LowersTheQuantityToFit(int quantity, decimal entry, decimal open, decimal capital, decimal pct, int expected)
    {
        Assert.Equal(expected, SwingTradeRules.LimitQuantityByExposure(quantity, entry, open, capital, pct).Quantity);
    }

    [Fact]
    public void Exposure_WithNoPercentageOrCapital_HasNoLimit()
    {
        Assert.Equal(10, SwingTradeRules.LimitQuantityByExposure(10, 100m, 999999m, 10000m, null).Quantity);
        Assert.Equal(10, SwingTradeRules.LimitQuantityByExposure(10, 100m, 999999m, 0m, 30m).Quantity);
    }

    [Fact]
    public void Exposure_ExplainsWhyItCut()
    {
        var (_, reason) = SwingTradeRules.LimitQuantityByExposure(10, 100m, 2500m, 10000m, 30m);

        Assert.Contains("exposure limit", reason);
        Assert.Contains("30%", reason);
    }
}
