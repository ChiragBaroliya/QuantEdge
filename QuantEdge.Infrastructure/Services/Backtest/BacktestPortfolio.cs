using System;
using System.Collections.Generic;
using System.Linq;

namespace QuantEdge.Infrastructure.Services.Backtest;

public sealed class PortfolioSettings
{
    public decimal Capital { get; init; } = 100_000m;
    public decimal AmountPerTrade { get; init; } = 10_000m;
    public bool RiskSizing { get; init; } = true;
    public decimal RiskPct { get; init; } = 1.0m;
    public int MaxPositions { get; init; } = SwingTradeRules.MaxConcurrentPositions;
    public int MaxTradesPerDay { get; init; } = 5;
    public decimal DailyLossLimit { get; init; } = 10_000m;
    public decimal SlippagePct { get; init; } = 0.10m;
    public IReadOnlyList<ChargeRates> Rates { get; init; } = ChargeRates.Defaults;
}

public sealed class PortfolioResult
{
    public List<BacktestTrade> Trades { get; } = new();
    public Dictionary<string, int> Skipped { get; } = new();
}

/// <summary>
/// Takes the strategy's signals in time order (highest score first within a scan, like the live bot) and applies the
/// live portfolio rules: one position per symbol, the daily trade cap, the daily loss limit, the open-position cap,
/// cash, and the position size. A trade's exit doesn't depend on the portfolio, so it is simulated once per signal and
/// reused by every run of the threshold sweep.
/// </summary>
public static class BacktestPortfolio
{
    private sealed class Open
    {
        public BacktestSignal Signal = null!;
        public BacktestExit Exit = null!;
        public int Qty;
        public decimal EntryFill;
        public decimal Cost;
    }

