using System;

namespace QuantEdge.Domain.Entities;

/// <summary>
/// Database entity representing a market candlestick bar.
/// </summary>
public class MarketCandle
{
    public int Id { get; set; }
    public string Symbol { get; set; } = string.Empty;
    public string Timeframe { get; set; } = string.Empty;
    public decimal Open { get; set; }
    public decimal High { get; set; }
    public decimal Low { get; set; }
    public decimal Close { get; set; }
    public long Volume { get; set; }
    public DateTime CandleTime { get; set; }
    public DateTime CreatedAt { get; set; }
}
