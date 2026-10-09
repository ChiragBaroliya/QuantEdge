using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Dapper;
using KiteConnect;
using QuantEdge.Domain.Entities;
using QuantEdge.Infrastructure.Interfaces;
using QuantEdge.Infrastructure.Configurations;
using QuantEdge.Infrastructure.Persistence;
using QuantEdge.Infrastructure.Persistence.Repositories;

namespace QuantEdge.Infrastructure.Services;

/// <summary>
/// A production-ready historical data service that retrieves historical candles 
/// from the Zerodha Kite Connect REST API and stores them in PostgreSQL.
/// Dynamically fetches instrument mappings from the database.
/// </summary>
public class ZerodhaHistoricalDataService : IHistoricalDataService
{
    private readonly BrokerConfig _config;
    private readonly IMarketCandleRepository _candleRepository;
    private readonly IDbConnectionFactory _connectionFactory;
    private readonly IStockMasterRepository _stockMasterRepository;
    private readonly IIndicatorService _indicatorService;
    private readonly ICacheService? _cacheService;
    private readonly ILogger<ZerodhaHistoricalDataService> _logger;
    private readonly TimeZoneInfo _indianTimeZone;
    private readonly IBrokerApiEventRecorder? _apiEventRecorder;

    public ZerodhaHistoricalDataService(
        IOptions<BrokerConfig> config,
        IMarketCandleRepository candleRepository,
        IDbConnectionFactory connectionFactory,
        IStockMasterRepository stockMasterRepository,
        IIndicatorService indicatorService,
        ICacheService? cacheService = null,
        ILogger<ZerodhaHistoricalDataService> logger = null!,
        IBrokerApiEventRecorder? apiEventRecorder = null)
    {
        _apiEventRecorder = apiEventRecorder;
        _config = config?.Value ?? throw new ArgumentNullException(nameof(config));
        _candleRepository = candleRepository ?? throw new ArgumentNullException(nameof(candleRepository));
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _stockMasterRepository = stockMasterRepository ?? throw new ArgumentNullException(nameof(stockMasterRepository));
        _indicatorService = indicatorService ?? throw new ArgumentNullException(nameof(indicatorService));
        _cacheService = cacheService;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _indianTimeZone = Helpers.TimeZoneHelper.IndianTimeZone;
    }

