using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using QuantEdge.Domain.Entities;
using QuantEdge.Infrastructure.Constants;
using QuantEdge.Infrastructure.DTOs;
using QuantEdge.Infrastructure.Interfaces;
using QuantEdge.Infrastructure.Persistence.Repositories;

namespace QuantEdge.Infrastructure.Services;

/// <summary>
/// Signal Dashboard verdict: loads the same candles AutoRealTradeSignalScanWorker loads, runs the same
/// SwingDecisionEngine evaluation, and maps it to what the user should do - so the dashboard can never
/// say BUY while the bot says no (or the reverse).
/// </summary>
public class StockVerdictService : IStockVerdictService
{
    // 15-min factors that decide entry timing (Risk:Reward is always awarded, so it is left out).
    private static readonly HashSet<string> TimingFactors = new(StringComparer.Ordinal)
    {
        "BREAKOUT_GROUP", "VOL_CONFIRMATION", "RSI_MOMENTUM", "MACD_BULLISH", "BULLISH_CANDLE"
    };

    private readonly IMarketCandleRepository _candleRepository;
    private readonly IStockMasterRepository _stockRepository;
    private readonly ISwingStrategySettingsRepository _strategySettingsRepository;
    private readonly IRealTradingRepository _realTradingRepository;
    private readonly IAutoRealTradeService _realTradeService;
    private readonly IZerodhaKiteBrokerService _brokerService;
    private readonly ILiveQuoteRepository _liveQuoteRepository;
    private readonly IMarketRegimeService _regimeService;

    public StockVerdictService(
        IMarketCandleRepository candleRepository,
        IStockMasterRepository stockRepository,
        ISwingStrategySettingsRepository strategySettingsRepository,
        IRealTradingRepository realTradingRepository,
        IAutoRealTradeService realTradeService,
        IZerodhaKiteBrokerService brokerService,
        ILiveQuoteRepository liveQuoteRepository,
        IMarketRegimeService regimeService)
    {
        _liveQuoteRepository = liveQuoteRepository ?? throw new ArgumentNullException(nameof(liveQuoteRepository));
        _regimeService = regimeService ?? throw new ArgumentNullException(nameof(regimeService));
        _candleRepository = candleRepository ?? throw new ArgumentNullException(nameof(candleRepository));
        _stockRepository = stockRepository ?? throw new ArgumentNullException(nameof(stockRepository));
        _strategySettingsRepository = strategySettingsRepository ?? throw new ArgumentNullException(nameof(strategySettingsRepository));
        _realTradingRepository = realTradingRepository ?? throw new ArgumentNullException(nameof(realTradingRepository));
        _realTradeService = realTradeService ?? throw new ArgumentNullException(nameof(realTradeService));
        _brokerService = brokerService ?? throw new ArgumentNullException(nameof(brokerService));
    }

