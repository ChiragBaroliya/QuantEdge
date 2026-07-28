using System;

namespace QuantEdge.Domain.Entities;

public class PaperPosition
{
    public int Id { get; set; }
    public int AccountId { get; set; }
    public string Symbol { get; set; } = string.Empty;
    public TradeSide Side { get; set; } = TradeSide.BUY;
    public int Quantity { get; set; }
    public decimal AverageEntryPrice { get; set; }
    public decimal CurrentPrice { get; set; }
    public decimal UnrealizedPnl { get; set; }
    public decimal? StopLoss { get; set; }
    public decimal? TakeProfit { get; set; }
    public PositionStatus Status { get; set; } = PositionStatus.OPEN;
    public DateTime OpenedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ClosedAt { get; set; }
    public decimal RealizedPnl { get; set; }
}