    /// <summary>
    /// Fetches historical candles from Zerodha and inserts them into PostgreSQL.
    /// </summary>
    public async Task<IEnumerable<MarketCandle>> FetchHistoricalCandlesAsync(
        string symbol, 
        string timeframe, 
        DateTime fromTime, 
        DateTime toTime, 
        CancellationToken cancellationToken)
    {
        // 1. Dynamic lookup in stock_master database table
        var stock = await _stockMasterRepository.GetBySymbolAsync(symbol);
        if (stock == null || !stock.IsActive)
        {
            _logger.LogWarning("Symbol {Symbol} is not configured or active in StockMaster. Historical fetch skipped.", symbol);
            return Enumerable.Empty<MarketCandle>();
        }

        uint instrumentToken = (uint)stock.InstrumentToken;

        _logger.LogInformation("Resolving active Zerodha session token...");
        string? token = await ResolveAccessTokenAsync();

        // A token Kite already rejected is not tried again for every remaining stock - that only produced one failed
        // call and one bell notification per stock. It is retried as soon as zerodha_sessions holds a different token.
        if (token != null && token == _rejectedToken)
        {
            _apiEventRecorder?.RecordFailure(BrokerApiSource.Session, "historical sync",
                "Candle sync paused: Zerodha rejected the access token (invalid / expired). Log in to Zerodha again (Token page) - sync resumes automatically with the new token.",
                level: "warning");
            throw new InvalidOperationException("Zerodha access token was rejected - waiting for a new login.");
        }

        string intervalStr = MapTimeframeToKite(timeframe);
        int maxDays = GetMaxDaysForInterval(timeframe);
        
        bool isIntraday = !timeframe.Equals("1d", StringComparison.OrdinalIgnoreCase);

        // Adjust start and end bounds to Indian Stock Market Trading Hours (09:15 AM to 03:30 PM IST) for intraday timeframes
        DateTime adjustedFromUtc = fromTime.Kind == DateTimeKind.Utc ? fromTime : DateTime.SpecifyKind(fromTime, DateTimeKind.Utc);
        DateTime adjustedToUtc = toTime.Kind == DateTimeKind.Utc ? toTime : DateTime.SpecifyKind(toTime, DateTimeKind.Utc);

        if (isIntraday)
        {
            DateTime fromIst = TimeZoneInfo.ConvertTimeFromUtc(adjustedFromUtc, _indianTimeZone);
            DateTime toIst = TimeZoneInfo.ConvertTimeFromUtc(adjustedToUtc, _indianTimeZone);

            DateTime fromDate = fromIst.Date;
            DateTime toDate = (toIst.TimeOfDay < new TimeSpan(9, 15, 0)) ? toIst.Date.AddDays(-1) : toIst.Date;

            DateTime marketStartIst = fromDate.Add(new TimeSpan(9, 15, 0));
            DateTime marketEndIst = toDate.Add(new TimeSpan(15, 30, 0));

            adjustedFromUtc = TimeZoneInfo.ConvertTimeToUtc(marketStartIst, _indianTimeZone);
            adjustedToUtc = TimeZoneInfo.ConvertTimeToUtc(marketEndIst, _indianTimeZone);
        }

        var savedCandles = new List<MarketCandle>();
        DateTime currentStart = adjustedFromUtc;

        _logger.LogInformation("Initiating chunked historical fetch for token {Token} ({Symbol}) from {From} to {To} (Interval: {Interval}, Max Days/Chunk: {MaxDays}, Market Hours: 09:15-15:30 IST)", 
            instrumentToken, symbol, adjustedFromUtc, adjustedToUtc, intervalStr, maxDays);

        try
        {
            var kite = new Kite(_config.ApiKey);
            kite.SetAccessToken(token);

            while (currentStart < adjustedToUtc)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                DateTime rawEndUtc = currentStart.AddDays(maxDays);
                DateTime currentEnd = rawEndUtc;

                if (isIntraday)
                {
                    DateTime rawEndIst = TimeZoneInfo.ConvertTimeFromUtc(rawEndUtc, _indianTimeZone);
                    DateTime marketEndIst = rawEndIst.Date.Add(new TimeSpan(15, 30, 0));
                    currentEnd = TimeZoneInfo.ConvertTimeToUtc(marketEndIst, _indianTimeZone);
                }

                if (currentEnd > adjustedToUtc)
                {
                    currentEnd = adjustedToUtc;
                }

                DateTime currentStartIst = TimeZoneInfo.ConvertTimeFromUtc(currentStart, _indianTimeZone);
                DateTime currentEndIst = TimeZoneInfo.ConvertTimeFromUtc(currentEnd, _indianTimeZone);

                _logger.LogInformation("Requesting chunk: {Symbol} from {FromIst:yyyy-MM-dd HH:mm} to {ToIst:yyyy-MM-dd HH:mm} IST", 
                    symbol, currentStartIst, currentEndIst);

                await WaitForHistoricalRateSlotAsync(cancellationToken);
                List<Historical> historicalList = await Task.Run(() =>
                    kite.GetHistoricalData(
                        InstrumentToken: instrumentToken.ToString(),
                        FromDate: currentStartIst,
                        ToDate: currentEndIst,
                        Interval: intervalStr
                    ), cancellationToken);

                if (historicalList != null && historicalList.Any())
                {
                    var chunkCandles = new List<MarketCandle>();
                    foreach (var record in historicalList)
                    {
                        if (cancellationToken.IsCancellationRequested)
                            break;

                        // Convert record.TimeStamp to UTC and then to IST for accurate market hour validation
                        DateTime candleUtc = record.TimeStamp.Kind == DateTimeKind.Utc
                            ? record.TimeStamp
                            : record.TimeStamp.Kind == DateTimeKind.Local
                                ? record.TimeStamp.ToUniversalTime()
                                : TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(record.TimeStamp, DateTimeKind.Unspecified), _indianTimeZone);

                        DateTime candleIst = TimeZoneInfo.ConvertTimeFromUtc(candleUtc, _indianTimeZone);

                        // Restrict intraday candles strictly to Indian market trading hours (09:15 AM to 03:30 PM IST)
                        if (isIntraday)
                        {
                            TimeSpan tod = candleIst.TimeOfDay;

                            if (tod < new TimeSpan(9, 15, 0) || tod > new TimeSpan(15, 30, 0))
                            {
                                continue; // Skip pre-market / post-market / out-of-hours candles
                            }
                        }

                        int deterministicId = GenerateDeterministicIntId(symbol, timeframe, candleUtc);

                        chunkCandles.Add(new MarketCandle
                        {
                            Id = deterministicId,
                            Symbol = symbol.ToUpper(),
                            Timeframe = timeframe,
                            Open = record.Open,
                            High = record.High,
                            Low = record.Low,
                            Close = record.Close,
                            Volume = (long)record.Volume,
                            CandleTime = candleUtc,
                            CreatedAt = DateTime.UtcNow
                        });
                    }

                    if (chunkCandles.Any())
                    {
                        await _candleRepository.InsertBatchAsync(chunkCandles);
                        savedCandles.AddRange(chunkCandles);
                    }
                }

                // Move forward to next start bound
                if (isIntraday)
                {
                    DateTime nextStartIst = currentEndIst.Date.AddDays(1).Add(new TimeSpan(9, 15, 0));
                    currentStart = TimeZoneInfo.ConvertTimeToUtc(nextStartIst, _indianTimeZone);
                }
                else
                {
                    currentStart = currentEnd;
                }

                // Add minor rate-limiting delay between sequential chunks to respect 3 requests/sec API rate limit
                if (currentStart < adjustedToUtc)
                {
                    await Task.Delay(350, cancellationToken);
                }
            }

