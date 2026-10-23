using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using QuantEdge.Infrastructure.DTOs;
using QuantEdge.Infrastructure.Helpers;

namespace QuantEdge.Infrastructure.Services;

/// <summary>
/// Trading Report maths on an already-loaded list of closed trades (net of charges). The summary, period table and
/// equity curve are all computed from the same list, so one request shows one consistent set of numbers.
///
/// Capital base: "invested" on a trade is its entry value, and summing it over trades gives turnover - the same
/// ₹1L traded 30 times counts as ₹30L. ROI and drawdown are therefore taken on the peak capital deployed (the most
/// entry value held open at one time, overlapping trades summed), which is the capital the trading actually needed.
/// </summary>
public static class TradingReportCalculator
{
    public static TradingReportSummaryDto Summary(IReadOnlyList<TradingReportTradeDto> trades)
    {
        if (trades.Count == 0)
        {
            return new TradingReportSummaryDto(
                TotalInvestedCapital: 0m,
                NetRealizedPnl: 0m,
                TotalRoiPct: 0m,
                TotalTrades: 0,
                WinningTrades: 0,
                LosingTrades: 0,
                WinRatePct: 0m,
                GrossProfit: 0m,
                GrossLoss: 0m,
                ProfitFactor: 0m,
                AvgTradePnl: 0m,
                AvgTradeRoiPct: 0m,
                MaxDrawdownPct: 0m,
                BestTradePnl: 0m,
                WorstTradePnl: 0m
            );
        }

        // All performance stats below are on NET P&L (after charges) - what actually reaches the account.
        decimal totalInvested = trades.Sum(t => t.InvestedAmount);
        decimal grossPnl = trades.Sum(t => t.RealizedPnl);
        decimal totalCharges = trades.Sum(t => t.Charges);
        decimal netPnl = trades.Sum(t => t.NetPnl);
        decimal capital = PeakCapitalDeployed(trades);

        int wins = trades.Count(t => t.NetPnl > 0);
        int losses = trades.Count(t => t.NetPnl < 0);
        decimal winRate = Math.Round((decimal)wins / trades.Count * 100m, 2);

        decimal grossProfit = trades.Where(t => t.NetPnl > 0).Sum(t => t.NetPnl);
        decimal grossLoss = Math.Abs(trades.Where(t => t.NetPnl < 0).Sum(t => t.NetPnl));
        decimal profitFactor = grossLoss > 0m ? Math.Round(grossProfit / grossLoss, 2) : (grossProfit > 0m ? 99.99m : 0m);

        // Drawdown on equity = capital + cumulative net P&L, measured from its running peak.
        decimal cumulative = 0m, peakPnl = 0m, maxDrawdownPct = 0m;
        foreach (var t in trades.OrderBy(x => x.ExecutedAt))
        {
            cumulative += t.NetPnl;
            if (cumulative > peakPnl) peakPnl = cumulative;
            decimal peakEquity = capital + peakPnl;
            if (peakEquity > 0m)
            {
                decimal dd = (peakPnl - cumulative) / peakEquity * 100m;
                if (dd > maxDrawdownPct) maxDrawdownPct = dd;
            }
        }

        return new TradingReportSummaryDto(
            TotalInvestedCapital: Math.Round(totalInvested, 2),
            NetRealizedPnl: Math.Round(netPnl, 2),
            TotalRoiPct: Pct(netPnl, capital),
            TotalTrades: trades.Count,
            WinningTrades: wins,
            LosingTrades: losses,
            WinRatePct: winRate,
            GrossProfit: Math.Round(grossProfit, 2),
            GrossLoss: Math.Round(grossLoss, 2),
            ProfitFactor: profitFactor,
            AvgTradePnl: Math.Round(netPnl / trades.Count, 2),
            AvgTradeRoiPct: Math.Round(trades.Average(t => t.ReturnPct), 2),
            MaxDrawdownPct: Math.Round(maxDrawdownPct, 2),
            BestTradePnl: Math.Round(trades.Max(t => t.NetPnl), 2),
            WorstTradePnl: Math.Round(trades.Min(t => t.NetPnl), 2),
            GrossRealizedPnl: Math.Round(grossPnl, 2),
            TotalCharges: Math.Round(totalCharges, 2),
            PeakCapitalDeployed: Math.Round(capital, 2)
        );
    }

