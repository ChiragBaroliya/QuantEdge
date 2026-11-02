using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Extensions.Logging;
using QuantEdge.Domain.Entities;
using QuantEdge.Infrastructure.Constants;
using QuantEdge.Infrastructure.DTOs;
using QuantEdge.Infrastructure.Persistence;
using QuantEdge.Infrastructure.Persistence.Repositories;

namespace QuantEdge.Infrastructure.Services.Backtest;

public sealed class BacktestRunRow
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public string? Label { get; set; }
    public string Status { get; set; } = "QUEUED";
    public int ProgressPct { get; set; }
    public string? Message { get; set; }
    public string ParamsJson { get; set; } = "{}";
    public string? SummaryJson { get; set; }
    public string? Error { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
}

public interface IBacktestService
{
    /// <summary>Validates, fills blank inputs from the live settings and queues the run. Returns the run id.</summary>
    Task<int> QueueAsync(BacktestParams input, int userId);
    Task<IReadOnlyList<BacktestRunRow>> ListRunsAsync(int limit = 30);
    Task<BacktestRunRow?> GetRunAsync(int id);
    Task<IReadOnlyList<BacktestTrade>> GetTradesAsync(int runId);
    /// <summary>A queued run is cancelled, a running one asked to stop; a finished one is deleted with its trades.</summary>
    Task<bool> CancelOrDeleteAsync(int id);
    /// <summary>Worker: marks runs left RUNNING by a restart as FAILED.</summary>
    Task FailInterruptedAsync();
    /// <summary>Worker: atomically moves the oldest QUEUED run to RUNNING and returns it.</summary>
    Task<BacktestRunRow?> ClaimNextAsync();
    /// <summary>Worker: replays the run and stores its trades and summary. Database only - no Zerodha call.</summary>
    Task RunAsync(BacktestRunRow run, int maxThreads, CancellationToken cancellationToken);
}

public class BacktestService : IBacktestService
{
    private const string NiftySymbol = "NIFTY 50";
    private const string VixSymbol = "INDIA VIX";
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static volatile bool _sideColumnEnsured;

    private readonly IDbConnectionFactory _connectionFactory;
    private readonly IStockMasterRepository _stockRepository;
    private readonly ISwingStrategySettingsRepository _strategyRepository;
    private readonly IRealTradingRepository _realTradingRepository;
    private readonly IMarketRegimeService _regimeService;
    private readonly IChargeRatesRepository _chargeRatesRepository;
    private readonly ILogger<BacktestService> _logger;

    public BacktestService(IDbConnectionFactory connectionFactory, IStockMasterRepository stockRepository,
        ISwingStrategySettingsRepository strategyRepository, IRealTradingRepository realTradingRepository,
        IMarketRegimeService regimeService, IChargeRatesRepository chargeRatesRepository, ILogger<BacktestService> logger)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _stockRepository = stockRepository ?? throw new ArgumentNullException(nameof(stockRepository));
        _strategyRepository = strategyRepository ?? throw new ArgumentNullException(nameof(strategyRepository));
        _realTradingRepository = realTradingRepository ?? throw new ArgumentNullException(nameof(realTradingRepository));
        _regimeService = regimeService ?? throw new ArgumentNullException(nameof(regimeService));
        _chargeRatesRepository = chargeRatesRepository ?? throw new ArgumentNullException(nameof(chargeRatesRepository));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Adds backtest_trades.side on first use (data_fixes/2026-10-30_backtest_short_side.sql does the same by hand). Earlier trades were all BUY.</summary>
    private async Task EnsureSideColumnAsync()
    {
        if (_sideColumnEnsured) return;
        using var connection = _connectionFactory.CreateConnection();
        await connection.ExecuteAsync("ALTER TABLE backtest_trades ADD COLUMN IF NOT EXISTS side VARCHAR(10) NOT NULL DEFAULT 'BUY';");
        _sideColumnEnsured = true;
    }

    // ------------------------------------------------------------------------------------------
    // Queue / read
    // ------------------------------------------------------------------------------------------

