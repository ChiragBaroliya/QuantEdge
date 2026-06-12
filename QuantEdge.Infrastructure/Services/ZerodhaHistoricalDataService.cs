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
    private readonly ILogger<ZerodhaHistoricalDataService> _logger;

    public ZerodhaHistoricalDataService(
        IOptions<BrokerConfig> config,
        IMarketCandleRepository candleRepository,
        IDbConnectionFactory connectionFactory,
        IStockMasterRepository stockMasterRepository,
        IIndicatorService indicatorService,
        ILogger<ZerodhaHistoricalDataService> logger)
    {
        _config = config?.Value ?? throw new ArgumentNullException(nameof(config));
        _candleRepository = candleRepository ?? throw new ArgumentNullException(nameof(candleRepository));
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _stockMasterRepository = stockMasterRepository ?? throw new ArgumentNullException(nameof(stockMasterRepository));
        _indicatorService = indicatorService ?? throw new ArgumentNullException(nameof(indicatorService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
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

        _logger.LogInformation("Resolving active Zerodha session from database...");
        string? token = null;
        try
        {
            using var conn = _connectionFactory.CreateConnection();
            token = await conn.QueryFirstOrDefaultAsync<string?>(
                "SELECT access_token FROM zerodha_sessions WHERE api_key = @ApiKey AND is_active = TRUE LIMIT 1;",
                new { ApiKey = _config.ApiKey }
            );
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to resolve active AccessToken from the database.");
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            _logger.LogError("Zerodha AccessToken is missing. Cannot fetch historical data.");
            throw new InvalidOperationException("Zerodha AccessToken is missing.");
        }

        string intervalStr = MapTimeframeToKite(timeframe);
        int maxDays = GetMaxDaysForInterval(timeframe);
        
        var savedCandles = new List<MarketCandle>();
        DateTime currentStart = fromTime;

        _logger.LogInformation("Initiating chunked historical fetch for token {Token} ({Symbol}) from {From} to {To} (Interval: {Interval}, Max Days/Chunk: {MaxDays})", 
            instrumentToken, symbol, fromTime, toTime, intervalStr, maxDays);

        try
        {
            var kite = new Kite(_config.ApiKey);
            kite.SetAccessToken(token);

            while (currentStart < toTime)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                DateTime currentEnd = currentStart.AddDays(maxDays);
                if (currentEnd > toTime)
                {
                    currentEnd = toTime;
                }

                _logger.LogInformation("Requesting chunk: {Symbol} from {From} to {To}", symbol, currentStart, currentEnd);

                List<Historical> historicalList = await Task.Run(() => 
                    kite.GetHistoricalData(
                        InstrumentToken: instrumentToken.ToString(),
                        FromDate: currentStart,
                        ToDate: currentEnd,
                        Interval: intervalStr
                    ), cancellationToken);

                if (historicalList != null && historicalList.Any())
                {
                    foreach (var record in historicalList)
                    {
                        if (cancellationToken.IsCancellationRequested)
                            break;

                        int deterministicId = GenerateDeterministicIntId(symbol, timeframe, record.TimeStamp);

                        var candle = new MarketCandle
                        {
                            Id = deterministicId,
                            Symbol = symbol.ToUpper(),
                            Timeframe = timeframe,
                            Open = record.Open,
                            High = record.High,
                            Low = record.Low,
                            Close = record.Close,
                            Volume = (long)record.Volume,
                            CandleTime = record.TimeStamp.ToUniversalTime(),
                            CreatedAt = DateTime.UtcNow
                        };

                        await _candleRepository.InsertAsync(candle);
                        savedCandles.Add(candle);
                    }
                }

                // Move forward (using currentEnd as next start time is safe as Dapper does UPSERT on conflict)
                currentStart = currentEnd;

                // Add minor rate-limiting delay between sequential chunks to respect 3 requests/sec API rate limit
                if (currentStart < toTime)
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
            throw;
        }
    }

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
            fromTime = lastCandle.CandleTime.Add(interval);
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
            await FetchHistoricalCandlesAsync(symbol, timeframe, fromTime, toTime, cancellationToken);
            
            // Calculate indicators for backfilled historical data
            await _indicatorService.BackfillHistoricalIndicatorsAsync(symbol, timeframe);
        }
        else
        {
            _logger.LogInformation("No historical gap sync required for Zerodha symbol {Symbol}.", symbol);
        }
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
}
