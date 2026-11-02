using System;
using System.Collections.Generic;
using System.Linq;
using QuantEdge.Domain.Entities;

namespace QuantEdge.Infrastructure.Services.Backtest;

/// <summary>What the check found in one stock's 15m bars: test days, days that can't be trusted, and days without a 15:15 bar.</summary>
public sealed record IntradayDataCheck(int Days, int BadDays, int NoCloseBarDays);

/// <summary>
/// Stops a run from looking valid on bad candles. History loaded on a server outside India before the 10-Aug-2026 fix stored
/// afternoon bars under morning times, so those days have too few bars and a "09:15" open that isn't the day's open.
/// </summary>
public static class BacktestDataCheck
{
    public const int MinBarsPerDay = 20;
    public const decimal MaxOpenGapPct = 0.5m;
    /// <summary>Above this share of bad stock-days, a run's verdict can't be trusted either way.</summary>
    public const decimal MaxBadSharePct = 1m;
    private static readonly TimeSpan FirstBar = new(9, 15, 0);
    private static readonly TimeSpan CloseBar = new(15, 15, 0);

    /// <summary><paramref name="daily"/> has one bar per trading day (<see cref="BacktestEngine.OneBarPerDay"/>).</summary>
    public static IntradayDataCheck Check15m(IReadOnlyList<MarketCandle> m15, IReadOnlyList<MarketCandle> daily, DateTime fromDate, DateTime toDate)
    {
        var dailyOpen = daily.ToDictionary(c => BacktestEngine.IstDate(c.CandleTime), c => c.Open);
        int days = 0, bad = 0, noClose = 0;
        foreach (var dayBars in m15.GroupBy(c => BacktestEngine.IstDate(c.CandleTime)))
        {
            if (dayBars.Key < fromDate.Date || dayBars.Key > toDate.Date) continue;
            var bars = dayBars.OrderBy(c => c.CandleTime).ToList();
            days++;
            if (BacktestEngine.ToIst(bars[^1].CandleTime).TimeOfDay < CloseBar) noClose++;
            bool wrongOpen = dailyOpen.TryGetValue(dayBars.Key, out decimal open) && open > 0m
                && Math.Abs(bars[0].Open - open) / open * 100m > MaxOpenGapPct;
            if (bars.Count < MinBarsPerDay || BacktestEngine.ToIst(bars[0].CandleTime).TimeOfDay != FirstBar || wrongOpen) bad++;
        }
        return new IntradayDataCheck(days, bad, noClose);
    }

    /// <summary>Adds the result to the report. Too many bad days means Not proven, even for a run that lost money: the bars themselves are wrong.</summary>
    public static void Apply(BacktestSummary summary, IReadOnlyCollection<IntradayDataCheck> checks)
    {
        int days = checks.Sum(c => c.Days), bad = checks.Sum(c => c.BadDays);
        summary.Coverage.StockDaysChecked = days;
        summary.Coverage.StockDaysBad = bad;
        summary.Coverage.StockDaysNoCloseBar = checks.Sum(c => c.NoCloseBarDays);
        summary.Coverage.SymbolsWithBadDays = checks.Count(c => c.BadDays > 0);
        decimal share = days > 0 ? Math.Round(100m * bad / days, 1) : 0m;
        if (share <= MaxBadSharePct) return;

        string warning = $"{share}% of stock-days have wrong or incomplete 15m bars ({bad:N0} of {days:N0}, {summary.Coverage.SymbolsWithBadDays} stocks), so this result can't be trusted. Repair the candles and run it again.";
        summary.DataWarning = warning;
        summary.Verdict = "NOT_PROVEN";
        summary.VerdictReason = warning;
        foreach (var side in summary.Sides)
        {
            side.Verdict = "NOT_PROVEN";
            side.VerdictReason = warning;
        }
    }
}