            _logger.LogInformation("Successfully completed chunked fetch. Saved {Count} total candles for {Symbol} ({Timeframe}) from Zerodha.", 
                savedCandles.Count, symbol, timeframe);
            return savedCandles;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while fetching or saving Zerodha historical candles for {Symbol} at chunk range starting {CurrentStart}.", symbol, currentStart);
            if (IsTokenRejected(ex))
            {
                // Not a problem with this stock: the token itself is bad. Forget it (so the next call re-reads
                // zerodha_sessions) and raise ONE notification for the whole sync instead of one per stock.
                _rejectedToken = token;
                if (_cacheService != null) await _cacheService.RemoveAsync(TokenCacheKey);
                _apiEventRecorder?.RecordFailure(BrokerApiSource.Session, "historical sync",
                    $"Candle sync paused: Zerodha rejected the access token ({ex.Message}). Log in to Zerodha again (Token page) - sync resumes automatically with the new token.",
                    level: "warning");
            }
            else if (ex is not OperationCanceledException)
            {
                bool rateLimited = ex.Message.Contains("Too many requests", StringComparison.OrdinalIgnoreCase)
                    || ex.Message.Contains("429", StringComparison.Ordinal);
                _apiEventRecorder?.RecordFailure(BrokerApiSource.Historical, $"historical {timeframe}",
                    rateLimited
                        ? $"RATE LIMITED by Zerodha while syncing {timeframe} candles: {ex.Message}"
                        : $"Candle sync failed ({timeframe}) - this stock's data may be out of date: {ex.Message}",
                    httpStatus: rateLimited ? 429 : null, symbol: symbol);
            }
            throw;
        }
    }

    // The access token changes every day: Zerodha expires it at 06:00 IST and a new login stores a new one in
    // zerodha_sessions. This service runs inside long-lived workers (e.g. the swing intraday job), so the token is only
    // cached for a few minutes - it used to be cached for 24 hours, which kept yesterday's token after the morning login
    // and failed every candle sync of the day with "invalid token".
    private static readonly TimeSpan TokenCacheTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan DailyTokenExpiryIst = new(6, 0, 0);

    // The last token Kite rejected (per process) - see FetchHistoricalCandlesAsync.
    private static string? _rejectedToken;

    private string TokenCacheKey => $"zerodha_session_token_{_config.ApiKey}";

    /// <summary>
    /// The newest active token for the configured API key, if it is still within Zerodha's validity window (created
    /// after the most recent 06:00 IST). Records one warning and throws when there is none.
    /// </summary>
    private async Task<string> ResolveAccessTokenAsync()
    {
        if (_cacheService != null)
        {
            string? cached = await _cacheService.GetAsync<string>(TokenCacheKey);
            if (!string.IsNullOrWhiteSpace(cached)) return cached;
        }

        SessionTokenRow? session = null;
        try
        {
            using var conn = _connectionFactory.CreateConnection();
            session = await conn.QueryFirstOrDefaultAsync<SessionTokenRow>(
                "SELECT access_token AS AccessToken, created_at AS CreatedAt FROM zerodha_sessions WHERE api_key = @ApiKey AND is_active = TRUE ORDER BY created_at DESC LIMIT 1;",
                new { ApiKey = _config.ApiKey });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to resolve active AccessToken from the database.");
        }

        if (session == null || string.IsNullOrWhiteSpace(session.AccessToken))
        {
            _logger.LogError("Zerodha AccessToken is missing. Cannot fetch historical data.");
            _apiEventRecorder?.RecordFailure(BrokerApiSource.Session, "historical sync",
                "Historical candle sync skipped: no Zerodha access token. Log in to Zerodha (Token page).", level: "warning");
            throw new InvalidOperationException("Zerodha AccessToken is missing.");
        }

        DateTime createdIst = TimeZoneInfo.ConvertTimeFromUtc(
            session.CreatedAt.Kind == DateTimeKind.Local ? session.CreatedAt.ToUniversalTime() : DateTime.SpecifyKind(session.CreatedAt, DateTimeKind.Utc),
            _indianTimeZone);
        if (!IsTokenFromCurrentSession(createdIst, TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, _indianTimeZone)))
        {
            _logger.LogWarning("Zerodha AccessToken was created {CreatedIst:dd-MMM HH:mm} IST and has expired (06:00 IST). Cannot fetch historical data.", createdIst);
            _apiEventRecorder?.RecordFailure(BrokerApiSource.Session, "historical sync",
                $"Historical candle sync skipped: the Zerodha token is from {createdIst:dd-MMM HH:mm} IST and expired at 06:00 IST. Log in to Zerodha (Token page).",
                level: "warning");
            throw new InvalidOperationException("Zerodha AccessToken has expired.");
        }

        if (_cacheService != null)
        {
            await _cacheService.SetAsync(TokenCacheKey, session.AccessToken, TokenCacheTtl);
        }
        return session.AccessToken!;
    }

    private sealed class SessionTokenRow
    {
        public string? AccessToken { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    /// <summary>A Zerodha token is valid until the next 06:00 IST after it was created.</summary>
    internal static bool IsTokenFromCurrentSession(DateTime createdIst, DateTime nowIst)
    {
        DateTime lastExpiry = nowIst.Date.Add(DailyTokenExpiryIst);
        if (nowIst < lastExpiry) lastExpiry = lastExpiry.AddDays(-1);
        return createdIst >= lastExpiry;
    }

    private static bool IsTokenRejected(Exception ex) =>
        ex is TokenException
        || ex.Message.Contains("invalid token", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("Incorrect `api_key` or `access_token`", StringComparison.OrdinalIgnoreCase);

    private static int GetMaxDaysForInterval(string timeframe)
    {
        return timeframe.ToLower() switch
        {
            "1m" => 60,
            "3m" => 100,
            "5m" => 100,
            "15m" => 200,
            "30m" => 200,
            "60m" => 400,
            "1d" => 2000,
            _ => 60 // Safe default
        };
    }

    /// <summary>
    /// Detects missing data gaps in local DB and fetches them from Zerodha.
    /// </summary>
    public async Task SyncGapsAsync(string symbol, string timeframe, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Checking database historical gaps for Zerodha symbol {Symbol} ({Timeframe})...", symbol, timeframe);

        var localHistory = await _candleRepository.GetHistoryAsync(symbol, timeframe, limit: 1);
        var lastCandle = localHistory.FirstOrDefault();

        DateTime fromTime;
        DateTime toTime = DateTime.UtcNow;

        if (lastCandle != null)
        {
            var interval = ParseTimeframe(timeframe);
            if (string.Equals(timeframe, "1d", StringComparison.OrdinalIgnoreCase))
            {
                // A daily candle synced during the session is a partial snapshot (e.g. the 09:45 price).
                // Keep it current WITHOUT extra Kite calls (historical API limit: 3 req/s):
                //  - today's row already stored -> rebuild it from today's stored 15m candles (DB only);
                //  - last row is an earlier day   -> one fetch starting AT that day, so a partial earlier
                //    day is corrected in the same single call the old "last + 1 day" start already made.
                if (DayChangeCalculator.IstDate(lastCandle.CandleTime) >= DayChangeCalculator.IstDate(DateTime.UtcNow))
                {
                    await RefreshTodayDailyFromIntradayAsync(symbol, lastCandle);
                    return;
                }
                fromTime = lastCandle.CandleTime;
            }
            else
            {
                // Intraday timeframes re-fetch the whole session from 09:15 (FetchHistoricalCandlesAsync
                // snaps the start to the market open), so the forming candle is refreshed too.
                fromTime = lastCandle.CandleTime.Add(interval);
            }
            _logger.LogInformation("Database has existing records. Last record time: {LastTime}. Backfilling starting from {FromTime}", lastCandle.CandleTime, fromTime);
        }
        else
        {
            // Backfill last 2 years if completely empty
            fromTime = DateTime.UtcNow.AddYears(-2);
            _logger.LogInformation("Database is empty for {Symbol} ({Timeframe}). Backfilling starting from {FromTime}", symbol, timeframe, fromTime);
        }

        if (fromTime < toTime)
        {
            try
            {
                await FetchHistoricalCandlesAsync(symbol, timeframe, fromTime, toTime, cancellationToken);
            }
            catch (Exception ex)
            {
                // Already reported to the bell inside FetchHistoricalCandlesAsync. (No mock data is generated.)
                _logger.LogWarning(ex, "Failed to fetch historical candles from Zerodha API for {Symbol} ({Timeframe}).", symbol, timeframe);
            }
            
            // Calculate indicators for backfilled historical data
            await _indicatorService.BackfillHistoricalIndicatorsAsync(symbol, timeframe);
        }
        else
        {
            _logger.LogInformation("No historical gap sync required for Zerodha symbol {Symbol}.", symbol);
        }
    }

    // Kite's historical API allows 3 requests/second. The chunk delay above only spaces chunks of ONE fetch;
    // this gate spaces EVERY historical request in this process (sequential stock loops and the today-reset
    // worker's 5 parallel fetches alike) to at most one per HistoricalMinSpacing.
    private static readonly TimeSpan HistoricalMinSpacing = TimeSpan.FromMilliseconds(350);
    private static readonly SemaphoreSlim HistoricalRateGate = new(1, 1);
    private static DateTime _lastHistoricalRequestUtc = DateTime.MinValue;

    private static async Task WaitForHistoricalRateSlotAsync(CancellationToken cancellationToken)
    {
        await HistoricalRateGate.WaitAsync(cancellationToken);
        try
        {
            var wait = _lastHistoricalRequestUtc + HistoricalMinSpacing - DateTime.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait, cancellationToken);
            _lastHistoricalRequestUtc = DateTime.UtcNow;
        }
        finally
        {
            HistoricalRateGate.Release();
        }
    }

    /// <summary>
    /// Rebuilds today's stored daily candle from today's stored 15m candles - no Kite call. The 30-minute job
    /// syncs 15m right before 1d, so this keeps the daily row as fresh as the 15m data. Skipped when the
    /// 15m session isn't complete from 09:15 (open/high/low would be wrong); the next day's daily fetch
    /// replaces the row with Kite's final candle either way.
    /// </summary>
    private async Task RefreshTodayDailyFromIntradayAsync(string symbol, MarketCandle todayDaily)
    {
        DateTime todayIst = DayChangeCalculator.IstDate(DateTime.UtcNow);
        var today15m = (await _candleRepository.GetHistoryAsync(symbol, "15m", limit: 40))
            .Where(c => DayChangeCalculator.IstDate(c.CandleTime) == todayIst)
            .OrderBy(c => c.CandleTime)
            .ToList();

        if (today15m.Count == 0) return;
        var firstIst = TimeZoneInfo.ConvertTimeFromUtc(
            today15m[0].CandleTime.Kind == DateTimeKind.Utc ? today15m[0].CandleTime : DateTime.SpecifyKind(today15m[0].CandleTime, DateTimeKind.Utc),
            _indianTimeZone);
        if (firstIst.TimeOfDay != new TimeSpan(9, 15, 0))
        {
            _logger.LogDebug("Today's 15m candles for {Symbol} don't start at 09:15 IST - daily row left as is.", symbol);
            return;
        }

        var refreshed = new MarketCandle
        {
            Id = todayDaily.Id,                 // same row (PK id + candle_time) - the upsert updates it in place
            Symbol = todayDaily.Symbol,
            Timeframe = "1d",
            Open = today15m[0].Open,
            High = today15m.Max(c => c.High),
            Low = today15m.Min(c => c.Low),
            Close = today15m[^1].Close,
            Volume = today15m.Sum(c => c.Volume),
            CandleTime = todayDaily.CandleTime,
            CreatedAt = todayDaily.CreatedAt
        };
        await _candleRepository.InsertBatchAsync(new[] { refreshed });
        await _indicatorService.BackfillHistoricalIndicatorsAsync(symbol, "1d", Helpers.TimeZoneHelper.IstTodayStartUtc(), DateTime.UtcNow);
    }

    private static string MapTimeframeToKite(string timeframe)
    {
        return timeframe.ToLower() switch
        {
            "1m" => "minute",
            "3m" => "3minute",
            "5m" => "5minute",
            "15m" => "15minute",
            "30m" => "30minute",
            "60m" => "60minute",
            "1d" => "day",
            _ => "minute"
        };
    }

    private static TimeSpan ParseTimeframe(string timeframe)
    {
        return timeframe.ToLower() switch
        {
            "1s" => TimeSpan.FromSeconds(1),
            "5s" => TimeSpan.FromSeconds(5),
            "1m" => TimeSpan.FromMinutes(1),
            "3m" => TimeSpan.FromMinutes(3),
            "5m" => TimeSpan.FromMinutes(5),
            "15m" => TimeSpan.FromMinutes(15),
            "30m" => TimeSpan.FromMinutes(30),
            "60m" => TimeSpan.FromMinutes(60),
            "1d" => TimeSpan.FromDays(1),
            _ => TimeSpan.FromMinutes(1)
        };
    }

    private static int GenerateDeterministicIntId(string symbol, string timeframe, DateTime candleTime)
    {
        string input = $"{symbol}_{timeframe}_{candleTime:yyyyMMddHHmmss}";
        uint hash = 2166136261;
        foreach (char c in input)
        {
            hash = (hash ^ c) * 16777619;
        }
        return (int)hash;
    }

    private async Task GenerateMockDailyCandlesAsync(string symbol, DateTime fromTime, DateTime toTime, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Generating mock daily candles for {Symbol} from {From} to {To}...", symbol, fromTime, toTime);
        var rand = new Random(string.IsNullOrEmpty(symbol) ? 42 : symbol.GetHashCode());

        decimal price = symbol.ToUpper() switch
        {
            "NIFTY 50" => 22000m,
            "NIFTYBEES" => 240m,
            "INFY" => 1500m,
            "TCS" => 3800m,
            "HDFCBANK" => 1600m,
            "RELIANCE" => 2400m,
            _ => 500m
        };

        DateTime current = fromTime.Date;
        var mockCandles = new List<MarketCandle>();

        while (current <= toTime.Date)
        {
            if (cancellationToken.IsCancellationRequested) break;

            // Skip weekends
            if (current.DayOfWeek == DayOfWeek.Saturday || current.DayOfWeek == DayOfWeek.Sunday)
            {
                current = current.AddDays(1);
                continue;
            }

            // Simulate daily stock price change
            // Drift upward bias (e.g. 0.03% to 0.08% average daily return) + random volatility
            double drift = symbol.ToUpper() == "NIFTY 50" || symbol.ToUpper() == "NIFTYBEES" ? 0.0004 : 0.0006;
            double volatility = symbol.ToUpper() == "NIFTY 50" || symbol.ToUpper() == "NIFTYBEES" ? 0.009 : 0.018;

            double randNormal = BoxMullerTransform(rand);
            double dailyReturn = drift + volatility * randNormal;

            decimal openPrice = price;
            // Add a small gap on open
            decimal gapPercent = (decimal)(rand.NextDouble() * 0.004 - 0.002);
            openPrice = openPrice * (1m + gapPercent);

            decimal closePrice = price * (1m + (decimal)dailyReturn);
            if (closePrice <= 0) closePrice = 1m;

            price = closePrice; // update price tracking

            // High and Low
            decimal maxOC = Math.Max(openPrice, closePrice);
            decimal minOC = Math.Min(openPrice, closePrice);
            decimal highPrice = maxOC + (decimal)(rand.NextDouble() * 0.015) * maxOC;
            decimal lowPrice = minOC - (decimal)(rand.NextDouble() * 0.015) * minOC;
            if (lowPrice <= 0) lowPrice = 0.01m;

            long baseVol = symbol.ToUpper() switch
            {
                "NIFTY 50" => 300000000,
                "NIFTYBEES" => 5000000,
                _ => 1500000
            };
            long volume = baseVol + rand.Next((int)(-baseVol * 0.4), (int)(baseVol * 1.5));
            if (volume < 0) volume = 10000;

            // Occasional volume spike (on positive days usually)
            if (closePrice > openPrice && rand.NextDouble() < 0.15)
            {
                volume = (long)(volume * (1.8 + rand.NextDouble() * 1.5));
            }

            int deterministicId = GenerateDeterministicIntId(symbol, "1d", current);
            var candle = new MarketCandle
            {
                Id = deterministicId,
                Symbol = symbol.ToUpper(),
                Timeframe = "1d",
                Open = Math.Round(openPrice, 2),
                High = Math.Round(highPrice, 2),
                Low = Math.Round(lowPrice, 2),
                Close = Math.Round(closePrice, 2),
                Volume = volume,
                CandleTime = DateTime.SpecifyKind(current.AddHours(15).AddMinutes(30), DateTimeKind.Utc), // 3:30 PM IST EOD
                CreatedAt = DateTime.UtcNow
            };

            await _candleRepository.InsertAsync(candle);
            mockCandles.Add(candle);

            current = current.AddDays(1);
        }

        _logger.LogInformation("Generated {Count} mock daily candles for {Symbol}.", mockCandles.Count, symbol);
    }

    private static double BoxMullerTransform(Random rand)
    {
        double u1 = 1.0 - rand.NextDouble();
        double u2 = 1.0 - rand.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }
}
