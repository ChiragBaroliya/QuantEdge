using System;

namespace QuantEdge.Domain.Entities;

public class RealOrder
{
    public int Id { get; set; }
    public int UserId { get; set; } = 1;
    public string? BrokerOrderId { get; set; }
    public string Symbol { get; set; } = string.Empty;
    public TradeSide Side { get; set; } = TradeSide.BUY;
    public int Quantity { get; set; }
    public PaperOrderType OrderType { get; set; } = PaperOrderType.Market;
    public decimal Price { get; set; }
    public decimal? StopLoss { get; set; } // Optional
    public decimal? TakeProfit { get; set; } // Optional
    public PaperOrderStatus Status { get; set; } = PaperOrderStatus.Pending;
    public decimal FilledPrice { get; set; }
    // Price the decision was made at (scan price for a BUY, live price for an exit) - FilledPrice minus this is slippage.
    public decimal? SignalPrice { get; set; }
    // Quantity Zerodha actually executed (filled_quantity) - can be below Quantity on a partial fill.
    public int? FilledQuantity { get; set; }
    public DateTime? FilledAt { get; set; }
    public string? RejectionReason { get; set; }
    public TradeType TradeType { get; set; } = TradeType.Auto;
    public string? Remarks { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
