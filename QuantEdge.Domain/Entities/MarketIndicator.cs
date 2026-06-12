using System;

namespace QuantEdge.Domain.Entities;

/// <summary>
/// Database entity representing computed technical market indicators at a specific candle timeframe.
/// </summary>
public class MarketIndicator
{
    public int Id { get; set; }
    public string Symbol { get; set; } = string.Empty;
    public string Timeframe { get; set; } = string.Empty;
    public decimal RSI { get; set; }
    public decimal EMA20 { get; set; }
    public decimal EMA50 { get; set; }
    public decimal MACD { get; set; }
    public decimal SignalLine { get; set; }
    public decimal VWAP { get; set; }
    public DateTime CandleTime { get; set; }
    public DateTime CreatedAt { get; set; }
}
