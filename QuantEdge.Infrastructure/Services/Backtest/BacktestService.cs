using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Extensions.Logging;
using QuantEdge.Domain.Entities;
using QuantEdge.Infrastructure.Constants;
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

    // ------------------------------------------------------------------------------------------
    // Queue / read
    // ------------------------------------------------------------------------------------------

    public async Task<int> QueueAsync(BacktestParams input, int userId)
    {
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

        p.GateMode = string.IsNullOrWhiteSpace(p.GateMode) ? strategy.MarketGateMode : p.GateMode.Trim().ToUpperInvariant();
        if (p.GateMode != SwingStrategySettings.GateModeNiftyFilter && p.GateMode != SwingStrategySettings.GateModeRegime)
            throw new ArgumentException("Gate mode must be NIFTY_FILTER or REGIME.");
        p.BuyScoreThreshold ??= strategy.BuyScoreThreshold;
        p.WatchScoreThreshold ??= strategy.WatchScoreThreshold;
        p.MinConditionsMatch ??= real.MinConditionsMatch;

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
        p.ProfitTargetPct ??= real.ProfitTargetPct;
        p.MaxDurationDays ??= real.MaxDurationDays;
        p.Label = string.IsNullOrWhiteSpace(p.Label)
            ? $"{p.GateMode} · score ≥ {p.BuyScoreThreshold} · {p.FromDate:dd-MMM-yy} → {p.ToDate:dd-MMM-yy}"
            : p.Label.Trim();
        return p;
    }

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
        using var connection = _connectionFactory.CreateConnection();
        var rows = await connection.QueryAsync<BacktestTrade>(@"
            SELECT symbol, sector, regime, signal_time AS SignalTime, entry_time AS EntryTime, entry_price AS EntryPrice,
                   exit_time AS ExitTime, exit_price AS ExitPrice, exit_reason AS ExitReason, exit_category AS ExitCategory,
                   quantity, stop_loss AS StopLoss, target, score, met_count AS MetCount, sessions_held AS SessionsHeld,
                   gross_pnl AS GrossPnl, charges, net_pnl AS NetPnl, r_multiple AS RMultiple, mfe_pct AS MfePct, mae_pct AS MaePct,
                   factors, end_of_data AS EndOfData
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
            var p = JsonSerializer.Deserialize<BacktestParams>(run.ParamsJson, Json) ?? throw new InvalidOperationException("Run has no parameters.");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var (summary, trades) = await ExecuteAsync(run.Id, p, Math.Max(1, maxThreads), cts);

            using var connection = _connectionFactory.CreateConnection();
            connection.Open();
            using var tx = connection.BeginTransaction();
            await connection.ExecuteAsync("DELETE FROM backtest_trades WHERE run_id = @id;", new { id = run.Id }, tx);
            await connection.ExecuteAsync(@"
                INSERT INTO backtest_trades (run_id, symbol, sector, regime, signal_time, entry_time, entry_price, exit_time, exit_price,
                    exit_reason, exit_category, quantity, stop_loss, target, score, met_count, sessions_held, gross_pnl, charges, net_pnl,
                    r_multiple, mfe_pct, mae_pct, factors, end_of_data)
                VALUES (@RunId, @Symbol, @Sector, @Regime, @SignalTime, @EntryTime, @EntryPrice, @ExitTime, @ExitPrice,
                    LEFT(@ExitReason, 200), @ExitCategory, @Quantity, @StopLoss, @Target, @Score, @MetCount, @SessionsHeld, @GrossPnl, @Charges, @NetPnl,
                    @RMultiple, @MfePct, @MaePct, LEFT(@Factors, 400), @EndOfData);",
                trades.Select(t => new
                {
                    RunId = run.Id, t.Symbol, t.Sector, t.Regime, t.SignalTime, t.EntryTime, t.EntryPrice, t.ExitTime, t.ExitPrice,
                    t.ExitReason, t.ExitCategory, t.Quantity, t.StopLoss, t.Target, t.Score, t.MetCount, t.SessionsHeld, t.GrossPnl, t.Charges,
                    t.NetPnl, t.RMultiple, t.MfePct, t.MaePct, t.Factors, t.EndOfData
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
        var engineSettings = regimeMode ? RegimeGate.WithoutNiftyGate(strategy) : strategy;
        var tradeParams = SwingTradeParams.From(new RealTradeSettings
        {
            ExitMode = p.ExitMode!, CloseCheckTime = p.CloseCheckTime!, TradingWindowEnd = p.TradingWindowEnd!,
            StopLossAtrMult = p.StopLossAtrMult!.Value, TrailAtrMult = p.TrailAtrMult!.Value, TargetAtrMult = p.TargetAtrMult!.Value,
            ProfitTargetPct = p.ProfitTargetPct!.Value
        });
        var policies = await _regimeService.GetPoliciesAsync();
        var chargeRates = await _chargeRatesRepository.GetAllAsync();

        // --- Daily candles for every stock (indicators, breadth, equity marks) ------------------
        await ReportProgressAsync(runId, 1, "Loading daily candles");
        DateTime fromUtc = BacktestEngine.DailyStamp(p.FromDate.AddDays(-480));
        DateTime toUtc = BacktestEngine.DailyStamp(p.ToDate.AddDays(1));
        var dailyAll = await LoadCandlesAsync("1d", symbols.Concat(new[] { NiftySymbol, VixSymbol }).ToList(), fromUtc, toUtc);
        var nifty = dailyAll.GetValueOrDefault(NiftySymbol) ?? new List<MarketCandle>();
        var vixByDate = (dailyAll.GetValueOrDefault(VixSymbol) ?? new List<MarketCandle>())
            .GroupBy(c => BacktestEngine.IstDate(c.CandleTime)).ToDictionary(g => g.Key, g => g.Last().Close);

        var sessions = dailyAll.Where(kv => kv.Key != VixSymbol).SelectMany(kv => kv.Value)
            .Select(c => BacktestEngine.IstDate(c.CandleTime)).Distinct().OrderBy(d => d).ToList();
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
            WindowStart = p.TradingWindowStart!,
            WindowEnd = p.TradingWindowEnd!,
            EntryDelayMinutes = p.EntryDelayMinutes!.Value,
            MaxDurationDays = p.MaxDurationDays!.Value,
            FromDate = p.FromDate,
            ToDate = p.ToDate,
            NiftyDaily = nifty,
            Nifty15mByDate = nifty15ByDate,
            RegimeFor = RegimeFor,
            SessionOf = SessionOf
        };

        // --- Replay every symbol (dedicated low-priority threads so the live feed in this process keeps its CPU) ---
        var queue = new ConcurrentQueue<string>(symbols);
        var signals = new ConcurrentBag<BacktestSignal>();
        var tapes = new ConcurrentDictionary<string, IntradayTape>();
        var splits = new ConcurrentBag<string>();
        var noData = new ConcurrentBag<string>();
        var intradayDates = new ConcurrentBag<(DateTime First, DateTime Last)>();
        long scans = 0;
        int done = 0;
        Exception? workerError = null;

        var workers = Enumerable.Range(0, Math.Min(threads, symbols.Count)).Select(_ => Task.Factory.StartNew(() =>
        {
            Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
            while (!token.IsCancellationRequested && queue.TryDequeue(out var symbol))
            {
                try
                {
                    var daily = dailyAll.GetValueOrDefault(symbol) ?? new List<MarketCandle>();
                    var m15 = LoadCandles("15m", symbol, BacktestEngine.DailyStamp(p.FromDate.AddDays(-10)), toUtc);
                    var h60 = LoadCandles("60m", symbol, BacktestEngine.DailyStamp(p.FromDate.AddDays(-40)), toUtc);
                    if (m15.Count == 0 || daily.Count < RealTradeSchedule.MinDailyCandles) { noData.Add(symbol); continue; }
                    intradayDates.Add((BacktestEngine.IstDate(m15[0].CandleTime), BacktestEngine.IstDate(m15[^1].CandleTime)));

                    var r = BacktestEngine.GenerateSignals(symbol, daily, h60, m15, ctx);
                    foreach (var s in r.Signals) signals.Add(s);
                    if (r.Tape != null) tapes[symbol] = r.Tape;
                    foreach (var d in r.SuspectedSplits.Where(d => d >= p.FromDate.AddDays(-400) && d <= p.ToDate)) splits.Add($"{symbol} {d:dd-MMM-yyyy}");
                    Interlocked.Add(ref scans, r.Scans);
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
            if (await ReportProgressAsync(runId, pct, $"Replaying stocks: {Volatile.Read(ref done)}/{symbols.Count} · {Interlocked.Read(ref scans):N0} scans")) cts.Cancel();
        }
        try { await all; } catch (OperationCanceledException) { }
        if (workerError != null) throw workerError;
        token.ThrowIfCancellationRequested();

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

        var ps = new PortfolioSettings
        {
            Capital = p.Capital,
            AmountPerTrade = p.AmountPerTrade,
            RiskSizing = p.SizingMode == "RISK",
            RiskPct = p.RiskPct,
            MaxPositions = p.MaxPositions!.Value,
            MaxTradesPerDay = p.MaxTradesPerDay!.Value,
            DailyLossLimit = p.DailyLossLimit!.Value,
            SlippagePct = p.SlippagePct,
            Rates = chargeRates.Count > 0 ? chargeRates : ChargeRates.Defaults
        };
        string? SectorOf(string symbol) => sectors.GetValueOrDefault(symbol);
        var closes = dailyAll.ToDictionary(kv => kv.Key, kv => kv.Value.Select(c => (Date: BacktestEngine.IstDate(c.CandleTime), c.Close)).ToList());
        decimal? CloseOn(string symbol, DateTime day)
        {
            if (!closes.TryGetValue(symbol, out var list) || list.Count == 0) return null;
            int lo = 0, hi = list.Count - 1, best = -1;
            while (lo <= hi) { int mid = (lo + hi) / 2; if (list[mid].Date <= day) { best = mid; lo = mid + 1; } else hi = mid - 1; }
            return best >= 0 ? list[best].Close : null;
        }

        var liveSignals = ordered.Where(s => s.IsLiveCandidate).ToList();
        var main = BacktestPortfolio.Run(liveSignals, ExitOf, ps, SectorOf);
        var equity = BacktestPortfolio.EquityCurve(main.Trades, p.Capital, testSessions, CloseOn, out decimal exposure);
        var kpis = BacktestPortfolio.Kpis(main.Trades, equity, p.Capital, p.FromDate, p.ToDate, exposure);

        DateTime mid = p.FromDate.AddDays((p.ToDate - p.FromDate).TotalDays / 2);
        var halves = BacktestPortfolio.Group(main.Trades, t => BacktestEngine.IstDate(t.EntryTime) < mid
            ? $"1st half ({p.FromDate:MMM yy} – {mid:MMM yy})" : $"2nd half ({mid:MMM yy} – {p.ToDate:MMM yy})", sortByKey: true);
        var (verdict, reason) = BacktestPortfolio.Verdict(kpis, halves);

        await ReportProgressAsync(runId, 96, "Threshold sweep");
        var sweep = new List<BacktestSweepRow>();
        foreach (int threshold in new[] { 60, 65, 70, 75, 80, 85, 90 }.Where(t => t >= ctx.SweepFloor))
        {
            token.ThrowIfCancellationRequested();
            var subset = ordered.Where(s => s.HardFiltersPassed && s.GateAllows && s.Score >= threshold && (!s.PolicyRequiresRs || s.BeatsNifty));
            var r = BacktestPortfolio.Run(subset, ExitOf, ps, SectorOf);
            var eq = BacktestPortfolio.EquityCurve(r.Trades, p.Capital, testSessions, CloseOn, out decimal ex);
            var k = BacktestPortfolio.Kpis(r.Trades, eq, p.Capital, p.FromDate, p.ToDate, ex);
            sweep.Add(new BacktestSweepRow
            {
                Threshold = threshold, Trades = k.Trades, WinRatePct = k.WinRatePct, ExpectancyR = k.ExpectancyR,
                ProfitFactor = k.ProfitFactor, NetPnl = k.NetPnl, MaxDrawdownPct = k.MaxDrawdownPct
            });
        }

        var firstLast = intradayDates.ToList();
        var summary = new BacktestSummary
        {
            Verdict = verdict,
            VerdictReason = reason,
            Kpis = kpis,
            Equity = equity,
            ByRegime = BacktestPortfolio.Group(main.Trades, t => t.Regime ?? "unknown"),
            ByExit = BacktestPortfolio.Group(main.Trades, t => t.ExitCategory),
            ByYear = BacktestPortfolio.Group(main.Trades, t => BacktestEngine.IstDate(t.EntryTime).Year.ToString(), sortByKey: true),
            ByMonth = BacktestPortfolio.Group(main.Trades, t => BacktestEngine.IstDate(t.EntryTime).ToString("yyyy-MM"), sortByKey: true),
            BySector = BacktestPortfolio.Group(main.Trades, t => t.Sector ?? "No sector"),
            ByScore = BacktestPortfolio.Group(main.Trades, t => BacktestPortfolio.ScoreBucket(t.Score), sortByKey: true),
            ByHalf = halves,
            ThresholdSweep = sweep,
            FactorEdge = BacktestPortfolio.FactorEdge(main.Trades),
            LiveSignals = liveSignals.Count,
            Skipped = main.Skipped.OrderByDescending(kv => kv.Value).ToDictionary(kv => kv.Key, kv => kv.Value),
            Coverage = new BacktestCoverage
            {
                SymbolsRequested = symbols.Count,
                SymbolsWithData = symbols.Count - noData.Count,
                FirstIntradayDate = firstLast.Count > 0 ? firstLast.Min(x => x.First) : null,
                LastIntradayDate = firstLast.Count > 0 ? firstLast.Max(x => x.Last) : null,
                Sessions = testSessions.Count,
                ScansEvaluated = scans,
                SymbolsWithoutData = noData.OrderBy(s => s).ToList(),
                SuspectedSplits = splits.OrderBy(s => s).ToList()
            },
            Assumptions = Assumptions(p, regimeMode, splits.Count, regimeByDate.Count)
        };
        return (summary, main.Trades);
    }

    private static List<string> Assumptions(BacktestParams p, bool regimeMode, int splits, int regimeDays) => new()
    {
        "Data: stored Zerodha candles (15m, 60m, daily) - no Zerodha calls. Past daily bars are NSE's official values where the bhavcopy was loaded.",
        "Signals: the live SwingDecisionEngine at every 15-minute bar close, seeing only what existed at that moment (today's daily bar is built from the 15m bars so far).",
        $"Entries: at the next 15m bar's open + {p.SlippagePct:0.##}% slippage; skipped if that open is ≥2% away from the signal price (live guard 12).",
        "Exits: the live SwingTradeRules on 15m bars. Inside a bar the price is assumed to go open→low→high→close (up bar) or open→high→low→close (down bar), and a bar touching both the stop and the target counts as a stop; a level crossed inside a bar fills at the level, a gap fills at the open.",
        $"Costs: Zerodha charges from charge_rates (same-day round trips at intraday rates) + {p.SlippagePct:0.##}% slippage on each side. R = net P&L ÷ (quantity × (entry − stop)).",
        regimeMode
            ? $"Market gate: REGIME - the regime is recomputed for each day from the same daily data ({regimeDays} days) and the previous session's reading applies, like the live bot."
            : "Market gate: NIFTY_FILTER - the engine's mandatory NIFTY trend filter, evaluated on NIFTY's daily bars up to that moment.",
        "Universe: today's active stocks. Stocks dropped from the list earlier are not tested (survivorship bias - real results are usually somewhat worse).",
        "Daily loss limit counts realised losses only. Order rejections, partial fills, circuit limits and liquidity are not modelled.",
        splits > 0
            ? $"{splits} suspected unadjusted split/bonus gap(s): signals in the {BacktestEngine.SplitExclusionSessions} sessions after one and trades held across one are excluded."
            : "No unadjusted split/bonus gaps found in the data."
    };

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
