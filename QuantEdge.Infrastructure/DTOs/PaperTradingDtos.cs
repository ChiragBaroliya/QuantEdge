using System;
using System.ComponentModel.DataAnnotations;
using QuantEdge.Domain.Entities;

namespace QuantEdge.Infrastructure.DTOs;

public class PlacePaperOrderDto
{
    [Required(ErrorMessage = "Symbol is required.")]
    public string Symbol { get; set; } = string.Empty;

    [Required(ErrorMessage = "Trade Side is required.")]
    public TradeSide Side { get; set; } = TradeSide.BUY;

    [Required(ErrorMessage = "Order Type is required.")]
    public PaperOrderType OrderType { get; set; } = PaperOrderType.Market;

    [Range(1, 100000, ErrorMessage = "Quantity must be between 1 and 100,000.")]
    public int Quantity { get; set; }

    public decimal Price { get; set; }

    public decimal? StopLoss { get; set; }

    public decimal? TakeProfit { get; set; }
}

public class PaperPortfolioDto
{
    public PaperAccount Account { get; set; } = new();
    public decimal TotalUnrealizedPnl { get; set; }
    public decimal TotalEquity => Account.CurrentBalance + TotalUnrealizedPnl;
    public bool AutoTradeEnabled { get; set; }
}

public class ClosePositionDto
{
    [Required]
    public int PositionId { get; set; }

    public decimal ExitPrice { get; set; }
}

public class PaperErrorDto
{
    public string ErrorCode { get; set; } = "ERROR";
    public string Message { get; set; } = string.Empty;
    public string? Field { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}
