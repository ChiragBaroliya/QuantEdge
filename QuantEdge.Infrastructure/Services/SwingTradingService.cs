using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using QuantEdge.Domain.Entities;
using QuantEdge.Infrastructure.DTOs;
using QuantEdge.Infrastructure.Hubs;
using QuantEdge.Infrastructure.Interfaces;
using QuantEdge.Infrastructure.Persistence;
using QuantEdge.Infrastructure.Persistence.Repositories;

namespace QuantEdge.Infrastructure.Services;

public class SwingTradingService : ISwingTradingService
{
    private readonly IStockMasterRepository _stockMasterRepository;
    private readonly IMarketCandleRepository _candleRepository;
    private readonly IHistoricalDataService _historicalDataService;
    private readonly IDbConnectionFactory _connectionFactory;
    private readonly IHubContext<MarketDataHub>? _hubContext;
    private readonly ICacheService? _cacheService;
    private readonly ILogger<SwingTradingService> _logger;

    public SwingTradingService(
        IStockMasterRepository stockMasterRepository,
        IMarketCandleRepository candleRepository,
        IHistoricalDataService historicalDataService,
        IDbConnectionFactory connectionFactory,
        ILogger<SwingTradingService> logger,
        IHubContext<MarketDataHub>? hubContext = null,
        ICacheService? cacheService = null)
    {
        _stockMasterRepository = stockMasterRepository ?? throw new ArgumentNullException(nameof(stockMasterRepository));
        _candleRepository = candleRepository ?? throw new ArgumentNullException(nameof(candleRepository));
        _historicalDataService = historicalDataService ?? throw new ArgumentNullException(nameof(historicalDataService));
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _hubContext = hubContext;
        _cacheService = cacheService;
    }

    public async Task<SwingTradingDashboardDto> GetDashboardDataAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Retrieving Swing Trading dashboard data...");

        string cacheKey = "swing_dashboard_data";
        if (_cacheService != null)
        {
            var cached = await _cacheService.GetAsync<SwingTradingDashboardDto>(cacheKey);
            if (cached != null)
            {
                _logger.LogInformation("Returning Swing Trading dashboard data from memory cache.");
                return cached;
            }
        }

        // Ensure we have active stocks and NIFTY 50
        var activeStocks = (await _stockMasterRepository.GetActiveStocksAsync()).ToList();
        var niftyStock = await _stockMasterRepository.GetBySymbolAsync("NIFTY 50");

        if (niftyStock == null)
        {
            _logger.LogWarning("NIFTY 50 is not active or present in stock_master. Activating it.");
            // Set active if exists
            using (var conn = _connectionFactory.CreateConnection())
            {
                await conn.ExecuteAsync("UPDATE stock_master SET is_active = TRUE WHERE symbol = 'NIFTY 50'");
            }
            niftyStock = await _stockMasterRepository.GetBySymbolAsync("NIFTY 50");
        }

        // 1. Fetch latest daily Nifty status
        NiftyStatusDto? niftyStatus = null;
        if (niftyStock != null)
        {
            var niftyCandles = (await _candleRepository.GetHistoryAsync("NIFTY 50", "1d", limit: 100))
                .OrderBy(c => c.CandleTime)
                .ToList();

            if (niftyCandles.Count >= 50)
            {
                var closes = niftyCandles.Select(c => c.Close).ToList();
                var sma50 = IndicatorCalculator.CalculateSma(closes, 50);
                var ema20 = IndicatorCalculator.CalculateEma(closes, 20);
                var ema50 = IndicatorCalculator.CalculateEma(closes, 50);

                int idx = niftyCandles.Count - 1;
                decimal lastClose = closes[idx];
                decimal lastSma50 = sma50[idx];
                decimal lastEma20 = ema20[idx];
                decimal lastEma50 = ema50[idx];

                bool isAboveSma50 = lastClose > lastSma50;
                bool isEmaBullish = lastEma20 > lastEma50;

                niftyStatus = new NiftyStatusDto(
                    Symbol: "NIFTY 50",
                    Close: lastClose,
                    Sma50: Math.Round(lastSma50, 2),
                    Ema20: Math.Round(lastEma20, 2),
                    Ema50: Math.Round(lastEma50, 2),
                    IsAboveSma50: isAboveSma50,
                    IsEmaBullish: isEmaBullish,
                    IsMarketFilterPassed: isAboveSma50 && isEmaBullish
                );
            }
        }

        if (niftyStatus == null)
        {
            niftyStatus = new NiftyStatusDto("NIFTY 50", 22000m, 21800m, 21900m, 21850m, true, true, true);
        }

        // 2. Fetch latest daily stock analysis records and evaluate with SwingDecisionEngine
        var stockSignals = new List<SwingStockSignalDto>();
        var niftyCandlesGlobal = (await _candleRepository.GetHistoryAsync("NIFTY 50", "1d", limit: 100))
            .OrderBy(c => c.CandleTime)
            .ToList();