    public static PortfolioResult Run(IEnumerable<BacktestSignal> orderedSignals, Func<BacktestSignal, BacktestExit?> exitOf,
        PortfolioSettings ps, Func<string, string?> sectorOf)
    {
        var result = new PortfolioResult();
        var open = new List<Open>();
        decimal cash = ps.Capital;
        var realizedByDay = new Dictionary<DateTime, decimal>();
        var entriesByDay = new Dictionary<DateTime, int>();
        decimal slip = ps.SlippagePct / 100m;

        void Skip(string reason) => result.Skipped[reason] = result.Skipped.GetValueOrDefault(reason) + 1;

        void CloseUntil(DateTime timeUtc)
        {
            foreach (var o in open.Where(o => o.Exit.ExitTimeUtc <= timeUtc).OrderBy(o => o.Exit.ExitTimeUtc).ToList())
            {
                decimal exitFill = Math.Round(o.Exit.ExitRaw * (1m - slip), 2);
                decimal proceeds = o.Qty * exitFill;
                var entryIst = BacktestEngine.ToIst(o.Signal.EntryTimeUtc);
                var exitIst = BacktestEngine.ToIst(o.Exit.ExitTimeUtc);
                var rates = ChargesCalculator.RatesFor(ps.Rates, ChargesCalculator.ProductFor(entryIst, exitIst), exitIst);
                decimal charges = ChargesCalculator.RoundTrip(o.Cost, proceeds, rates).Total;
                decimal gross = proceeds - o.Cost;
                decimal net = gross - charges;
                cash += proceeds - charges;
                realizedByDay[o.Exit.SessionDate] = realizedByDay.GetValueOrDefault(o.Exit.SessionDate) + net;

                decimal riskPerShare = o.Signal.EntryRaw - o.Signal.StopLoss;
                if (riskPerShare <= 0m) riskPerShare = o.Signal.EntryRaw * SwingTradeRules.DefaultStopLossPct / 100m;
                result.Trades.Add(new BacktestTrade
                {
                    Symbol = o.Signal.Symbol,
                    Sector = sectorOf(o.Signal.Symbol),
                    Regime = o.Signal.Regime,
                    SignalTime = o.Signal.SignalTimeUtc,
                    EntryTime = o.Signal.EntryTimeUtc,
                    EntryPrice = o.EntryFill,
                    ExitTime = o.Exit.ExitTimeUtc,
                    ExitPrice = exitFill,
                    ExitReason = o.Exit.Reason,
                    ExitCategory = o.Exit.Category,
                    Quantity = o.Qty,
                    StopLoss = o.Signal.StopLoss,
                    Target = o.Signal.Target,
                    Score = o.Signal.Score,
                    MetCount = o.Signal.MetCount,
                    SessionsHeld = o.Exit.SessionsHeld,
                    GrossPnl = Math.Round(gross, 2),
                    Charges = Math.Round(charges, 2),
                    NetPnl = Math.Round(net, 2),
                    RMultiple = Math.Round(net / (o.Qty * riskPerShare), 3),
                    MfePct = o.Exit.MfePct,
                    MaePct = o.Exit.MaePct,
                    Factors = BacktestEngine.FactorsText(o.Signal.FactorPoints),
                    EndOfData = o.Exit.EndOfData
                });
                open.Remove(o);
            }
        }

        foreach (var s in orderedSignals)
        {
            CloseUntil(s.EntryTimeUtc);
            DateTime day = s.SessionDate;

            if (open.Any(o => o.Signal.Symbol == s.Symbol)) { Skip("already holding the stock (guard 9)"); continue; }
            if (s.SkipReason != null) { Skip(s.SkipReason); continue; }
            if (entriesByDay.GetValueOrDefault(day) >= ps.MaxTradesPerDay) { Skip("daily trade cap (guard 6)"); continue; }
            if (realizedByDay.GetValueOrDefault(day) <= -ps.DailyLossLimit) { Skip("daily loss limit (guard 7)"); continue; }
            if (open.Count >= Math.Min(ps.MaxPositions, s.PolicyMaxPositions)) { Skip("max open positions (guard 8 / regime policy)"); continue; }
            if (cash < ps.AmountPerTrade) { Skip("not enough cash (guard 11)"); continue; }

            var exit = exitOf(s);
            if (exit == null) { Skip(s.SkipReason ?? "could not simulate"); continue; }

            decimal entryFill = Math.Round(s.EntryRaw * (1m + slip), 2);
            decimal riskPct = s.PolicyRiskPct > 0m ? s.PolicyRiskPct : ps.RiskPct;
            decimal bookEquity = cash + open.Sum(o => o.Cost);
            int qty = ps.RiskSizing
                ? SwingTradeRules.RiskSizedQuantity(s.EntryRaw, s.StopLoss, bookEquity, ps.AmountPerTrade, riskPct).Quantity
                : (int)Math.Floor(ps.AmountPerTrade / s.EntryRaw);
            qty = Math.Min(qty, (int)Math.Floor(cash / entryFill));
            if (qty < 1) { Skip("quantity 0 at this price (guard 13)"); continue; }

            decimal cost = qty * entryFill;
            cash -= cost;
            entriesByDay[day] = entriesByDay.GetValueOrDefault(day) + 1;
            open.Add(new Open { Signal = s, Exit = exit, Qty = qty, EntryFill = entryFill, Cost = cost });
        }

        CloseUntil(DateTime.MaxValue);
        result.Trades.Sort((a, b) => a.EntryTime.CompareTo(b.EntryTime));
        return result;
    }

    // ------------------------------------------------------------------------------------------
    // Metrics
    // ------------------------------------------------------------------------------------------

