using System;
using System.Collections.Generic;
using System.Linq;
using QuantEdge.Domain.Entities;

namespace QuantEdge.Infrastructure.Services.Backtest;

/// <summary>Which stocks a new rule may trade on a given day, using only what was known by that day's close.</summary>
public static class BacktestUniverse
{
    private static readonly string[] EtfSymbolParts = { "BEES", "ETF", "NIFTY", "SENSEX" };

    /// <summary>ETFs share the stocks' instrument type, so they are recognised by name. The run's report lists what this excluded.</summary>
    public static bool IsEtfLike(string symbol, string? name)
    {
        string n = (name ?? string.Empty).ToUpperInvariant();
        if (n.Contains("ETF") || n.Contains("EXCHANGE TRADED") || n.Contains("BEES")) return true;
        string s = symbol.ToUpperInvariant();
        return EtfSymbolParts.Any(s.Contains) || s.EndsWith("SILVER", StringComparison.Ordinal);
    }

    /// <summary>For each day: the <paramref name="size"/> symbols with the highest median daily turnover over the last <paramref name="lookback"/> bars ending that day.</summary>
    public static Dictionary<DateTime, HashSet<string>> TopByTurnover(IReadOnlyDictionary<string, List<MarketCandle>> daily, int size, int lookback)
    {
        var byDay = new Dictionary<DateTime, List<(string Symbol, double Median)>>();
        var window = new double[lookback];
        foreach (var (symbol, bars) in daily)
        {
            for (int i = lookback - 1; i < bars.Count; i++)
            {
                for (int k = 0; k < lookback; k++)
                {
                    var b = bars[i - k];
                    window[k] = (double)b.Close * b.Volume;
                }
                Array.Sort(window);
                double median = lookback % 2 == 1 ? window[lookback / 2] : (window[lookback / 2 - 1] + window[lookback / 2]) / 2;
                DateTime day = BacktestEngine.IstDate(bars[i].CandleTime);
                if (!byDay.TryGetValue(day, out var list)) byDay[day] = list = new List<(string, double)>();
                list.Add((symbol, median));
            }
        }
        return byDay.ToDictionary(kv => kv.Key,
            kv => kv.Value.OrderByDescending(x => x.Median).ThenBy(x => x.Symbol, StringComparer.Ordinal).Take(size).Select(x => x.Symbol).ToHashSet());
    }
}