        using (var conn = _connectionFactory.CreateConnection())
        {
            foreach (var stock in activeStocks)
            {
                if (stock.Symbol == "NIFTY 50") continue;

                var stockCandles = (await _candleRepository.GetHistoryAsync(stock.Symbol, "1d", limit: 300))
                    .OrderBy(c => c.CandleTime)
                    .ToList();

                var stockCandles60m = (await conn.QueryAsync<MarketCandle>(@"
                    SELECT * FROM market_candles_60m 
                    WHERE symbol = @Symbol 
                    ORDER BY candle_time DESC 
                    LIMIT 100",
                    new { Symbol = stock.Symbol }))
                    .OrderBy(c => c.CandleTime)
                    .ToList();

                if (stockCandles.Count >= 50)
                {
                    var evalResult = SwingDecisionEngine.Evaluate(stock, stockCandles, stockCandles60m, niftyCandlesGlobal);
                    int idx = stockCandles.Count - 1;
                    var c = stockCandles[idx];

                    var closes = stockCandles.Select(x => x.Close).ToList();
                    var highs = stockCandles.Select(x => x.High).ToList();
                    var lows = stockCandles.Select(x => x.Low).ToList();
                    var volumes = stockCandles.Select(x => x.Volume).ToList();

                    var ema20 = IndicatorCalculator.CalculateEma(closes, 20);
                    var ema50 = IndicatorCalculator.CalculateEma(closes, 50);
                    var ema200 = IndicatorCalculator.CalculateEma(closes, 200);
                    var rsi14 = IndicatorCalculator.CalculateRsi(closes, 14);
                    var (macd, macdSignal) = IndicatorCalculator.CalculateMacd(closes);
                    var adx14 = IndicatorCalculator.CalculateAdx(highs, lows, closes, 14);
                    var atr14 = IndicatorCalculator.CalculateAtr(highs, lows, closes, 14);
                    var high52W = IndicatorCalculator.Calculate52WeekHigh(highs, Math.Min(250, highs.Count));

                    var prev20Vol = volumes.Skip(Math.Max(0, idx - 20)).Take(Math.Min(20, idx)).ToList();
                    decimal avgVol20 = prev20Vol.Any() ? (decimal)prev20Vol.Average(v => (double)v) : 0m;
                    decimal volMult = avgVol20 > 0m ? Math.Round(c.Volume / avgVol20, 2) : 0m;
                    bool is52W = c.Close >= 0.90m * high52W[idx];

                    stockSignals.Add(new SwingStockSignalDto(
                        Symbol: stock.Symbol,
                        Close: c.Close,
                        Open: c.Open,
                        High: c.High,
                        Low: c.Low,
                        Ema20: Math.Round(ema20[idx], 2),
                        Ema50: Math.Round(ema50[idx], 2),
                        Ema200: Math.Round(ema200[idx], 2),
                        Rsi14: Math.Round(rsi14[idx], 2),
                        Macd: Math.Round(macd[idx], 2),
                        MacdSignal: Math.Round(macdSignal[idx], 2),
                        Adx14: Math.Round(adx14[idx], 2),
                        Atr14: Math.Round(atr14[idx], 2),
                        Volume: c.Volume,
                        AvgVolume20: (long)avgVol20,
                        VolumeMultiplier: volMult,
                        Is52WeekHigh: is52W,
                        High52Week: Math.Round(high52W[idx], 2),
                        ClosenessTo52WeekHighPct: high52W[idx] > 0m ? Math.Round(c.Close / high52W[idx] * 100m, 2) : 0m,
                        IsLastCandleBullish: c.Close > c.Open,
                        MeetsStockFilter: evalResult.IsBuySignal,
                        MeetsAllBuyRules: evalResult.IsBuySignal && niftyStatus.IsMarketFilterPassed,
                        Decision: evalResult.Decision,
                        Reason: evalResult.Reason,
                        Checklist: evalResult.Checklist,
                        Score: evalResult.Score,
                        ConfidencePct: evalResult.ConfidencePct,
                        EntryPrice: evalResult.EntryPrice,
                        StopLoss: evalResult.StopLoss,
                        Target1: evalResult.Target1,
                        Target2: evalResult.Target2,
                        RiskRewardRatio: evalResult.RiskRewardRatio,
                        PassedRules: evalResult.PassedRules,
                        FailedRules: evalResult.FailedRules,
                        Sector: evalResult.Sector
                    ));
                }
                else
                {
                    var emptyChecklist = BuildConditionChecklist(niftyStatus, 0m, 0m, 0m, 0m, 0m, 0m, 0m, 0m, 0m, 0, 0m, false, false, "");
                    stockSignals.Add(new SwingStockSignalDto(
                        stock.Symbol, 0m, 0m, 0m, 0m, 0m, 0m, 0m, 0m, 0m, 0m, 0m, 0m, 0, 0m, 0m, false, 0m, 0m, false, false, false, "HOLD", "Insufficient candle data. Please run EOD Job.", emptyChecklist
                    ));
                }
            }

            // 3. Fetch active positions and trades
            var allTradesRaw = (await conn.QueryAsync<dynamic>(@"
                SELECT id, symbol, 
                       entry_date::text AS entry_date_str, 
                       entry_price, quantity, is_closed, 
                       exit_date::text AS exit_date_str, 
                       exit_price, exit_reason,
                       COALESCE(
                           CASE 
                               WHEN is_closed = TRUE THEN (exit_date - entry_date)
                               ELSE (CURRENT_DATE - entry_date)
                           END, 0) AS hold_days,
                       COALESCE(
                           CASE 
                               WHEN is_closed = TRUE THEN ROUND(((exit_price - entry_price) / entry_price * 100)::numeric, 2)
                               ELSE ROUND((((SELECT close_price FROM daily_stock_analysis d JOIN stock_master s ON d.stock_id = s.id WHERE s.symbol = t.symbol ORDER BY d.trade_date DESC LIMIT 1) - entry_price) / entry_price * 100)::numeric, 2)
                           END, 0) AS profit_loss_pct
                FROM swing_positions t
                ORDER BY entry_date DESC")).ToList();

            var allTrades = new List<SwingTradeDto>();
            foreach (var r in allTradesRaw)
            {
                DateTime entryD = DateTime.UtcNow;
                DateTime pEntry = DateTime.UtcNow;
                if (r.entry_date_str != null && DateTime.TryParse(Convert.ToString(r.entry_date_str), out pEntry))
                {
                    entryD = pEntry;
                }

                DateTime? exitD = null;
                DateTime pExit = DateTime.UtcNow;
                if (r.exit_date_str != null && DateTime.TryParse(Convert.ToString(r.exit_date_str), out pExit))
                {
                    exitD = pExit;
                }

                allTrades.Add(new SwingTradeDto
                {
                    Id = (int)r.id,
                    Symbol = (string)r.symbol,
                    EntryDate = entryD,
                    EntryPrice = r.entry_price != null ? Convert.ToDecimal(r.entry_price) : 0m,
                    Quantity = r.quantity != null ? Convert.ToInt32(r.quantity) : 0,
                    IsClosed = r.is_closed != null && Convert.ToBoolean(r.is_closed),
                    ExitDate = exitD,
                    ExitPrice = r.exit_price != null ? Convert.ToDecimal(r.exit_price) : null,
                    ExitReason = r.exit_reason != null ? Convert.ToString(r.exit_reason) : null,
                    HoldDays = r.hold_days != null ? Convert.ToInt32(r.hold_days) : 0,
                    ProfitLossPct = r.profit_loss_pct != null ? Convert.ToDecimal(r.profit_loss_pct) : 0m
                });
            }



            // Calculate backtest stats for 15 days
            var trades15 = allTrades.Where(t => t.EntryDate >= DateTime.UtcNow.AddDays(-15)).ToList();
            var stats15 = CalculatePeriodStats(trades15, 15);

            // Calculate backtest stats for 30 days
            var trades30 = allTrades.Where(t => t.EntryDate >= DateTime.UtcNow.AddDays(-30)).ToList();
            var stats30 = CalculatePeriodStats(trades30, 30);

            var dashboardResult = new SwingTradingDashboardDto(
                NiftyStatus: niftyStatus,
                StockSignals: stockSignals,
                BacktestStats15Days: stats15,
                BacktestStats30Days: stats30,
                RecentTrades: allTrades.Take(30).ToList()
            );

            if (_cacheService != null)
            {
                await _cacheService.SetAsync("swing_dashboard_data", dashboardResult, TimeSpan.FromMinutes(2));
            }

            return dashboardResult;
        }


    }

    private static BacktestStatsDto CalculatePeriodStats(List<SwingTradeDto> trades, int days)
    {
        if (trades.Count == 0)
        {
            return new BacktestStatsDto(days, 0, 0, 0, 0m, 0m, 0m);
        }

        int total = trades.Count;
        int wins = trades.Count(t => t.ProfitLossPct > 0m);
        int losses = trades.Count(t => t.ProfitLossPct <= 0m);
        decimal winRate = (decimal)wins / total * 100m;
        decimal netPnl = trades.Sum(t => t.ProfitLossPct);
        decimal avgPnl = trades.Average(t => t.ProfitLossPct);

        return new BacktestStatsDto(
            PeriodDays: days,
            TotalTrades: total,
            WinTrades: wins,
            LossTrades: losses,
            WinRatePct: Math.Round(winRate, 2),
            NetProfitLossPct: Math.Round(netPnl, 2),
            AvgProfitLossPct: Math.Round(avgPnl, 2)
        );
    }

    public async Task RunEodJobAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Running Swing Trading EOD Job...");

        // Step 1: Sync instruments first to make sure Nifty 50 and active stocks are populated
        var activeStocks = (await _stockMasterRepository.GetActiveStocksAsync()).ToList();
        var niftyStock = await _stockMasterRepository.GetBySymbolAsync("NIFTY 50");

        if (niftyStock == null)
        {
            _logger.LogWarning("NIFTY 50 is missing. Provisioning NIFTY 50 in stock_master.");
            using (var conn = _connectionFactory.CreateConnection())
            {
                await conn.ExecuteAsync(@"
                    INSERT INTO stock_master (symbol, instrument_token, is_active, name, segment, exchange)
                    VALUES ('NIFTY 50', 256265, TRUE, 'NIFTY 50', 'INDICES', 'NSE')
                    ON CONFLICT (symbol) DO UPDATE SET is_active = TRUE;");
            }
            niftyStock = await _stockMasterRepository.GetBySymbolAsync("NIFTY 50");
        }

        // Step 2: Download daily candles for all active symbols
        var symbolsToSync = activeStocks.Select(s => s.Symbol).ToList();
        if (!symbolsToSync.Contains("NIFTY 50") && niftyStock != null)
        {
            symbolsToSync.Add("NIFTY 50");
        }

        UpdateJobProgress("eod", true, 10, "Syncing daily market candles for active stocks...");
        foreach (var symbol in symbolsToSync)
        {
            if (cancellationToken.IsCancellationRequested) break;
            try
            {
                await _historicalDataService.SyncGapsAsync(symbol, "1d", cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to sync EOD candles for {Symbol}.", symbol);
            }
        }

        UpdateJobProgress("eod", true, 60, "Evaluating 8-Point BUY/SELL recommendations...");

        // Step 3: Run daily analysis
        using (var conn = _connectionFactory.CreateConnection())
        {
            // Load Nifty daily candles
            var niftyCandles = (await _candleRepository.GetHistoryAsync("NIFTY 50", "1d", limit: 100))
                .OrderBy(c => c.CandleTime)
                .ToList();

            if (niftyCandles.Count < 50)
            {
                _logger.LogWarning("Insufficient Nifty 50 daily data. Expected 50 candles, found {Count}.", niftyCandles.Count);
                UpdateJobProgress("eod", false, 0, "Failed: Insufficient Nifty 50 data", "Insufficient Nifty 50 candles");
                return;
            }

            // Calculate Nifty indicators
            var niftyCloses = niftyCandles.Select(c => c.Close).ToList();
            var niftySma50 = IndicatorCalculator.CalculateSma(niftyCloses, 50);
            var niftyEma20 = IndicatorCalculator.CalculateEma(niftyCloses, 20);
            var niftyEma50 = IndicatorCalculator.CalculateEma(niftyCloses, 50);

            int nIdx = niftyCandles.Count - 1;
            bool niftyAboveSma50 = niftyCandles[nIdx].Close > niftySma50[nIdx];
            bool niftyEmaBullish = niftyEma20[nIdx] > niftyEma50[nIdx];
            bool marketFilterPassed = niftyAboveSma50 && niftyEmaBullish;

            _logger.LogInformation("Nifty 50 Status: Close={Close}, SMA50={SMA50}, EMA20={EMA20}, EMA50={EMA50}. FilterPassed={Passed}",
                niftyCandles[nIdx].Close, niftySma50[nIdx], niftyEma20[nIdx], niftyEma50[nIdx], marketFilterPassed);

            // Fetch EOD analysis date
            DateTime tradeDate = niftyCandles[nIdx].CandleTime.Date;

            foreach (var stock in activeStocks)
            {
                if (stock.Symbol == "NIFTY 50") continue;
                if (cancellationToken.IsCancellationRequested) break;

                try
                {
                    await AnalyzeStockForDateAsync(conn, stock, tradeDate, marketFilterPassed, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed EOD analysis for stock {Symbol} on {Date}.", stock.Symbol, tradeDate);
                }
            }
        }

        _logger.LogInformation("EOD Job completed successfully!");
        UpdateJobProgress("eod", false, 100, "Swing Trading EOD Daily Job completed successfully!");
        await BroadcastSwingDashboardUpdateAsync(cancellationToken);
    }

    public async Task RunIntraday30MinJobAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Executing 30-Minute Intraday Swing Trading Job...");
        UpdateJobProgress("intraday30m", true, 5, "Initiating 30-minute intraday Swing Trading analysis...");

        try
        {
            // Sync candles and execute swing analysis
            await RunEodJobAsync(cancellationToken);

            UpdateJobProgress("intraday30m", true, 90, "Broadcasting updated Swing Dashboard via SignalR...");
            await BroadcastSwingDashboardUpdateAsync(cancellationToken);

            UpdateJobProgress("intraday30m", false, 100, "30-minute Swing Trading intraday job completed successfully!");
            _logger.LogInformation("30-Minute Intraday Swing Trading Job completed successfully!");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred during 30-Minute Intraday Swing Trading Job.");
            UpdateJobProgress("intraday30m", false, 0, "30-minute Swing Trading job failed.", ex.Message);
            throw;
        }
    }

    private async Task BroadcastSwingDashboardUpdateAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (_cacheService != null)
            {
                await _cacheService.RemoveAsync("swing_dashboard_data");
            }

            if (_hubContext != null)
            {
                var dashboardData = await GetDashboardDataAsync(cancellationToken);
                await _hubContext.Clients.Group("SwingDashboard").SendAsync("ReceiveSwingDashboardUpdate", dashboardData, cancellationToken);
                await _hubContext.Clients.All.SendAsync("ReceiveSwingDashboardUpdate", dashboardData, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to broadcast Swing Dashboard updates over SignalR.");
        }
    }


    private async Task AnalyzeStockForDateAsync(
        IDbConnection conn, 
        StockMaster stock, 
        DateTime tradeDate, 
        bool marketFilterPassed,
        CancellationToken cancellationToken)
    {
        var candles = (await _candleRepository.GetHistoryAsync(stock.Symbol, "1d", limit: 300))
            .OrderBy(c => c.CandleTime)
            .ToList();

        if (candles.Count < 50)
        {
            _logger.LogWarning("Insufficient candle data for {Symbol} daily analysis. Required 50, found {Count}.", stock.Symbol, candles.Count);
            return;
        }

        var niftyCandles = (await _candleRepository.GetHistoryAsync("NIFTY 50", "1d", limit: 100))
            .OrderBy(c => c.CandleTime)
            .ToList();

        var candles60m = (await conn.QueryAsync<MarketCandle>(@"
            SELECT * FROM market_candles_60m 
            WHERE symbol = @Symbol AND candle_time <= @MaxTime
            ORDER BY candle_time DESC
            LIMIT 100",
            new { Symbol = stock.Symbol, MaxTime = tradeDate.Date.AddHours(16) }))
            .OrderBy(c => c.CandleTime)
            .ToList();

        var evalResult = SwingDecisionEngine.Evaluate(stock, candles, candles60m, niftyCandles);

        int idx = candles.Count - 1;
        decimal price = candles[idx].Close;
        decimal high = candles[idx].High;
        decimal low = candles[idx].Low;
        long vol = candles[idx].Volume;

        var closes = candles.Select(c => c.Close).ToList();
        var highs = candles.Select(c => c.High).ToList();
        var lows = candles.Select(c => c.Low).ToList();

        var ema20 = IndicatorCalculator.CalculateEma(closes, 20);
        var ema50 = IndicatorCalculator.CalculateEma(closes, 50);
        var ema200 = IndicatorCalculator.CalculateEma(closes, 200);
        var rsi14 = IndicatorCalculator.CalculateRsi(closes, 14);
        var (macd, macdSignal) = IndicatorCalculator.CalculateMacd(closes);
        var adx14 = IndicatorCalculator.CalculateAdx(highs, lows, closes, 14);
        var atr14 = IndicatorCalculator.CalculateAtr(highs, lows, closes, 14);
        var high52W = IndicatorCalculator.Calculate52WeekHigh(highs, Math.Min(250, highs.Count));

        var prev20VolList = candles.Select(c => c.Volume).Skip(Math.Max(0, idx - 20)).Take(Math.Min(20, idx)).ToList();
        decimal avgVol20 = prev20VolList.Any() ? (decimal)prev20VolList.Average(v => (double)v) : 0m;

        // Position evaluation
        var openPosition = await conn.QueryFirstOrDefaultAsync<dynamic>(@"
            SELECT * FROM swing_positions WHERE symbol = @Symbol AND is_closed = FALSE LIMIT 1",
            new { Symbol = stock.Symbol });

        bool buySignal = false;
        bool sellSignal = false;
        string recommendation = evalResult.Decision;
        string reason = evalResult.Reason;

        if (openPosition != null)
        {
            decimal entryPrice = Convert.ToDecimal(openPosition.entry_price);
            DateTime entryDate = ConvertToDateTime(openPosition.entry_date);

            int holdDays = candles.Count(c => c.CandleTime.Date >= entryDate.Date && c.CandleTime.Date <= tradeDate.Date) - 1;
            if (holdDays < 0) holdDays = 0;

            decimal curAtr = Math.Max(0.1m, atr14[idx]);
            decimal stopLossPrice = Math.Max(0.01m, entryPrice - (1.0m * curAtr));
            decimal targetPrice = entryPrice + (2.0m * curAtr);

            bool hitTarget = high >= targetPrice || high >= entryPrice * 1.05m;
            bool hitStop = low <= stopLossPrice || low <= entryPrice * 0.97m;
            bool trendExit = price < ema20[idx];
            bool macdBearish = macd[idx] < macdSignal[idx] && macd[idx - 1] >= macdSignal[idx - 1];
            bool rsiExit = rsi14[idx] < 45m;
            bool timeExit = holdDays >= 20;

            if (hitTarget || hitStop || trendExit || macdBearish || rsiExit || timeExit)
            {
                sellSignal = true;
                recommendation = "SELL";

                decimal exitPrice;
                string exitReason;

                if (hitStop && hitTarget)
                {
                    exitPrice = stopLossPrice;
                    exitReason = "Stop Loss & Profit Target Hit (Conservative Exit)";
                }
                else if (hitStop)
                {
                    exitPrice = stopLossPrice;
                    exitReason = $"ATR Stop Loss Hit (₹{stopLossPrice:F2})";
                }
                else if (hitTarget)
                {
                    exitPrice = targetPrice;
                    exitReason = $"ATR Profit Target 1 Hit (₹{targetPrice:F2})";
                }
                else if (trendExit)
                {
                    exitPrice = price;
                    exitReason = "Trend Reversal (Close < EMA20)";
                }
                else if (macdBearish)
                {
                    exitPrice = price;
                    exitReason = "MACD Bearish Crossover";
                }
                else if (rsiExit)
                {
                    exitPrice = price;
                    exitReason = "RSI Momentum Failure (<45)";
                }
                else
                {
                    exitPrice = price;
                    exitReason = "Time Exit (20 Trading Days)";
                }

                reason = $"SELL Order triggered on {tradeDate:yyyy-MM-dd}. Reason: {exitReason}. Hold Days: {holdDays}. P&L: {Math.Round((exitPrice - entryPrice) / entryPrice * 100, 2)}%";

                await conn.ExecuteAsync(@"
                    UPDATE swing_positions 
                    SET is_closed = TRUE, exit_date = @ExitDate, exit_price = @ExitPrice, exit_reason = @ExitReason 
                    WHERE id = @Id",
                    new { ExitDate = tradeDate.Date, ExitPrice = exitPrice, ExitReason = exitReason, Id = openPosition.id });

                _logger.LogInformation("SELL Order Generated for {Symbol}. Reason: {ExitReason}", stock.Symbol, exitReason);
            }
            else
            {
                recommendation = "HOLD";
                reason = $"HOLD active position. entry={entryPrice:F2}, current={price:F2}, stop={stopLossPrice:F2}, target={targetPrice:F2}, hold_days={holdDays}";
            }
        }
        else
        {
            if (evalResult.IsBuySignal)
            {
                buySignal = true;
                recommendation = evalResult.Decision;
                reason = evalResult.Reason;

                await conn.ExecuteAsync(@"
                    INSERT INTO swing_positions (symbol, entry_date, entry_price, quantity, is_closed)
                    VALUES (@Symbol, @EntryDate, @EntryPrice, 100, FALSE)",
                    new { Symbol = stock.Symbol, EntryDate = tradeDate.Date.AddDays(1), EntryPrice = price });

                _logger.LogInformation("BUY Order ({Decision}, Score {Score}) Generated for {Symbol} at EOD Close price {Price}.", evalResult.Decision, evalResult.Score, stock.Symbol, price);
            }
        }

        int buyScore = evalResult.Score;
        int sellScore = (price < ema20[idx] ? 30 : 0) + (rsi14[idx] < 45m ? 30 : 0) + (macd[idx] < macdSignal[idx] ? 40 : 0);

        await conn.ExecuteAsync(@"
            INSERT INTO daily_stock_analysis (
                stock_id, trade_date, close_price, volume, ema20, ema50, ema200, 
                rsi14, macd, macd_signal, adx14, atr14, average_volume20, 
                is_52_week_high, buy_score, sell_score, buy_signal, sell_signal, 
                recommendation, reason, created_on
            )
            VALUES (
                @StockId, @TradeDate, @ClosePrice, @Volume, @Ema20, @Ema50, @Ema200,
                @Rsi14, @Macd, @MacdSignal, @Adx14, @Atr14, @AvgVolume20,
                @Is52WeekHigh, @BuyScore, @SellScore, @BuySignal, @SellSignal,
                @Recommendation, @Reason, NOW()
            )
            ON CONFLICT (stock_id, trade_date)
            DO UPDATE SET
                close_price = EXCLUDED.close_price,
                volume = EXCLUDED.volume,
                ema20 = EXCLUDED.ema20,
                ema50 = EXCLUDED.ema50,
                ema200 = EXCLUDED.ema200,
                rsi14 = EXCLUDED.rsi14,
                macd = EXCLUDED.macd,
                macd_signal = EXCLUDED.macd_signal,
                adx14 = EXCLUDED.adx14,
                atr14 = EXCLUDED.atr14,
                average_volume20 = EXCLUDED.average_volume20,
                is_52_week_high = EXCLUDED.is_52_week_high,
                buy_score = EXCLUDED.buy_score,
                sell_score = EXCLUDED.sell_score,
                buy_signal = EXCLUDED.buy_signal,
                sell_signal = EXCLUDED.sell_signal,
                recommendation = EXCLUDED.recommendation,
                reason = EXCLUDED.reason,
                created_on = NOW()",
            new {
                StockId = stock.Id,
                TradeDate = tradeDate.Date,
                ClosePrice = price,
                Volume = vol,
                Ema20 = ema20[idx],
                Ema50 = ema50[idx],
                Ema200 = ema200[idx],
                Rsi14 = rsi14[idx],
                Macd = macd[idx],
                MacdSignal = macdSignal[idx],
                Adx14 = adx14[idx],
                Atr14 = atr14[idx],
                AvgVolume20 = avgVol20,
                Is52WeekHigh = price >= 0.90m * high52W[idx],
                BuyScore = buyScore,
                SellScore = sellScore,
                BuySignal = buySignal,
                SellSignal = sellSignal,
                Recommendation = recommendation,
                Reason = reason
            });
    }

    public async Task BackfillHistoricalAnalysesAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Backfilling historical daily analyses for all active stocks...");
        UpdateJobProgress("backfill", true, 5, "Preparing clean historical backtest environment...");

        // Ensure active stocks are populated
        var activeStocks = (await _stockMasterRepository.GetActiveStocksAsync()).ToList();

        using (var conn = _connectionFactory.CreateConnection())
        {
            // Clear existing analyses and simulated positions to run a clean backtest
            await conn.ExecuteAsync("TRUNCATE TABLE daily_stock_analysis CASCADE;");
            await conn.ExecuteAsync("TRUNCATE TABLE swing_positions CASCADE;");

            // Load Nifty daily candles
            var niftyCandles = (await _candleRepository.GetHistoryAsync("NIFTY 50", "1d", limit: 600))
                .OrderBy(c => c.CandleTime)
                .ToList();

            if (niftyCandles.Count < 50)
            {
                _logger.LogWarning("Insufficient Nifty daily candles for backfill. Expected at least 50, found {Count}.", niftyCandles.Count);
                UpdateJobProgress("backfill", false, 0, "Failed: Insufficient Nifty candles for backfill", "Insufficient Nifty candles");
                return;
            }

            int startIndex = Math.Min(250, niftyCandles.Count - 1);
            int totalDays = niftyCandles.Count - startIndex;

            for (int i = startIndex; i < niftyCandles.Count; i++)
            {
                if (cancellationToken.IsCancellationRequested) break;

                int step = i - startIndex;
                int pct = (int)((double)step / Math.Max(1, totalDays) * 90) + 5;
                DateTime currentDate = niftyCandles[i].CandleTime.Date;

                UpdateJobProgress("backfill", true, pct, $"Re-simulating trading day {step + 1}/{totalDays} ({currentDate:yyyy-MM-dd})...");

                var niftySub = niftyCandles.Take(i + 1).ToList();

                foreach (var stock in activeStocks)
                {
                    if (stock.Symbol == "NIFTY 50") continue;

                    var stockHistory = (await conn.QueryAsync<MarketCandle>(@"
                        SELECT * FROM market_candles_1d 
                        WHERE symbol = @Symbol AND candle_time <= @CurrentDate
                        ORDER BY candle_time DESC
                        LIMIT 300",
                        new { Symbol = stock.Symbol, CurrentDate = currentDate }))
                        .OrderBy(c => c.CandleTime)
                        .ToList();

                    if (stockHistory.Count < 50) continue;

                    var stockHistory60m = (await conn.QueryAsync<MarketCandle>(@"
                        SELECT * FROM market_candles_60m 
                        WHERE symbol = @Symbol AND candle_time <= @MaxTime
                        ORDER BY candle_time DESC
                        LIMIT 100",
                        new { Symbol = stock.Symbol, MaxTime = currentDate.Date.AddHours(16) }))
                        .OrderBy(c => c.CandleTime)
                        .ToList();

                    var evalResult = SwingDecisionEngine.Evaluate(stock, stockHistory, stockHistory60m, niftySub);

                    var closes = stockHistory.Select(c => c.Close).ToList();
                    var highs = stockHistory.Select(c => c.High).ToList();
                    var lows = stockHistory.Select(c => c.Low).ToList();
                    var volumes = stockHistory.Select(c => c.Volume).ToList();

                    var ema20 = IndicatorCalculator.CalculateEma(closes, 20);
                    var ema50 = IndicatorCalculator.CalculateEma(closes, 50);
                    var ema200 = IndicatorCalculator.CalculateEma(closes, 200);
                    var rsi14 = IndicatorCalculator.CalculateRsi(closes, 14);
                    var (macd, macdSignal) = IndicatorCalculator.CalculateMacd(closes);
                    var adx14 = IndicatorCalculator.CalculateAdx(highs, lows, closes, 14);
                    var atr14 = IndicatorCalculator.CalculateAtr(highs, lows, closes, 14);
                    var high52W = IndicatorCalculator.Calculate52WeekHigh(highs, Math.Min(250, highs.Count));

                    int idx = stockHistory.Count - 1;
                    decimal price = closes[idx];
                    decimal high = highs[idx];
                    decimal low = lows[idx];
                    long vol = volumes[idx];

                    var prev20VolList = volumes.Skip(Math.Max(0, idx - 20)).Take(Math.Min(20, idx)).ToList();
                    decimal avgVol20 = prev20VolList.Any() ? (decimal)prev20VolList.Average(v => (double)v) : 0m;

                    var openPosition = await conn.QueryFirstOrDefaultAsync<dynamic>(@"
                        SELECT * FROM swing_positions WHERE symbol = @Symbol AND is_closed = FALSE LIMIT 1",
                        new { Symbol = stock.Symbol });

                    bool buySignal = false;
                    bool sellSignal = false;
                    string recommendation = evalResult.Decision;
                    string reason = evalResult.Reason;

                    if (openPosition != null)
                    {
                        decimal entryPrice = Convert.ToDecimal(openPosition.entry_price);
                        DateTime entryDate = ConvertToDateTime(openPosition.entry_date);

                        int holdDays = stockHistory.Count(c => c.CandleTime.Date >= entryDate.Date && c.CandleTime.Date <= currentDate.Date) - 1;
                        if (holdDays < 0) holdDays = 0;

                        decimal curAtr = Math.Max(0.1m, atr14[idx]);
                        decimal stopLossPrice = Math.Max(0.01m, entryPrice - (1.0m * curAtr));
                        decimal targetPrice = entryPrice + (2.0m * curAtr);

                        bool hitTarget = high >= targetPrice || high >= entryPrice * 1.05m;
                        bool hitStop = low <= stopLossPrice || low <= entryPrice * 0.97m;
                        bool trendExit = price < ema20[idx];
                        bool macdBearish = macd[idx] < macdSignal[idx] && macd[idx - 1] >= macdSignal[idx - 1];
                        bool rsiExit = rsi14[idx] < 45m;
                        bool timeExit = holdDays >= 20;

                        if (hitTarget || hitStop || trendExit || macdBearish || rsiExit || timeExit)
                        {
                            sellSignal = true;
                            recommendation = "SELL";

                            decimal exitPrice;
                            string exitReason;

                            if (hitStop && hitTarget)
                            {
                                exitPrice = stopLossPrice;
                                exitReason = "Stop Loss & Profit Target Hit (Conservative)";
                            }
                            else if (hitStop)
                            {
                                exitPrice = stopLossPrice;
                                exitReason = $"ATR Stop Loss Hit (₹{stopLossPrice:F2})";
                            }
                            else if (hitTarget)
                            {
                                exitPrice = targetPrice;
                                exitReason = $"ATR Profit Target 1 Hit (₹{targetPrice:F2})";
                            }
                            else if (trendExit)
                            {
                                exitPrice = price;
                                exitReason = "Trend Reversal (Close < EMA20)";
                            }
                            else if (macdBearish)
                            {
                                exitPrice = price;
                                exitReason = "MACD Bearish Crossover";
                            }
                            else if (rsiExit)
                            {
                                exitPrice = price;
                                exitReason = "RSI Momentum Failure (<45)";
                            }
                            else
                            {
                                exitPrice = price;
                                exitReason = "Time Exit (20 Trading Days)";
                            }

                            reason = $"SELL Order triggered. Reason: {exitReason}. Hold Days: {holdDays}. P&L: {Math.Round((exitPrice - entryPrice) / entryPrice * 100, 2)}%";

                            await conn.ExecuteAsync(@"
                                UPDATE swing_positions 
                                SET is_closed = TRUE, exit_date = @ExitDate, exit_price = @ExitPrice, exit_reason = @ExitReason 
                                WHERE id = @Id",
                                new { ExitDate = currentDate, ExitPrice = exitPrice, ExitReason = exitReason, Id = openPosition.id });
                        }
                        else
                        {
                            recommendation = "HOLD";
                            reason = $"HOLD active position. entry={entryPrice:F2}, current={price:F2}, stop={stopLossPrice:F2}, target={targetPrice:F2}, hold_days={holdDays}";
                        }
                    }
                    else
                    {
                        if (evalResult.IsBuySignal)
                        {
                            buySignal = true;
                            recommendation = evalResult.Decision;
                            reason = evalResult.Reason;

                            decimal nextOpen = price;
                            DateTime nextDate = currentDate.AddDays(1);
                            
                            var nextCandle = await conn.QueryFirstOrDefaultAsync<dynamic>(@"
                                SELECT open, candle_time FROM market_candles_1d 
                                WHERE symbol = @Symbol AND candle_time > @CurrentDate 
                                ORDER BY candle_time ASC LIMIT 1",
                                new { Symbol = stock.Symbol, CurrentDate = currentDate });

                            if (nextCandle != null)
                            {
                                nextOpen = Convert.ToDecimal(nextCandle.open);
                                nextDate = ConvertToDateTime(nextCandle.candle_time);
                            }

                            await conn.ExecuteAsync(@"
                                INSERT INTO swing_positions (symbol, entry_date, entry_price, quantity, is_closed)
                                VALUES (@Symbol, @EntryDate, @EntryPrice, 100, FALSE)",
                                new { Symbol = stock.Symbol, EntryDate = nextDate.Date, EntryPrice = nextOpen });
                        }
                    }

                    int buyScore = evalResult.Score;
                    int sellScore = (price < ema20[idx] ? 30 : 0) + (rsi14[idx] < 45m ? 30 : 0) + (macd[idx] < macdSignal[idx] ? 40 : 0);

                    await conn.ExecuteAsync(@"
                        INSERT INTO daily_stock_analysis (
                            stock_id, trade_date, close_price, volume, ema20, ema50, ema200, 
                            rsi14, macd, macd_signal, adx14, atr14, average_volume20, 
                            is_52_week_high, buy_score, sell_score, buy_signal, sell_signal, 
                            recommendation, reason, created_on
                        )
                        VALUES (
                            @StockId, @TradeDate, @ClosePrice, @Volume, @Ema20, @Ema50, @Ema200,
                            @Rsi14, @Macd, @MacdSignal, @Adx14, @Atr14, @AvgVolume20,
                            @Is52WeekHigh, @BuyScore, @SellScore, @BuySignal, @SellSignal,
                            @Recommendation, @Reason, NOW()
                        )
                        ON CONFLICT (stock_id, trade_date) DO NOTHING;",
                        new {
                            StockId = stock.Id,
                            TradeDate = currentDate,
                            ClosePrice = price,
                            Volume = vol,
                            Ema20 = ema20[idx],
                            Ema50 = ema50[idx],
                            Ema200 = ema200[idx],
                            Rsi14 = rsi14[idx],
                            Macd = macd[idx],
                            MacdSignal = macdSignal[idx],
                            Adx14 = adx14[idx],
                            Atr14 = atr14[idx],
                            AvgVolume20 = avgVol20,
                            Is52WeekHigh = price >= 0.90m * high52W[idx],
                            BuyScore = buyScore,
                            SellScore = sellScore,
                            BuySignal = buySignal,
                            SellSignal = sellSignal,
                            Recommendation = recommendation,
                            Reason = reason
                        });
                }
            }
        }

        _logger.LogInformation("Backfill of historical analyses completed!");
        UpdateJobProgress("backfill", false, 100, "Historical 2-year backtest completed successfully!");
    }


    private static ConditionChecklistDto BuildConditionChecklist(
        NiftyStatusDto niftyStatus,
        decimal close,
        decimal ema20,
        decimal ema50,
        decimal ema200,
        decimal rsi14,
        decimal macd,
        decimal macdSignal,
        decimal adx14,
        decimal volumeMultiplier,
        long volume,
        decimal avgVolume20,
        bool is52WeekHigh,
        bool isLastCandleBullish,
        string reason = "")
    {
        bool isMarketMet = niftyStatus != null && niftyStatus.IsMarketFilterPassed;
        bool isPriceTrendMet = close > ema20 && ema20 > ema50 && ema50 > ema200;
        bool isVolSpikeMet = volumeMultiplier >= 1.5m;
        bool isRsiMet = rsi14 >= 55m && rsi14 <= 70m;
        bool isAdxMet = adx14 > 25m;
        bool isMacdMet = macd > macdSignal;
        bool is52WMet = is52WeekHigh;
        bool isCandleMet = isLastCandleBullish;

        var conditions = new List<ConditionItemDto>
        {
            new ConditionItemDto("MARKET_FILTER", "Nifty Market Filter", "Nifty 50 Close > 50 DMA & EMA20 > EMA50",
                isMarketMet ? "Passed (Close > SMA50 & EMA20 > EMA50)" : "Failed", "Nifty Close > 50 DMA & EMA20 > EMA50", isMarketMet),

            new ConditionItemDto("PRICE_TREND", "Stock Price Trend", "Close > EMA20 > EMA50 > EMA200 (Sustained Uptrend)",
                isPriceTrendMet ? $"Met ({close:F2} > {ema20:F1} > {ema50:F1} > {ema200:F1})" : $"Close:{close:F2}, EMA20:{ema20:F1}, EMA50:{ema50:F1}, EMA200:{ema200:F1}",
                "Close > EMA20 > EMA50 > EMA200", isPriceTrendMet),

            new ConditionItemDto("VOL_SPIKE", "Volume Spike", "Daily Volume >= 1.5x of 20-Day Average Volume",
                $"{volumeMultiplier:F1}x ({(volume / 100000m):F1}L vs Avg {(avgVolume20 / 100000m):F1}L)",
                ">= 1.5x Avg Volume", isVolSpikeMet),

            new ConditionItemDto("RSI_ZONE", "RSI Momentum Zone", "14-Period RSI between 55 and 70",
                $"{rsi14:F1}", "55.0 - 70.0", isRsiMet),

            new ConditionItemDto("ADX_ZONE", "ADX Trend Strength", "14-Period ADX > 25 (Strong Trend)",
                $"{adx14:F1}", "> 25.0", isAdxMet),

            new ConditionItemDto("MACD_BULLISH", "MACD Bullish Signal", "MACD Line above Signal Line",
                $"MACD: {macd:F2}, Signal: {macdSignal:F2}", "MACD > Signal", isMacdMet),

            new ConditionItemDto("NEAR_52W", "52-Week High Proximity", "Close Price within 10% of 52-Week High",
                is52WMet ? "Within 10% of 52W High" : "Below 10% threshold", "Close >= 90% of 52W High", is52WMet),

            new ConditionItemDto("BULLISH_CANDLE", "Bullish Candle", "Daily Close Price > Daily Open Price",
                isCandleMet ? "Bullish (Close > Open)" : "Bearish / Neutral", "Close > Open", isCandleMet)
        };

        if (!string.IsNullOrEmpty(reason) && reason.Contains("60m Filter"))
        {
            bool is60mMet = !reason.Contains("Failed factors") || !reason.Contains("60m Filter");
            conditions.Add(new ConditionItemDto("HOURLY_60M", "60m Hourly Trend Filter", "Hourly Close > EMA20 and Hourly RSI between 40-65",
                is60mMet ? "Passed" : "Pending", "60m Close > EMA20 & RSI 40-65", is60mMet));
        }

        int metCount = conditions.Count(c => c.IsMet);
        return new ConditionChecklistDto(metCount, conditions.Count, conditions);
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SwingJobStatusDto> _jobStatuses = new();

    public SwingJobStatusDto GetJobStatus(string jobType)
    {
        string key = (jobType ?? "backfill").ToLowerInvariant();
        if (_jobStatuses.TryGetValue(key, out var status))
        {
            return status;
        }
        return new SwingJobStatusDto(key, false, 0, "Idle", null, null, null);
    }

    public bool IsJobRunning(string jobType)
    {
        var status = GetJobStatus(jobType);
        return status.IsRunning;
    }

    public void UpdateJobProgress(string jobType, bool running, int progress, string message, string? error = null)
    {
        string key = (jobType ?? "backfill").ToLowerInvariant();
        _jobStatuses.AddOrUpdate(
            key,
            new SwingJobStatusDto(key, running, progress, message, DateTime.UtcNow, running ? null : DateTime.UtcNow, error),
            (k, old) => new SwingJobStatusDto(key, running, progress, message, old.StartedAt ?? DateTime.UtcNow, running ? null : DateTime.UtcNow, error)
        );
    }

    private static DateTime ConvertToDateTime(object value)
    {
        if (value is null) return DateTime.MinValue;
        if (value is DateTime dt) return dt;
        if (value is DateOnly d) return d.ToDateTime(TimeOnly.MinValue);
        if (DateTime.TryParse(Convert.ToString(value), out var parsed)) return parsed;
        return DateTime.MinValue;
    }
}