    /// <summary>Daily equity = capital + realised net P&amp;L + open positions marked at that day's close.</summary>
    public static List<BacktestEquityPoint> EquityCurve(IReadOnlyList<BacktestTrade> trades, decimal capital, IReadOnlyList<DateTime> sessions,
        Func<string, DateTime, decimal?> closeOn, out decimal avgExposurePct)
    {
        var points = new List<BacktestEquityPoint>(sessions.Count);
        decimal peak = capital, exposureSum = 0m;
        foreach (var day in sessions)
        {
            decimal realized = 0m, unrealized = 0m, invested = 0m;
            foreach (var t in trades)
            {
                DateTime entryDay = BacktestEngine.IstDate(t.EntryTime), exitDay = BacktestEngine.IstDate(t.ExitTime);
                if (exitDay <= day) { realized += t.NetPnl; continue; }
                if (entryDay > day) continue;
                decimal mark = closeOn(t.Symbol, day) ?? t.EntryPrice;
                unrealized += t.Quantity * (mark - t.EntryPrice);
                invested += t.Quantity * mark;
            }
            decimal equity = capital + realized + unrealized;
            peak = Math.Max(peak, equity);
            exposureSum += equity > 0m ? invested / equity * 100m : 0m;
            points.Add(new BacktestEquityPoint(day, Math.Round(equity, 2), peak > 0m ? Math.Round((equity - peak) / peak * 100m, 2) : 0m));
        }
        avgExposurePct = sessions.Count > 0 ? Math.Round(exposureSum / sessions.Count, 1) : 0m;
        return points;
    }

    public static BacktestKpis Kpis(IReadOnlyList<BacktestTrade> trades, IReadOnlyList<BacktestEquityPoint> equity, decimal capital,
        DateTime from, DateTime to, decimal exposurePct)
    {
        var k = new BacktestKpis { Trades = trades.Count, ExposurePct = exposurePct };
        if (trades.Count > 0)
        {
            var wins = trades.Where(t => t.NetPnl > 0m).ToList();
            var losses = trades.Where(t => t.NetPnl <= 0m).ToList();
            k.Wins = wins.Count;
            k.WinRatePct = Math.Round(100m * wins.Count / trades.Count, 1);
            k.AvgWinR = wins.Count > 0 ? Math.Round(wins.Average(t => t.RMultiple), 2) : 0m;
            k.AvgLossR = losses.Count > 0 ? Math.Round(losses.Average(t => t.RMultiple), 2) : 0m;
            k.ExpectancyR = Math.Round(trades.Average(t => t.RMultiple), 3);
            decimal grossWin = wins.Sum(t => t.NetPnl), grossLoss = -losses.Sum(t => t.NetPnl);
            k.ProfitFactor = grossLoss > 0m ? Math.Round(grossWin / grossLoss, 2) : (grossWin > 0m ? 99m : 0m);
            k.GrossPnl = Math.Round(trades.Sum(t => t.GrossPnl), 2);
            k.Charges = Math.Round(trades.Sum(t => t.Charges), 2);
            k.NetPnl = Math.Round(trades.Sum(t => t.NetPnl), 2);
            k.AvgSessionsHeld = Math.Round((decimal)trades.Average(t => t.SessionsHeld), 1);
            k.BestTradeR = trades.Max(t => t.RMultiple);
            k.WorstTradeR = trades.Min(t => t.RMultiple);
            int run = 0;
            foreach (var t in trades)
            {
                run = t.NetPnl <= 0m ? run + 1 : 0;
                k.MaxConsecutiveLosses = Math.Max(k.MaxConsecutiveLosses, run);
            }
        }

        k.ReturnPct = capital > 0m ? Math.Round(k.NetPnl / capital * 100m, 2) : 0m;
        double years = Math.Max(1.0 / 12, (to - from).TotalDays / 365.25);
        decimal final = equity.Count > 0 ? equity[^1].Equity : capital + k.NetPnl;
        k.CagrPct = capital > 0m && final > 0m ? Math.Round((decimal)(Math.Pow((double)(final / capital), 1 / years) - 1) * 100m, 2) : 0m;
        k.TradesPerMonth = Math.Round(trades.Count / (decimal)(years * 12), 1);
        if (equity.Count > 0)
        {
            k.MaxDrawdownPct = equity.Min(e => e.DrawdownPct);
            decimal peak = capital, worst = 0m;
            foreach (var e in equity) { peak = Math.Max(peak, e.Equity); worst = Math.Min(worst, e.Equity - peak); }
            k.MaxDrawdownRs = Math.Round(worst, 2);
        }
        return k;
    }

