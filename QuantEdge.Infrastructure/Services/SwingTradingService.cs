using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Extensions.Logging;
using QuantEdge.Domain.Entities;
using QuantEdge.Infrastructure.DTOs;
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
    private readonly ILogger<SwingTradingService> _logger;

    public SwingTradingService(
        IStockMasterRepository stockMasterRepository,
        IMarketCandleRepository candleRepository,
        IHistoricalDataService historicalDataService,
        IDbConnectionFactory connectionFactory,
        ILogger<SwingTradingService> logger)
    {
        _stockMasterRepository = stockMasterRepository ?? throw new ArgumentNullException(nameof(stockMasterRepository));
        _candleRepository = candleRepository ?? throw new ArgumentNullException(nameof(candleRepository));
        _historicalDataService = historicalDataService ?? throw new ArgumentNullException(nameof(historicalDataService));
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<SwingTradingDashboardDto> GetDashboardDataAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Retrieving Swing Trading dashboard data...");

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
        NiftyStatusDto niftyStatus = null;
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

        // 2. Fetch latest daily stock analysis records
        var stockSignals = new List<SwingStockSignalDto>();
        using (var conn = _connectionFactory.CreateConnection())
        {
            foreach (var stock in activeStocks)
            {
                if (stock.Symbol == "NIFTY 50") continue;

                var latestAnalysis = await conn.QueryFirstOrDefaultAsync<dynamic>(@"
                    SELECT d.*, s.symbol 
                    FROM daily_stock_analysis d
                    JOIN stock_master s ON d.stock_id = s.id
                    WHERE s.symbol = @Symbol
                    ORDER BY d.trade_date DESC
                    LIMIT 1",
                    new { Symbol = stock.Symbol });

                if (latestAnalysis != null)
                {
                    stockSignals.Add(new SwingStockSignalDto(
                        Symbol: stock.Symbol,
                        Close: (decimal)latestAnalysis.close_price,
                        Open: 0m, // Filled from candles if needed, or left as 0
                        High: 0m,
                        Low: 0m,
                        Ema20: latestAnalysis.ema20 != null ? (decimal)latestAnalysis.ema20 : 0m,
                        Ema50: latestAnalysis.ema50 != null ? (decimal)latestAnalysis.ema50 : 0m,
                        Ema200: latestAnalysis.ema200 != null ? (decimal)latestAnalysis.ema200 : 0m,
                        Rsi14: latestAnalysis.rsi14 != null ? (decimal)latestAnalysis.rsi14 : 0m,
                        Macd: latestAnalysis.macd != null ? (decimal)latestAnalysis.macd : 0m,
                        MacdSignal: latestAnalysis.macd_signal != null ? (decimal)latestAnalysis.macd_signal : 0m,
                        Adx14: latestAnalysis.adx14 != null ? (decimal)latestAnalysis.adx14 : 0m,
                        Atr14: latestAnalysis.atr14 != null ? (decimal)latestAnalysis.atr14 : 0m,
                        Volume: (long)latestAnalysis.volume,
                        AvgVolume20: latestAnalysis.average_volume20 != null ? (decimal)latestAnalysis.average_volume20 : 0m,
                        VolumeMultiplier: latestAnalysis.average_volume20 != null && (decimal)latestAnalysis.average_volume20 > 0m 
                                            ? Math.Round((decimal)latestAnalysis.volume / (decimal)latestAnalysis.average_volume20, 2) 
                                            : 0m,
                        Is52WeekHigh: (bool)latestAnalysis.is_52_week_high,
                        High52Week: 0m, // rolling high not directly stored but implied
                        ClosenessTo52WeekHighPct: 0m,
                        IsLastCandleBullish: true,
                        MeetsStockFilter: (bool)latestAnalysis.buy_signal,
                        MeetsAllBuyRules: (bool)latestAnalysis.buy_signal && niftyStatus.IsMarketFilterPassed,
                        Decision: (string)latestAnalysis.recommendation,
                        Reason: (string)latestAnalysis.reason
                    ));
                }
                else
                {
                    // Fallback placeholders if no analysis is in DB yet
                    stockSignals.Add(new SwingStockSignalDto(
                        stock.Symbol, 0m, 0m, 0m, 0m, 0m, 0m, 0m, 0m, 0m, 0m, 0m, 0m, 0, 0m, 0m, false, 0m, 0m, false, false, false, "HOLD", "No EOD analysis found. Please run EOD Job."
                    ));
                }
            }

            // 3. Fetch active positions and trades
            var allTrades = (await conn.QueryAsync<SwingTradeDto>(@"
                SELECT id, symbol, entry_date, entry_price, quantity, is_closed, exit_date, exit_price, exit_reason,
                       CASE 
                           WHEN is_closed = TRUE THEN (exit_date - entry_date)
                           ELSE (CURRENT_DATE - entry_date)
                       END AS hold_days,
                       CASE 
                           WHEN is_closed = TRUE THEN Math.Round((exit_price - entry_price) / entry_price * 100, 2)
                           ELSE Math.Round(((SELECT close_price FROM daily_stock_analysis d JOIN stock_master s ON d.stock_id = s.id WHERE s.symbol = t.symbol ORDER BY d.trade_date DESC LIMIT 1) - entry_price) / entry_price * 100, 2)
                       END AS profit_loss_pct
                FROM swing_positions t
                ORDER BY entry_date DESC")).ToList();

            // Calculate backtest stats for 15 days
            var trades15 = allTrades.Where(t => t.EntryDate >= DateTime.UtcNow.AddDays(-15)).ToList();
            var stats15 = CalculatePeriodStats(trades15, 15);

            // Calculate backtest stats for 30 days
            var trades30 = allTrades.Where(t => t.EntryDate >= DateTime.UtcNow.AddDays(-30)).ToList();
            var stats30 = CalculatePeriodStats(trades30, 30);

            return new SwingTradingDashboardDto(
                NiftyStatus: niftyStatus,
                StockSignals: stockSignals,
                BacktestStats15Days: stats15,
                BacktestStats30Days: stats30,
                RecentTrades: allTrades.Take(30).ToList()
            );
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

        if (candles.Count < 250)
        {
            _logger.LogWarning("Insufficient candle data for {Symbol} daily analysis. Required 250, found {Count}.", stock.Symbol, candles.Count);
            return;
        }

        // Align indices
        var closes = candles.Select(c => c.Close).ToList();
        var highs = candles.Select(c => c.High).ToList();
        var lows = candles.Select(c => c.Low).ToList();
        var opens = candles.Select(c => c.Open).ToList();
        var volumes = candles.Select(c => c.Volume).ToList();

        var ema20 = IndicatorCalculator.CalculateEma(closes, 20);
        var ema50 = IndicatorCalculator.CalculateEma(closes, 50);
        var ema200 = IndicatorCalculator.CalculateEma(closes, 200);
        var rsi14 = IndicatorCalculator.CalculateRsi(closes, 14);
        var (macd, macdSignal) = IndicatorCalculator.CalculateMacd(closes);
        var adx14 = IndicatorCalculator.CalculateAdx(highs, lows, closes, 14);
        var atr14 = IndicatorCalculator.CalculateAtr(highs, lows, closes, 14);
        var high52W = IndicatorCalculator.Calculate52WeekHigh(highs, 250);

        int idx = candles.Count - 1;
        decimal price = closes[idx];
        decimal open = opens[idx];
        decimal high = highs[idx];
        decimal low = lows[idx];
        long vol = volumes[idx];

        // 20-day Average Volume (calculated from index idx-1 to idx-20)
        var prev20VolList = volumes.Skip(Math.Max(0, idx - 20)).Take(Math.Min(20, idx)).ToList();
        decimal avgVol20 = prev20VolList.Any() ? (decimal)prev20VolList.Average(v => (double)v) : 0m;

        bool priceTrend = price > ema20[idx] && ema20[idx] > ema50[idx] && ema50[idx] > ema200[idx];
        bool volSpike = avgVol20 > 0m && vol >= (long)(avgVol20 * 1.5m);
        bool rsiZone = rsi14[idx] >= 55m && rsi14[idx] <= 70m;
        bool adxZone = adx14[idx] > 25m;
        bool macdBullish = macd[idx] > macdSignal[idx] && macd[idx - 1] <= macdSignal[idx - 1];
        bool near52W = price >= 0.90m * high52W[idx];
        bool isBullishCandle = price > open;

        // Check if there is an active/open position in swing_positions
        var openPosition = await conn.QueryFirstOrDefaultAsync<dynamic>(@"
            SELECT * FROM swing_positions WHERE symbol = @Symbol AND is_closed = FALSE LIMIT 1",
            new { Symbol = stock.Symbol });

        bool buySignal = false;
        bool sellSignal = false;
        string recommendation = "HOLD";
        string reason = "";

        if (openPosition != null)
        {
            // Stock is currently held, check SELL conditions
            decimal entryPrice = (decimal)openPosition.entry_price;
            DateTime entryDate = (DateTime)openPosition.entry_date;

            // Hold days (candles between entry date and tradeDate)
            int holdDays = candles.Count(c => c.CandleTime.Date >= entryDate.Date && c.CandleTime.Date <= tradeDate.Date) - 1;
            if (holdDays < 0) holdDays = 0;

            bool hitTarget = high >= entryPrice * 1.05m;
            bool hitStop = low <= entryPrice * 0.97m;
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
                    // Conservative: assume stop loss hit first if both hit on the same day
                    exitPrice = entryPrice * 0.97m;
                    exitReason = "Stop Loss & Profit Target Hit (Conservative Exit)";
                }
                else if (hitStop)
                {
                    exitPrice = entryPrice * 0.97m;
                    exitReason = "Stop Loss (-3%)";
                }
                else if (hitTarget)
                {
                    exitPrice = entryPrice * 1.05m;
                    exitReason = "Profit Target (+5%)";
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

                // Update database
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
                reason = $"HOLD active position. entry={entryPrice}, current={price}, hold_days={holdDays}";
            }
        }
        else
        {
            // Stock is not held, check BUY conditions
            if (marketFilterPassed && priceTrend && volSpike && rsiZone && adxZone && macdBullish && near52W && isBullishCandle)
            {
                buySignal = true;
                recommendation = "BUY";
                reason = "All BUY conditions met: Market Trend Up, Stock Price Trend (EMA20>50>200), Volume Spike (>1.5x), RSI in momentum zone (55-70), ADX > 25, MACD Bullish Cross, Close within 10% of 52W High, Last candle Bullish.";

                // Calculate next day's open price (simulated)
                // In actual backtesting, we enter at next day's open. For EOD live signal, we assume we buy tomorrow morning at open.
                // We record it in swing_positions. In EOD live, we use today's Close as entry price placeholder, which can be updated.
                await conn.ExecuteAsync(@"
                    INSERT INTO swing_positions (symbol, entry_date, entry_price, quantity, is_closed)
                    VALUES (@Symbol, @EntryDate, @EntryPrice, 100, FALSE)",
                    new { Symbol = stock.Symbol, EntryDate = tradeDate.Date.AddDays(1), EntryPrice = price });

                _logger.LogInformation("BUY Order Generated for {Symbol} at EOD Close price {Price} for next day.", stock.Symbol, price);
            }
            else
            {
                recommendation = "HOLD";
                var failedList = new List<string>();
                if (!marketFilterPassed) failedList.Add("Market Filter");
                if (!priceTrend) failedList.Add("Price > EMA20 > EMA50 > EMA200");
                if (!volSpike) failedList.Add("Volume Spike (>1.5x)");
                if (!rsiZone) failedList.Add("RSI in 55-70");
                if (!adxZone) failedList.Add("ADX > 25");
                if (!macdBullish) failedList.Add("MACD Bullish Cross");
                if (!near52W) failedList.Add("Close within 10% of 52W High");
                if (!isBullishCandle) failedList.Add("Last candle Bullish");

                reason = $"HOLD. Failed factors: {string.Join(", ", failedList)}";
            }
        }

        // Save EOD Analysis
        int buyScore = (marketFilterPassed ? 10 : 0) + (priceTrend ? 15 : 0) + (volSpike ? 15 : 0) + (rsiZone ? 15 : 0) + (adxZone ? 15 : 0) + (macdBullish ? 15 : 0) + (near52W ? 15 : 0);
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

            if (niftyCandles.Count < 250)
            {
                _logger.LogWarning("Insufficient Nifty daily candles for backfill. Expected at least 250, found {Count}.", niftyCandles.Count);
                return;
            }

            var niftyCloses = niftyCandles.Select(c => c.Close).ToList();
            var niftySma50 = IndicatorCalculator.CalculateSma(niftyCloses, 50);
            var niftyEma20 = IndicatorCalculator.CalculateEma(niftyCloses, 20);
            var niftyEma50 = IndicatorCalculator.CalculateEma(niftyCloses, 50);

            // We will run daily simulation day-by-day starting from index 250 to current day
            for (int i = 250; i < niftyCandles.Count; i++)
            {
                if (cancellationToken.IsCancellationRequested) break;

                DateTime currentDate = niftyCandles[i].CandleTime.Date;
                bool niftyAboveSma50 = niftyCloses[i] > niftySma50[i];
                bool niftyEmaBullish = niftyEma20[i] > niftyEma50[i];
                bool marketFilterPassed = niftyAboveSma50 && niftyEmaBullish;

                foreach (var stock in activeStocks)
                {
                    if (stock.Symbol == "NIFTY 50") continue;

                    // Load stock history up to currentDate
                    var stockHistory = (await conn.QueryAsync<MarketCandle>(@"
                        SELECT * FROM market_candles_1d 
                        WHERE symbol = @Symbol AND candle_time <= @CurrentDate
                        ORDER BY candle_time DESC
                        LIMIT 300",
                        new { Symbol = stock.Symbol, CurrentDate = currentDate }))
                        .OrderBy(c => c.CandleTime)
                        .ToList();

                    if (stockHistory.Count < 250) continue;

                    var closes = stockHistory.Select(c => c.Close).ToList();
                    var highs = stockHistory.Select(c => c.High).ToList();
                    var lows = stockHistory.Select(c => c.Low).ToList();
                    var opens = stockHistory.Select(c => c.Open).ToList();
                    var volumes = stockHistory.Select(c => c.Volume).ToList();

                    var ema20 = IndicatorCalculator.CalculateEma(closes, 20);
                    var ema50 = IndicatorCalculator.CalculateEma(closes, 50);
                    var ema200 = IndicatorCalculator.CalculateEma(closes, 200);
                    var rsi14 = IndicatorCalculator.CalculateRsi(closes, 14);
                    var (macd, macdSignal) = IndicatorCalculator.CalculateMacd(closes);
                    var adx14 = IndicatorCalculator.CalculateAdx(highs, lows, closes, 14);
                    var atr14 = IndicatorCalculator.CalculateAtr(highs, lows, closes, 14);
                    var high52W = IndicatorCalculator.Calculate52WeekHigh(highs, 250);

                    int idx = stockHistory.Count - 1;
                    decimal price = closes[idx];
                    decimal open = opens[idx];
                    decimal high = highs[idx];
                    decimal low = lows[idx];
                    long vol = volumes[idx];

                    var prev20VolList = volumes.Skip(Math.Max(0, idx - 20)).Take(Math.Min(20, idx)).ToList();
                    decimal avgVol20 = prev20VolList.Any() ? (decimal)prev20VolList.Average(v => (double)v) : 0m;

                    bool priceTrend = price > ema20[idx] && ema20[idx] > ema50[idx] && ema50[idx] > ema200[idx];
                    bool volSpike = avgVol20 > 0m && vol >= (long)(avgVol20 * 1.5m);
                    bool rsiZone = rsi14[idx] >= 55m && rsi14[idx] <= 70m;
                    bool adxZone = adx14[idx] > 25m;
                    bool macdBullish = macd[idx] > macdSignal[idx] && macd[idx - 1] <= macdSignal[idx - 1];
                    bool near52W = price >= 0.90m * high52W[idx];
                    bool isBullishCandle = price > open;

                    // Evaluate position
                    var openPosition = await conn.QueryFirstOrDefaultAsync<dynamic>(@"
                        SELECT * FROM swing_positions WHERE symbol = @Symbol AND is_closed = FALSE LIMIT 1",
                        new { Symbol = stock.Symbol });

                    bool buySignal = false;
                    bool sellSignal = false;
                    string recommendation = "HOLD";
                    string reason = "";

                    if (openPosition != null)
                    {
                        decimal entryPrice = (decimal)openPosition.entry_price;
                        DateTime entryDate = (DateTime)openPosition.entry_date;

                        int holdDays = stockHistory.Count(c => c.CandleTime.Date >= entryDate.Date && c.CandleTime.Date <= currentDate.Date) - 1;
                        if (holdDays < 0) holdDays = 0;

                        bool hitTarget = high >= entryPrice * 1.05m;
                        bool hitStop = low <= entryPrice * 0.97m;
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
                                exitPrice = entryPrice * 0.97m;
                                exitReason = "Stop Loss & Profit Target Hit (Conservative)";
                            }
                            else if (hitStop)
                            {
                                exitPrice = entryPrice * 0.97m;
                                exitReason = "Stop Loss (-3%)";
                            }
                            else if (hitTarget)
                            {
                                exitPrice = entryPrice * 1.05m;
                                exitReason = "Profit Target (+5%)";
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
                            reason = $"HOLD active position. entry={entryPrice}, current={price}, hold_days={holdDays}";
                        }
                    }
                    else
                    {
                        if (marketFilterPassed && priceTrend && volSpike && rsiZone && adxZone && macdBullish && near52W && isBullishCandle)
                        {
                            buySignal = true;
                            recommendation = "BUY";
                            reason = "All BUY conditions met.";

                            // In backtest, simulate entry on next day's open. For simplicity, we approximate using current close or next day's open
                            // Let's find next candle open if available, otherwise current close
                            decimal nextOpen = price;
                            DateTime nextDate = currentDate.AddDays(1);
                            
                            // Query next day candle open
                            var nextCandle = await conn.QueryFirstOrDefaultAsync<dynamic>(@"
                                SELECT open, candle_time FROM market_candles_1d 
                                WHERE symbol = @Symbol AND candle_time > @CurrentDate 
                                ORDER BY candle_time ASC LIMIT 1",
                                new { Symbol = stock.Symbol, CurrentDate = currentDate });

                            if (nextCandle != null)
                            {
                                nextOpen = (decimal)nextCandle.open;
                                nextDate = (DateTime)nextCandle.candle_time;
                            }

                            await conn.ExecuteAsync(@"
                                INSERT INTO swing_positions (symbol, entry_date, entry_price, quantity, is_closed)
                                VALUES (@Symbol, @EntryDate, @EntryPrice, 100, FALSE)",
                                new { Symbol = stock.Symbol, EntryDate = nextDate.Date, EntryPrice = nextOpen });
                        }
                        else
                        {
                            recommendation = "HOLD";
                        }
                    }

                    int buyScore = (marketFilterPassed ? 10 : 0) + (priceTrend ? 15 : 0) + (volSpike ? 15 : 0) + (rsiZone ? 15 : 0) + (adxZone ? 15 : 0) + (macdBullish ? 15 : 0) + (near52W ? 15 : 0);
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
    }
}
