using System;
using System.Collections.Generic;
using System.Linq;

namespace QuantEdge.Infrastructure.Services.Backtest;

/// <summary>
/// Checks every simulated trade against the rules a BUY and a SHORT must obey (stop and target on the right side, P&amp;L
/// in the right direction, shorts closed the same day...). A failure means the logic or the data is wrong, whatever the
/// profit says. Passing shows the trades obey these rules; whether the rules make money is the verdict's question.
/// </summary>
public static class BacktestLogicAudit
{
    private const int MaxExamples = 5;

    public static List<BacktestLogicCheck> Run(IReadOnlyList<BacktestTrade> trades, string shortEntryCutoff, string shortSquareOffTime)
    {
        var checks = new List<BacktestLogicCheck>();
        TimeSpan cutoff = SwingTradeRules.ParseIstTime(shortEntryCutoff, SwingTradeRules.DefaultShortEntryCutoff);
        TimeSpan squareOff = SwingTradeRules.ParseIstTime(shortSquareOffTime, SwingTradeRules.DefaultShortSquareOffTime);
        var overlapping = OverlappingTrades(trades);

        foreach (var side in trades.GroupBy(t => t.Side).OrderBy(g => g.Key))
        {
            bool isShort = side.Key == BacktestSide.Short;
            var list = side.ToList();

            void Check(string rule, Func<BacktestTrade, bool> applies, Func<BacktestTrade, bool> holds)
            {
                var relevant = list.Where(applies).ToList();
                var broken = relevant.Where(t => !holds(t)).ToList();
                checks.Add(new BacktestLogicCheck
                {
                    Side = side.Key,
                    Rule = rule,
                    Checked = relevant.Count,
                    Failed = broken.Count,
                    Examples = broken.Take(MaxExamples).Select(Describe).ToList()
                });
            }

            Check(isShort ? "Stop above the entry, target below it" : "Stop below the entry, target above it",
                _ => true,
                // A rule with no fixed target stores 0: only the stop applies.
                t => isShort ? t.StopLoss > t.EntryRaw && (t.Target <= 0m || t.Target < t.EntryRaw) : t.StopLoss < t.EntryRaw && (t.Target <= 0m || t.Target > t.EntryRaw));
            Check("A target exit happened at or beyond the target",
                t => t.ExitCategory == "Target",
                t => isShort ? t.ExitRaw <= t.Target : t.ExitRaw >= t.Target);
            Check("A stop exit happened at or beyond the stop",
                t => t.ExitCategory is "Stop loss" or "Emergency stop",
                t => isShort ? t.ExitRaw >= t.StopLoss : t.ExitRaw <= t.StopLoss);
            Check(isShort ? "Profit when the price fell, loss when it rose (before charges)" : "Profit when the price rose, loss when it fell (before charges)",
                _ => true,
                t => Math.Round(t.Quantity * (isShort ? t.EntryPrice - t.ExitPrice : t.ExitPrice - t.EntryPrice), 2) == t.GrossPnl);
            Check("Net P&L is the gross P&L less charges",
                _ => true,
                t => t.Charges >= 0m && t.GrossPnl - t.Charges == t.NetPnl);
            Check("The entry came after the signal, and the exit after the entry (no look-ahead)",
                _ => true,
                t => t.EntryTime >= t.SignalTime && t.ExitTime >= t.EntryTime);
            Check("Only one open position per stock at a time",
                _ => true,
                t => !overlapping.Contains(t));

            if (isShort)
            {
                Check($"No new short at or after {shortEntryCutoff}",
                    _ => true,
                    t => BacktestEngine.ToIst(t.SignalTime).TimeOfDay < cutoff);
                Check("Every short was bought back the same day",
                    t => !t.EndOfData,
                    t => t.SessionsHeld == 0);
                Check($"The square-off happened at or after {shortSquareOffTime}, not earlier",
                    t => t.ExitCategory == "Square-off" && t.SessionsHeld == 0,
                    t => BacktestEngine.ToIst(t.ExitTime).TimeOfDay >= squareOff);
            }
        }
        return checks;
    }

    /// <summary>The checks a new rule adds to <see cref="Run"/>: its portfolio limits and, for an intraday rule, flat by the close and no late entries.</summary>
    public static List<BacktestLogicCheck> RunRuleLimits(IReadOnlyList<BacktestTrade> trades, int maxPositions, int maxPerDay, int maxPerSector,
        string? intradayLastSignal)
    {
        var checks = new List<BacktestLogicCheck>();
        var tooManyOpen = trades.Where(t => trades.Count(o => o.EntryTime <= t.EntryTime && o.ExitTime > t.EntryTime) > maxPositions).ToHashSet();
        var tooManyInDay = trades.GroupBy(t => BacktestEngine.IstDate(t.EntryTime)).Where(g => g.Count() > maxPerDay).SelectMany(g => g).ToHashSet();
        var tooManyInSector = maxPerSector <= 0 ? new HashSet<BacktestTrade>() : trades
            .Where(t => t.Sector != null && trades.Count(o => o.Sector == t.Sector && o.EntryTime <= t.EntryTime && o.ExitTime > t.EntryTime) > maxPerSector)
            .ToHashSet();
        TimeSpan lastSignal = SwingTradeRules.ParseIstTime(intradayLastSignal, TimeSpan.Zero);

        foreach (var side in trades.GroupBy(t => t.Side).OrderBy(g => g.Key))
        {
            var list = side.ToList();
            void Check(string rule, Func<BacktestTrade, bool> applies, Func<BacktestTrade, bool> holds)
            {
                var relevant = list.Where(applies).ToList();
                var broken = relevant.Where(t => !holds(t)).ToList();
                checks.Add(new BacktestLogicCheck
                {
                    Side = side.Key, Rule = rule, Checked = relevant.Count, Failed = broken.Count,
                    Examples = broken.Take(MaxExamples).Select(Describe).ToList()
                });
            }

            Check($"No more than {maxPositions} positions open at once", _ => true, t => !tooManyOpen.Contains(t));
            Check($"No more than {maxPerDay} new entries a day", _ => true, t => !tooManyInDay.Contains(t));
            if (maxPerSector > 0)
                Check($"No more than {maxPerSector} open positions in a sector", t => t.Sector != null, t => !tooManyInSector.Contains(t));
            if (intradayLastSignal != null)
            {
                Check($"No signal after {intradayLastSignal}", _ => true, t => BacktestEngine.ToIst(t.SignalTime).TimeOfDay <= lastSignal);
                Check("Every trade closed the same day", t => !t.EndOfData, t => t.SessionsHeld == 0);
            }
        }
        return checks;
    }

    private static HashSet<BacktestTrade> OverlappingTrades(IReadOnlyList<BacktestTrade> trades)
    {
        var overlapping = new HashSet<BacktestTrade>();
        foreach (var symbol in trades.GroupBy(t => t.Symbol))
        {
            BacktestTrade? previous = null;
            foreach (var t in symbol.OrderBy(t => t.EntryTime))
            {
                if (previous != null && t.EntryTime < previous.ExitTime) overlapping.Add(t);
                if (previous == null || t.ExitTime > previous.ExitTime) previous = t;
            }
        }
        return overlapping;
    }

    private static string Describe(BacktestTrade t) =>
        $"{t.Symbol} {BacktestEngine.ToIst(t.EntryTime):dd-MMM-yy HH:mm}: entry {t.EntryRaw:0.##}, stop {t.StopLoss:0.##}, target {t.Target:0.##}, exit {t.ExitRaw:0.##} ({t.ExitCategory})";
}