    public async Task<StockVerdictDto> GetVerdictAsync(string symbol, int userId = 1)
    {
        symbol = symbol.ToUpper().Trim();
        var strategy = await _strategySettingsRepository.GetSettingsAsync() ?? SwingStrategySettings.Default;
        var settings = await _realTradeService.GetSettingsAsync(userId);

        var dto = new StockVerdictDto
        {
            Symbol = symbol,
            AsOfUtc = DateTime.UtcNow,
            BuyThreshold = strategy.BuyScoreThreshold,
            WatchThreshold = strategy.WatchScoreThreshold,
            MinConditionsMatch = settings.MinConditionsMatch
        };

        // Same candle set as the scan worker.
        var candles1d = await LoadAsync(symbol, "1d");
        var candles15m = await LoadAsync(symbol, "15m");
        var candles60m = await LoadAsync(symbol, "60m");
        var nifty = await LoadAsync("NIFTY 50", "1d");
        if (!nifty.Any()) nifty = await LoadAsync("NIFTYBEES", "1d");

        // Price and day change exactly as NSE shows them (Ltp vs previous session close) - see DayChangeCalculator.
        var live = await _liveQuoteRepository.GetAsync(new[] { symbol });
        var quote = DayChangeCalculator.Resolve(symbol, live.TryGetValue(symbol, out var q) ? q : null, candles1d, candles15m);
        dto.DayChangePct = quote?.ChangePct;
        dto.PrevClose = quote?.PrevClose;
        dto.PriceAsOfUtc = quote?.AsOfUtc;
        dto.PriceSource = quote?.Source;

        dto.Holding = await FindHoldingAsync(symbol, userId);

        if (candles1d.Count < RealTradeSchedule.MinDailyCandles)
        {
            dto.Verdict = "NO_DATA";
            dto.LastPrice = quote?.Ltp ?? candles15m.LastOrDefault()?.Close ?? candles1d.LastOrDefault()?.Close ?? 0m;
            dto.EngineReason = $"Only {candles1d.Count} daily candles - the bot needs at least {RealTradeSchedule.MinDailyCandles} to score a stock.";
            return dto;
        }

        var stock = await _stockRepository.GetBySymbolAsync(symbol) ?? new StockMaster { Symbol = symbol };
        // Same market gate as the bot (Plan Phase 1): REGIME mode scores without the NIFTY hard gate.
        MarketGateDecision? regimeGate = strategy.UsesRegimeGate ? await _regimeService.GetRegimeGateAsync() : null;
        var result = SwingDecisionEngine.Evaluate(stock, candles1d, candles15m, candles60m, nifty,
            regimeGate != null ? RegimeGate.WithoutNiftyGate(strategy) : strategy);

        dto.EngineDecision = result.Decision;
        dto.Score = result.Score;
        dto.EngineReason = result.Reason;
        dto.LastPrice = quote?.Ltp ?? result.EntryPrice;
        // The stop / target the bot would actually place with these settings (Plan D6), not the engine's 15m-ATR levels.
        var botLevels = SwingTradeRules.BotLevelsFor(result, SwingTradeParams.From(settings));
        dto.StopLoss = botLevels.StopLoss;
        dto.Target1 = botLevels.Target;
        dto.MetCount = result.Checklist?.MetCount ?? 0;
        dto.TotalConditions = result.Checklist?.TotalCount ?? 11;

        dto.MarketPassed = regimeGate?.AllowsEntries ?? result.IsMarketFilterPassed;
        dto.MarketRequired = regimeGate != null || strategy.RequireNiftyMarketFilter;
        dto.MarketGateMode = regimeGate != null ? SwingStrategySettings.GateModeRegime : SwingStrategySettings.GateModeNiftyFilter;
        dto.MarketReason = regimeGate?.Reason;
        dto.MarketPenalty = result.MarketPenaltyApplied;
        // Daily-trend rules only; a failed mandatory NIFTY filter is reported separately via MarketPassed.
        dto.TrendPassed = result.EmaTrendPassed && result.AdxPassed;
        dto.EmaTrendPassed = result.EmaTrendPassed;
        dto.AdxPassed = result.AdxPassed;
        dto.Adx1d = result.Adx1d;
        dto.Ema20_1d = result.Ema20_1d;
        dto.Ema50_1d = result.Ema50_1d;
        dto.Has60mData = result.Has60mData;
        dto.Rsi60m = result.Rsi60m;
        dto.Rsi15m = result.Rsi15m;
        dto.VolumeMultiple = result.VolumeMultiple;
        dto.Factors = result.Factors;
        dto.SetupPassed = result.Factors.Any(f => f.Code == "MULTITIMEFRAME" && f.Points > 0);
        dto.TimingPoints = result.Factors.Where(f => TimingFactors.Contains(f.Code)).Sum(f => f.Points);
        dto.TimingMaxPoints = result.Factors.Where(f => TimingFactors.Contains(f.Code)).Sum(f => f.MaxPoints);

        // The scan worker hands a stock to the buy guards when it is a BUY signal OR meets the user's
        // MinConditionsMatch - so "BUY" here means exactly "the bot will try to buy it".
        // In REGIME mode the regime policy decides instead (score bar, must-beat-NIFTY), exactly as in the scan workers.
        dto.IsBotCandidate = regimeGate?.Policy != null
            ? regimeGate.AllowsEntries && RegimeGate.Evaluate(result, regimeGate.Policy).Allowed
            : result.HardFiltersPassed && (result.IsBuySignal || dto.MetCount >= settings.MinConditionsMatch);
        if (regimeGate?.Policy != null && !dto.IsBotCandidate && result.HardFiltersPassed)
        {
            dto.EngineReason = $"{result.Reason} Market regime: {RegimeGate.Evaluate(result, regimeGate.Policy).Reason}.";
        }

        if (dto.Holding != null)
        {
            dto.Verdict = "HOLD";
        }
        else if (dto.IsBotCandidate)
        {
            dto.Verdict = "BUY";
            dto.BotPreview = await _realTradeService.PreviewBuyGuardsAsync(symbol, result.EntryPrice, dto.MetCount, result.IsBuySignal, userId);
        }
        else
        {
            dto.Verdict = result.Decision == "REJECT" ? "AVOID" : "WAIT";
        }

        // Blocked only by the market: score the stock as if NIFTY were healthy, for display only.
        // The verdict, score and bot candidacy above are untouched, so the bot and dashboard still agree.
        if (dto.MarketRequired && !dto.MarketPassed && dto.TrendPassed)
        {
            var whatIf = SwingDecisionEngine.Evaluate(stock, candles1d, candles15m, candles60m, nifty, WithoutMarketGate(strategy));
            dto.ReadyIfMarketRecovers = new StockVerdictReadinessDto
            {
                Score = whatIf.Score,
                Verdict = whatIf.IsBuySignal ? "BUY" : "WAIT",
                Has60mData = whatIf.Has60mData,
                SetupPassed = whatIf.Factors.Any(f => f.Code == "MULTITIMEFRAME" && f.Points > 0),
                Rsi60m = whatIf.Rsi60m,
                Rsi15m = whatIf.Rsi15m,
                VolumeMultiple = whatIf.VolumeMultiple,
                TimingPoints = whatIf.Factors.Where(f => TimingFactors.Contains(f.Code)).Sum(f => f.Points),
                TimingMaxPoints = whatIf.Factors.Where(f => TimingFactors.Contains(f.Code)).Sum(f => f.MaxPoints),
                Factors = whatIf.Factors
            };
        }

        return dto;
    }

