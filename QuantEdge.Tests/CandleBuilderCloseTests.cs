using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using QuantEdge.Infrastructure.Configurations;
using QuantEdge.Infrastructure.DTOs;
using QuantEdge.Infrastructure.Helpers;
using QuantEdge.Infrastructure.Services;
using Xunit;

namespace QuantEdge.Tests;

/// <summary>
/// Daily / last-bar candles must close at the 15:30 IST session end, not on the next trading day's first tick -
/// a Friday's daily candle used to wait for Monday (and was lost if the feed process restarted over the weekend).
/// </summary>
public class CandleBuilderCloseTests
{
    private static readonly DateTime Friday = new(2026, 10, 23);   // a Friday

    private static DateTime Utc(DateTime ist) =>
        TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(ist, DateTimeKind.Unspecified), TimeZoneHelper.IndianTimeZone);

    private static (CandleBuilderService Builder, List<CandleDto> Closed) NewBuilder(params string[] timeframes)
    {
        var builder = new CandleBuilderService(Options.Create(new BrokerConfig { Timeframes = timeframes }));
        var closed = new List<CandleDto>();
        builder.OnCandleClosed += c => { closed.Add(c); return Task.CompletedTask; };
        return (builder, closed);
    }

    private static TickDataDto Tick(decimal ltp, DateTime ist) => new("INFY", ltp, 10, Utc(ist));

    [Fact]
    public void PeriodEnd_DailyCandleEndsAtSessionClose()
    {
        var start = CandleBuilderService.GetIntervalStart(Utc(Friday.AddHours(10)), TimeSpan.FromDays(1));

        Assert.Equal(Utc(Friday.AddHours(15).AddMinutes(30)), CandleBuilderService.GetPeriodEnd(start, TimeSpan.FromDays(1)));
    }

    [Fact]
    public void PeriodEnd_LastHourlyBarIsCappedAtSessionClose()
    {
        var start = Utc(Friday.AddHours(15).AddMinutes(15));

        Assert.Equal(Utc(Friday.AddHours(15).AddMinutes(30)), CandleBuilderService.GetPeriodEnd(start, TimeSpan.FromMinutes(60)));
        Assert.Equal(Utc(Friday.AddHours(10).AddMinutes(15)), CandleBuilderService.GetPeriodEnd(Utc(Friday.AddHours(9).AddMinutes(15)), TimeSpan.FromMinutes(60)));
    }

    [Fact]
    public async Task FridayDailyCandle_ClosesAtSessionEnd_WithoutAMondayTick()
    {
        var (builder, closed) = NewBuilder("1d");
        await builder.ProcessTickAsync(Tick(100m, Friday.AddHours(9).AddMinutes(15)));
        await builder.ProcessTickAsync(Tick(110m, Friday.AddHours(12)));
        await builder.ProcessTickAsync(Tick(105m, Friday.AddHours(15).AddMinutes(29)));

        await builder.CloseCompletedCandlesAsync(Utc(Friday.AddHours(15).AddMinutes(29).AddSeconds(30)));
        Assert.Empty(closed);   // session still open

        await builder.CloseCompletedCandlesAsync(Utc(Friday.AddHours(15).AddMinutes(30)));

        var daily = Assert.Single(closed);
        Assert.True(daily.IsClosed);
        Assert.Equal((100m, 110m, 100m, 105m), (daily.Open, daily.High, daily.Low, daily.Close));
        Assert.Equal(Utc(Friday), daily.Timestamp);   // 00:00 IST, same as Kite's daily candle
    }

    [Fact]
    public async Task ClosedByTime_LateTickForSamePeriodIsIgnored_NextPeriodStartsFresh()
    {
        var (builder, closed) = NewBuilder("15m");
        await builder.ProcessTickAsync(Tick(100m, Friday.AddHours(10).AddMinutes(1)));
        await builder.CloseCompletedCandlesAsync(Utc(Friday.AddHours(10).AddMinutes(15)));
        Assert.Single(closed);

        // A tick stamped inside the closed 10:00 bar arrives late - it must not reopen (and later overwrite) that bar.
        await builder.ProcessTickAsync(Tick(90m, Friday.AddHours(10).AddMinutes(14)));
        await builder.ProcessTickAsync(Tick(101m, Friday.AddHours(10).AddMinutes(16)));
        await builder.ProcessTickAsync(Tick(102m, Friday.AddHours(10).AddMinutes(31)));

        Assert.Equal(2, closed.Count);
        Assert.Equal(Utc(Friday.AddHours(10).AddMinutes(15)), closed[1].Timestamp);
        Assert.Equal(101m, closed[1].Open);
    }
}
