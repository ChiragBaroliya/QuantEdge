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
        var nowIst = SwingTradeRules.NowIst();
        var shortUsers = activeUserSettings
            .Where(u => u.IsAutoShortEnabled && SwingTradeRules.IsBeforeShortEntryCutoff(u.ShortEntryCutoff, nowIst))
            .ToList();
        bool shortGateOpen = shortUsers.Count > 0 && SwingShortDecisionEngine.IsNiftyBearishFilterPassed(niftyCandles);

        MarketGateDecision? regimeGate = null;
        var engineSettings = strategySettings;
        bool longGateOpen = true;
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
        }

        if (!longGateOpen && !shortGateOpen)
        {
            return;
        }
        if (shortGateOpen)
        {
            _logger.LogInformation("NIFTY bearish filter passed - REAL SHORT scan active for {Count} user(s) with Auto Short enabled.", shortUsers.Count);
        }

        // Single pass: collect candidate stocks
        var candidateStocks = new List<(Domain.Entities.StockMaster Stock, decimal EntryPrice, int MetCount, int Score, bool IsBuySignal, decimal EngineStopLoss, decimal EngineTarget, decimal DailyAtr)>();
        var shortCandidates = new List<(Domain.Entities.StockMaster Stock, decimal EntryPrice, int MetCount, int Score, bool IsSellSignal, decimal EngineStopLoss, decimal EngineTarget, decimal DailyAtr)>();
        int shortMetCountThreshold = shortUsers.Count > 0 ? shortUsers.Min(u => u.MinConditionsMatch) : int.MaxValue;

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
                    if (shortResult.HardFiltersPassed && (shortResult.IsSellSignal || shortMetCount >= shortMetCountThreshold))
                    {
                        shortCandidates.Add((stock, shortResult.EntryPrice, shortMetCount, shortResult.Score, shortResult.IsSellSignal,
                            shortResult.StopLoss, shortResult.Target1, shortResult.DailyAtr));
                    }
                }

                if (!longGateOpen) continue;

                var evalResult = SwingDecisionEngine.Evaluate(stock, stockCandles1d, stockCandles15m, stockCandles60m, niftyCandles, engineSettings);
                if (evalResult == null || evalResult.Checklist == null) continue;

                int metCount = evalResult.Checklist.MetCount;

                if (regimeGate?.Policy != null)
                {
                    // Regime mode: only the regime policy decides (the MinConditionsMatch path doesn't apply).
                    var (allowed, _) = RegimeGate.Evaluate(evalResult, regimeGate.Policy);
                    if (allowed)
                    {
                        candidateStocks.Add((stock, evalResult.EntryPrice, metCount, evalResult.Score, true, evalResult.StopLoss, evalResult.Target1, evalResult.DailyAtr));
                    }
                    continue;
                }

                // Threshold filter: Collect candidate if confirmed Buy or meets the most lenient active user's threshold
                if (evalResult.IsBuySignal || metCount >= candidateMetCountThreshold)
                {
                    candidateStocks.Add((stock, evalResult.EntryPrice, metCount, evalResult.Score, evalResult.IsBuySignal, evalResult.StopLoss, evalResult.Target1, evalResult.DailyAtr));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error scanning symbol {Symbol} during Single-Pass Real Auto Trade Scan.", stock.Symbol);
            }
        }

        _logger.LogInformation("Single-Pass Scan identified {CandidateCount} candidate stocks ({ShortCount} short candidates). Distributing to {UserCount} active user(s)...",
            candidateStocks.Count, shortCandidates.Count, activeUserSettings.Count);

        // Distribute candidate signals to each active user based on their specific settings
        foreach (var userSettings in activeUserSettings)
        {
            if (stoppingToken.IsCancellationRequested) break;

            await DistributeLongCandidatesAsync(provider, realTradeService, userSettings, candidateStocks, regimeGate);

            // Auto Short - only for users who switched it on (and are still before their short entry cut-off).
            if (shortGateOpen && shortUsers.Any(u => u.UserId == userSettings.UserId))
            {
                foreach (var candidate in shortCandidates.OrderByDescending(c => c.Score))
                {
                    if (stoppingToken.IsCancellationRequested) break;
                    if (!candidate.IsSellSignal && candidate.MetCount < userSettings.MinConditionsMatch) continue;

                    bool executed = await realTradeService.EvaluateAndExecuteRealShortAsync(
                        candidate.Stock.Symbol, candidate.EntryPrice, candidate.MetCount, userSettings.UserId, candidate.IsSellSignal,
                        candidate.EngineStopLoss, candidate.EngineTarget, candidate.DailyAtr);

                    if (executed)
                    {
                        _logger.LogInformation("✅ Live SHORT Executed for User {UserId}: {Symbol} @ ₹{Price:F2} (Score {Score}/100, Met {MetCount}/11)",
                            userSettings.UserId, candidate.Stock.Symbol, candidate.EntryPrice, candidate.Score, candidate.MetCount);
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
        List<(Domain.Entities.StockMaster Stock, decimal EntryPrice, int MetCount, int Score, bool IsBuySignal, decimal EngineStopLoss, decimal EngineTarget, decimal DailyAtr)> candidateStocks,
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
            if (candidate.IsBuySignal || candidate.MetCount >= userSettings.MinConditionsMatch)
            {
                bool executed = await realTradeService.EvaluateAndExecuteRealBuyAsync(
                    candidate.Stock.Symbol, candidate.EntryPrice, candidate.MetCount, userSettings.UserId, candidate.IsBuySignal,
                    candidate.EngineStopLoss, candidate.EngineTarget, dailyAtr: candidate.DailyAtr);

                if (executed)
                {
                    executedOrdersCount++;
                    _logger.LogInformation("✅ Live BUY Executed for User {UserId}: {Symbol} @ ₹{Price:F2} (Score {Score}/100, Met {MetCount}/11)",
                        userSettings.UserId, candidate.Stock.Symbol, candidate.EntryPrice, candidate.Score, candidate.MetCount);
                }
            }
        }
    }
}
