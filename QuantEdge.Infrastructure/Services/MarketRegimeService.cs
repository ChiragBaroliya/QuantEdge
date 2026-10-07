using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Extensions.Logging;
using QuantEdge.Domain.Entities;
using QuantEdge.Infrastructure.Constants;
using QuantEdge.Infrastructure.Persistence;
using QuantEdge.Infrastructure.Persistence.Repositories;

namespace QuantEdge.Infrastructure.Services;

/// <summary>How new entries are allowed in one regime (regime_policy).</summary>
public sealed class RegimePolicy
{
    public string Regime { get; set; } = MarketRegimes.Sideways;
    public int MinStockScore { get; set; } = 78;
    public bool RequireRelativeStrength { get; set; } = true;
    public int MaxPositions { get; set; } = 5;
    public decimal RiskPct { get; set; } = 0.75m;
    public decimal MaxExposurePct { get; set; } = 50m;
    public string? Description { get; set; }

    // Same values as the regime_policy seed in schema.sql - used when the table isn't there yet.
    public static IReadOnlyList<RegimePolicy> Defaults { get; } = new[]
    {
        new RegimePolicy { Regime = MarketRegimes.Bullish, MinStockScore = 70, RequireRelativeStrength = false, MaxPositions = 10, RiskPct = 1.0m, MaxExposurePct = 100, Description = "Normal trading" },
        new RegimePolicy { Regime = MarketRegimes.BullishWeakening, MinStockScore = 75, RequireRelativeStrength = true, MaxPositions = 7, RiskPct = 0.75m, MaxExposurePct = 70, Description = "Fewer, stronger trades; only stocks beating NIFTY" },
        new RegimePolicy { Regime = MarketRegimes.Sideways, MinStockScore = 78, RequireRelativeStrength = true, MaxPositions = 5, RiskPct = 0.75m, MaxExposurePct = 50, Description = "High-quality setups only" },
        new RegimePolicy { Regime = MarketRegimes.Bearish, MinStockScore = 82, RequireRelativeStrength = true, MaxPositions = 3, RiskPct = 0.5m, MaxExposurePct = 30, Description = "Only leaders that keep rising while NIFTY falls; half risk" },
        new RegimePolicy { Regime = MarketRegimes.StrongBearish, MinStockScore = 88, RequireRelativeStrength = true, MaxPositions = 1, RiskPct = 0.25m, MaxExposurePct = 15, Description = "Exceptional strength only, quarter risk" },
    };
}

/// <summary>Whether the scan may open new positions right now, and on what terms.</summary>
public sealed class MarketGateDecision
{
    public string Mode { get; set; } = SwingStrategySettings.GateModeNiftyFilter;
    public bool AllowsEntries { get; set; }
    public string Reason { get; set; } = string.Empty;
    public MarketRegimeReading? Reading { get; set; }
    public RegimePolicy? Policy { get; set; }
}

public interface IMarketRegimeService
{
    Task<MarketRegimeReading?> GetLatestAsync();
    Task<IReadOnlyList<MarketRegimeReading>> GetHistoryAsync(int days);
    Task<IReadOnlyList<RegimePolicy>> GetPoliciesAsync();

    /// <summary>Computes and stores every missing reading of the last <paramref name="backfillDays"/> sessions and always recomputes the latest one. DB only.</summary>
    Task<MarketRegimeReading?> ComputeAndStoreAsync(int backfillDays = 60);

    /// <summary>The regime-mode gate: the latest reading + its policy. Computes a reading if none is stored; blocks entries if none can be made.</summary>
    Task<MarketGateDecision> GetRegimeGateAsync();
}

public class MarketRegimeService : IMarketRegimeService
{
    private const string NiftySymbol = "NIFTY 50";
    private const string VixSymbol = "INDIA VIX";

    private readonly IDbConnectionFactory _connectionFactory;
    private readonly IMarketCandleRepository _candleRepository;
    private readonly IStockMasterRepository _stockRepository;
    private readonly ILogger<MarketRegimeService> _logger;

    public MarketRegimeService(IDbConnectionFactory connectionFactory, IMarketCandleRepository candleRepository,
        IStockMasterRepository stockRepository, ILogger<MarketRegimeService> logger)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _candleRepository = candleRepository ?? throw new ArgumentNullException(nameof(candleRepository));
        _stockRepository = stockRepository ?? throw new ArgumentNullException(nameof(stockRepository));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    private const string SelectReading = @"
        SELECT trade_date::timestamp AS TradeDate, nifty_close AS NiftyClose, ema50, ema200, day_change_pct AS DayChangePct,
               drawdown_pct AS DrawdownPct, vix, vol_source AS VolSource, vol_percentile AS VolPercentile,
               pct_above_ema50 AS PctAboveEma50, pct_above_ema200 AS PctAboveEma200, net_advances_10 AS NetAdvances10,
               breadth_stocks AS BreadthStocks, trend_pts AS TrendPts, breadth_pts AS BreadthPts, vol_pts AS VolPts,
               drawdown_pts AS DrawdownPts, score, raw_regime AS RawRegime, regime, regime_streak AS RegimeStreak, notes
        FROM market_regime_daily";

