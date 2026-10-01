using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using QuantEdge.Domain.Entities;
using QuantEdge.Infrastructure.Constants;
using QuantEdge.Infrastructure.DTOs;
using QuantEdge.Infrastructure.Interfaces;
using QuantEdge.Infrastructure.Persistence.Repositories;

namespace QuantEdge.Infrastructure.Services;

/// <summary>
/// Sector Dashboard: Market -> Sector strength -> Stocks in sector -> trading conditions -> BUY / WATCH / NO TRADE.
/// Each stock is scored with the same candles and SwingDecisionEngine evaluation as StockVerdictService
/// (and so the auto-trading bot). The sector is one extra gate on top: a stock the bot would buy is shown
/// as WATCH, not BUY, while its sector is WEAK - a green sector alone never makes a BUY.
/// One snapshot of every sector is built per user, so the overview and every detail page are served from
/// the same evaluation. Candles for all stocks load in one query per timeframe. A snapshot older than
/// SnapshotFreshFor is still served instantly while a fresh one is built in the background; only the
/// very first load (or an explicit refresh) waits for a build.
/// </summary>
public class SectorDashboardService : ISectorDashboardService
{
    private static readonly TimeSpan SnapshotFreshFor = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan SnapshotRetention = TimeSpan.FromMinutes(15);
    private static readonly SemaphoreSlim BuildLock = new(1, 1);
    private static readonly ConcurrentDictionary<int, byte> BackgroundBuilds = new();

    // Sector strength: STRONG needs the sector up by StrongChangePct AND at least StrongBreadth of its
    // stocks up; WEAK is the mirror image. Anything in between is NEUTRAL (mixed).
    private const decimal StrongChangePct = 0.5m;
    private const decimal StrongBreadth = 0.6m;

    // 15-min factors that decide entry timing - the same set StockVerdictService reports.
    private static readonly HashSet<string> TimingFactors = new(StringComparer.Ordinal)
    {
        "BREAKOUT_GROUP", "VOL_CONFIRMATION", "RSI_MOMENTUM", "MACD_BULLISH", "BULLISH_CANDLE"
    };

    private static readonly Dictionary<string, string> FactorShortNames = new(StringComparer.Ordinal)
    {
        ["BREAKOUT_GROUP"] = "Breakout",
        ["VOL_CONFIRMATION"] = "Volume",
        ["RELATIVE_STRENGTH"] = "Beats NIFTY",
        ["MULTITIMEFRAME"] = "60m trend",
        ["RSI_MOMENTUM"] = "RSI momentum",
        ["MACD_BULLISH"] = "MACD",
        ["BULLISH_CANDLE"] = "Bullish candle",
        ["RISK_REWARD"] = "Risk:reward"
    };

    private static readonly Regex ConditionPrefix = new(@"^\d+\.\s*(\[[^\]]*\]\s*)?", RegexOptions.Compiled);

    private readonly ISectorRepository _sectorRepository;
    private readonly IMarketCandleRepository _candleRepository;
    private readonly ISwingStrategySettingsRepository _strategySettingsRepository;
    private readonly IAutoRealTradeService _realTradeService;
    private readonly ICacheService _cache;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SectorDashboardService> _logger;