    public static List<BacktestGroupRow> Group(IEnumerable<BacktestTrade> trades, Func<BacktestTrade, string> key, bool sortByKey = false)
    {
        var rows = trades.GroupBy(key).Select(g =>
        {
            var list = g.ToList();
            decimal win = list.Where(t => t.NetPnl > 0m).Sum(t => t.NetPnl), loss = -list.Where(t => t.NetPnl <= 0m).Sum(t => t.NetPnl);
            return new BacktestGroupRow
            {
                Key = g.Key,
                Trades = list.Count,
                WinRatePct = Math.Round(100m * list.Count(t => t.NetPnl > 0m) / list.Count, 1),
                ExpectancyR = Math.Round(list.Average(t => t.RMultiple), 3),
                NetPnl = Math.Round(list.Sum(t => t.NetPnl), 2),
                ProfitFactor = loss > 0m ? Math.Round(win / loss, 2) : (win > 0m ? 99m : 0m)
            };
        });
        return sortByKey ? rows.OrderBy(r => r.Key).ToList() : rows.OrderByDescending(r => r.Trades).ToList();
    }

    public static string ScoreBucket(int score) =>
        score >= 85 ? "85+" : score >= 80 ? "80-84" : score >= 75 ? "75-79" : score >= 70 ? "70-74" : score >= 60 ? "60-69" : "<60";

    /// <summary>For each scoring factor: how the trades that earned its points did vs the trades that didn't.</summary>
    public static List<BacktestFactorRow> FactorEdge(IReadOnlyList<BacktestTrade> trades)
    {
        var parsed = trades.Select(t => (Trade: t, Points: t.Factors.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Split(':')).Where(x => x.Length == 2)
            .ToDictionary(x => x[0], x => int.TryParse(x[1], out var v) ? v : 0))).ToList();
        var codes = parsed.SelectMany(p => p.Points.Keys).Distinct().ToList();
        return codes.Select(code =>
        {
            var with = parsed.Where(p => p.Points.GetValueOrDefault(code) > 0).Select(p => p.Trade).ToList();
            var without = parsed.Where(p => p.Points.GetValueOrDefault(code) <= 0).Select(p => p.Trade).ToList();
            return new BacktestFactorRow
            {
                Code = code,
                TradesWith = with.Count,
                ExpectancyWithR = with.Count > 0 ? Math.Round(with.Average(t => t.RMultiple), 3) : 0m,
                WinRateWithPct = with.Count > 0 ? Math.Round(100m * with.Count(t => t.NetPnl > 0m) / with.Count, 1) : 0m,
                TradesWithout = without.Count,
                ExpectancyWithoutR = without.Count > 0 ? Math.Round(without.Average(t => t.RMultiple), 3) : 0m,
                WinRateWithoutPct = without.Count > 0 ? Math.Round(100m * without.Count(t => t.NetPnl > 0m) / without.Count, 1) : 0m
            };
        }).ToList();
    }

    /// <summary>The plan's go-live rule: at least 30 trades, expectancy ≥ +0.2R after costs, and positive in both halves.</summary>
    public static (string Verdict, string Reason) Verdict(BacktestKpis k, IReadOnlyList<BacktestGroupRow> halves)
    {
        if (k.Trades < 30)
            return ("NOT_PROVEN", $"Only {k.Trades} trades - at least 30 are needed before the result means anything. Widen the date range or the stock list.");
        if (k.ExpectancyR <= 0m)
            return ("FAIL", $"Expectancy {k.ExpectancyR:+0.00;-0.00}R per trade after charges and slippage: these rules lost money over {k.Trades} trades.");
        bool bothHalves = halves.Count == 2 && halves.All(h => h.ExpectancyR > 0m);
        if (k.ExpectancyR >= 0.2m && bothHalves)
            return ("PASS", $"Expectancy {k.ExpectancyR:+0.00}R per trade over {k.Trades} trades, positive in both halves of the period. Next: confirm with 2–3 months of paper trading before adding capital.");
        return ("NOT_PROVEN", k.ExpectancyR < 0.2m
            ? $"Expectancy {k.ExpectancyR:+0.00}R per trade is positive but below the +0.2R bar - too thin to survive real-world costs and mistakes."
            : $"Expectancy {k.ExpectancyR:+0.00}R overall, but one half of the period lost money - the edge isn't stable.");
    }
}
