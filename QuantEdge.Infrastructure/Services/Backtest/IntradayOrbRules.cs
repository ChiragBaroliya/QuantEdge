using System;
using System.Collections.Generic;
using System.Linq;
using QuantEdge.Domain.Entities;

namespace QuantEdge.Infrastructure.Services.Backtest;

/// <summary>
/// The same-day opening range breakout rule, kept separate from the swing rules. The numbers are fixed here rather than on
/// the form, so they can't be tuned until a backtest looks good. Backtest only: no live trading uses this rule.
/// </summary>
public static class IntradayOrbRules
{
    public const int UniverseSize = MomentumDailyRules.UniverseSize;
    public const decimal MinVolumeMultiple = 2m;
    public const int VolumeLookback = 20;
    public const int StockAverageDays = 20;
    public const decimal MinStopPct = 0.75m;
    public const decimal MaxStopPct = 2.5m;
    public const decimal TargetR = 2m;
    public const decimal RiskPct = 0.5m;
    public const decimal MaxPerTrade = 20_000m;
    public const int MaxPositions = 3;
    public const int MaxEntriesPerDay = 3;
    public const int MinTrades = 100;
    public const string LastSignalText = "12:30";
    /// <summary>A signal exactly at 12:30 is allowed, and the short audit's check is "before this time".</summary>
    public const string ShortCutoffText = "12:31";
    public const string SquareOffText = "15:15";

    public static readonly TimeSpan RangeStart = new(9, 15, 0);
    public static readonly TimeSpan RangeSecondBar = new(9, 30, 0);
    public static readonly TimeSpan RangeEnd = new(9, 45, 0);
    public static readonly TimeSpan LastSignal = new(12, 30, 0);
    public static readonly TimeSpan SquareOff = new(15, 15, 0);

    public sealed record OpeningRange(decimal High, decimal Low, long Volume);

    /// <summary>The 09:15-09:45 range from one day's 15m bars; null if either bar is missing.</summary>
    public static OpeningRange? Range(IReadOnlyList<MarketCandle> dayBars)
    {
        var first = dayBars.FirstOrDefault(b => BacktestEngine.ToIst(b.CandleTime).TimeOfDay == RangeStart);
        var second = dayBars.FirstOrDefault(b => BacktestEngine.ToIst(b.CandleTime).TimeOfDay == RangeSecondBar);
        return first == null || second == null ? null
            : new OpeningRange(Math.Max(first.High, second.High), Math.Min(first.Low, second.Low), first.Volume + second.Volume);
    }

    /// <summary>+1 for a close above the range, -1 below it, 0 inside.</summary>
    public static int Breakout(decimal close, OpeningRange r) => close > r.High ? 1 : close < r.Low ? -1 : 0;

    /// <summary>Long needs NIFTY above yesterday's close and the stock's previous close above its average; a short needs both below.</summary>
    public static bool DirectionAllowed(bool isShort, decimal niftyNow, decimal niftyPrevClose, decimal stockPrevClose, decimal stockPrevAverage) =>
        isShort ? niftyNow < niftyPrevClose && stockPrevClose < stockPrevAverage
                : niftyNow > niftyPrevClose && stockPrevClose > stockPrevAverage;

    /// <summary>Stop on the opposite side of the range, target 2R away. Null when the stop would be outside the allowed distance, or the entry is past it.</summary>
    public static (decimal Stop, decimal Target)? Levels(bool isShort, decimal entryRaw, OpeningRange r)
    {
        decimal stop = Math.Round(isShort ? r.High : r.Low, 2);
        decimal risk = isShort ? stop - entryRaw : entryRaw - stop;
        decimal riskPct = entryRaw > 0m ? risk / entryRaw * 100m : 0m;
        if (riskPct < MinStopPct || riskPct > MaxStopPct) return null;
        decimal target = Math.Round(isShort ? entryRaw - TargetR * risk : entryRaw + TargetR * risk, 2);
        return (stop, target);
    }

    public static Dictionary<string, decimal> Numbers() => new()
    {
        ["universeSize"] = UniverseSize, ["minVolumeMultiple"] = MinVolumeMultiple, ["volumeLookback"] = VolumeLookback,
        ["stockAverageDays"] = StockAverageDays, ["minStopPct"] = MinStopPct, ["maxStopPct"] = MaxStopPct, ["targetR"] = TargetR,
        ["riskPct"] = RiskPct, ["maxPerTrade"] = MaxPerTrade, ["maxPositions"] = MaxPositions, ["maxEntriesPerDay"] = MaxEntriesPerDay,
        ["minTrades"] = MinTrades
    };
}