    public SectorDashboardService(
        ISectorRepository sectorRepository,
        IMarketCandleRepository candleRepository,
        ISwingStrategySettingsRepository strategySettingsRepository,
        IAutoRealTradeService realTradeService,
        ICacheService cache,
        IServiceScopeFactory scopeFactory,
        ILogger<SectorDashboardService> logger)
    {
        _sectorRepository = sectorRepository ?? throw new ArgumentNullException(nameof(sectorRepository));
        _candleRepository = candleRepository ?? throw new ArgumentNullException(nameof(candleRepository));
        _strategySettingsRepository = strategySettingsRepository ?? throw new ArgumentNullException(nameof(strategySettingsRepository));
        _realTradeService = realTradeService ?? throw new ArgumentNullException(nameof(realTradeService));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<SectorOverviewDto> GetOverviewAsync(int userId = 1, bool refresh = false)
    {
        var snapshot = await GetSnapshotAsync(userId, refresh);
        return new SectorOverviewDto
        {
            AsOfUtc = snapshot.AsOfUtc,
            MarketPassed = snapshot.MarketPassed,
            MarketRequired = snapshot.MarketRequired,
            Sectors = snapshot.Sectors.Select(s => s.Summary).ToList()
        };
    }

    public async Task<SectorDetailDto?> GetSectorDetailAsync(int sectorId, int userId = 1, bool refresh = false)
    {
        var snapshot = await GetSnapshotAsync(userId, refresh);
        var sector = snapshot.Sectors.FirstOrDefault(s => s.Summary.SectorId == sectorId);
        if (sector == null) return null;

        return new SectorDetailDto
        {
            AsOfUtc = snapshot.AsOfUtc,
            MarketPassed = snapshot.MarketPassed,
            MarketRequired = snapshot.MarketRequired,
            BuyThreshold = snapshot.BuyThreshold,
            WatchThreshold = snapshot.WatchThreshold,
            MinConditionsMatch = snapshot.MinConditionsMatch,
            Sector = sector.Summary,
            Stocks = sector.Stocks
        };
    }

    // ------------------------------------------------------------------------------------------
    // Snapshot
    // ------------------------------------------------------------------------------------------

    private static string CacheKey(int userId) => $"sector-dashboard:{userId}";

    private async Task<Snapshot> GetSnapshotAsync(int userId, bool refresh)
    {
        var cached = await _cache.GetAsync<Snapshot>(CacheKey(userId));
        if (cached != null && !refresh)
        {
            // Stale-while-revalidate: answer now, rebuild behind the scenes for the next poll.
            if (DateTime.UtcNow - cached.AsOfUtc > SnapshotFreshFor) StartBackgroundBuild(userId);
            return cached;
        }

        return await BuildAndCacheAsync(userId, refresh ? DateTime.UtcNow : null);
    }

    // One build at a time: requests that arrive during a build share it. A refresh only reuses a
    // snapshot built after it asked (builtAfterUtc); otherwise any fresh snapshot is reused.
    private async Task<Snapshot> BuildAndCacheAsync(int userId, DateTime? builtAfterUtc)
    {
        await BuildLock.WaitAsync();
        try
        {
            var cached = await _cache.GetAsync<Snapshot>(CacheKey(userId));
            bool reusable = cached != null && (builtAfterUtc.HasValue
                ? cached.AsOfUtc >= builtAfterUtc.Value
                : DateTime.UtcNow - cached.AsOfUtc <= SnapshotFreshFor);
            if (reusable) return cached!;

            var snapshot = await BuildSnapshotAsync(userId);
            await _cache.SetAsync(CacheKey(userId), snapshot, SnapshotRetention);
            return snapshot;
        }
        finally
        {
            BuildLock.Release();
        }
    }

    // The request that triggered it has already been answered, so the build runs in its own DI scope.
    private void StartBackgroundBuild(int userId)
    {
        if (!BackgroundBuilds.TryAdd(userId, 0)) return;
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var service = (SectorDashboardService)scope.ServiceProvider.GetRequiredService<ISectorDashboardService>();
                await service.BuildAndCacheAsync(userId, null);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Background rebuild of the sector dashboard failed for user {UserId}.", userId);
            }
            finally
            {
                BackgroundBuilds.TryRemove(userId, out _);
            }
        });
    }

    private async Task<Snapshot> BuildSnapshotAsync(int userId)
    {
        var started = DateTime.UtcNow;
        var strategyTask = _strategySettingsRepository.GetSettingsAsync();
        var settingsTask = _realTradeService.GetSettingsAsync(userId);
        var rows = await _sectorRepository.GetSectorStocksAsync();

        var symbols = rows.Where(r => !string.IsNullOrWhiteSpace(r.Symbol))
            .Select(r => r.Symbol!.Trim().ToUpperInvariant())
            .Distinct()
            .ToList();
        var sectorNames = rows.Select(r => r.SectorName.Trim().ToUpperInvariant()).Distinct();

        // Three candle queries in total; the daily one also covers NIFTY and the sector indices.
        var dailyTask = LoadBatchAsync(symbols.Concat(sectorNames).Concat(new[] { "NIFTY 50", "NIFTYBEES" }).Distinct().ToList(), "1d");
        var m15Task = LoadBatchAsync(symbols, "15m");
        var m60Task = LoadBatchAsync(symbols, "60m");
        await Task.WhenAll(strategyTask, settingsTask, dailyTask, m15Task, m60Task);

        var strategy = strategyTask.Result ?? SwingStrategySettings.Default;
        var settings = settingsTask.Result;
        var daily = dailyTask.Result;
        var m15 = m15Task.Result;
        var m60 = m60Task.Result;

        var nifty = Candles(daily, "NIFTY 50");
        if (!nifty.Any()) nifty = Candles(daily, "NIFTYBEES");

        // Each stock is scored once, even when it sits in several sectors. Pure CPU from here on.
        var bySymbol = symbols.AsParallel()
            .Select(s => EvaluateStock(s, Candles(daily, s), Candles(m15, s), Candles(m60, s), nifty, strategy, settings.MinConditionsMatch))
            .ToDictionary(e => e.Symbol, StringComparer.OrdinalIgnoreCase);

        var snapshot = new Snapshot
        {
            AsOfUtc = DateTime.UtcNow,
            MarketPassed = SwingDecisionEngine.IsNiftyMarketFilterPassed(nifty, strategy),
            MarketRequired = strategy.RequireNiftyMarketFilter,
            BuyThreshold = strategy.BuyScoreThreshold,
            WatchThreshold = strategy.WatchScoreThreshold,
            MinConditionsMatch = settings.MinConditionsMatch
        };

        foreach (var group in rows.GroupBy(r => new { r.SectorId, r.SectorName }))
        {
            var members = group.Where(r => !string.IsNullOrWhiteSpace(r.Symbol))
                .Select(r => (Row: r, Eval: bySymbol[r.Symbol!.Trim().ToUpperInvariant()]))
                .ToList();

            var index = Candles(daily, group.Key.SectorName.Trim().ToUpperInvariant());
            var summary = BuildSectorSummary(group.Key.SectorId, group.Key.SectorName, members.Select(m => m.Eval).ToList(), index);
            var stocks = members.Select(m => BuildStockSignal(m.Row, m.Eval, summary)).ToList();

            summary.BuyCount = stocks.Count(s => s.Signal == "BUY");
            summary.WatchCount = stocks.Count(s => s.Signal == "WATCH");
            summary.NoTradeCount = stocks.Count(s => s.Signal == "NO_TRADE");
            summary.NoDataCount = stocks.Count(s => s.Signal == "NO_DATA");

            snapshot.Sectors.Add(new SectorSnapshot { Summary = summary, Stocks = stocks });
        }

        _logger.LogInformation("Sector dashboard built for user {UserId}: {Sectors} sectors, {Stocks} stocks in {ElapsedMs} ms.",
            userId, snapshot.Sectors.Count, symbols.Count, (int)(DateTime.UtcNow - started).TotalMilliseconds);
        return snapshot;
    }

    // Same candle set, engine call and bot-candidate rule as StockVerdictService.GetVerdictAsync.
    private static StockEvaluation EvaluateStock(string symbol, List<MarketCandle> candles1d, List<MarketCandle> candles15m, List<MarketCandle> candles60m,
        List<MarketCandle> nifty, SwingStrategySettings strategy, int minConditionsMatch)
    {
        var eval = new StockEvaluation
        {
            Symbol = symbol,
            LastPrice = candles15m.LastOrDefault()?.Close ?? candles1d.LastOrDefault()?.Close ?? 0m
        };

        if (candles1d.Count >= 2)
        {
            decimal prevClose = candles1d[^2].Close;
            eval.DayChangePct = prevClose > 0m ? Math.Round((candles1d[^1].Close - prevClose) / prevClose * 100m, 2) : null;
        }

        if (candles1d.Count < RealTradeSchedule.MinDailyCandles)
        {
            eval.NoDataReason = candles1d.Count == 0
                ? "No candles stored - activate the stock and sync its history."
                : $"Only {candles1d.Count} daily candles - the engine needs at least {RealTradeSchedule.MinDailyCandles}.";
            return eval;
        }

        // The engine only reads the symbol from StockMaster, so no stock_master lookup is needed.
        var result = SwingDecisionEngine.Evaluate(new StockMaster { Symbol = symbol }, candles1d, candles15m, candles60m, nifty, strategy);
        int metCount = result.Checklist?.MetCount ?? 0;

        eval.Result = result;
        eval.LastPrice = result.EntryPrice;
        eval.IsBotCandidate = result.HardFiltersPassed && (result.IsBuySignal || metCount >= minConditionsMatch);
        return eval;
    }

    private static SectorSummaryDto BuildSectorSummary(int sectorId, string name, List<StockEvaluation> stocks, List<MarketCandle> indexCandles)
    {
        var scored = stocks.Where(s => s.Result != null).ToList();
        var withChange = stocks.Where(s => s.DayChangePct.HasValue).ToList();
        int advancing = withChange.Count(s => s.DayChangePct > 0m);
        int declining = withChange.Count(s => s.DayChangePct < 0m);

        var summary = new SectorSummaryDto
        {
            SectorId = sectorId,
            Name = name,
            TotalStocks = stocks.Count,
            ScoredStocks = scored.Count,
            AdvancingStocks = advancing,
            DecliningStocks = declining,
            MomentumPct = scored.Count > 0
                ? (int)Math.Round(100m * scored.Count(s => s.Result!.EmaTrendPassed && s.Result.AdxPassed) / scored.Count)
                : 0
        };

        // The sector index itself when its candles are stored under the sector name (e.g. "NIFTY BANK").
        var index = indexCandles.Skip(Math.Max(0, indexCandles.Count - 2)).ToList();
        if (index.Count >= 2 && index[0].Close > 0m)
        {
            summary.IndexValue = index[1].Close;
            summary.ChangePct = Math.Round((index[1].Close - index[0].Close) / index[0].Close * 100m, 2);
            summary.ChangeSource = "INDEX";
        }
        else if (withChange.Any())
        {
            summary.ChangePct = Math.Round(withChange.Average(s => s.DayChangePct!.Value), 2);
            summary.ChangeSource = "STOCKS";
        }

        if (summary.ChangePct == null)
        {
            summary.Strength = "NO_DATA";
            return summary;
        }

        decimal change = summary.ChangePct.Value;
        decimal breadth = advancing + declining > 0 ? (decimal)advancing / (advancing + declining) : 0.5m;

        summary.Strength = change >= StrongChangePct && breadth >= StrongBreadth ? "STRONG"
            : change <= -StrongChangePct && breadth <= 1m - StrongBreadth ? "WEAK"
            : "NEUTRAL";

        // Half day change (-2% -> 0, +2% -> 100), half breadth.
        decimal changePart = Math.Clamp(50m + change * 25m, 0m, 100m);
        summary.StrengthScore = (int)Math.Round(changePart * 0.5m + breadth * 100m * 0.5m);
        return summary;
    }

    // ------------------------------------------------------------------------------------------
    // One stock's final status
    // ------------------------------------------------------------------------------------------

    private static SectorStockSignalDto BuildStockSignal(SectorStockRow row, StockEvaluation eval, SectorSummaryDto sector)
    {
        var dto = new SectorStockSignalDto
        {
            Symbol = eval.Symbol,
            Name = row.StockName,
            LastPrice = eval.LastPrice,
            DayChangePct = eval.DayChangePct
        };

        var r = eval.Result;
        if (r == null)
        {
            dto.Signal = "NO_DATA";
            dto.BotVerdict = "NO_DATA";
            dto.Summary = eval.NoDataReason;
            dto.Highlight = "No data";
            return dto;
        }

        bool sectorWeak = sector.Strength == "WEAK";
        bool rejected = !r.HardFiltersPassed;

        dto.EngineDecision = r.Decision;
        dto.BotVerdict = eval.IsBotCandidate ? "BUY" : r.Decision == "REJECT" ? "AVOID" : "WAIT";
        dto.Score = r.Score;
        dto.MetCount = r.Checklist?.MetCount ?? 0;
        dto.TotalConditions = r.Checklist?.TotalCount ?? 11;
        dto.VolumeMultiple = r.VolumeMultiple;
        dto.Rsi15m = r.Rsi15m;
        dto.Rsi60m = r.Rsi60m;
        dto.Adx1d = r.Adx1d;
        dto.TimingPoints = r.Factors.Where(f => TimingFactors.Contains(f.Code)).Sum(f => f.Points);
        dto.TimingMaxPoints = r.Factors.Where(f => TimingFactors.Contains(f.Code)).Sum(f => f.MaxPoints);
        dto.Entry = r.EntryPrice;
        dto.StopLoss = rejected ? 0m : r.StopLoss;
        dto.Target1 = rejected ? 0m : r.Target1;
        dto.RiskReward = rejected ? 0m : r.RiskRewardRatio;

        dto.Conditions.Add(new SectorConditionDto
        {
            Code = "SECTOR_STRENGTH",
            Name = "Sector is not weak",
            Detail = $"{sector.Name} must not be WEAK today (STRONG or NEUTRAL passes)",
            Value = sector.Strength == "NO_DATA"
                ? "No sector data"
                : $"{sector.Strength} ({FormatPct(sector.ChangePct)}, {sector.AdvancingStocks} of {sector.AdvancingStocks + sector.DecliningStocks} stocks up)",
            IsMet = !sectorWeak,
            IsGate = true
        });
        foreach (var c in r.Checklist?.Conditions ?? new List<ConditionItemDto>())
        {
            bool isGate = c.Code.StartsWith("HARD_", StringComparison.Ordinal);
            dto.Conditions.Add(new SectorConditionDto
            {
                Code = c.Code,
                Name = ConditionPrefix.Replace(c.Name, string.Empty),
                Detail = c.Description,
                Value = c.ActualValueText,
                IsMet = c.IsMet,
                IsGate = isGate,
                // A hard-filter reject returns before the scoring rules run.
                IsChecked = !rejected || isGate || c.Code == "MARKET_CONTEXT_FILTER"
            });
        }

        dto.Pillars = BuildPillars(r, rejected);

        if (eval.IsBotCandidate && !sectorWeak)
        {
            dto.Signal = "BUY";
            dto.Highlight = JoinFactors(r.Factors.Where(f => f.Points > 0 && f.Code != "RISK_REWARD").OrderByDescending(f => f.Points).Take(2), "All conditions met");
            dto.Summary = $"Bot-ready: score {r.Score}/100 with {dto.MetCount} of {dto.TotalConditions} conditions met, and the sector is {sector.Strength}.";
        }
        else if (eval.IsBotCandidate)
        {
            dto.Signal = "WATCH";
            dto.Highlight = "Sector weak";
            dto.Summary = $"The stock itself is bot-ready (score {r.Score}/100), but {sector.Name} is WEAK today - wait for the sector to turn before buying.";
        }
        else if (r.Decision == "WATCH")
        {
            dto.Signal = "WATCH";
            var missing = r.Factors.Where(f => f.Points < f.MaxPoints && f.Code != "RISK_REWARD").OrderByDescending(f => f.MaxPoints - f.Points).Take(2).ToList();
            dto.Highlight = missing.Any() ? "Waiting for " + JoinFactors(missing, string.Empty).ToLowerInvariant() : "Waiting for confirmation";
            dto.Summary = $"Trend filters pass, but the score is {r.Score}/100 - BUY needs more confirmation{(missing.Any() ? " (" + JoinFactors(missing, string.Empty) + ")" : string.Empty)}.";
        }
        else
        {
            dto.Signal = "NO_TRADE";
            if (rejected)
            {
                var failed = dto.Conditions.Where(c => c.IsGate && !c.IsMet && c.Code != "SECTOR_STRENGTH").Select(c => c.Name).ToList();
                dto.Highlight = failed.FirstOrDefault() ?? "Hard filter failed";
                dto.Summary = $"Rejected by a hard filter: {string.Join(", ", failed)}.";
            }
            else
            {
                dto.Highlight = $"Score {r.Score}";
                dto.Summary = $"Score {r.Score}/100 is below the WATCH line - not enough of the conditions are met.";
            }
        }

        return dto;
    }

    private static List<SectorStockPillarDto> BuildPillars(SwingEvaluationResult r, bool rejected)
    {
        int Points(string code) => r.Factors.FirstOrDefault(f => f.Code == code)?.Points ?? 0;
        const string Na = "Not checked";

        var pillars = new List<SectorStockPillarDto>
        {
            new()
            {
                Label = "Trend",
                Value = r.EmaTrendPassed && r.AdxPassed ? $"Bullish (ADX {r.Adx1d:F0})"
                    : r.EmaTrendPassed ? $"Choppy (ADX {r.Adx1d:F0} < 20)"
                    : "Not bullish",
                State = r.EmaTrendPassed && r.AdxPassed ? "PASS" : "FAIL"
            }
        };

        if (rejected)
        {
            pillars.Add(new() { Label = "Momentum", Value = Na });
            pillars.Add(new() { Label = "Volume", Value = Na });
            pillars.Add(new() { Label = "Breakout", Value = Na });
            pillars.Add(new() { Label = "Risk/Reward", Value = Na });
            return pillars;
        }

        bool rsiOk = Points("RSI_MOMENTUM") > 0, macdOk = Points("MACD_BULLISH") > 0;
        pillars.Add(new()
        {
            Label = "Momentum",
            Value = (rsiOk && macdOk ? "Strong" : rsiOk || macdOk ? "Building" : "Weak") + $" (RSI {r.Rsi15m:F0})",
            State = rsiOk && macdOk ? "PASS" : "FAIL"
        });
        pillars.Add(new()
        {
            Label = "Volume",
            Value = (r.VolumeMultiple >= 1.5m ? "Above average" : "Below average") + $" ({r.VolumeMultiple:F1}x)",
            State = r.VolumeMultiple >= 1.5m ? "PASS" : "FAIL"
        });
        pillars.Add(new()
        {
            Label = "Breakout",
            Value = Points("BREAKOUT_GROUP") > 0 ? "Confirmed" : "Inside range",
            State = Points("BREAKOUT_GROUP") > 0 ? "PASS" : "FAIL"
        });
        pillars.Add(new()
        {
            Label = "Risk/Reward",
            Value = $"1:{r.RiskRewardRatio:0.#}",
            State = r.RiskRewardRatio >= 2.0m ? "PASS" : "FAIL"
        });
        return pillars;
    }

    private static string JoinFactors(IEnumerable<SwingFactorScore> factors, string fallback)
    {
        var names = factors.Select(f => FactorShortNames.TryGetValue(f.Code, out var n) ? n : f.Name).ToList();
        return names.Any() ? string.Join(" + ", names) : fallback;
    }

    private static string FormatPct(decimal? pct) => pct == null ? "n/a" : $"{(pct >= 0 ? "+" : string.Empty)}{pct:F2}%";

    // Latest CandleHistoryCount candles per symbol (the scan worker's window), oldest first, keyed by upper-case symbol.
    private async Task<Dictionary<string, List<MarketCandle>>> LoadBatchAsync(IReadOnlyCollection<string> symbols, string timeframe) =>
        (await _candleRepository.GetRecentHistoryBatchAsync(symbols, timeframe, RealTradeSchedule.CandleHistoryCount))
            .GroupBy(c => c.Symbol.ToUpperInvariant())
            .ToDictionary(g => g.Key, g => g.OrderBy(c => c.CandleTime).ToList());

    private static List<MarketCandle> Candles(Dictionary<string, List<MarketCandle>> bySymbol, string symbol) =>
        bySymbol.TryGetValue(symbol.ToUpperInvariant(), out var list) ? list : new List<MarketCandle>();

    private sealed class StockEvaluation
    {
        public string Symbol { get; set; } = string.Empty;
        public decimal LastPrice { get; set; }
        public decimal? DayChangePct { get; set; }
        public SwingEvaluationResult? Result { get; set; }
        public bool IsBotCandidate { get; set; }
        public string NoDataReason { get; set; } = string.Empty;
    }

    private sealed class Snapshot
    {
        public DateTime AsOfUtc { get; set; }
        public bool MarketPassed { get; set; }
        public bool MarketRequired { get; set; }
        public int BuyThreshold { get; set; }
        public int WatchThreshold { get; set; }
        public int MinConditionsMatch { get; set; }
        public List<SectorSnapshot> Sectors { get; } = new();
    }

    private sealed class SectorSnapshot
    {
        public SectorSummaryDto Summary { get; set; } = new();
        public List<SectorStockSignalDto> Stocks { get; set; } = new();
    }
}
