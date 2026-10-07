using System;
using System.Collections.Generic;
using System.Linq;
using QuantEdge.Domain.Entities;
using QuantEdge.Infrastructure.DTOs;
using QuantEdge.Infrastructure.Helpers;

namespace QuantEdge.Infrastructure.Services;

/// <summary>
/// The one day-change formula every screen uses, matching NSE / TradingView:
///   Change = Ltp - PrevClose,  ChangePct = Change / PrevClose x 100,
/// where PrevClose is the close of the last session BEFORE the session Ltp belongs to.
///
/// Sources, in order:
///   1. LIVE    - the live_quotes row (Kite tick: last price + ohlc.close = previous session close), when it is
///                from the same or a newer session than the stored candles.
///   2. CANDLES - Ltp = newest stored intraday/daily close; PrevClose = last daily close dated before Ltp's
///                session. Never "last two daily rows": today's daily row can be a partial snapshot, or the
///                same day can be stored twice (UTC vs IST stamps).
/// </summary>
public static class DayChangeCalculator
{
    public static DayQuoteDto? Resolve(
        string symbol,
        LiveQuote? live,
        IReadOnlyList<MarketCandle>? dailyCandles,
        IReadOnlyList<MarketCandle>? intradayCandles = null)
    {
        var daily = dailyCandles ?? Array.Empty<MarketCandle>();
        var intraday = intradayCandles ?? Array.Empty<MarketCandle>();

        // Newest stored price and the session it belongs to.
        var lastIntraday = intraday.Count > 0 ? intraday[^1] : null;
        var lastDaily = daily.Count > 0 ? daily[^1] : null;
        MarketCandle? newestCandle = lastIntraday;
        if (newestCandle == null || (lastDaily != null && IstDate(lastDaily.CandleTime) > IstDate(lastIntraday!.CandleTime)))
        {
            newestCandle = lastDaily;
        }

        if (live != null && live.Ltp > 0m &&
            (newestCandle == null || IstDate(live.AsOfUtc) >= IstDate(newestCandle.CandleTime)))
        {
            decimal prev = live.PrevClose > 0m ? live.PrevClose : PrevCloseBefore(daily, IstDate(live.AsOfUtc));
            return Build(symbol, live.Ltp, prev, live.AsOfUtc, "LIVE");
        }

        if (newestCandle == null || newestCandle.Close <= 0m) return null;

        DateTime session = IstDate(newestCandle.CandleTime);
        decimal prevClose = PrevCloseBefore(daily, session);
        DateTime asOf = newestCandle == lastIntraday
            ? newestCandle.CandleTime.Add(IntervalOf(newestCandle.Timeframe))
            : newestCandle.CandleTime;
        return Build(symbol, newestCandle.Close, prevClose, asOf, "CANDLES");
    }

    /// <summary>Close of the newest daily candle whose IST trading date is before <paramref name="session"/>; 0 when none.</summary>
    public static decimal PrevCloseBefore(IReadOnlyList<MarketCandle> daily, DateTime session)
    {
        for (int i = daily.Count - 1; i >= 0; i--)
        {
            if (IstDate(daily[i].CandleTime) < session && daily[i].Close > 0m) return daily[i].Close;
        }
        return 0m;
    }

    public static DateTime IstDate(DateTime time)
    {
        var utc = time.Kind switch
        {
            DateTimeKind.Utc => time,
            DateTimeKind.Local => time.ToUniversalTime(),
            _ => DateTime.SpecifyKind(time, DateTimeKind.Utc)
        };
        return TimeZoneInfo.ConvertTimeFromUtc(utc, TimeZoneHelper.IndianTimeZone).Date;
    }

    private static DayQuoteDto Build(string symbol, decimal ltp, decimal prevClose, DateTime asOf, string source)
    {
        bool hasPrev = prevClose > 0m;
        var asOfUtc = asOf.Kind == DateTimeKind.Utc ? asOf : DateTime.SpecifyKind(asOf, DateTimeKind.Utc);
        return new DayQuoteDto
        {
            Symbol = symbol.ToUpperInvariant(),
            Ltp = Math.Round(ltp, 2),
            PrevClose = Math.Round(prevClose, 2),
            Change = hasPrev ? Math.Round(ltp - prevClose, 2) : null,
            ChangePct = hasPrev ? Math.Round((ltp - prevClose) / prevClose * 100m, 2) : null,
            // A still-forming intraday candle's end lies in the future; never report a future time.
            AsOfUtc = asOfUtc > DateTime.UtcNow ? DateTime.UtcNow : asOfUtc,
            Source = source
        };
    }

    private static TimeSpan IntervalOf(string? timeframe) => (timeframe ?? string.Empty).ToLowerInvariant() switch
    {
        "1m" => TimeSpan.FromMinutes(1),
        "5m" => TimeSpan.FromMinutes(5),
        "15m" => TimeSpan.FromMinutes(15),
        "60m" => TimeSpan.FromMinutes(60),
        _ => TimeSpan.Zero
    };
}