    /// <summary>Period buckets by IST trading date, newest first. CumulativePnl runs oldest → newest.</summary>
    public static List<TradingReportPeriodDto> Periods(IReadOnlyList<TradingReportTradeDto> trades, string? periodType)
    {
        if (trades.Count == 0) return new List<TradingReportPeriodDto>();

        periodType = (periodType ?? "daily").ToLowerInvariant();
        var grouped = trades
            .OrderBy(t => t.ExecutedAt)
            .GroupBy(t => GetPeriodKey(DayChangeCalculator.IstDate(t.ExecutedAt), periodType));

        var periodList = new List<TradingReportPeriodDto>();
        decimal runningPnl = 0m;

        foreach (var g in grouped)
        {
            var periodTrades = g.ToList();
            var minDate = DayChangeCalculator.IstDate(periodTrades.Min(t => t.ExecutedAt));
            var maxDate = DayChangeCalculator.IstDate(periodTrades.Max(t => t.ExecutedAt));

            decimal periodInvested = periodTrades.Sum(t => t.InvestedAmount);
            decimal periodGrossProfit = periodTrades.Where(t => t.NetPnl > 0).Sum(t => t.NetPnl);
            decimal periodGrossLoss = Math.Abs(periodTrades.Where(t => t.NetPnl < 0).Sum(t => t.NetPnl));
            decimal periodNetPnl = periodTrades.Sum(t => t.NetPnl);

            int winCount = periodTrades.Count(t => t.NetPnl > 0);
            int lossCount = periodTrades.Count(t => t.NetPnl < 0);
            decimal winRate = Math.Round((decimal)winCount / periodTrades.Count * 100m, 2);

            runningPnl += periodNetPnl;

            periodList.Add(new TradingReportPeriodDto(
                PeriodKey: g.Key,
                PeriodLabel: FormatPeriodLabel(g.Key, minDate, maxDate, periodType),
                StartDate: minDate,
                EndDate: maxDate,
                TotalTrades: periodTrades.Count,
                WinTrades: winCount,
                LossTrades: lossCount,
                WinRatePct: winRate,
                InvestedCapital: Math.Round(periodInvested, 2),
                GrossProfit: Math.Round(periodGrossProfit, 2),
                GrossLoss: Math.Round(periodGrossLoss, 2),
                NetPnl: Math.Round(periodNetPnl, 2),
                RoiPct: Pct(periodNetPnl, PeakCapitalDeployed(periodTrades)),
                CumulativePnl: Math.Round(runningPnl, 2)
            ));
        }

        return periodList.OrderByDescending(p => p.StartDate).ToList();
    }

    public static PagedResult<TradingReportPeriodDto> PagePeriods(IReadOnlyList<TradingReportPeriodDto> periods, string? pnlFilter, int page, int pageSize)
    {
        IEnumerable<TradingReportPeriodDto> filtered = periods;
        if (string.Equals(pnlFilter, "profit", StringComparison.OrdinalIgnoreCase))
        {
            filtered = periods.Where(p => p.NetPnl > 0);
        }
        else if (string.Equals(pnlFilter, "loss", StringComparison.OrdinalIgnoreCase))
        {
            filtered = periods.Where(p => p.NetPnl < 0);
        }

        var all = filtered.ToList();
        page = page < 1 ? 1 : page;
        pageSize = pageSize < 1 ? 10 : pageSize;
        long totalCount = all.Count;
        int totalPages = totalCount > 0 ? (int)Math.Ceiling((double)totalCount / pageSize) : 0;

        var pagedItems = all.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        return new PagedResult<TradingReportPeriodDto>(pagedItems, totalCount, page, pageSize, totalPages);
    }

    public static List<TradingReportEquityPointDto> EquityCurve(IReadOnlyList<TradingReportTradeDto> trades)
    {
        var ordered = trades.OrderBy(t => t.ExecutedAt).ToList();
        var peakAtClose = PeakDeployedAtEachClose(ordered);

        var result = new List<TradingReportEquityPointDto>(ordered.Count);
        decimal runningPnl = 0m;
        decimal runningInvested = 0m;

        for (int i = 0; i < ordered.Count; i++)
        {
            var t = ordered[i];
            runningPnl += t.NetPnl;
            runningInvested += t.InvestedAmount;

            result.Add(new TradingReportEquityPointDto(
                Timestamp: t.ExecutedAt,
                Label: ToIst(t.ExecutedAt).ToString("dd MMM yyyy HH:mm"),
                TradePnl: t.NetPnl,
                CumulativePnl: Math.Round(runningPnl, 2),
                InvestedCapital: Math.Round(runningInvested, 2),
                CumulativeRoiPct: Pct(runningPnl, peakAtClose[i])
            ));
        }

        return result;
    }