    public async Task<int> QueueAsync(BacktestParams input, int userId)
    {
        await EnsureSideColumnAsync();
        var p = await ResolveAsync(input, userId);
        using var connection = _connectionFactory.CreateConnection();
        return await connection.ExecuteScalarAsync<int>(@"
            INSERT INTO backtest_runs (user_id, label, status, params, message)
            VALUES (@userId, @label, 'QUEUED', CAST(@json AS jsonb), 'Waiting for the worker')
            RETURNING id;", new { userId, label = p.Label, json = JsonSerializer.Serialize(p, Json) });
    }

    /// <summary>Fills every blank input from the live settings, so the stored params describe the run completely.</summary>
    public async Task<BacktestParams> ResolveAsync(BacktestParams input, int userId)
    {
        var strategy = await _strategyRepository.GetSettingsAsync();
        var real = await _realTradingRepository.GetSettingsAsync(userId);

        var p = input;
        p.ToDate = (p.ToDate == default ? BacktestEngine.IstDate(DateTime.UtcNow).AddDays(-1) : p.ToDate).Date;
        p.FromDate = (p.FromDate == default ? p.ToDate.AddYears(-2) : p.FromDate).Date;
        if (p.FromDate > p.ToDate) throw new ArgumentException("From date must be on or before the To date.");
        if ((p.ToDate - p.FromDate).TotalDays > 366 * 5) throw new ArgumentException("At most 5 years per run.");
        p.Symbols = (p.Symbols ?? new()).Select(s => s.Trim().ToUpperInvariant()).Where(s => s.Length > 0).Distinct().ToList();

        p.Side = string.IsNullOrWhiteSpace(p.Side) ? BacktestSide.Buy : p.Side.Trim().ToUpperInvariant();
        if (p.Side != BacktestSide.Buy && p.Side != BacktestSide.Short && p.Side != BacktestSide.Both)
            throw new ArgumentException("Side must be BUY, SHORT or BOTH.");
        p.Strategy = BacktestStrategy.Normalize(p.Strategy);
        if (!BacktestStrategy.IsKnown(p.Strategy)) throw new ArgumentException("Strategy must be SCORE, MOMENTUM_DAILY or INTRADAY_ORB.");
        if (p.Strategy == BacktestStrategy.MomentumDaily && p.Side != BacktestSide.Buy)
            throw new ArgumentException("Daily momentum is a Buy-only rule.");

        p.GateMode = string.IsNullOrWhiteSpace(p.GateMode) ? strategy.MarketGateMode : p.GateMode.Trim().ToUpperInvariant();
        if (p.GateMode != SwingStrategySettings.GateModeNiftyFilter && p.GateMode != SwingStrategySettings.GateModeRegime)
            throw new ArgumentException("Gate mode must be NIFTY_FILTER or REGIME.");
        p.BuyScoreThreshold ??= strategy.BuyScoreThreshold;
        p.WatchScoreThreshold ??= strategy.WatchScoreThreshold;
        p.MinConditionsMatch ??= real.MinConditionsMatch;
        p.Rules ??= strategy.Rules;

        if (p.Capital <= 0m) throw new ArgumentException("Capital must be positive.");
        if (p.AmountPerTrade <= 0m) throw new ArgumentException("Amount per trade must be positive.");
        p.SizingMode = string.Equals(p.SizingMode, "FIXED", StringComparison.OrdinalIgnoreCase) ? "FIXED" : "RISK";
        p.RiskPct = p.RiskPct > 0m ? p.RiskPct : SwingTradeRules.DefaultRiskPerTradePct;
        p.MaxPositions ??= SwingTradeRules.MaxConcurrentPositions;
        p.MaxTradesPerDay ??= real.MaxTradesPerDay;
        p.DailyLossLimit ??= Math.Round(p.Capital * SwingTradeRules.DefaultDailyLossLimitFactor, 2);
        p.SlippagePct = Math.Clamp(p.SlippagePct, 0m, 2m);

        p.TradingWindowStart ??= real.TradingWindowStart;
        p.TradingWindowEnd ??= real.TradingWindowEnd;
        p.EntryDelayMinutes ??= real.EntryDelayMinutes;
        p.ExitMode ??= real.ExitMode;
        p.CloseCheckTime ??= real.CloseCheckTime;
        p.StopLossAtrMult ??= real.StopLossAtrMult;
        p.TrailAtrMult ??= real.TrailAtrMult;
        p.TargetAtrMult ??= real.TargetAtrMult;
        p.ShortStopLossAtrMult ??= real.ShortStopLossAtrMult;
        p.ShortTrailAtrMult ??= real.ShortTrailAtrMult;
        p.ShortTargetAtrMult ??= real.ShortTargetAtrMult;
        p.TrailActivationPct ??= real.TrailActivationPct;
        p.ProfitTargetPct ??= real.ProfitTargetPct;
        p.MaxDurationDays ??= real.MaxDurationDays;
        p.ShortEntryCutoff = string.IsNullOrWhiteSpace(p.ShortEntryCutoff) ? real.ShortEntryCutoff : p.ShortEntryCutoff.Trim();
        p.ShortSquareOffTime = string.IsNullOrWhiteSpace(p.ShortSquareOffTime) ? real.ShortSquareOffTime : p.ShortSquareOffTime.Trim();
        var shortTimeErrors = ShortSellingSettingsValidator.Validate(p.ShortEntryCutoff, p.ShortSquareOffTime).ToList();
        if (BacktestSide.IncludesShort(p.Side) && shortTimeErrors.Count > 0) throw new ArgumentException(string.Join(" ", shortTimeErrors));
        ApplyFixedRules(p);
        p.Label = string.IsNullOrWhiteSpace(p.Label)
            ? p.Strategy == BacktestStrategy.Score
                ? $"{p.Side} · {StocksText(p.Symbols)} · {p.GateMode} · score ≥ {p.BuyScoreThreshold} · {p.FromDate:dd-MMM-yy} → {p.ToDate:dd-MMM-yy}"
                : $"{BacktestStrategy.NameOf(p.Strategy)} · {p.Side} · {StocksText(p.Symbols)} · {p.FromDate:dd-MMM-yy} → {p.ToDate:dd-MMM-yy}"
            : p.Label.Trim();
        return p;
    }

    /// <summary>A new rule's numbers replace the form's, so the stored params say what really ran and the numbers can't be tuned to the data.</summary>
    public static void ApplyFixedRules(BacktestParams p)
    {
        if (p.Strategy == BacktestStrategy.MomentumDaily)
        {
            p.RuleSet = MomentumDailyRules.Numbers();
            p.RiskPct = MomentumDailyRules.RiskPct;
            p.AmountPerTrade = Math.Min(MomentumDailyRules.MaxPerTrade, p.Capital);
            p.MaxPositions = MomentumDailyRules.MaxPositions;
            p.MaxTradesPerDay = MomentumDailyRules.MaxEntriesPerDay;
            p.MaxDurationDays = MomentumDailyRules.MaxSessions;
        }
        else if (p.Strategy == BacktestStrategy.IntradayOrb)
        {
            p.RuleSet = IntradayOrbRules.Numbers();
            p.RiskPct = IntradayOrbRules.RiskPct;
            p.AmountPerTrade = Math.Min(IntradayOrbRules.MaxPerTrade, p.Capital);
            p.MaxPositions = IntradayOrbRules.MaxPositions;
            p.MaxTradesPerDay = IntradayOrbRules.MaxEntriesPerDay;
            p.MaxDurationDays = 0;
        }
        else
        {
            return;
        }
        p.SizingMode = "RISK";
        p.DailyLossLimit = Math.Round(p.Capital * SwingTradeRules.DefaultDailyLossLimitFactor, 2);
    }

    /// <summary>The stock list for a run's label, so runs over different stocks can be told apart in the list.</summary>
    public static string StocksText(IReadOnlyList<string> symbols) =>
        symbols.Count == 0 ? "All stocks"
        : symbols.Count <= 3 ? string.Join(", ", symbols)
        : $"{string.Join(", ", symbols.Take(3))} +{symbols.Count - 3} more";

    private const string SelectRun = @"
        SELECT id, user_id AS UserId, label, status, progress_pct AS ProgressPct, message, params::text AS ParamsJson,
               summary::text AS SummaryJson, error, created_at AS CreatedAt, started_at AS StartedAt, finished_at AS FinishedAt
        FROM backtest_runs";

    public async Task<IReadOnlyList<BacktestRunRow>> ListRunsAsync(int limit = 30)
    {
        using var connection = _connectionFactory.CreateConnection();
        // The summary can be large (equity curve) - the list only needs the headline numbers.
        var rows = await connection.QueryAsync<BacktestRunRow>(@"
            SELECT id, user_id AS UserId, label, status, progress_pct AS ProgressPct, message, params::text AS ParamsJson,
                   jsonb_build_object('verdict', summary->'verdict', 'kpis', summary->'kpis')::text AS SummaryJson,
                   error, created_at AS CreatedAt, started_at AS StartedAt, finished_at AS FinishedAt
            FROM backtest_runs ORDER BY id DESC LIMIT @limit;", new { limit = Math.Clamp(limit, 1, 200) });
        return rows.ToList();
    }

    public async Task<BacktestRunRow?> GetRunAsync(int id)
    {
        using var connection = _connectionFactory.CreateConnection();
        return await connection.QueryFirstOrDefaultAsync<BacktestRunRow>($"{SelectRun} WHERE id = @id;", new { id });
    }

    public async Task<IReadOnlyList<BacktestTrade>> GetTradesAsync(int runId)
    {
        await EnsureSideColumnAsync();
        using var connection = _connectionFactory.CreateConnection();
        var rows = await connection.QueryAsync<BacktestTrade>(@"
            SELECT symbol, side, sector, regime, signal_time AS SignalTime, entry_time AS EntryTime, entry_price AS EntryPrice,
                   exit_time AS ExitTime, exit_price AS ExitPrice, exit_reason AS ExitReason, exit_category AS ExitCategory,
                   quantity, stop_loss AS StopLoss, target, score, met_count AS MetCount, sessions_held AS SessionsHeld,
                   gross_pnl AS GrossPnl, charges, net_pnl AS NetPnl, r_multiple AS RMultiple, mfe_pct AS MfePct, mae_pct AS MaePct,
                   factors, end_of_data AS EndOfData, sub_type AS SubType, rsi_15m AS Rsi15m, rsi_60m AS Rsi60m,
                   volume_multiple AS VolumeMultiple, adx_1d AS Adx1d, signal_daily_atr AS SignalDailyAtr
            FROM backtest_trades WHERE run_id = @runId ORDER BY entry_time;", new { runId });
        return rows.ToList();
    }

    public async Task<bool> CancelOrDeleteAsync(int id)
    {
        using var connection = _connectionFactory.CreateConnection();
        int n = await connection.ExecuteAsync(@"
            UPDATE backtest_runs SET status = 'CANCELLED', message = 'Cancelled before it started', finished_at = NOW()
            WHERE id = @id AND status = 'QUEUED';", new { id });
        if (n > 0) return true;
        n = await connection.ExecuteAsync(@"
            UPDATE backtest_runs SET status = 'CANCEL_REQUESTED', message = 'Stopping…' WHERE id = @id AND status = 'RUNNING';", new { id });
        if (n > 0) return true;
        return await connection.ExecuteAsync("DELETE FROM backtest_runs WHERE id = @id AND status IN ('DONE', 'FAILED', 'CANCELLED');", new { id }) > 0;
    }

    public async Task FailInterruptedAsync()
    {
        using var connection = _connectionFactory.CreateConnection();
        await connection.ExecuteAsync(@"
            UPDATE backtest_runs SET status = CASE WHEN status = 'CANCEL_REQUESTED' THEN 'CANCELLED' ELSE 'FAILED' END,
                   error = COALESCE(error, 'Interrupted - the worker restarted while this run was in progress. Queue it again.'),
                   finished_at = NOW()
            WHERE status IN ('RUNNING', 'CANCEL_REQUESTED');");
    }

    public async Task<BacktestRunRow?> ClaimNextAsync()
    {
        using var connection = _connectionFactory.CreateConnection();
        var id = await connection.ExecuteScalarAsync<int?>(@"
            UPDATE backtest_runs SET status = 'RUNNING', started_at = NOW(), progress_pct = 0, message = 'Loading data'
            WHERE id = (SELECT id FROM backtest_runs WHERE status = 'QUEUED' ORDER BY id LIMIT 1 FOR UPDATE SKIP LOCKED)
            RETURNING id;");
        return id.HasValue ? await GetRunAsync(id.Value) : null;
    }

    private async Task<bool> ReportProgressAsync(int id, int pct, string message)
    {
        using var connection = _connectionFactory.CreateConnection();
        var status = await connection.ExecuteScalarAsync<string?>(@"
            UPDATE backtest_runs SET progress_pct = GREATEST(progress_pct, @pct), message = @message
            WHERE id = @id RETURNING status;", new { id, pct, message });
        return status == "CANCEL_REQUESTED";
    }

    // ------------------------------------------------------------------------------------------
    // Run
    // ------------------------------------------------------------------------------------------

    public async Task RunAsync(BacktestRunRow run, int maxThreads, CancellationToken cancellationToken)
    {
        var started = DateTime.UtcNow;
        try
        {
            await EnsureSideColumnAsync();
            var p = JsonSerializer.Deserialize<BacktestParams>(run.ParamsJson, Json) ?? throw new InvalidOperationException("Run has no parameters.");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var (summary, trades) = await ExecuteAsync(run.Id, p, Math.Max(1, maxThreads), cts);

            using var connection = _connectionFactory.CreateConnection();
            connection.Open();
            using var tx = connection.BeginTransaction();
            await connection.ExecuteAsync("DELETE FROM backtest_trades WHERE run_id = @id;", new { id = run.Id }, tx);
            await connection.ExecuteAsync(@"
                INSERT INTO backtest_trades (run_id, symbol, side, sector, regime, signal_time, entry_time, entry_price, exit_time, exit_price,
                    exit_reason, exit_category, quantity, stop_loss, target, score, met_count, sessions_held, gross_pnl, charges, net_pnl,
                    r_multiple, mfe_pct, mae_pct, factors, end_of_data, sub_type, rsi_15m, rsi_60m, volume_multiple, adx_1d, signal_daily_atr)
                VALUES (@RunId, @Symbol, @Side, @Sector, @Regime, @SignalTime, @EntryTime, @EntryPrice, @ExitTime, @ExitPrice,
                    LEFT(@ExitReason, 200), @ExitCategory, @Quantity, @StopLoss, @Target, @Score, @MetCount, @SessionsHeld, @GrossPnl, @Charges, @NetPnl,
                    @RMultiple, @MfePct, @MaePct, LEFT(@Factors, 400), @EndOfData, @SubType, @Rsi15m, @Rsi60m, @VolumeMultiple, @Adx1d, @SignalDailyAtr);",
                trades.Select(t => new
                {
                    RunId = run.Id, t.Symbol, t.Side, t.Sector, t.Regime, t.SignalTime, t.EntryTime, t.EntryPrice, t.ExitTime, t.ExitPrice,
                    t.ExitReason, t.ExitCategory, t.Quantity, t.StopLoss, t.Target, t.Score, t.MetCount, t.SessionsHeld, t.GrossPnl, t.Charges,
                    t.NetPnl, t.RMultiple, t.MfePct, t.MaePct, t.Factors, t.EndOfData, t.SubType, t.Rsi15m, t.Rsi60m, t.VolumeMultiple, t.Adx1d, t.SignalDailyAtr
                }), tx);
            await connection.ExecuteAsync(@"
                UPDATE backtest_runs SET status = 'DONE', progress_pct = 100, summary = CAST(@json AS jsonb), message = @message,
                       finished_at = NOW(), error = NULL
                WHERE id = @id;", new { id = run.Id, json = JsonSerializer.Serialize(summary, Json), message = summary.VerdictReason }, tx);
            tx.Commit();
            _logger.LogInformation("Backtest #{Id} done in {Seconds:N0} s: {Trades} trades, expectancy {Exp}R, verdict {Verdict}.",
                run.Id, (DateTime.UtcNow - started).TotalSeconds, summary.Kpis.Trades, summary.Kpis.ExpectancyR, summary.Verdict);
        }
        catch (OperationCanceledException)
        {
            using var connection = _connectionFactory.CreateConnection();
            await connection.ExecuteAsync(@"
                UPDATE backtest_runs SET status = 'CANCELLED', message = 'Stopped', finished_at = NOW() WHERE id = @id;", new { id = run.Id });
            _logger.LogInformation("Backtest #{Id} cancelled.", run.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Backtest #{Id} failed.", run.Id);
            using var connection = _connectionFactory.CreateConnection();
            await connection.ExecuteAsync(@"
                UPDATE backtest_runs SET status = 'FAILED', error = @error, message = 'Failed', finished_at = NOW() WHERE id = @id;",
                new { id = run.Id, error = ex.Message });
            throw;
        }
    }

    private async Task<(BacktestSummary Summary, List<BacktestTrade> Trades)> ExecuteAsync(int runId, BacktestParams p, int threads,
        CancellationTokenSource cts)
    {
        if (BacktestStrategy.Normalize(p.Strategy) != BacktestStrategy.Score)
            return await ExecuteNewRuleAsync(runId, p, threads, cts);

        var token = cts.Token;
        bool regimeMode = p.GateMode == SwingStrategySettings.GateModeRegime;

        // --- Universe, sectors, settings ------------------------------------------------------
        var symbols = (await _stockRepository.GetActiveStocksAsync())
            .Select(s => s.Symbol.Trim().ToUpperInvariant())
            .Where(s => s != NiftySymbol && s != "NIFTYBEES" && s != VixSymbol)
            .Distinct().ToList();
        if (p.Symbols.Count > 0) symbols = p.Symbols.Where(s => s != NiftySymbol && s != VixSymbol).ToList();
        if (symbols.Count == 0) throw new InvalidOperationException("No stocks to test - no active stocks in stock_master.");
        var sectors = await LoadSectorsAsync();

        var strategy = await _strategyRepository.GetSettingsAsync();
        strategy.BuyScoreThreshold = p.BuyScoreThreshold!.Value;
        strategy.WatchScoreThreshold = p.WatchScoreThreshold!.Value;
        strategy.MarketGateMode = p.GateMode;
        strategy.Rules = p.Rules ?? strategy.Rules;
        var engineSettings = regimeMode ? RegimeGate.WithoutNiftyGate(strategy) : strategy;
        var tradeParams = SwingTradeParams.From(new RealTradeSettings
        {
            ExitMode = p.ExitMode!, CloseCheckTime = p.CloseCheckTime!, TradingWindowEnd = p.TradingWindowEnd!,
            StopLossAtrMult = p.StopLossAtrMult!.Value, TrailAtrMult = p.TrailAtrMult!.Value, TargetAtrMult = p.TargetAtrMult!.Value,
            ShortStopLossAtrMult = p.ShortStopLossAtrMult!.Value, ShortTrailAtrMult = p.ShortTrailAtrMult!.Value, ShortTargetAtrMult = p.ShortTargetAtrMult!.Value,
            TrailActivationPct = p.TrailActivationPct.GetValueOrDefault(),
            ProfitTargetPct = p.ProfitTargetPct!.Value
        });
        var policies = await _regimeService.GetPoliciesAsync();
        var chargeRates = await _chargeRatesRepository.GetAllAsync();

        // --- Daily candles for every stock (indicators, breadth, equity marks) ------------------
        await ReportProgressAsync(runId, 1, "Loading daily candles");
        DateTime fromUtc = BacktestEngine.DailyStamp(p.FromDate.AddDays(-480));
        DateTime toUtc = BacktestEngine.DailyStamp(p.ToDate.AddDays(1));
        var dailyLoaded = await LoadCandlesAsync("1d", symbols.Concat(new[] { NiftySymbol, VixSymbol }).ToList(), fromUtc, toUtc);
        var dailyAll = dailyLoaded.ToDictionary(kv => kv.Key, kv => BacktestEngine.OneBarPerDay(kv.Value));
        var nifty = dailyAll.GetValueOrDefault(NiftySymbol) ?? new List<MarketCandle>();
        var vixByDate = (dailyAll.GetValueOrDefault(VixSymbol) ?? new List<MarketCandle>())
            .GroupBy(c => BacktestEngine.IstDate(c.CandleTime)).ToDictionary(g => g.Key, g => g.Last().Close);

        var sessions = SessionsOf(dailyAll, nifty);
        int SessionOf(DateTime day)
        {
            int idx = sessions.BinarySearch(day.Date);
            return idx >= 0 ? idx : ~idx;
        }
        var testSessions = sessions.Where(d => d >= p.FromDate && d <= p.ToDate).ToList();

        // --- Market regime for every day, from the same data (previous session's reading applies) ---
        await ReportProgressAsync(runId, 3, "Computing the market regime for each day");
        var regimeByDate = ComputeRegimes(nifty, symbols.Select(s => dailyAll.GetValueOrDefault(s)).Where(l => l != null).Cast<IReadOnlyList<MarketCandle>>().ToList(),
            vixByDate, sessions.Where(d => d >= p.FromDate.AddDays(-14) && d <= p.ToDate).ToList(), token);
        var regimeDates = regimeByDate.Keys.OrderBy(d => d).ToList();
        (string? Regime, RegimePolicy? Policy) RegimeFor(DateTime day)
        {
            int idx = regimeDates.BinarySearch(day.Date);
            idx = (idx >= 0 ? idx : ~idx) - 1;                 // latest reading strictly before the day
            if (idx < 0) return (null, null);
            var reading = regimeByDate[regimeDates[idx]];
            var policy = policies.FirstOrDefault(x => x.Regime == reading.Regime) ?? RegimePolicy.Defaults.First(x => x.Regime == reading.Regime);
            return (reading.Regime, policy);
        }

        var nifty15 = await LoadCandlesAsync("15m", new List<string> { NiftySymbol },
            BacktestEngine.DailyStamp(p.FromDate), toUtc);
        var nifty15ByDate = (nifty15.GetValueOrDefault(NiftySymbol) ?? new List<MarketCandle>())
            .GroupBy(c => BacktestEngine.IstDate(c.CandleTime)).ToDictionary(g => g.Key, g => g.OrderBy(c => c.CandleTime).ToList());

        var ctx = new BacktestContext
        {
            EngineSettings = engineSettings,
            RegimeMode = regimeMode,
            MinConditionsMatch = p.MinConditionsMatch!.Value,
            SweepFloor = Math.Min(60, p.BuyScoreThreshold!.Value),
            TradeParams = tradeParams,
            TestBuy = BacktestSide.IncludesBuy(p.Side),
            TestShort = BacktestSide.IncludesShort(p.Side),
            ShortEntryCutoff = p.ShortEntryCutoff ?? "15:00",
            ShortSquareOffTime = p.ShortSquareOffTime ?? "15:15",
            WindowStart = p.TradingWindowStart!,
            WindowEnd = p.TradingWindowEnd!,
            EntryDelayMinutes = p.EntryDelayMinutes!.Value,
            MaxDurationDays = p.MaxDurationDays!.Value,
            FromDate = p.FromDate,
            ToDate = p.ToDate,
            NiftyDaily = nifty,
            Nifty15mByDate = nifty15ByDate,
            RegimeFor = RegimeFor,
            SessionOf = SessionOf,
            SlippagePct = p.SlippagePct
        };

        // --- Replay every symbol (dedicated low-priority threads so the live feed in this process keeps its CPU) ---
        var signals = new ConcurrentBag<BacktestSignal>();
        var tapes = new ConcurrentDictionary<string, IntradayTape>();
        var splits = new ConcurrentBag<string>();
        var noData = new ConcurrentBag<string>();
        var intradayDates = new ConcurrentBag<(DateTime First, DateTime Last)>();
        var dataChecks = new ConcurrentBag<IntradayDataCheck>();
        long scans = 0;

        await ReplayAsync(runId, symbols, threads, cts, () => Interlocked.Read(ref scans), symbol =>
        {
            var daily = dailyAll.GetValueOrDefault(symbol) ?? new List<MarketCandle>();
            var m15 = LoadCandles("15m", symbol, BacktestEngine.DailyStamp(p.FromDate.AddDays(-10)), toUtc);
            var h60 = LoadCandles("60m", symbol, BacktestEngine.DailyStamp(p.FromDate.AddDays(-40)), toUtc);
            if (m15.Count == 0 || daily.Count < RealTradeSchedule.MinDailyCandles) { noData.Add(symbol); return; }
            intradayDates.Add((BacktestEngine.IstDate(m15[0].CandleTime), BacktestEngine.IstDate(m15[^1].CandleTime)));
            dataChecks.Add(BacktestDataCheck.Check15m(m15, daily, p.FromDate, p.ToDate));

            var r = BacktestEngine.GenerateSignals(symbol, daily, h60, m15, ctx);
            foreach (var s in r.Signals) signals.Add(s);
            if (r.Tape != null) tapes[symbol] = r.Tape;
            foreach (var d in r.SuspectedSplits.Where(d => d >= p.FromDate.AddDays(-400) && d <= p.ToDate)) splits.Add($"{symbol} {d:dd-MMM-yyyy}");
            Interlocked.Add(ref scans, r.Scans);
        });

        // --- Portfolio, sweep, metrics ----------------------------------------------------------
        await ReportProgressAsync(runId, 92, "Simulating the portfolio");
        var ordered = signals.OrderBy(s => s.EntryTimeUtc).ThenByDescending(s => s.Score).ThenBy(s => s.Symbol).ToList();
        var simulated = new HashSet<BacktestSignal>();
        BacktestExit? ExitOf(BacktestSignal s)
        {
            if (simulated.Add(s) && s.SkipReason == null && tapes.TryGetValue(s.Symbol, out var tape))
            {
                s.Exit = BacktestEngine.SimulateExit(tape, s, ctx, out var skip);
                if (skip != null) s.SkipReason = skip;
            }
            return s.Exit;
        }

        var ps = PortfolioSettingsOf(p, chargeRates);
        string? SectorOf(string symbol) => sectors.GetValueOrDefault(symbol);
        var CloseOn = CloseLookup(dailyAll);

        var liveSignals = ordered.Where(s => s.IsLiveCandidate).ToList();
        var main = BacktestPortfolio.Run(liveSignals, ExitOf, ps, SectorOf);

        await ReportProgressAsync(runId, 96, "Threshold sweep");
        var testedSides = new[] { false, true }.Where(isShort => isShort ? ctx.TestShort : ctx.TestBuy).ToList();
        var sweep = new List<BacktestSweepRow>();
        foreach (bool isShort in testedSides)
        foreach (int threshold in new[] { 60, 65, 70, 75, 80, 85, 90 }.Where(t => t >= ctx.SweepFloor))
        {
            token.ThrowIfCancellationRequested();
            var subset = ordered.Where(s => s.IsShort == isShort && s.HardFiltersPassed && s.GateAllows && s.Score >= threshold && (!s.PolicyRequiresRs || s.BeatsNifty));
            var r = BacktestPortfolio.Run(subset, ExitOf, ps, SectorOf);
            var eq = BacktestPortfolio.EquityCurve(r.Trades, p.Capital, testSessions, CloseOn, out decimal ex);
            var k = BacktestPortfolio.Kpis(r.Trades, eq, p.Capital, p.FromDate, p.ToDate, ex);
            sweep.Add(new BacktestSweepRow
            {
                Side = BacktestSide.Of(isShort),
                Threshold = threshold, Trades = k.Trades, WinRatePct = k.WinRatePct, ExpectancyR = k.ExpectancyR,
                ProfitFactor = k.ProfitFactor, NetPnl = k.NetPnl, MaxDrawdownPct = k.MaxDrawdownPct
            });
        }

        var firstLast = intradayDates.ToList();
        var summary = BuildSummary(p, testSessions, CloseOn, main, liveSignals, testedSides, logicChecks: BacktestLogicAudit.Run(main.Trades, ctx.ShortEntryCutoff, ctx.ShortSquareOffTime),
            byScore: BacktestPortfolio.ScoreBucket, minTrades: null);
        summary.Direction = BacktestEngine.DirectionAccuracy(liveSignals, symbol => tapes.GetValueOrDefault(symbol));
        summary.ThresholdSweep = sweep;
        summary.FactorEdge = BacktestPortfolio.FactorEdge(main.Trades);
        summary.Coverage = new BacktestCoverage
        {
            SymbolsRequested = symbols.Count,
            SymbolsWithData = symbols.Count - noData.Count,
            FirstIntradayDate = firstLast.Count > 0 ? firstLast.Min(x => x.First) : null,
            LastIntradayDate = firstLast.Count > 0 ? firstLast.Max(x => x.Last) : null,
            Sessions = testSessions.Count,
            ScansEvaluated = scans,
            SymbolsWithoutData = noData.OrderBy(s => s).ToList(),
            SuspectedSplits = splits.OrderBy(s => s).ToList()
        };
        summary.Assumptions = Assumptions(p, regimeMode, splits.Count, regimeByDate.Count);
        AddDailyRepairNote(summary, dailyLoaded, dailyAll);
        BacktestDataCheck.Apply(summary, dataChecks.ToList());
        return (summary, main.Trades);
    }

    // ------------------------------------------------------------------------------------------
    // Shared by every rule
    // ------------------------------------------------------------------------------------------

    private static List<DateTime> SessionsOf(IReadOnlyDictionary<string, List<MarketCandle>> dailyAll, IReadOnlyList<MarketCandle> nifty) =>
        BacktestEngine.TradingSessions(dailyAll.Where(kv => kv.Key != VixSymbol).Select(kv => (IReadOnlyList<MarketCandle>)kv.Value).ToList(), nifty);

    /// <summary>Runs <paramref name="replay"/> for every symbol on dedicated low-priority threads, so the live feed in this process keeps its CPU.</summary>
    private async Task ReplayAsync(int runId, IReadOnlyCollection<string> symbols, int threads, CancellationTokenSource cts, Func<long> scans, Action<string> replay)
    {
        var token = cts.Token;
        var queue = new ConcurrentQueue<string>(symbols);
        int done = 0;
        Exception? workerError = null;

        var workers = Enumerable.Range(0, Math.Min(threads, symbols.Count)).Select(_ => Task.Factory.StartNew(() =>
        {
            Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
            while (!token.IsCancellationRequested && queue.TryDequeue(out var symbol))
            {
                try
                {
                    replay(symbol);
                }
                catch (Exception ex)
                {
                    workerError ??= new InvalidOperationException($"{symbol}: {ex.Message}", ex);
                    cts.Cancel();
                }
                finally
                {
                    Interlocked.Increment(ref done);
                }
            }
        }, token, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToList();

        var all = Task.WhenAll(workers);
        while (!all.IsCompleted)
        {
            await Task.WhenAny(all, Task.Delay(TimeSpan.FromSeconds(3)));
            int pct = 5 + (int)(85.0 * Volatile.Read(ref done) / symbols.Count);
            if (await ReportProgressAsync(runId, pct, $"Replaying stocks: {Volatile.Read(ref done)}/{symbols.Count} · {scans():N0} scans")) cts.Cancel();
        }
        try { await all; } catch (OperationCanceledException) { }
        if (workerError != null) throw workerError;
        token.ThrowIfCancellationRequested();
    }

    /// <summary>Says in the report how many stored daily rows the replay re-dated or ignored (<see cref="BacktestEngine.OneBarPerDay"/>).</summary>
    private static void AddDailyRepairNote(BacktestSummary summary, IReadOnlyDictionary<string, List<MarketCandle>> loaded, IReadOnlyDictionary<string, List<MarketCandle>> used)
    {
        int ignored = loaded.Sum(kv => kv.Value.Count - used[kv.Key].Count);
        int reDated = loaded.Values.SelectMany(v => v).Count(c => BacktestEngine.DailyBarTradingDay(c.CandleTime) != BacktestEngine.IstDate(c.CandleTime));
        if (ignored > 0 || reDated > 0)
            summary.Assumptions.Add($"Daily candles: {reDated:N0} stored with the previous evening's time were read as the correct trading day, and {ignored:N0} extra row(s) for a day that already had a candle were ignored. The stored rows were not changed.");
    }

    private static PortfolioSettings PortfolioSettingsOf(BacktestParams p, IReadOnlyList<ChargeRates> chargeRates)
    {
        bool momentum = p.Strategy == BacktestStrategy.MomentumDaily;
        return new PortfolioSettings
        {
            Capital = p.Capital,
            AmountPerTrade = p.AmountPerTrade,
            RiskSizing = p.SizingMode == "RISK",
            RiskPct = p.RiskPct,
            MaxPositions = p.MaxPositions!.Value,
            MaxTradesPerDay = p.MaxTradesPerDay!.Value,
            DailyLossLimit = p.DailyLossLimit!.Value,
            SlippagePct = p.SlippagePct,
            Rates = chargeRates.Count > 0 ? chargeRates : ChargeRates.Defaults,
            MaxPerSector = momentum ? MomentumDailyRules.MaxPerSector : 0,
            DrawdownHaltPct = momentum ? MomentumDailyRules.DrawdownHaltPct : 0m,
            DrawdownResumePct = momentum ? MomentumDailyRules.DrawdownResumePct : 0m
        };
    }

    private static Func<string, DateTime, decimal?> CloseLookup(IReadOnlyDictionary<string, List<MarketCandle>> dailyAll)
    {
        var closes = dailyAll.ToDictionary(kv => kv.Key, kv => kv.Value.Select(c => (Date: BacktestEngine.IstDate(c.CandleTime), c.Close)).ToList());
        return (symbol, day) =>
        {
            if (!closes.TryGetValue(symbol, out var list) || list.Count == 0) return null;
            int lo = 0, hi = list.Count - 1, best = -1;
            while (lo <= hi) { int mid = (lo + hi) / 2; if (list[mid].Date <= day) { best = mid; lo = mid + 1; } else hi = mid - 1; }
            return best >= 0 ? list[best].Close : null;
        };
    }

    /// <summary>The report's shared part. A <paramref name="minTrades"/> asks for the new rules' stricter verdict; null keeps the score rule's.</summary>
    private static BacktestSummary BuildSummary(BacktestParams p, IReadOnlyList<DateTime> testSessions, Func<string, DateTime, decimal?> closeOn, PortfolioResult main,
        IReadOnlyList<BacktestSignal> liveSignals, IReadOnlyList<bool> testedSides, List<BacktestLogicCheck> logicChecks, Func<int, string> byScore, int? minTrades)
    {
        var equity = BacktestPortfolio.EquityCurve(main.Trades, p.Capital, testSessions, closeOn, out decimal exposure);
        var kpis = BacktestPortfolio.Kpis(main.Trades, equity, p.Capital, p.FromDate, p.ToDate, exposure);
        DateTime mid = p.FromDate.AddDays((p.ToDate - p.FromDate).TotalDays / 2);
        string HalfOf(BacktestTrade t) => BacktestEngine.IstDate(t.EntryTime) < mid
            ? $"1st half ({p.FromDate:MMM yy} – {mid:MMM yy})" : $"2nd half ({mid:MMM yy} – {p.ToDate:MMM yy})";
        (string, string) VerdictOf(BacktestKpis k, List<BacktestGroupRow> halves, IReadOnlyList<BacktestTrade> trades, IEnumerable<BacktestLogicCheck> checks) =>
            minTrades is int min ? BacktestPortfolio.RuleVerdict(k, halves, trades, checks.ToList(), min) : BacktestPortfolio.Verdict(k, halves);

        var halves = BacktestPortfolio.Group(main.Trades, HalfOf, sortByKey: true);
        var (verdict, reason) = VerdictOf(kpis, halves, main.Trades, logicChecks);

        // Each side's own answer, from the trades the shared portfolio took (the same cash and position limits as the live bot).
        var sideResults = testedSides.Select(isShort =>
        {
            string side = BacktestSide.Of(isShort);
            var ofSide = main.Trades.Where(t => t.Side == side).ToList();
            var sideEquity = BacktestPortfolio.EquityCurve(ofSide, p.Capital, testSessions, closeOn, out decimal sideExposure);
            var sideKpis = BacktestPortfolio.Kpis(ofSide, sideEquity, p.Capital, p.FromDate, p.ToDate, sideExposure);
            var sideHalves = BacktestPortfolio.Group(ofSide, HalfOf, sortByKey: true);
            var (sideVerdict, sideReason) = VerdictOf(sideKpis, sideHalves, ofSide, logicChecks.Where(c => c.Side == side));
            return new BacktestSideResult
            {
                Side = side,
                Verdict = sideVerdict,
                VerdictReason = sideReason,
                Signals = liveSignals.Count(s => s.IsShort == isShort),
                Kpis = sideKpis,
                ByHalf = sideHalves,
                ByExit = BacktestPortfolio.Group(ofSide, t => t.ExitCategory)
            };
        }).ToList();

        return new BacktestSummary
        {
            Sides = sideResults,
            LogicChecks = logicChecks,
            Verdict = verdict,
            VerdictReason = reason,
            Kpis = kpis,
            Equity = equity,
            ByRegime = BacktestPortfolio.Group(main.Trades, t => t.Regime ?? "unknown"),
            ByExit = BacktestPortfolio.Group(main.Trades, t => t.ExitCategory),
            ByYear = BacktestPortfolio.Group(main.Trades, t => BacktestEngine.IstDate(t.EntryTime).Year.ToString(), sortByKey: true),
            ByMonth = BacktestPortfolio.Group(main.Trades, t => BacktestEngine.IstDate(t.EntryTime).ToString("yyyy-MM"), sortByKey: true),
            BySector = BacktestPortfolio.Group(main.Trades, t => t.Sector ?? "No sector"),
            ByScore = BacktestPortfolio.Group(main.Trades, t => byScore(t.Score), sortByKey: true),
            ByHalf = halves,
            LiveSignals = liveSignals.Count,
            Skipped = main.Skipped.OrderByDescending(kv => kv.Value).ToDictionary(kv => kv.Key, kv => kv.Value)
        };
    }

    // ------------------------------------------------------------------------------------------
    // The new rules (backtest only)
    // ------------------------------------------------------------------------------------------

    private async Task<(BacktestSummary Summary, List<BacktestTrade> Trades)> ExecuteNewRuleAsync(int runId, BacktestParams p, int threads, CancellationTokenSource cts)
    {
        var token = cts.Token;
        ApplyFixedRules(p);
        bool momentum = p.Strategy == BacktestStrategy.MomentumDaily;

        // --- Universe: today's active stocks less ETFs; each day's top list comes from that day's turnover ---------------
        var active = (await _stockRepository.GetActiveStocksAsync())
            .Select(s => (Symbol: s.Symbol.Trim().ToUpperInvariant(), s.Name))
            .Where(s => s.Symbol != NiftySymbol && s.Symbol != VixSymbol)
            .DistinctBy(s => s.Symbol).ToList();
        var names = active.ToDictionary(s => s.Symbol, s => s.Name);
        var requested = p.Symbols.Count > 0 ? p.Symbols.Where(s => s != NiftySymbol && s != VixSymbol).ToList() : active.Select(s => s.Symbol).ToList();
        var etfs = requested.Where(s => BacktestUniverse.IsEtfLike(s, names.GetValueOrDefault(s))).OrderBy(s => s).ToList();
        var symbols = requested.Except(etfs).ToList();
        if (symbols.Count == 0) throw new InvalidOperationException("No stocks to test - no active stocks in stock_master.");
        var sectors = await LoadSectorsAsync();
        var chargeRates = await _chargeRatesRepository.GetAllAsync();

        await ReportProgressAsync(runId, 1, "Loading daily candles");
        DateTime fromUtc = BacktestEngine.DailyStamp(p.FromDate.AddDays(-480));
        DateTime toUtc = BacktestEngine.DailyStamp(p.ToDate.AddDays(1));
        var dailyLoaded = await LoadCandlesAsync("1d", symbols.Concat(new[] { NiftySymbol, VixSymbol }).ToList(), fromUtc, toUtc);
        var dailyAll = dailyLoaded.ToDictionary(kv => kv.Key, kv => BacktestEngine.OneBarPerDay(kv.Value));
        var nifty = dailyAll.GetValueOrDefault(NiftySymbol) ?? new List<MarketCandle>();
        var vixByDate = (dailyAll.GetValueOrDefault(VixSymbol) ?? new List<MarketCandle>())
            .GroupBy(c => BacktestEngine.IstDate(c.CandleTime)).ToDictionary(g => g.Key, g => g.Last().Close);
        var stockDaily = dailyAll.Where(kv => kv.Key != NiftySymbol && kv.Key != VixSymbol).ToDictionary(kv => kv.Key, kv => kv.Value);

        var sessions = SessionsOf(dailyAll, nifty);
        int SessionOf(DateTime day)
        {
            int idx = sessions.BinarySearch(day.Date);
            return idx >= 0 ? idx : ~idx;
        }
        var testSessions = sessions.Where(d => d >= p.FromDate && d <= p.ToDate).ToList();
        var noDaily = symbols.Where(s => !stockDaily.ContainsKey(s)).OrderBy(s => s).ToList();

        await ReportProgressAsync(runId, 2, "Computing the market regime and the most-traded list for each day");
        var regimeByDate = ComputeRegimes(nifty, stockDaily.Values.Cast<IReadOnlyList<MarketCandle>>().ToList(), vixByDate,
            sessions.Where(d => d >= p.FromDate.AddDays(-14) && d <= p.ToDate).ToList(), token);
        var regimeDates = regimeByDate.Keys.OrderBy(d => d).ToList();
        string? RegimeFor(DateTime day)
        {
            int idx = regimeDates.BinarySearch(day.Date);
            idx = (idx >= 0 ? idx : ~idx) - 1;                 // latest reading strictly before the day
            return idx >= 0 ? regimeByDate[regimeDates[idx]].Regime : null;
        }
        var universe = BacktestUniverse.TopByTurnover(stockDaily, MomentumDailyRules.UniverseSize, MomentumDailyRules.TurnoverLookback);
        var splitsBySymbol = stockDaily.ToDictionary(kv => kv.Key, kv => BacktestEngine.SuspectedSplits(kv.Value));
        var splits = splitsBySymbol.SelectMany(kv => kv.Value.Where(d => d >= p.FromDate.AddDays(-400) && d <= p.ToDate).Select(d => $"{kv.Key} {d:dd-MMM-yyyy}"))
            .OrderBy(s => s).ToList();
        HashSet<int> SplitSessions(string symbol) => splitsBySymbol.GetValueOrDefault(symbol)?.Select(SessionOf).ToHashSet() ?? new HashSet<int>();

        var signals = new ConcurrentBag<BacktestSignal>();
        long scans = 0;
        DateTime? firstDate, lastDate;
        var noData = new ConcurrentBag<string>(noDaily);
        var dataChecks = new ConcurrentBag<IntradayDataCheck>();

        if (momentum)
        {
            // --- Daily momentum ---------------------------------------------------------------------
            var niftyCloses = nifty.Select(c => c.Close).ToList();
            var niftyAverage = IndicatorCalculator.CalculateSma(niftyCloses, MomentumDailyRules.NiftyAverageDays);
            var niftyAbove = new HashSet<DateTime>();
            for (int i = MomentumDailyRules.NiftyAverageDays - 1; i < nifty.Count; i++)
                if (niftyCloses[i] > niftyAverage[i]) niftyAbove.Add(BacktestEngine.IstDate(nifty[i].CandleTime));
            var ranks = MomentumDailyBacktest.StrengthRanks(stockDaily, universe);
            var ctx = new MomentumDailyBacktest.Context
            {
                FromDate = p.FromDate, ToDate = p.ToDate, SlippagePct = p.SlippagePct, SessionOf = SessionOf, RegimeFor = RegimeFor,
                NiftyAboveAverage = niftyAbove.Contains,
                StrengthRank = (symbol, day) => ranks.TryGetValue(day, out var ofDay) && ofDay.TryGetValue(symbol, out int rank) ? rank : null
            };
            await ReplayAsync(runId, stockDaily.Keys.ToList(), threads, cts, () => Interlocked.Read(ref scans), symbol =>
            {
                var bars = stockDaily[symbol];
                var splitSessions = SplitSessions(symbol);
                foreach (var s in MomentumDailyBacktest.GenerateSignals(symbol, bars, splitSessions, ctx))
                {
                    if (s.SkipReason == null)
                    {
                        s.Exit = MomentumDailyBacktest.SimulateExit(bars, s, splitSessions, SessionOf, out var skip);
                        s.SkipReason = skip;
                    }
                    signals.Add(s);
                }
                Interlocked.Add(ref scans, bars.Count(b => BacktestEngine.IstDate(b.CandleTime) >= p.FromDate && BacktestEngine.IstDate(b.CandleTime) <= p.ToDate));
            });
            var days = stockDaily.Values.Where(v => v.Count > 0).ToList();
            firstDate = days.Count > 0 ? days.Min(v => BacktestEngine.IstDate(v[0].CandleTime)) : null;
            lastDate = days.Count > 0 ? days.Max(v => BacktestEngine.IstDate(v[^1].CandleTime)) : null;
        }
        else
        {
            // --- Intraday opening range breakout: the list on the previous session's close applies, because today's turnover isn't known yet ---
            bool InUniverse(string symbol, DateTime day)
            {
                int session = sessions.BinarySearch(day.Date);
                session = (session >= 0 ? session : ~session) - 1;
                return session >= 0 && universe.TryGetValue(sessions[session], out var list) && list.Contains(symbol);
            }
            var nifty15 = await LoadCandlesAsync("15m", new List<string> { NiftySymbol }, BacktestEngine.DailyStamp(p.FromDate), toUtc);
            var niftyAtBar = (nifty15.GetValueOrDefault(NiftySymbol) ?? new List<MarketCandle>()).ToDictionary(c => c.CandleTime, c => c.Close);
            var niftyDays = nifty.Select(c => BacktestEngine.IstDate(c.CandleTime)).ToList();
            var ctx = new IntradayOrbBacktest.Context
            {
                FromDate = p.FromDate, ToDate = p.ToDate, SlippagePct = p.SlippagePct, SessionOf = SessionOf, RegimeFor = RegimeFor,
                TestBuy = BacktestSide.IncludesBuy(p.Side), TestShort = BacktestSide.IncludesShort(p.Side),
                InUniverse = InUniverse,
                NiftyAtBar = barStart => niftyAtBar.TryGetValue(barStart, out var close) ? close : null,
                NiftyPrevClose = day =>
                {
                    int idx = niftyDays.BinarySearch(day);
                    idx = (idx >= 0 ? idx : ~idx) - 1;
                    return idx >= 0 ? nifty[idx].Close : null;
                }
            };
            // Only stocks that are ever on the list need their 15m bars loaded.
            var candidates = stockDaily.Keys.Where(s => testSessions.Any(d => InUniverse(s, d))).ToList();
            var dates = new ConcurrentBag<(DateTime First, DateTime Last)>();
            await ReplayAsync(runId, candidates, threads, cts, () => Interlocked.Read(ref scans), symbol =>
            {
                var m15 = LoadCandles("15m", symbol, BacktestEngine.DailyStamp(p.FromDate.AddDays(-40)), toUtc);
                if (m15.Count == 0) { noData.Add(symbol); return; }
                dates.Add((BacktestEngine.IstDate(m15[0].CandleTime), BacktestEngine.IstDate(m15[^1].CandleTime)));
                dataChecks.Add(BacktestDataCheck.Check15m(m15, stockDaily[symbol], p.FromDate, p.ToDate));
                foreach (var s in IntradayOrbBacktest.GenerateSignals(symbol, m15, stockDaily[symbol], SplitSessions(symbol), ctx)) signals.Add(s);
                Interlocked.Add(ref scans, m15.Count);
            });
            firstDate = dates.IsEmpty ? null : dates.Min(x => x.First);
            lastDate = dates.IsEmpty ? null : dates.Max(x => x.Last);
        }

        // --- Portfolio and report ---------------------------------------------------------------------
        await ReportProgressAsync(runId, 92, "Simulating the portfolio");
        var ordered = signals.OrderBy(s => s.EntryTimeUtc).ThenByDescending(s => s.Score).ThenBy(s => s.Symbol).ToList();
        var ps = PortfolioSettingsOf(p, chargeRates);
        string? SectorOf(string symbol) => sectors.GetValueOrDefault(symbol);
        var main = BacktestPortfolio.Run(ordered, s => s.Exit, ps, SectorOf);

        var testedSides = new[] { false, true }.Where(isShort => isShort ? BacktestSide.IncludesShort(p.Side) : BacktestSide.IncludesBuy(p.Side)).ToList();
        var checks = BacktestLogicAudit.Run(main.Trades, IntradayOrbRules.ShortCutoffText, IntradayOrbRules.SquareOffText);
        checks.AddRange(BacktestLogicAudit.RunRuleLimits(main.Trades, p.MaxPositions!.Value, p.MaxTradesPerDay!.Value, ps.MaxPerSector,
            momentum ? null : IntradayOrbRules.LastSignalText));
        var summary = BuildSummary(p, testSessions, CloseLookup(dailyAll), main, ordered, testedSides, checks,
            momentum ? BacktestPortfolio.RankBucket : BacktestPortfolio.VolumeBucket, momentum ? MomentumDailyRules.MinTrades : IntradayOrbRules.MinTrades);
        summary.Coverage = new BacktestCoverage
        {
            SymbolsRequested = symbols.Count,
            SymbolsWithData = symbols.Count - noData.Distinct().Count(),
            FirstIntradayDate = firstDate,
            LastIntradayDate = lastDate,
            Sessions = testSessions.Count,
            ScansEvaluated = Interlocked.Read(ref scans),
            SymbolsWithoutData = noData.Distinct().OrderBy(s => s).ToList(),
            SuspectedSplits = splits,
            DataLabel = momentum ? "daily" : "15m",
            ExcludedEtfs = etfs
        };
        summary.Assumptions = NewRuleAssumptions(p, momentum, splits.Count);
        AddDailyRepairNote(summary, dailyLoaded, dailyAll);
        if (!momentum) BacktestDataCheck.Apply(summary, dataChecks.ToList());
        return (summary, main.Trades);
    }

    private static List<string> NewRuleAssumptions(BacktestParams p, bool momentum, int splits)
    {
        var list = new List<string>
        {
            $"Run by: {WorkerBuild()}.",
            "This rule is for backtesting only: no live, Auto or Manual trading uses it. Its numbers are fixed in code; the form's score, gate, exit and sizing fields don't apply.",
            $"Stocks: the top {MomentumDailyRules.UniverseSize} of today's active stocks by {MomentumDailyRules.TurnoverLookback}-day median daily turnover, worked out for each day from data up to then, without ETFs (recognised by name, listed under Data used). Stocks dropped from the list earlier are not tested (survivorship bias - real results are usually somewhat worse).",
            $"Costs: Zerodha charges from charge_rates (same-day round trips at intraday rates) + {p.SlippagePct:0.##}% slippage on each side. R = net P&L ÷ (quantity × the distance from entry to stop)."
        };
        if (momentum)
        {
            list.Add($"Signal, at a daily close: NIFTY above its {MomentumDailyRules.NiftyAverageDays}-day average; top 20% of the list by {MomentumDailyRules.ReturnLookback}-session return; within {MomentumDailyRules.MaxBelowYearHighPct:0}% of the {MomentumDailyRules.YearHighLookback}-session high; close above the previous {MomentumDailyRules.BreakoutLookback} sessions' high; volume at least {MomentumDailyRules.MinVolumeMultiple} × the {MomentumDailyRules.VolumeLookback}-day average. A stock needs over {MomentumDailyRules.YearHighLookback} daily bars, so recent listings are left out.");
            list.Add($"Entry: the next session's open; skipped if that is ≥{SwingTradeRules.MaxSignalDriftPct:0}% from the signal close. Strongest rank first.");
            list.Add($"Exits: stop {MomentumDailyRules.StopAtrMult:0} × ATR (at most {SwingTradeRules.MaxStopLossPct:0}%), checked at the close; trail from entry + {MomentumDailyRules.TrailStartAtrMult:0} × ATR, {MomentumDailyRules.TrailAtrMult:0} × ATR below the highest close, never below the stop; out if not above the entry after {MomentumDailyRules.NoProgressSessions} sessions; out after {MomentumDailyRules.MaxSessions} sessions. All of these sell at the next open. Emergency stop {MomentumDailyRules.EmergencyAtrMult:0} × ATR (at most {SwingTradeRules.MaxEmergencyStopPct:0}%) acts inside the day. No fixed target (shown as 0).");
            list.Add($"Limits: {MomentumDailyRules.RiskPct}% risk per trade, at most ₹{MomentumDailyRules.MaxPerTrade:N0} a trade, {MomentumDailyRules.MaxPositions} positions, {MomentumDailyRules.MaxPerSector} per sector (stocks with no sector aren't limited), {MomentumDailyRules.MaxEntriesPerDay} new entries a day.");
            list.Add($"Drawdown halt: no new entries once equity is {MomentumDailyRules.DrawdownHaltPct:0}% below its peak, until it is within {MomentumDailyRules.DrawdownResumePct:0}% of the peak or 30 calendar days pass (the peak then resets, because a halt with nothing open can't recover by itself). Equity here counts realised results only.");
            list.Add("Not modelled: earnings dates (no earnings data is stored), order rejections, partial fills, circuit limits and liquidity.");
        }
        else
        {
            list.Add($"Signal: the first 15m close beyond the 09:15-09:45 range, up to {IntradayOrbRules.LastSignalText}, with opening-range volume at least {IntradayOrbRules.MinVolumeMultiple} × its average over the previous {IntradayOrbRules.VolumeLookback} sessions. Long needs NIFTY above yesterday's close and the stock's previous close above its {IntradayOrbRules.StockAverageDays}-day average; short needs both below.");
            list.Add($"Entry: the next 15m open. Skipped if the stop is under {IntradayOrbRules.MinStopPct}% or over {IntradayOrbRules.MaxStopPct}% away.");
            list.Add($"Exits: stop at the far side of the range and target {IntradayOrbRules.TargetR:0}R, both live; square-off at the {IntradayOrbRules.SquareOffText} bar's open (or the day's last close if that bar isn't stored). A bar touching both the stop and the target counts as a stop.");
            list.Add($"Limits: {IntradayOrbRules.RiskPct}% risk per trade, at most ₹{IntradayOrbRules.MaxPerTrade:N0} a trade, {IntradayOrbRules.MaxPositions} positions, {IntradayOrbRules.MaxEntriesPerDay} new entries a day, one trade per stock per day.");
            list.Add("Not modelled: stocks that can't be shorted that day, MIS margin (the full position value is blocked), order rejections, partial fills, circuit limits and liquidity. 15m bars make fills less precise than live ones.");
        }
        list.Add(splits > 0
            ? $"{splits} suspected unadjusted split/bonus gap(s): signals in the {(momentum ? MomentumDailyBacktest.SplitExclusionSessions : IntradayOrbBacktest.SplitExclusionSessions)} sessions after one and trades held across one are excluded."
            : "No unadjusted split/bonus gaps found in the data.");
        return list;
    }

    /// <summary>Which machine and code version replayed a run, so a result can be traced to a deployment.</summary>
    public static string WorkerBuild()
    {
        var version = typeof(BacktestService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
        return $"{Environment.MachineName}, build {version}";
    }

    private static List<string> Assumptions(BacktestParams p, bool regimeMode, int splits, int regimeDays)
    {
        var list = new List<string>
        {
            $"Run by: {WorkerBuild()}.",
            "Data: stored Zerodha candles (15m, 60m, daily) - no Zerodha calls. Past daily bars are NSE's official values where the bhavcopy was loaded.",
            "Signals: the live SwingDecisionEngine (BUY) and SwingShortDecisionEngine (SHORT) at every 15-minute bar close, seeing only what existed at that moment (today's daily bar is built from the 15m bars so far).",
            "Trade Conditions: the default rules with all 11 conditions ON. A custom Trade Conditions profile is not applied, so a profile with conditions switched off can trade differently live.",
            $"Entries: at the next 15m bar's open with {p.SlippagePct:0.##}% slippage against the trade; skipped if that open is ≥2% away from the signal price (live guard 12).",
            "Exits: the live SwingTradeRules on 15m bars. Inside a bar the price is assumed to go open→low→high→close (up bar) or open→high→low→close (down bar), and a bar touching both the stop and the target counts as a stop; a level crossed inside a bar fills at the level, a gap fills at the open.",
            $"Costs: Zerodha charges from charge_rates (same-day round trips at intraday rates) + {p.SlippagePct:0.##}% slippage on each side. R = net P&L ÷ (quantity × the distance from entry to stop).",
            regimeMode
                ? $"Market gate: REGIME - the regime is recomputed for each day from the same daily data ({regimeDays} days) and the previous session's reading applies, like the live bot."
                : "Market gate: NIFTY_FILTER - the engine's mandatory NIFTY trend filter, evaluated on NIFTY's daily bars up to that moment.",
            "Universe: today's active stocks. Stocks dropped from the list earlier are not tested (survivorship bias - real results are usually somewhat worse).",
            "Daily loss limit counts realised losses only. Order rejections, partial fills, circuit limits and liquidity are not modelled.",
            splits > 0
                ? $"{splits} suspected unadjusted split/bonus gap(s): signals in the {BacktestEngine.SplitExclusionSessions} sessions after one and trades held across one are excluded."
                : "No unadjusted split/bonus gaps found in the data."
        };
        if (BacktestSide.IncludesShort(p.Side))
        {
            list.Add($"Shorts: intraday only - no new short at/after {p.ShortEntryCutoff}, bought back at the first 15m bar starting at/after {p.ShortSquareOffTime} (at the day's last close if that bar isn't stored; \"By exit\" counts these as missing bar), with live stops. Shorts use the mirrored NIFTY-bearish filter in every gate mode, as live (regime limits apply to BUYs only). The money blocked for a short is the full position value, as on the paper account; real MIS margin is lower.");
            list.Add("Not modelled for shorts: stocks that can't be shorted that day (Zerodha's MIS ban list, upper circuit) and intraday square-off penalties. Real results can be worse.");
        }
        return list;
    }

    // ------------------------------------------------------------------------------------------
    // Data
    // ------------------------------------------------------------------------------------------

    private Dictionary<DateTime, MarketRegimeReading> ComputeRegimes(IReadOnlyList<MarketCandle> nifty, IReadOnlyList<IReadOnlyList<MarketCandle>> universe,
        IReadOnlyDictionary<DateTime, decimal> vixByDate, IReadOnlyList<DateTime> dates, CancellationToken token)
    {
        var readings = new Dictionary<DateTime, MarketRegimeReading>();
        var previous = new List<MarketRegimeReading>();
        foreach (var date in dates)
        {
            token.ThrowIfCancellationRequested();
            var niftyUpTo = nifty.Where(c => BacktestEngine.IstDate(c.CandleTime) <= date).ToList();
            if (niftyUpTo.Count < 60 || BacktestEngine.IstDate(niftyUpTo[^1].CandleTime) != date) continue;
            try
            {
                var reading = MarketRegimeEngine.Score(niftyUpTo, MarketRegimeEngine.Breadth(universe, date),
                    vixByDate.TryGetValue(date, out var v) ? v : null, previous);
                readings[date] = reading;
                previous.Add(reading);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Backtest: regime for {Date:yyyy-MM-dd} could not be computed.", date);
            }
        }
        return readings;
    }

    private async Task<Dictionary<string, string>> LoadSectorsAsync()
    {
        try
        {
            using var connection = _connectionFactory.CreateConnection();
            var rows = await connection.QueryAsync<(string Symbol, string Name)>(@"
                SELECT UPPER(sm.symbol), s.name FROM stock_sectors ss
                JOIN stock_master sm ON sm.id = ss.stock_id JOIN sectors s ON s.id = ss.sector_id
                ORDER BY s.name;");
            return rows.GroupBy(r => r.Symbol).ToDictionary(g => g.Key, g => g.First().Name);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Backtest: sectors not readable - sector breakdown skipped.");
            return new Dictionary<string, string>();
        }
    }

    private const string SelectCandles = "SELECT id, candle_time AS CandleTime, symbol, timeframe, open, high, low, close, volume FROM market_candles_{0}";

    private async Task<Dictionary<string, List<MarketCandle>>> LoadCandlesAsync(string timeframe, List<string> symbols, DateTime fromUtc, DateTime toUtc)
    {
        using var connection = _connectionFactory.CreateConnection();
        var rows = await connection.QueryAsync<MarketCandle>(
            string.Format(SelectCandles, timeframe) + " WHERE symbol = ANY(@symbols) AND candle_time >= @fromUtc AND candle_time < @toUtc ORDER BY symbol, candle_time;",
            new { symbols = symbols.ToArray(), fromUtc, toUtc }, commandTimeout: 300);
        return rows.GroupBy(c => c.Symbol.ToUpperInvariant()).ToDictionary(g => g.Key, g => DistinctByTime(g));
    }

    private List<MarketCandle> LoadCandles(string timeframe, string symbol, DateTime fromUtc, DateTime toUtc)
    {
        using var connection = _connectionFactory.CreateConnection();
        var rows = connection.Query<MarketCandle>(
            string.Format(SelectCandles, timeframe) + " WHERE symbol = @symbol AND candle_time >= @fromUtc AND candle_time < @toUtc ORDER BY candle_time;",
            new { symbol, fromUtc, toUtc }, commandTimeout: 300);
        return DistinctByTime(rows);
    }

    /// <summary>One bar per timestamp (a duplicate row would otherwise be scored twice), in time order.</summary>
    private static List<MarketCandle> DistinctByTime(IEnumerable<MarketCandle> rows)
    {
        var list = new List<MarketCandle>();
        foreach (var c in rows.OrderBy(c => c.CandleTime))
        {
            c.CandleTime = DateTime.SpecifyKind(c.CandleTime.ToUniversalTime(), DateTimeKind.Utc);
            if (list.Count > 0 && list[^1].CandleTime == c.CandleTime) list[^1] = c;
            else list.Add(c);
        }
        return list;
    }
}