    // Same thresholds, but NIFTY neither blocks nor costs points.
    private static SwingStrategySettings WithoutMarketGate(SwingStrategySettings s) => new()
    {
        Id = s.Id,
        BuyScoreThreshold = s.BuyScoreThreshold,
        WatchScoreThreshold = s.WatchScoreThreshold,
        RequireNiftyMarketFilter = false,
        MarketContextScorePenalty = 0,
        MarketContextPositionSizeFactor = s.MarketContextPositionSizeFactor,
        MarketProtectionBufferPct = s.MarketProtectionBufferPct,
        UpdatedAt = s.UpdatedAt
    };

    private async Task<List<MarketCandle>> LoadAsync(string symbol, string timeframe) =>
        (await _candleRepository.GetHistoryAsync(symbol, timeframe, RealTradeSchedule.HistoryCountFor(timeframe)))
            .OrderBy(c => c.CandleTime)
            .ToList();

    // A bot-monitored position first (no broker call); otherwise the Zerodha account itself.
    private async Task<StockVerdictHoldingDto?> FindHoldingAsync(string symbol, int userId)
    {
        var position = await _realTradingRepository.GetOpenPositionBySymbolAsync(userId, symbol);
        if (position != null)
        {
            return new StockVerdictHoldingDto
            {
                Source = "BOT",
                Quantity = position.Quantity,
                AveragePrice = position.AverageEntryPrice,
                StopLoss = position.StopLoss,
                TakeProfit = position.TakeProfit
            };
        }

        var token = await _brokerService.ValidateSessionTokenAsync(userId);
        if (!token.IsValid) return null;

        var holdings = await _brokerService.GetLiveHoldingsAsync(userId);
        var holding = holdings.Success
            ? holdings.Holdings?.FirstOrDefault(h => string.Equals(h.TradingSymbol, symbol, StringComparison.OrdinalIgnoreCase) && h.Quantity + h.T1Quantity > 0)
            : null;
        if (holding != null)
        {
            return new StockVerdictHoldingDto { Source = "ZERODHA", Quantity = holding.Quantity + holding.T1Quantity, AveragePrice = holding.AveragePrice };
        }

        var positions = await _brokerService.GetLivePositionsAsync(userId);
        var brokerPosition = positions.Success
            ? positions.Positions?.Net?.FirstOrDefault(p => string.Equals(p.TradingSymbol, symbol, StringComparison.OrdinalIgnoreCase) && p.Quantity > 0)
            : null;
        return brokerPosition == null ? null
            : new StockVerdictHoldingDto { Source = "ZERODHA", Quantity = brokerPosition.Quantity, AveragePrice = brokerPosition.BuyPrice };
    }
}