    /// <summary>Most entry value held open at one time across <paramref name="trades"/>.</summary>
    public static decimal PeakCapitalDeployed(IReadOnlyList<TradingReportTradeDto> trades)
    {
        var peaks = PeakDeployedAtEachClose(trades);
        return peaks.Length == 0 ? 0m : peaks.Max();
    }

    // Sweeps entries (+entry value) and exits (−entry value) in time order; for each trade returns the peak deployed
    // capital seen up to its exit. An unknown entry time counts as opened at the exit (an instantaneous trade still
    // needed its capital). Entries sort before exits at the same instant.
    private static decimal[] PeakDeployedAtEachClose(IReadOnlyList<TradingReportTradeDto> trades)
    {
        var events = new List<(DateTime At, int Order, int Index, decimal Delta)>(trades.Count * 2);
        for (int i = 0; i < trades.Count; i++)
        {
            var t = trades[i];
            DateTime close = t.ExecutedAt;
            DateTime open = t.OpenedAt.HasValue && t.OpenedAt.Value < close ? t.OpenedAt.Value : close;
            events.Add((open, 0, i, t.InvestedAmount));
            events.Add((close, 1, i, -t.InvestedAmount));
        }
        events.Sort((a, b) => a.At != b.At ? a.At.CompareTo(b.At) : a.Order.CompareTo(b.Order));

        var result = new decimal[trades.Count];
        decimal deployed = 0m, peak = 0m;
        foreach (var e in events)
        {
            deployed += e.Delta;
            if (deployed > peak) peak = deployed;
            if (e.Order == 1) result[e.Index] = peak;
        }
        return result;
    }

    private static decimal Pct(decimal value, decimal basis) =>
        basis > 0m ? Math.Round(value / basis * 100m, 2) : 0m;

    private static DateTime ToIst(DateTime time)
    {
        var utc = time.Kind switch
        {
            DateTimeKind.Utc => time,
            DateTimeKind.Local => time.ToUniversalTime(),
            _ => DateTime.SpecifyKind(time, DateTimeKind.Utc)
        };
        return TimeZoneInfo.ConvertTimeFromUtc(utc, TimeZoneHelper.IndianTimeZone);
    }

    // istDate is the IST trading date. Weekly keys use the ISO week-year: 29-31 Dec can be week 1 of the next year.
    private static string GetPeriodKey(DateTime istDate, string periodType)
    {
        return periodType switch
        {
            "daily" => istDate.ToString("yyyy-MM-dd"),
            "weekly" => $"{ISOWeek.GetYear(istDate)}-W{ISOWeek.GetWeekOfYear(istDate):D2}",
            "fortnightly" => istDate.Day <= 15 ? $"{istDate:yyyy-MM}-H1" : $"{istDate:yyyy-MM}-H2",
            "monthly" => istDate.ToString("yyyy-MM"),
            "yearly" => istDate.ToString("yyyy"),
            _ => istDate.ToString("yyyy-MM-dd")
        };
    }

    private static string FormatPeriodLabel(string key, DateTime minDate, DateTime maxDate, string periodType)
    {
        return periodType switch
        {
            "daily" => minDate.ToString("dd MMM yyyy (ddd)"),
            "weekly" => $"Week {ISOWeek.GetWeekOfYear(minDate)} ({minDate:dd MMM} – {maxDate:dd MMM yyyy})",
            "fortnightly" => key.EndsWith("-H1")
                ? $"01–15 {minDate:MMM yyyy}"
                : $"16–{DateTime.DaysInMonth(minDate.Year, minDate.Month):D2} {minDate:MMM yyyy}",
            "monthly" => minDate.ToString("MMMM yyyy"),
            "yearly" => $"Year {minDate.Year}",
            _ => minDate.ToString("dd MMM yyyy")
        };
    }
}
