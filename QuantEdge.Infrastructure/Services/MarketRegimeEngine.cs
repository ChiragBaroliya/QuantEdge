using System;
using System.Collections.Generic;
using System.Linq;
using QuantEdge.Domain.Entities;

namespace QuantEdge.Infrastructure.Services;

/// <summary>The five market regimes, mildest first (Plan H).</summary>
public static class MarketRegimes
{
    public const string Bullish = "BULLISH";
    public const string BullishWeakening = "BULLISH_WEAKENING";
    public const string Sideways = "SIDEWAYS";
    public const string Bearish = "BEARISH";
    public const string StrongBearish = "STRONG_BEARISH";

    public static readonly string[] All = { Bullish, BullishWeakening, Sideways, Bearish, StrongBearish };

    public static string ForScore(int score) => score >= 70 ? Bullish : score >= 55 ? BullishWeakening : score >= 40 ? Sideways : score >= 25 ? Bearish : StrongBearish;
}

/// <summary>Market breadth for one day across the active-stock universe.</summary>
public sealed class BreadthSnapshot
{
    public int Stocks { get; set; }
    public decimal PctAboveEma50 { get; set; }
    /// <summary>Null when too few stocks have the 220+ bars a real EMA200 needs.</summary>
    public decimal? PctAboveEma200 { get; set; }
    /// <summary>Sum over the last 10 sessions of (advancing − declining) stocks.</summary>
    public int NetAdvances10 { get; set; }
}

/// <summary>One day's market regime reading (market_regime_daily).</summary>
public sealed class MarketRegimeReading
{
    public DateTime TradeDate { get; set; }
    public decimal NiftyClose { get; set; }
    public decimal Ema50 { get; set; }
    public decimal Ema200 { get; set; }
    public decimal DayChangePct { get; set; }
    public decimal DrawdownPct { get; set; }
    public decimal? Vix { get; set; }
    /// <summary>VIX, or ATR_PERCENTILE when no India VIX data is stored.</summary>
    public string VolSource { get; set; } = "VIX";
    public decimal? VolPercentile { get; set; }
    public decimal PctAboveEma50 { get; set; }
    public decimal? PctAboveEma200 { get; set; }
    public int NetAdvances10 { get; set; }
    public int BreadthStocks { get; set; }
    public int TrendPts { get; set; }
    public int BreadthPts { get; set; }
    public int VolPts { get; set; }
    public int DrawdownPts { get; set; }
    public int Score { get; set; }
    /// <summary>The band today's score falls in (before hysteresis).</summary>
    public string RawRegime { get; set; } = MarketRegimes.Sideways;
    /// <summary>The regime in force: changes only after two days in a new band (STRONG_BEARISH immediately).</summary>
    public string Regime { get; set; } = MarketRegimes.Sideways;
    public int RegimeStreak { get; set; } = 1;
    public string Notes { get; set; } = string.Empty;
}

