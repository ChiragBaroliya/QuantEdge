using System;

namespace QuantEdge.Domain.Entities;

public class RealTradeHistory
{
    public int Id { get; set; }
    public int UserId { get; set; } = 1;
    public int OrderId { get; set; }
    public string? BrokerOrderId { get; set; }
    public string Symbol { get; set; } = string.Empty;
    public TradeSide Side { get; set; }
    public int Quantity { get; set; }
    public decimal EntryPrice { get; set; }
    public decimal ExecutedPrice { get; set; }
    public decimal RealizedPnl { get; set; }
    public TradeType TradeType { get; set; } = TradeType.Auto;
    public string? ExitReason { get; set; }
    // True = this row closed a trade (SELL a long / BUY to cover a short), false = it opened one. Null on rows
    // written without it - readers then fall back to the old "SELL = exit" rule.
    public bool? IsExit { get; set; }
    public DateTime ExecutedAt { get; set; } = DateTime.UtcNow;
    public string? Remarks { get; set; }
}
