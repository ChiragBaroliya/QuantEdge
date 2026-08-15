using System;

namespace QuantEdge.Domain.Entities;

public class RealPosition
{
    public int Id { get; set; }
    public int UserId { get; set; } = 1;
    public string Symbol { get; set; } = string.Empty;
    public TradeSide Side { get; set; } = TradeSide.BUY;
    public int Quantity { get; set; }
    public decimal AverageEntryPrice { get; set; }
    public decimal CurrentPrice { get; set; }
    public decimal UnrealizedPnl { get; set; }
    public decimal? StopLoss { get; set; } // Optional
    public decimal? TakeProfit { get; set; } // Optional
    public decimal? TrailingStopLoss { get; set; } // Optional Trailing SL value
    public PositionStatus Status { get; set; } = PositionStatus.OPEN;
    public TradeType TradeType { get; set; } = TradeType.Auto;
    public string? ExitReason { get; set; }
    public DateTime OpenedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ClosedAt { get; set; }
    public decimal RealizedPnl { get; set; }
}
