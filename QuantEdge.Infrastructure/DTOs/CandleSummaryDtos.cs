using System;
using System.Collections.Generic;

namespace QuantEdge.Infrastructure.DTOs;

public class CandleSummaryFilterDto
{
    public DateTime? FromDate { get; set; }
    public DateTime? ToDate { get; set; }
    public string Symbol { get; set; } = "ALL";
    public string Timeframe { get; set; } = "ALL";
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 25;
}

public class StockTimeframeCandleCountDto
{
    public string Symbol { get; set; } = string.Empty;
    public string StockName { get; set; } = string.Empty;
    public int Candles1d { get; set; }
    public int Candles60m { get; set; }
    public int Candles15m { get; set; }
    public int Candles5m { get; set; }
    public int Candles1m { get; set; }
    public int TotalCandles { get; set; }
    public DateTime? LatestCandleTime { get; set; }
    public long TotalRecords { get; set; }
}

public class CandleSummaryResponseDto
{
    public DateTime FromDate { get; set; }
    public DateTime ToDate { get; set; }
    public string SelectedSymbol { get; set; } = "ALL";
    public string SelectedTimeframe { get; set; } = "ALL";
    public int TotalStocks { get; set; }
    public long TotalCandlesCount { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalPages { get; set; }
    public IEnumerable<StockTimeframeCandleCountDto> Items { get; set; } = new List<StockTimeframeCandleCountDto>();
}
