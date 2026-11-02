using System;
using System.Collections.Generic;

namespace QuantEdge.Infrastructure.Services.Backtest;

/// <summary>
/// The daily momentum breakout rule, as pure decisions on numbers the caller has already worked out from closed daily bars.
/// The numbers are fixed here rather than on the form, so they can't be tuned until a backtest looks good. Backtest only:
/// no live trading uses this rule.
/// </summary>
public static class MomentumDailyRules
{
    public const int UniverseSize = 200;
    public const int TurnoverLookback = 60;
    public const int NiftyAverageDays = 200;
    public const int ReturnLookback = 126;
    public const int MinStrengthRank = 80;       // top 20%
    public const int YearHighLookback = 252;
    public const decimal MaxBelowYearHighPct = 5m;
    public const int BreakoutLookback = 50;
    public const decimal MinVolumeMultiple = 1.5m;
    public const int VolumeLookback = 20;
    public const int AtrPeriod = 14;
    public const decimal StopAtrMult = 2m;
    public const decimal EmergencyAtrMult = 3m;
    public const decimal TrailStartAtrMult = 2m;
    public const decimal TrailAtrMult = 3m;
    public const int NoProgressSessions = 10;
    public const int MaxSessions = 60;
    public const decimal RiskPct = 0.5m;
    public const decimal MaxPerTrade = 20_000m;
    public const int MaxPositions = 5;
    public const int MaxPerSector = 2;
    public const int MaxEntriesPerDay = 2;
    public const decimal DrawdownHaltPct = 10m;
    public const decimal DrawdownResumePct = 5m;
    public const int MinTrades = 100;

    public const string StopCategory = "Stop (next open)";
    public const string TrailCategory = "Trailing stop";
    public const string NoProgressCategory = "No progress";
    public const string MaxDaysCategory = "Max days";

    /// <summary>What a signal day's close looks like. PriorHigh is the highest high before today; YearHigh includes today.</summary>
    public sealed record EntryFacts(decimal Close, decimal PriorHigh, decimal YearHigh, decimal VolumeMultiple, int StrengthRank, bool NiftyAboveAverage);

    /// <summary>The first rule the day fails, or null when it is a signal.</summary>
    public static string? EntryBlock(EntryFacts f)
    {
        if (!f.NiftyAboveAverage) return "NIFTY below its 200-day average";
        if (f.StrengthRank < MinStrengthRank) return "not in the top 20% by 6-month return";
        if (f.Close < f.YearHigh * (1m - MaxBelowYearHighPct / 100m)) return "more than 5% below the 52-week high";
        if (f.Close <= f.PriorHigh) return "no close above the 50-day high";
        if (f.VolumeMultiple < MinVolumeMultiple) return "volume below 1.5 × average";
        return null;
    }

    /// <summary>Stop and emergency stop, from the entry before slippage (like the live levels), within the live caps.</summary>
    public static (decimal Stop, decimal Emergency) Levels(decimal entryRaw, decimal atr)
    {
        decimal unit = Math.Max(atr, entryRaw * SwingTradeRules.MinAtrPct / 100m);
        decimal stop = Math.Round(Math.Max(entryRaw - StopAtrMult * unit, entryRaw * (1m - SwingTradeRules.MaxStopLossPct / 100m)), 2);
        decimal emergency = Math.Round(Math.Max(entryRaw - EmergencyAtrMult * unit, entryRaw * (1m - SwingTradeRules.MaxEmergencyStopPct / 100m)), 2);
        return (stop, Math.Min(stop, emergency));
    }

    /// <summary>The emergency stop's fill on a bar: the open on a gap through it, else the level if the low reached it, else null.</summary>
    public static decimal? EmergencyFill(decimal open, decimal low, decimal emergency) =>
        open <= emergency ? open : low <= emergency ? emergency : null;

    /// <summary>An open trade's state, updated at each day's close by <see cref="AtClose"/>.</summary>
    public sealed class OpenTrade
    {
        public decimal EntryRaw { get; init; }
        public decimal EntryFill { get; init; }
        public decimal Stop { get; init; }
        public decimal Atr { get; init; }
        public decimal HighestClose { get; set; }
        public decimal? Trail { get; set; }
        public bool ProgressChecked { get; set; }
    }

    /// <summary>Decides at a day's close whether to sell at the next open (category and reason), then moves the trail up. It never moves down.</summary>
    public static (string Category, string Reason)? AtClose(OpenTrade t, decimal close, int sessionsHeld)
    {
        if (close <= t.Stop) return (StopCategory, $"Close below the stop @ ₹{t.Stop:F2} - sold at the next open");
        if (t.Trail is decimal trail && close <= trail) return (TrailCategory, $"Close below the trail @ ₹{trail:F2} - sold at the next open");
        if (sessionsHeld >= NoProgressSessions && !t.ProgressChecked)
        {
            t.ProgressChecked = true;
            if (close <= t.EntryFill) return (NoProgressCategory, $"Not above the entry after {NoProgressSessions} sessions - sold at the next open");
        }
        if (sessionsHeld >= MaxSessions) return (MaxDaysCategory, $"Held {MaxSessions} sessions - sold at the next open");

        t.HighestClose = Math.Max(t.HighestClose, close);
        decimal unit = Math.Max(t.Atr, t.EntryRaw * SwingTradeRules.MinAtrPct / 100m);
        if (t.Trail != null || t.HighestClose >= t.EntryRaw + TrailStartAtrMult * unit)
            t.Trail = Math.Round(Math.Max(Math.Max(t.Stop, t.Trail ?? 0m), t.HighestClose - TrailAtrMult * unit), 2);
        return null;
    }

    public static Dictionary<string, decimal> Numbers() => new()
    {
        ["universeSize"] = UniverseSize, ["turnoverLookback"] = TurnoverLookback, ["niftyAverageDays"] = NiftyAverageDays,
        ["returnLookback"] = ReturnLookback, ["minStrengthRank"] = MinStrengthRank, ["yearHighLookback"] = YearHighLookback,
        ["maxBelowYearHighPct"] = MaxBelowYearHighPct, ["breakoutLookback"] = BreakoutLookback, ["minVolumeMultiple"] = MinVolumeMultiple,
        ["volumeLookback"] = VolumeLookback, ["atrPeriod"] = AtrPeriod, ["stopAtrMult"] = StopAtrMult, ["emergencyAtrMult"] = EmergencyAtrMult,
        ["trailStartAtrMult"] = TrailStartAtrMult, ["trailAtrMult"] = TrailAtrMult, ["noProgressSessions"] = NoProgressSessions,
        ["maxSessions"] = MaxSessions, ["riskPct"] = RiskPct, ["maxPerTrade"] = MaxPerTrade, ["maxPositions"] = MaxPositions,
        ["maxPerSector"] = MaxPerSector, ["maxEntriesPerDay"] = MaxEntriesPerDay, ["drawdownHaltPct"] = DrawdownHaltPct,
        ["drawdownResumePct"] = DrawdownResumePct, ["minTrades"] = MinTrades
    };
}