    public async Task<MarketRegimeReading?> GetLatestAsync()
    {
        using var connection = _connectionFactory.CreateConnection();
        return await connection.QueryFirstOrDefaultAsync<MarketRegimeReading>($"{SelectReading} ORDER BY trade_date DESC LIMIT 1;");
    }

    public async Task<IReadOnlyList<MarketRegimeReading>> GetHistoryAsync(int days)
    {
        using var connection = _connectionFactory.CreateConnection();
        var rows = await connection.QueryAsync<MarketRegimeReading>($"{SelectReading} ORDER BY trade_date DESC LIMIT @days;", new { days = Math.Clamp(days, 1, 750) });
        return rows.OrderBy(r => r.TradeDate).ToList();
    }

    public async Task<IReadOnlyList<RegimePolicy>> GetPoliciesAsync()
    {
        try
        {
            using var connection = _connectionFactory.CreateConnection();
            var rows = (await connection.QueryAsync<RegimePolicy>(@"
                SELECT regime, min_stock_score AS MinStockScore, require_relative_strength AS RequireRelativeStrength,
                       max_positions AS MaxPositions, risk_pct AS RiskPct, max_exposure_pct AS MaxExposurePct, description
                FROM regime_policy;")).ToList();
            return rows.Count > 0 ? rows : RegimePolicy.Defaults;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "regime_policy not readable (apply schema.sql) - using built-in defaults.");
            return RegimePolicy.Defaults;
        }
    }

    public async Task<MarketRegimeReading?> ComputeAndStoreAsync(int backfillDays = 60)
    {
        int history = RealTradeSchedule.DailyCandleHistoryCount + backfillDays;
        var nifty = (await _candleRepository.GetHistoryAsync(NiftySymbol, "1d", history)).OrderBy(c => c.CandleTime).ToList();
        if (nifty.Count < 60)
        {
            _logger.LogWarning("Market regime: only {Count} NIFTY 50 daily candles stored - need at least 60.", nifty.Count);
            return null;
        }

        var symbols = (await _stockRepository.GetActiveStocksAsync())
            .Select(s => s.Symbol.Trim().ToUpperInvariant())
            .Where(s => s != NiftySymbol && s != "NIFTYBEES" && s != VixSymbol)
            .Distinct().ToList();
        var universe = (await _candleRepository.GetRecentHistoryBatchAsync(symbols, "1d", history))
            .GroupBy(c => c.Symbol.ToUpperInvariant())
            .Select(g => (IReadOnlyList<MarketCandle>)g.OrderBy(c => c.CandleTime).ToList())
            .ToList();
        var vixByDate = (await _candleRepository.GetHistoryAsync(VixSymbol, "1d", backfillDays + 5))
            .GroupBy(c => DayChangeCalculator.IstDate(c.CandleTime))
            .ToDictionary(g => g.Key, g => g.Last().Close);

        var stored = (await GetHistoryAsync(backfillDays + 15)).ToList();
        var storedDates = stored.Select(r => r.TradeDate.Date).ToHashSet();

        // Sessions to (re)compute: missing ones in the window, plus always the latest (its candle may have been
        // replaced by NSE's official close since it was last computed).
        var dates = nifty.Select(c => DayChangeCalculator.IstDate(c.CandleTime)).Distinct().ToList();
        var targets = dates.Skip(Math.Max(60, dates.Count - backfillDays)).ToList();
        DateTime latest = dates[^1];

        MarketRegimeReading? last = null;
        foreach (var date in targets)
        {
            if (storedDates.Contains(date) && date != latest) continue;

            var niftyUpTo = nifty.Where(c => DayChangeCalculator.IstDate(c.CandleTime) <= date).ToList();
            var breadth = MarketRegimeEngine.Breadth(universe, date);
            decimal? vix = vixByDate.TryGetValue(date, out var v) ? v : null;
            var reading = MarketRegimeEngine.Score(niftyUpTo, breadth, vix, stored.Where(r => r.TradeDate.Date < date).ToList());
            await UpsertAsync(reading);

            stored.RemoveAll(r => r.TradeDate.Date == date);
            stored.Add(reading);
            last = reading;
        }

        if (last != null)
        {
            _logger.LogInformation("Market regime {Date:yyyy-MM-dd}: {Regime} (score {Score}; raw {Raw}).", last.TradeDate, last.Regime, last.Score, last.RawRegime);
        }
        return last ?? stored.OrderBy(r => r.TradeDate).LastOrDefault();
    }

    public async Task<MarketGateDecision> GetRegimeGateAsync()
    {
        var decision = new MarketGateDecision { Mode = SwingStrategySettings.GateModeRegime };
        MarketRegimeReading? reading;
        try
        {
            reading = await GetLatestAsync() ?? await ComputeAndStoreAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Market regime unavailable.");
            reading = null;
        }

        if (reading == null)
        {
            decision.AllowsEntries = false;
            decision.Reason = "No market regime could be computed (apply schema.sql / check NIFTY 50 daily candles) - no new entries.";
            return decision;
        }

        var policy = (await GetPoliciesAsync()).FirstOrDefault(p => p.Regime == reading.Regime)
            ?? RegimePolicy.Defaults.First(p => p.Regime == reading.Regime);
        decision.Reading = reading;
        decision.Policy = policy;
        decision.AllowsEntries = policy.MaxPositions > 0;
        decision.Reason = decision.AllowsEntries
            ? $"{reading.Regime} (score {reading.Score}, {reading.TradeDate:dd-MMM}): score ≥ {policy.MinStockScore}{(policy.RequireRelativeStrength ? ", must beat NIFTY" : string.Empty)}, max {policy.MaxPositions} positions"
            : $"{reading.Regime} (score {reading.Score}): regime_policy allows 0 positions - no new entries.";
        return decision;
    }

    private async Task UpsertAsync(MarketRegimeReading r)
    {
        using var connection = _connectionFactory.CreateConnection();
        const string sql = @"
            INSERT INTO market_regime_daily (trade_date, nifty_close, ema50, ema200, day_change_pct, drawdown_pct, vix, vol_source, vol_percentile,
                pct_above_ema50, pct_above_ema200, net_advances_10, breadth_stocks, trend_pts, breadth_pts, vol_pts, drawdown_pts, score,
                raw_regime, regime, regime_streak, notes, computed_at)
            VALUES (@TradeDate, @NiftyClose, @Ema50, @Ema200, @DayChangePct, @DrawdownPct, @Vix, @VolSource, @VolPercentile,
                @PctAboveEma50, @PctAboveEma200, @NetAdvances10, @BreadthStocks, @TrendPts, @BreadthPts, @VolPts, @DrawdownPts, @Score,
                @RawRegime, @Regime, @RegimeStreak, LEFT(@Notes, 500), NOW())
            ON CONFLICT (trade_date) DO UPDATE SET
                nifty_close = EXCLUDED.nifty_close, ema50 = EXCLUDED.ema50, ema200 = EXCLUDED.ema200, day_change_pct = EXCLUDED.day_change_pct,
                drawdown_pct = EXCLUDED.drawdown_pct, vix = EXCLUDED.vix, vol_source = EXCLUDED.vol_source, vol_percentile = EXCLUDED.vol_percentile,
                pct_above_ema50 = EXCLUDED.pct_above_ema50, pct_above_ema200 = EXCLUDED.pct_above_ema200, net_advances_10 = EXCLUDED.net_advances_10,
                breadth_stocks = EXCLUDED.breadth_stocks, trend_pts = EXCLUDED.trend_pts, breadth_pts = EXCLUDED.breadth_pts, vol_pts = EXCLUDED.vol_pts,
                drawdown_pts = EXCLUDED.drawdown_pts, score = EXCLUDED.score, raw_regime = EXCLUDED.raw_regime, regime = EXCLUDED.regime,
                regime_streak = EXCLUDED.regime_streak, notes = EXCLUDED.notes, computed_at = NOW();";
        await connection.ExecuteAsync(sql, new
        {
            TradeDate = r.TradeDate.Date, r.NiftyClose, r.Ema50, r.Ema200, r.DayChangePct, r.DrawdownPct, r.Vix, r.VolSource, r.VolPercentile,
            r.PctAboveEma50, r.PctAboveEma200, r.NetAdvances10, r.BreadthStocks, r.TrendPts, r.BreadthPts, r.VolPts, r.DrawdownPts, r.Score,
            r.RawRegime, r.Regime, r.RegimeStreak, r.Notes
        });
    }
}

/// <summary>Regime-mode check for one scored stock (Plan H): its score must reach the regime's bar and, where required, beat NIFTY.</summary>
public static class RegimeGate
{
    public static (bool Allowed, string Reason) Evaluate(SwingEvaluationResult r, RegimePolicy p)
    {
        if (!r.HardFiltersPassed) return (false, "stock trend filters failed");
        if (r.Score < p.MinStockScore) return (false, $"score {r.Score} < {p.MinStockScore} needed in {p.Regime}");
        if (p.RequireRelativeStrength)
        {
            bool beatsNifty = r.Factors.Any(f => f.Code == "RELATIVE_STRENGTH" && f.Points > 0);
            if (!beatsNifty) return (false, $"not outperforming NIFTY (required in {p.Regime})");
        }
        return (true, $"score {r.Score} ≥ {p.MinStockScore} in {p.Regime}");
    }

    /// <summary>The engine settings to score with in regime mode: no NIFTY hard gate or penalty - the regime policy replaces both.</summary>
    public static SwingStrategySettings WithoutNiftyGate(SwingStrategySettings s) => new()
    {
        Id = s.Id,
        BuyScoreThreshold = s.BuyScoreThreshold,
        WatchScoreThreshold = s.WatchScoreThreshold,
        RequireNiftyMarketFilter = false,
        MarketContextScorePenalty = 0,
        MarketContextPositionSizeFactor = 1m,
        MarketProtectionBufferPct = s.MarketProtectionBufferPct,
        MarketGateMode = s.MarketGateMode,
        UpdatedAt = s.UpdatedAt
    };
}
