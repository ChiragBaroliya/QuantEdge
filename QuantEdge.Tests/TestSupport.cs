using QuantEdge.Domain.Entities;
using QuantEdge.Infrastructure.Helpers;
using QuantEdge.Infrastructure.Services;
using Xunit;

// The tests pin SwingTradeRules' static clock, so they must not run in parallel.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace QuantEdge.Tests;

/// <summary>Pins the rules clock to a fixed IST time on a normal trading day (Wednesday) and restores it afterwards.</summary>
public abstract class ClockedTest : IDisposable
{
    // Wednesday 07-Oct-2026, 11:00 IST.
    protected static readonly DateTime TradingDayIst = new(2026, 10, 7, 11, 0, 0, DateTimeKind.Unspecified);

    protected ClockedTest() => SetIstNow(TradingDayIst);

    protected static void SetIstNow(DateTime ist)
    {
        DateTime utc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(ist, DateTimeKind.Unspecified), TimeZoneHelper.IndianTimeZone);
        SwingTradeRules.UtcNow = () => utc;
    }

    protected static DateTime IstToUtc(DateTime ist) =>
        TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(ist, DateTimeKind.Unspecified), TimeZoneHelper.IndianTimeZone);

    public void Dispose()
    {
        SwingTradeRules.UtcNow = () => DateTime.UtcNow;
        GC.SuppressFinalize(this);
    }
}

/// <summary>Deterministic synthetic candles for the decision engines.</summary>
public static class Candles
{
    /// <summary>
    /// Daily candles ending yesterday: a steady trend of <paramref name="dailyPct"/> % per day (negative = falling), with a
    /// small zig-zag so ADX/ATR are realistic.
    /// </summary>
    public static List<MarketCandle> Daily(string symbol, int count, decimal start, decimal dailyPct)
    {
        var list = new List<MarketCandle>();
        DateTime day = DateTime.UtcNow.Date.AddDays(-count - 1);
        decimal close = start;
        for (int i = 0; i < count; i++)
        {
            decimal wiggle = i % 2 == 0 ? 0.3m : -0.3m;
            decimal open = close;
            close = Math.Max(1m, close * (1m + (dailyPct + wiggle * 0.1m) / 100m));
            decimal high = Math.Max(open, close) * 1.006m;
            decimal low = Math.Min(open, close) * 0.994m;
            list.Add(new MarketCandle { Symbol = symbol, Timeframe = "1d", CandleTime = day.AddDays(i), Open = open, High = high, Low = low, Close = close, Volume = 100000 });
        }
        return list;
    }

    /// <summary>
    /// Intraday candles (15m / 60m) over the last few days ending well in the past, trending by <paramref name="barPct"/> %
    /// per bar. The final bar is a strong candle in the trend direction with a volume spike.
    /// </summary>
    public static List<MarketCandle> Intraday(string symbol, string timeframe, int count, decimal start, decimal barPct)
    {
        int minutes = timeframe == "60m" ? 60 : 15;
        int barsPerDay = timeframe == "60m" ? 6 : 25;
        var list = new List<MarketCandle>();
        DateTime firstDayUtc = DateTime.UtcNow.Date.AddDays(-(count / barsPerDay) - 3);
        decimal close = start;
        for (int i = 0; i < count; i++)
        {
            int day = i / barsPerDay;
            int slot = i % barsPerDay;
            DateTime time = firstDayUtc.AddDays(day).AddHours(3).AddMinutes(45 + slot * minutes); // 09:15 IST + slot
            bool last = i == count - 1;
            decimal wiggle = i % 2 == 0 ? 0.05m : -0.05m;
            decimal open = close;
            close = close * (1m + (barPct * (last ? 4m : 1m) + (last ? 0m : wiggle)) / 100m);
            bool falling = close < open;
            // Strong final candle: closes at its extreme in the trend direction.
            decimal high = last && !falling ? close : Math.Max(open, close) * 1.001m;
            decimal low = last && falling ? close : Math.Min(open, close) * 0.999m;
            list.Add(new MarketCandle
            {
                Symbol = symbol, Timeframe = timeframe, CandleTime = time,
                Open = open, High = high, Low = low, Close = close,
                Volume = last ? 5000 : 1000
            });
        }
        return list;
    }
}
