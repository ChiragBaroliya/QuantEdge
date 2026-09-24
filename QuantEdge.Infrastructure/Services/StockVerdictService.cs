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

    public StockVerdictService(
        IMarketCandleRepository candleRepository,
        IStockMasterRepository stockRepository,
        ISwingStrategySettingsRepository strategySettingsRepository,
        IRealTradingRepository realTradingRepository,
        IAutoRealTradeService realTradeService,
        IZerodhaKiteBrokerService brokerService)
    {
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

        if (candles1d.Count >= 2)
        {
            decimal prevClose = candles1d[^2].Close;
            dto.DayChangePct = prevClose > 0m ? Math.Round((candles1d[^1].Close - prevClose) / prevClose * 100m, 2) : null;
        }

        dto.Holding = await FindHoldingAsync(symbol, userId);

        if (candles1d.Count < RealTradeSchedule.MinDailyCandles)
        {
            dto.Verdict = "NO_DATA";
            dto.LastPrice = candles15m.LastOrDefault()?.Close ?? candles1d.LastOrDefault()?.Close ?? 0m;
            dto.EngineReason = $"Only {candles1d.Count} daily candles - the bot needs at least {RealTradeSchedule.MinDailyCandles} to score a stock.";
            return dto;
        }

        var stock = await _stockRepository.GetBySymbolAsync(symbol) ?? new StockMaster { Symbol = symbol };
        var result = SwingDecisionEngine.Evaluate(stock, candles1d, candles15m, candles60m, nifty, strategy);

        dto.EngineDecision = result.Decision;
        dto.Score = result.Score;
        dto.EngineReason = result.Reason;
        dto.LastPrice = result.EntryPrice;
        dto.StopLoss = result.StopLoss;
        dto.Target1 = result.Target1;
        dto.MetCount = result.Checklist?.MetCount ?? 0;
        dto.TotalConditions = result.Checklist?.TotalCount ?? 11;

        dto.MarketPassed = result.IsMarketFilterPassed;
        dto.MarketPenalty = result.MarketPenaltyApplied;
        dto.TrendPassed = result.HardFiltersPassed;
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
        dto.IsBotCandidate = result.HardFiltersPassed && (result.IsBuySignal || dto.MetCount >= settings.MinConditionsMatch);

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

        return dto;
    }

    private async Task<List<MarketCandle>> LoadAsync(string symbol, string timeframe) =>
        (await _candleRepository.GetHistoryAsync(symbol, timeframe, RealTradeSchedule.CandleHistoryCount))
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
