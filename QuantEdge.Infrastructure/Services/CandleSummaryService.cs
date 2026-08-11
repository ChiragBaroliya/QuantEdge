using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Extensions.Logging;
using QuantEdge.Infrastructure.DTOs;
using QuantEdge.Infrastructure.Interfaces;
using QuantEdge.Infrastructure.Persistence;
using QuantEdge.Infrastructure.Persistence.Repositories;

namespace QuantEdge.Infrastructure.Services;

public class CandleSummaryService : ICandleSummaryService
{
    private readonly IDbConnectionFactory _connectionFactory;
    private readonly ICacheService _cacheService;
    private readonly ILogger<CandleSummaryService> _logger;

    public CandleSummaryService(
        IDbConnectionFactory connectionFactory,
        ICacheService cacheService,
        ILogger<CandleSummaryService> logger)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _cacheService = cacheService ?? throw new ArgumentNullException(nameof(cacheService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<IEnumerable<string>> GetActiveSymbolsAsync()
    {
        string cacheKey = "candlesummary:active_symbols";
        var cached = await _cacheService.GetAsync<List<string>>(cacheKey);
        if (cached != null && cached.Any()) return cached;

        using var connection = _connectionFactory.CreateConnection();
        string sql = @"SELECT symbol FROM stock_master WHERE is_active = TRUE ORDER BY symbol ASC;";
        var symbols = (await connection.QueryAsync<string>(sql)).ToList();

        await _cacheService.SetAsync(cacheKey, symbols, TimeSpan.FromMinutes(15));
        return symbols;
    }

    public async Task<CandleSummaryResponseDto> GetCandleSummaryAsync(CandleSummaryFilterDto filter)
    {
        filter ??= new CandleSummaryFilterDto();

        // Calculate IST Today start/end default
        DateTime istNow;
        try
        {
            var istZone = TimeZoneInfo.FindSystemTimeZoneById("India Standard Time");
            istNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, istZone);
        }
        catch
        {
            istNow = DateTime.UtcNow.AddHours(5).AddMinutes(30);
        }

        DateTime fromDate = filter.FromDate?.Date ?? istNow.Date;
        DateTime toDate = filter.ToDate?.Date.AddDays(1).AddTicks(-1) ?? istNow.Date.AddDays(1).AddTicks(-1);

        string symbol = string.IsNullOrWhiteSpace(filter.Symbol) ? "ALL" : filter.Symbol.Trim().ToUpperInvariant();
        string timeframe = string.IsNullOrWhiteSpace(filter.Timeframe) ? "ALL" : filter.Timeframe.Trim().ToLowerInvariant();
        int page = Math.Max(1, filter.Page);
        int pageSize = Math.Clamp(filter.PageSize, 10, 100);

        string cacheKey = $"candlesummary:{fromDate:yyyyMMdd}:{toDate:yyyyMMdd}:{symbol}:{timeframe}:{page}:{pageSize}";
        var cachedResponse = await _cacheService.GetAsync<CandleSummaryResponseDto>(cacheKey);
        if (cachedResponse != null)
        {
            return cachedResponse;
        }

        using var connection = _connectionFactory.CreateConnection();

        string storedFuncSql = "SELECT * FROM sp_get_candle_timeframe_summary(@p_from_date, @p_to_date, @p_symbol, @p_timeframe, @p_page, @p_page_size);";
        var items = (await connection.QueryAsync<StockTimeframeCandleCountDto>(storedFuncSql, new
        {
            p_from_date = fromDate,
            p_to_date = toDate,
            p_symbol = symbol,
            p_timeframe = timeframe,
            p_page = page,
            p_page_size = pageSize
        })).ToList();

        int totalStocks = (int)(items.FirstOrDefault()?.TotalRecords ?? 0);
        long totalCandlesCount = items.Sum(i => (long)i.TotalCandles);
        int totalPages = (int)Math.Ceiling((double)totalStocks / pageSize);
        if (totalPages < 1) totalPages = 1;

        var response = new CandleSummaryResponseDto
        {
            FromDate = fromDate,
            ToDate = filter.ToDate?.Date ?? istNow.Date,
            SelectedSymbol = symbol,
            SelectedTimeframe = timeframe,
            TotalStocks = totalStocks,
            TotalCandlesCount = totalCandlesCount,
            Page = page,
            PageSize = pageSize,
            TotalPages = totalPages,
            Items = items
        };

        // Cache response for 2 minutes
        await _cacheService.SetAsync(cacheKey, response, TimeSpan.FromMinutes(2));

        return response;
    }
}
