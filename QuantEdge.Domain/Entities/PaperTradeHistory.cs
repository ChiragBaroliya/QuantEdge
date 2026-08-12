using System;

namespace QuantEdge.Domain.Entities;

public class PaperTradeHistory
{
    public int Id { get; set; }
    public int AccountId { get; set; }
    public int OrderId { get; set; }
    public string Symbol { get; set; } = string.Empty;
    public TradeSide Side { get; set; }
    public int Quantity { get; set; }
    public decimal EntryPrice { get; set; }
    public decimal ExecutedPrice { get; set; }
    public decimal RealizedPnl { get; set; }
    public TradeType TradeType { get; set; } = TradeType.Manual;
    public string? ExitReason { get; set; }
    public DateTime ExecutedAt { get; set; } = DateTime.UtcNow;
    public string Remarks { get; set; } = string.Empty;
}

