using System;

namespace QuantEdge.Domain.Entities;

/// <summary>
/// Database entity representing an AI-generated trading BUY/SELL/HOLD signal.
/// </summary>
public class TradingSignal
{
    public int Id { get; set; }
    public string Symbol { get; set; } = string.Empty;
    public string SignalType { get; set; } = string.Empty;
    public decimal SignalStrength { get; set; }
    public decimal EntryPrice { get; set; }
    public string Reason { get; set; } = string.Empty;
    public DateTime CandleTime { get; set; }
    public DateTime CreatedAt { get; set; }
}
