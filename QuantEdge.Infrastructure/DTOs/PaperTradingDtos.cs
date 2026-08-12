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

public class AutoTradeSettingsDto
{
    public bool IsAutoTradeEnabled { get; set; } = false;
    public string TradingMode { get; set; } = "Paper"; // "Paper" or "Live"
    public string AutoTradeTimeframe { get; set; } = "1m";
    public decimal AutoTradeMinSignalStrength { get; set; } = 70m;
    public int AutoTradeQuantity { get; set; } = 25;
    public decimal AutoTradeStopLossPercent { get; set; } = 1.0m;
    public decimal AutoTradeTakeProfitPercent { get; set; } = 2.0m;
    public int MaxOpenPositions { get; set; } = 5;
    public decimal DailyMaxLossLimit { get; set; } = 2000m;
}

public class PaperTradeHistoryFilterDto
{
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 10;
    public string? Symbol { get; set; }
    public TradeSide? Side { get; set; }
    public DateTime? FromDate { get; set; }
    public DateTime? ToDate { get; set; }
}

public class PagedResultDto<T>
{
    public IEnumerable<T> Items { get; set; } = new List<T>();
    public int TotalCount { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalPages => PageSize > 0 ? (int)Math.Ceiling((double)TotalCount / PageSize) : 0;
}