/// <summary>
/// Scores the market 0-100 from NIFTY's trend, the universe's breadth, volatility and drawdown, and maps it to a regime
/// (Plan D.1 / H). Pure: callers supply candles, breadth, VIX and the previous readings.
///
///   Trend 40     close > EMA50 (10) · close > EMA200 (10) · EMA50 > EMA200 (10) · EMA50 rising over 20 days (10)
///   Breadth 35   % of stocks above EMA50: 20% → 0 … 70% → 20 · 10-day net advances > 0 (8) · ≥ 50% above EMA200 (7)
///   Volatility 15 India VIX < 14 → 15, < 18 → 10, < 22 → 5, else 0 (no VIX: NIFTY ATR% percentile over a year)
///   Drawdown 10  NIFTY below its 52-week high by < 5% → 10, < 10% → 5, else 0
///
/// Bands: ≥ 70 BULLISH · 55-69 BULLISH_WEAKENING · 40-54 SIDEWAYS · 25-39 BEARISH · &lt; 25 STRONG_BEARISH.
/// A ≥ 70 score that fell ≥ 15 points in 10 sessions reads BULLISH_WEAKENING. NIFTY −3% in a day or VIX > 25
/// reads STRONG_BEARISH whatever the score.
/// </summary>
public static class MarketRegimeEngine
{
    public static MarketRegimeReading Score(IReadOnlyList<MarketCandle> nifty, BreadthSnapshot breadth, decimal? vix,
        IReadOnlyList<MarketRegimeReading> previous)
    {
        if (nifty == null || nifty.Count < 60) throw new ArgumentException("At least 60 NIFTY daily candles are needed.", nameof(nifty));

        var closes = nifty.Select(c => c.Close).ToList();
        var highs = nifty.Select(c => c.High).ToList();
        var lows = nifty.Select(c => c.Low).ToList();
        int i = closes.Count - 1;
        var ema50 = IndicatorCalculator.CalculateEma(closes, 50);
        var ema200 = IndicatorCalculator.CalculateEma(closes, 200);
        bool hasEma200 = closes.Count >= SwingDecisionEngine.MinBarsForEma200;

        var r = new MarketRegimeReading
        {
            TradeDate = DayChangeCalculator.IstDate(nifty[i].CandleTime),
            NiftyClose = closes[i],
            Ema50 = Math.Round(ema50[i], 2),
            Ema200 = hasEma200 ? Math.Round(ema200[i], 2) : 0m,
            DayChangePct = closes[i - 1] > 0m ? Math.Round((closes[i] - closes[i - 1]) / closes[i - 1] * 100m, 2) : 0m,
            PctAboveEma50 = breadth.PctAboveEma50,
            PctAboveEma200 = breadth.PctAboveEma200,
            NetAdvances10 = breadth.NetAdvances10,
            BreadthStocks = breadth.Stocks,
            Vix = vix
        };
        var notes = new List<string>();

        // Trend (40)
        int trend = 0;
        if (closes[i] > ema50[i]) trend += 10;
        if (hasEma200 && closes[i] > ema200[i]) trend += 10;
        if (hasEma200 && ema50[i] > ema200[i]) trend += 10;
        if (i >= 20 && ema50[i] > ema50[i - 20]) trend += 10;
        if (!hasEma200) notes.Add("NIFTY has fewer than 220 daily candles - EMA200 parts of the trend score skipped");
        r.TrendPts = trend;

        // Breadth (35)
        int breadthPts = (int)Math.Round(Math.Clamp((breadth.PctAboveEma50 - 20m) / 50m * 20m, 0m, 20m));
        if (breadth.NetAdvances10 > 0) breadthPts += 8;
        if (breadth.PctAboveEma200 >= 50m) breadthPts += 7;
        if (breadth.Stocks < 100) notes.Add($"breadth measured on only {breadth.Stocks} stocks");
        r.BreadthPts = breadthPts;

        // Volatility (15)
        if (vix.HasValue && vix.Value > 0m)
        {
            r.VolSource = "VIX";
            r.VolPts = vix < 14m ? 15 : vix < 18m ? 10 : vix < 22m ? 5 : 0;
        }
        else
        {
            r.VolSource = "ATR_PERCENTILE";
            var atr = IndicatorCalculator.CalculateAtr(highs, lows, closes, 14);
            var atrPct = Enumerable.Range(0, closes.Count).Select(k => closes[k] > 0m ? atr[k] / closes[k] : 0m).ToList();
            var window = atrPct.Skip(Math.Max(14, atrPct.Count - 250)).ToList();
            decimal today = atrPct[i];
            decimal pct = window.Count > 0 ? (decimal)window.Count(v => v <= today) / window.Count * 100m : 50m;
            r.VolPercentile = Math.Round(pct, 1);
            r.VolPts = pct < 33m ? 15 : pct < 66m ? 10 : pct < 90m ? 5 : 0;
            notes.Add("no India VIX stored - volatility from NIFTY ATR% percentile");
        }

        // Drawdown (10)
        decimal high52 = highs.Skip(Math.Max(0, highs.Count - 250)).Max();
        r.DrawdownPct = high52 > 0m ? Math.Round((high52 - closes[i]) / high52 * 100m, 2) : 0m;
        r.DrawdownPts = r.DrawdownPct < 5m ? 10 : r.DrawdownPct < 10m ? 5 : 0;

        r.Score = Math.Clamp(r.TrendPts + r.BreadthPts + r.VolPts + r.DrawdownPts, 0, 100);

        // Raw band (+ fast-deterioration rules)
        string raw = MarketRegimes.ForScore(r.Score);
        var tenAgo = previous.Where(p => p.TradeDate < r.TradeDate).OrderByDescending(p => p.TradeDate).Skip(9).FirstOrDefault();
        if (raw == MarketRegimes.Bullish && tenAgo != null && tenAgo.Score - r.Score >= 15)
        {
            raw = MarketRegimes.BullishWeakening;
            notes.Add($"score fell {tenAgo.Score - r.Score} pts in 10 sessions");
        }
        if (r.DayChangePct <= -3m || vix > 25m)
        {
            raw = MarketRegimes.StrongBearish;
            notes.Add(r.DayChangePct <= -3m ? $"NIFTY {r.DayChangePct}% today" : $"India VIX {vix}");
        }
        r.RawRegime = raw;

        ApplyHysteresis(r, previous.Where(p => p.TradeDate < r.TradeDate).OrderByDescending(p => p.TradeDate).FirstOrDefault(), notes);
        r.Notes = string.Join("; ", notes);
        return r;
    }

