using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using QuantEdge.Domain.Entities;
using QuantEdge.Infrastructure.Constants;
using QuantEdge.Infrastructure.Helpers;
using QuantEdge.Infrastructure.Interfaces;
using QuantEdge.Infrastructure.Persistence.Repositories;
using QuantEdge.Infrastructure.Services;

namespace QuantEdge.Worker.Workers;

public class AutoRealTradeSignalScanWorker : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<AutoRealTradeSignalScanWorker> _logger;

    public AutoRealTradeSignalScanWorker(
        IServiceProvider serviceProvider,
        ILogger<AutoRealTradeSignalScanWorker> logger)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("AutoRealTradeSignalScanWorker (REAL MONEY) background service starting up...");

        // Startup delay
        await Task.Delay(12000, stoppingToken);

        // Only one process may run the real-money scan (a second one would place every real entry twice).
        await using var lease = await RealMoneyWorkerLease.AcquireAsync(_serviceProvider, "real_worker:scan",
            nameof(AutoRealTradeSignalScanWorker), _logger, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var marketHoursService = scope.ServiceProvider.GetRequiredService<IMarketHoursService>();
                var realTradeCache = scope.ServiceProvider.GetService<IRealTradeCacheService>();
                bool isMarketOpen = await marketHoursService.IsWithinMarketHoursAsync();

                // 1. Warmup Cache at 09:00 AM or if not warmed up during market hours
                if (realTradeCache != null && (!realTradeCache.IsWarmedUp && isMarketOpen))
                {
                    await realTradeCache.WarmupMarketCacheAsync();
                }

                if (!isMarketOpen)
                {
                    DateTime nowIst = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneHelper.IndianTimeZone);
                    _logger.LogDebug("Outside Market Trading Window or Market Holiday ({Time} IST). Real Auto Trade Signal Scan waiting...", nowIst.ToString("HH:mm:ss"));
                }
                else
                {
                    var realTradeService = scope.ServiceProvider.GetRequiredService<IAutoRealTradeService>();
                    var activeUserSettings = realTradeCache != null && realTradeCache.IsWarmedUp
                        ? realTradeCache.GetActiveUsersSettings().ToList()
                        : (await scope.ServiceProvider.GetRequiredService<IRealTradingRepository>().GetActiveSettingsAsync()).ToList();

                    if (activeUserSettings.Any())
                    {
                        _logger.LogInformation("Executing 15-minute Single-Pass REAL MONEY Scan over active stocks for {UserCount} active user(s)...", activeUserSettings.Count);
                        await RunSinglePassScanAndExecuteAsync(scope.ServiceProvider, realTradeService, activeUserSettings, stoppingToken);
                    }
                    else
                    {
                        _logger.LogDebug("No users currently have Real Auto Trade Master Switch ON. Skipping 15-minute scan cycle.");
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error occurred in AutoRealTradeSignalScanWorker cycle.");
            }

            // Next scan just after the next 15m candle close (Plan D2), not 15 min after this one started.
            await Task.Delay(RealTradeSchedule.DelayUntilNextScan(DateTime.UtcNow), stoppingToken);
        }
    }

    private async Task RunSinglePassScanAndExecuteAsync(
        IServiceProvider provider,
        IAutoRealTradeService realTradeService,
        List<RealTradeSettings> activeUserSettings,
        CancellationToken stoppingToken)
    {
        var stockRepo = provider.GetRequiredService<IStockMasterRepository>();
        var candleRepo = provider.GetRequiredService<IMarketCandleRepository>();
        var strategySettingsRepo = provider.GetRequiredService<ISwingStrategySettingsRepository>();
        var strategySettings = await strategySettingsRepo.GetSettingsAsync();

        var activeStocks = (await stockRepo.GetActiveStocksAsync()).ToList();
        if (!activeStocks.Any())
        {
            _logger.LogWarning("No active stocks found in stock_master repository for Real Auto Trade Scan.");
            return;
        }

        var niftyCandles = (await candleRepo.GetHistoryAsync("NIFTY 50", "1d", RealTradeSchedule.DailyCandleHistoryCount))
            .OrderBy(c => c.CandleTime)
            .ToList();

        if (!niftyCandles.Any())
        {
            niftyCandles = (await candleRepo.GetHistoryAsync("NIFTYBEES", "1d", RealTradeSchedule.DailyCandleHistoryCount))
                .OrderBy(c => c.CandleTime)
                .ToList();
        }

        // Mandatory market gate (checked once per scan): when the NIFTY filter fails - or its data is
        // missing - no new REAL entry is taken, including via the MinConditionsMatch path below.
        // Exits / SL / targets on open positions are handled elsewhere and are unaffected.
        // REGIME mode (swing_strategy_settings.market_gate_mode): the daily market regime + regime_policy decide instead
        // of the all-or-nothing NIFTY filter - strong stocks can still qualify in a weak market, with a higher score bar,
        // a relative-strength requirement and fewer positions. NIFTY_FILTER mode keeps the previous behaviour.
        // Auto Short Selling (OFF by default, per user) has its own, mirrored market gate: shorts only while NIFTY is
        // bearish (Close < 50 DMA & EMA20 < EMA50; missing data = no shorts), and only before each user's entry cut-off.
        // Trade Conditions: a user who switched a side's NIFTY condition OFF is not stopped by that side's NIFTY gate.
        var nowIst = SwingTradeRules.NowIst();
        bool regimeMode = strategySettings.UsesRegimeGate;

        // Each user's Trade Conditions profile per side. No stored row = default = the original path (shared candidate
        // lists below); a custom profile gets its own candidate list, judged by TradeConditionRules.
        var storedProfiles = await provider.GetRequiredService<ITradeConditionProfileRepository>()
            .GetAllForModeAsync(TradeConditionProfile.ModeAutoReal);
        var buyProfiles = new Dictionary<int, TradeConditionProfile>();
        var shortProfiles = new Dictionary<int, TradeConditionProfile>();
        foreach (var u in activeUserSettings)
        {
            buyProfiles[u.UserId] = ProfileFor(storedProfiles, u.UserId, TradeConditionProfile.SideBuy);
            shortProfiles[u.UserId] = ProfileFor(storedProfiles, u.UserId, TradeConditionProfile.SideShort);
        }

        bool niftyBearish = SwingShortDecisionEngine.IsNiftyBearishFilterPassed(niftyCandles, strategySettings);
        var shortUsers = activeUserSettings
            .Where(u => u.IsAutoShortEnabled && SwingTradeRules.IsBeforeShortEntryCutoff(u.ShortEntryCutoff, nowIst)
                && (niftyBearish || !shortProfiles[u.UserId].IsEnabled(TradeConditionRules.Market)))
            .ToList();
        bool shortGateOpen = shortUsers.Count > 0;

        MarketGateDecision? regimeGate = null;
        var engineSettings = strategySettings;
        bool longGateOpen = true;
        bool niftyLongFailed = false;
        if (strategySettings.UsesRegimeGate)
        {
            regimeGate = await provider.GetRequiredService<IMarketRegimeService>().GetRegimeGateAsync();
            if (!regimeGate.AllowsEntries)
            {
                _logger.LogWarning("⛔ Market regime gate: {Reason} - no new REAL entries this scan.", regimeGate.Reason);
                longGateOpen = false;
            }
            else
            {
                _logger.LogInformation("Market regime gate: {Reason}", regimeGate.Reason);
                engineSettings = RegimeGate.WithoutNiftyGate(strategySettings);
            }
        }
        else if (strategySettings.RequireNiftyMarketFilter && !SwingDecisionEngine.IsNiftyMarketFilterPassed(niftyCandles, strategySettings))
        {
            _logger.LogWarning("⛔ NIFTY Market Filter FAILED (Close <= 50 DMA / EMA20 <= EMA50 or {Count} daily candles available) - no new REAL entries this scan.",
                niftyCandles.Count);
            longGateOpen = false;
            niftyLongFailed = true;
        }

        // Users who may take a new long this scan: everyone when the gate is open; when only the NIFTY filter failed,
        // those whose BUY profile switched it OFF.
        var longUsers = activeUserSettings
            .Where(u => longGateOpen || (niftyLongFailed && !buyProfiles[u.UserId].IsEnabled(TradeConditionRules.Market)))
            .ToList();
        if (!longGateOpen && longUsers.Count > 0)
        {
            _logger.LogInformation("NIFTY Market Filter failed, but {Count} user(s) switched it OFF in Trade Conditions - REAL BUY scan continues for them.", longUsers.Count);
        }

        if (longUsers.Count == 0 && !shortGateOpen)
        {
            return;
        }
        if (shortGateOpen)
        {
            _logger.LogInformation("REAL SHORT scan active for {Count} user(s) with Auto Short enabled (NIFTY bearish: {Bearish}).", shortUsers.Count, niftyBearish);
        }

        var defaultShortUsers = shortUsers.Where(u => shortProfiles[u.UserId].IsDefault).ToList();
        var profiledShortUsers = shortUsers.Where(u => !shortProfiles[u.UserId].IsDefault).ToList();
        bool legacyLongScan = longGateOpen && longUsers.Any(u => buyProfiles[u.UserId].IsDefault);
        var profiledLongUsers = longUsers.Where(u => !buyProfiles[u.UserId].IsDefault).ToList();

        // Single pass: collect candidate stocks
        var candidateStocks = new List<ScanCandidate>();
        var shortCandidates = new List<ScanCandidate>();
        var profiledLongCandidates = profiledLongUsers.ToDictionary(u => u.UserId, _ => new List<ScanCandidate>());
        var profiledShortCandidates = profiledShortUsers.ToDictionary(u => u.UserId, _ => new List<ScanCandidate>());
        int shortMetCountThreshold = defaultShortUsers.Count > 0 ? defaultShortUsers.Min(u => u.MinConditionsMatch) : int.MaxValue;

        // The per-user filter below only re-checks candidates already collected here, so this
        // pre-filter must never be stricter than the most lenient active user's MinConditionsMatch
        // - otherwise a user with an aggressive (low) threshold would silently lose signals that
        // never made it into the candidate list in the first place.
        int candidateMetCountThreshold = activeUserSettings.Min(u => u.MinConditionsMatch);

        foreach (var stock in activeStocks)
        {
            if (stoppingToken.IsCancellationRequested) break;
            if (stock.Symbol == "NIFTY 50" || stock.Symbol == "NIFTYBEES") continue;

            try
            {
                var stockCandles1d = (await candleRepo.GetHistoryAsync(stock.Symbol, "1d", RealTradeSchedule.DailyCandleHistoryCount))
                    .OrderBy(c => c.CandleTime)
                    .ToList();
                var stockCandles15m = (await candleRepo.GetHistoryAsync(stock.Symbol, "15m", RealTradeSchedule.CandleHistoryCount))
                    .OrderBy(c => c.CandleTime)
                    .ToList();
                var stockCandles60m = (await candleRepo.GetHistoryAsync(stock.Symbol, "60m", RealTradeSchedule.CandleHistoryCount))
                    .OrderBy(c => c.CandleTime)
                    .ToList();

                if (stockCandles1d.Count < RealTradeSchedule.MinDailyCandles) continue;

                // Auto Short candidates - the mirrored engine on the same candles.
                if (shortGateOpen)
                {
                    var shortResult = SwingShortDecisionEngine.Evaluate(stock, stockCandles1d, stockCandles15m, stockCandles60m, niftyCandles, strategySettings);
                    int shortMetCount = shortResult.Checklist?.MetCount ?? 0;
                    if (defaultShortUsers.Count > 0 && shortResult.HardFiltersPassed && (shortResult.IsSellSignal || shortMetCount >= shortMetCountThreshold))
                    {
                        shortCandidates.Add(new ScanCandidate(stock, shortResult.EntryPrice, shortMetCount, shortResult.Score, shortResult.IsSellSignal,
                            shortResult.StopLoss, shortResult.Target1, shortResult.DailyAtr, null));
                    }

                    // Scored once per stock, only if a profile needs it (it switched off a hard filter the stock failed).
                    SwingEvaluationResult? shortUngated = null;
                    foreach (var u in profiledShortUsers)
                    {
                        var (judged, scored) = TradeConditionRules.Evaluate(shortProfiles[u.UserId], shortResult,
                            () => shortUngated ??= SwingShortDecisionEngine.Evaluate(stock, stockCandles1d, stockCandles15m, stockCandles60m, niftyCandles, strategySettings, skipHardFilterGate: true),
                            strategySettings, regimeMode, u.MinConditionsMatch);
                        if (judged.Qualifies)
                        {
                            profiledShortCandidates[u.UserId].Add(new ScanCandidate(stock, scored.EntryPrice, judged.MetCount, judged.Score, judged.IsSignal,
                                scored.StopLoss, scored.Target1, scored.DailyAtr, ConditionGate.From(judged)));
                        }
                    }
                }

                if (longUsers.Count == 0) continue;

                var evalResult = SwingDecisionEngine.Evaluate(stock, stockCandles1d, stockCandles15m, stockCandles60m, niftyCandles, engineSettings);
                if (evalResult == null || evalResult.Checklist == null) continue;

                int metCount = evalResult.Checklist.MetCount;

                if (legacyLongScan)
                {
                    if (regimeGate?.Policy != null)
                    {
                        // Regime mode: only the regime policy decides (the MinConditionsMatch path doesn't apply).
                        var (allowed, _) = RegimeGate.Evaluate(evalResult, regimeGate.Policy);
                        if (allowed)
                        {
                            candidateStocks.Add(new ScanCandidate(stock, evalResult.EntryPrice, metCount, evalResult.Score, true,
                                evalResult.StopLoss, evalResult.Target1, evalResult.DailyAtr, null));
                        }
                    }
                    // Threshold filter: Collect candidate if confirmed Buy or meets the most lenient active user's threshold
                    else if (evalResult.IsBuySignal || metCount >= candidateMetCountThreshold)
                    {
                        candidateStocks.Add(new ScanCandidate(stock, evalResult.EntryPrice, metCount, evalResult.Score, evalResult.IsBuySignal,
                            evalResult.StopLoss, evalResult.Target1, evalResult.DailyAtr, null));
                    }
                }

                SwingEvaluationResult? longUngated = null;
                foreach (var u in profiledLongUsers)
                {
                    var (judged, scored) = TradeConditionRules.Evaluate(buyProfiles[u.UserId], evalResult,
                        () => longUngated ??= SwingDecisionEngine.Evaluate(stock, stockCandles1d, stockCandles15m, stockCandles60m, niftyCandles, engineSettings, skipHardFilterGate: true),
                        engineSettings, regimeMode, u.MinConditionsMatch, regimeGate?.Policy);
                    if (judged.Qualifies)
                    {
                        // In regime mode the policy already decided - like the default path, the candidate counts as a signal.
                        profiledLongCandidates[u.UserId].Add(new ScanCandidate(stock, scored.EntryPrice, judged.MetCount, judged.Score,
                            judged.IsSignal || regimeGate?.Policy != null, scored.StopLoss, scored.Target1, scored.DailyAtr, ConditionGate.From(judged)));
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error scanning symbol {Symbol} during Single-Pass Real Auto Trade Scan.", stock.Symbol);
            }
        }

        _logger.LogInformation("Single-Pass Scan identified {CandidateCount} candidate stocks ({ShortCount} short candidates, {ProfiledCount} user(s) with custom Trade Conditions). Distributing to {UserCount} active user(s)...",
            candidateStocks.Count, shortCandidates.Count, profiledLongUsers.Count + profiledShortUsers.Count, activeUserSettings.Count);

        // Distribute candidate signals to each active user based on their specific settings
        foreach (var userSettings in activeUserSettings)
        {
            if (stoppingToken.IsCancellationRequested) break;

            if (longUsers.Any(u => u.UserId == userSettings.UserId))
            {
                var userLongCandidates = profiledLongCandidates.TryGetValue(userSettings.UserId, out var ownLong) ? ownLong : candidateStocks;
                await DistributeLongCandidatesAsync(provider, realTradeService, userSettings, userLongCandidates, regimeGate);
            }

            // Auto Short - only for users who switched it on (and are still before their short entry cut-off).
            if (shortGateOpen && shortUsers.Any(u => u.UserId == userSettings.UserId))
            {
                var userShortCandidates = profiledShortCandidates.TryGetValue(userSettings.UserId, out var ownShort) ? ownShort : shortCandidates;
                foreach (var candidate in userShortCandidates.OrderByDescending(c => c.Score))
                {
                    if (stoppingToken.IsCancellationRequested) break;
                    if (!candidate.IsSignal && candidate.MetCount < (candidate.Gate?.MinConditionsMatch ?? userSettings.MinConditionsMatch)) continue;

                    bool executed = await realTradeService.EvaluateAndExecuteRealShortAsync(
                        candidate.Stock.Symbol, candidate.EntryPrice, candidate.MetCount, userSettings.UserId, candidate.IsSignal,
                        candidate.EngineStopLoss, candidate.EngineTarget, candidate.DailyAtr, candidate.Gate);

                    if (executed)
                    {
                        _logger.LogInformation("✅ Live SHORT Executed for User {UserId}: {Symbol} @ ₹{Price:F2} (Score {Score}/100, Met {MetCount}/{Total})",
                            userSettings.UserId, candidate.Stock.Symbol, candidate.EntryPrice, candidate.Score, candidate.MetCount, candidate.TotalConditions);
                    }
                }
            }
        }
    }

    // The long (BUY) part of the per-user distribution - unchanged from before Auto Short existed.
    private async Task DistributeLongCandidatesAsync(
        IServiceProvider provider,
        IAutoRealTradeService realTradeService,
        RealTradeSettings userSettings,
        List<ScanCandidate> candidateStocks,
        MarketGateDecision? regimeGate)
    {
        int executedOrdersCount = 0;

        // Regime mode: at most policy.MaxPositions open positions per user (stronger regimes allow more).
        int positionRoom = int.MaxValue;
        if (regimeGate?.Policy != null)
        {
            int open = (await provider.GetRequiredService<IRealTradingRepository>().GetOpenPositionsAsync(userSettings.UserId)).Count();
            positionRoom = regimeGate.Policy.MaxPositions - open;
            if (positionRoom <= 0)
            {
                _logger.LogInformation("User {UserId}: {Open} open positions - {Regime} allows {Max}; no new REAL entries.",
                    userSettings.UserId, open, regimeGate.Policy.Regime, regimeGate.Policy.MaxPositions);
                return;
            }
        }

        foreach (var candidate in candidateStocks.OrderByDescending(c => c.Score))
        {
            if (executedOrdersCount >= positionRoom) break;
            if (candidate.IsSignal || candidate.MetCount >= (candidate.Gate?.MinConditionsMatch ?? userSettings.MinConditionsMatch))
            {
                bool executed = await realTradeService.EvaluateAndExecuteRealBuyAsync(
                    candidate.Stock.Symbol, candidate.EntryPrice, candidate.MetCount, userSettings.UserId, candidate.IsSignal,
                    candidate.EngineStopLoss, candidate.EngineTarget, dailyAtr: candidate.DailyAtr, conditionGate: candidate.Gate);

                if (executed)
                {
                    executedOrdersCount++;
                    _logger.LogInformation("✅ Live BUY Executed for User {UserId}: {Symbol} @ ₹{Price:F2} (Score {Score}/100, Met {MetCount}/{Total})",
                        userSettings.UserId, candidate.Stock.Symbol, candidate.EntryPrice, candidate.Score, candidate.MetCount, candidate.TotalConditions);
                }
            }
        }
    }

    private static TradeConditionProfile ProfileFor(IReadOnlyList<TradeConditionProfile> stored, int userId, string side) =>
        stored.FirstOrDefault(p => p.UserRef == userId.ToString() && p.Side == side)
        ?? TradeConditionProfile.Default(userId.ToString(), TradeConditionProfile.ModeAutoReal, side);

    /// <summary>A stock that passed the scan for one side. Gate is set when a user's custom Trade Conditions decided it.</summary>
    private sealed record ScanCandidate(Domain.Entities.StockMaster Stock, decimal EntryPrice, int MetCount, int Score, bool IsSignal,
        decimal EngineStopLoss, decimal EngineTarget, decimal DailyAtr, ConditionGate? Gate)
    {
        public int TotalConditions => Gate?.TotalConditions ?? TradeConditionRules.TotalConditions;
    }
}
