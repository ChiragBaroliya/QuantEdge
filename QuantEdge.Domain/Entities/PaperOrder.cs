using System;

namespace QuantEdge.Domain.Entities;

public class PaperOrder
{
    public int Id { get; set; }
    public int AccountId { get; set; }
    public string Symbol { get; set; } = string.Empty;
    public PaperOrderType OrderType { get; set; } = PaperOrderType.Market;
    public TradeSide Side { get; set; } = TradeSide.BUY;
    public int Quantity { get; set; }
    public decimal Price { get; set; }
    public decimal? TriggerPrice { get; set; }
    public decimal? StopLoss { get; set; }
    public decimal? TakeProfit { get; set; }
    public PaperOrderStatus Status { get; set; } = PaperOrderStatus.Pending;
    public decimal? FilledPrice { get; set; }
    public DateTime? FilledAt { get; set; }
    public TradeType TradeType { get; set; } = TradeType.Manual;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string? Remarks { get; set; }
}