    /// <summary>
    /// The regime in force changes only when the new band shows on two consecutive days (yesterday's raw band equals
    /// today's), so one noisy day can't flip the policy. A move into STRONG_BEARISH applies at once.
    /// </summary>
    private static void ApplyHysteresis(MarketRegimeReading r, MarketRegimeReading? prev, List<string> notes)
    {
        if (prev == null || prev.Regime == r.RawRegime)
        {
            r.Regime = r.RawRegime;
            r.RegimeStreak = prev != null && prev.Regime == r.RawRegime ? prev.RegimeStreak + 1 : 1;
            return;
        }
        if (r.RawRegime == MarketRegimes.StrongBearish || prev.RawRegime == r.RawRegime)
        {
            r.Regime = r.RawRegime;
            r.RegimeStreak = 1;
            return;
        }
        r.Regime = prev.Regime;
        r.RegimeStreak = prev.RegimeStreak + 1;
        notes.Add($"today reads {r.RawRegime}; {prev.Regime} kept until a second day confirms the change");
    }

    /// <summary>Breadth for <paramref name="date"/> (IST) from each stock's daily candles up to and including that date.</summary>
    public static BreadthSnapshot Breadth(IEnumerable<IReadOnlyList<MarketCandle>> universe, DateTime date)
    {
        int stocks = 0, above50 = 0, with200 = 0, above200 = 0, netAdv = 0;
        foreach (var candles in universe)
        {
            // Point in time: only candles dated on or before the reading's date.
            int last = -1;
            for (int k = candles.Count - 1; k >= 0; k--)
            {
                if (DayChangeCalculator.IstDate(candles[k].CandleTime) <= date) { last = k; break; }
            }
            if (last < 50 || DayChangeCalculator.IstDate(candles[last].CandleTime) != date) continue;

            var closes = candles.Take(last + 1).Select(c => c.Close).ToList();
            var e50 = IndicatorCalculator.CalculateEma(closes, 50);
            stocks++;
            if (closes[last] > e50[last]) above50++;
            if (closes.Count >= SwingDecisionEngine.MinBarsForEma200)
            {
                with200++;
                if (closes[last] > IndicatorCalculator.CalculateEma(closes, 200)[last]) above200++;
            }
            for (int k = Math.Max(1, last - 9); k <= last; k++)
            {
                if (closes[k] > closes[k - 1]) netAdv++;
                else if (closes[k] < closes[k - 1]) netAdv--;
            }
        }
        return new BreadthSnapshot
        {
            Stocks = stocks,
            PctAboveEma50 = stocks > 0 ? Math.Round(100m * above50 / stocks, 1) : 50m,
            PctAboveEma200 = with200 >= 20 ? Math.Round(100m * above200 / with200, 1) : null,
            NetAdvances10 = netAdv
        };
    }
}
