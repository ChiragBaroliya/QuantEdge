using System.ComponentModel.DataAnnotations;

namespace QuantEdge.Infrastructure.DTOs;

/// <summary>
/// Manual Paper Trade BUY request from the Manual Trading page - same shape and validation as
/// ManualRealBuyRequestDto, so manual paper and manual real trades are sized and protected alike.
/// </summary>
public class ManualPaperBuyRequestDto
{
    public string Symbol { get; set; } = string.Empty;
    public decimal EntryPrice { get; set; }

    [Range(1, 1000000, ErrorMessage = "Quantity must be a positive whole number.")]
    public int Quantity { get; set; }

    /// <summary>Trade-wise Stop Loss % - applies to this trade only, never written back to settings.</summary>
    [Range(typeof(decimal), "0.1", "100.0", ErrorMessage = "Stop Loss % must be between 0.1% and 100%.")]
    public decimal StopLossPct { get; set; }

    /// <summary>Trade-wise Trailing Stop Loss %, same semantics as StopLossPct above.</summary>
    [Range(typeof(decimal), "0.1", "50.0", ErrorMessage = "Trailing Stop Loss % must be between 0.1% and 50%.")]
    public decimal TrailingSlPct { get; set; }
}

/// <summary>
/// Editable Manual Paper Trading settings (manual_paper_trade_settings) - same rule set and validation
/// ranges as AutoTradeSettingsUpdateDto, minus the auto-only fields (condition score).
/// </summary>
public class ManualPaperTradeSettingsUpdateDto
{
    public bool IsManualTradeEnabled { get; set; } = true;

    [Range(typeof(decimal), "100", "10000000", ErrorMessage = "Available Capital must be between ₹100 and ₹1,00,00,000.")]
    public decimal AvailableCapital { get; set; } = 100000.00m;

    [Range(typeof(decimal), "0.1", "100.0", ErrorMessage = "Profit Target % must be between 0.1% and 100%.")]
    public decimal ProfitTargetPct { get; set; } = 5.00m;

    [Range(typeof(decimal), "0.1", "100.0", ErrorMessage = "Default Stop Loss % must be between 0.1% and 100%.")]
    public decimal StopLossPct { get; set; } = 3.00m;

    [Range(typeof(decimal), "0.1", "50.0", ErrorMessage = "Default Trailing Stop Loss % must be between 0.1% and 50%.")]
    public decimal TrailingSlPct { get; set; } = 2.00m;

    [Range(typeof(decimal), "10", "10000000", ErrorMessage = "Daily Loss Limit must be between ₹10 and ₹1,00,00,000.")]
    public decimal? MaxDailyLossLimit { get; set; }

    [Range(0, 120, ErrorMessage = "Entry Delay must be between 0 and 120 minutes.")]
    public int EntryDelayMinutes { get; set; } = 15;

    [RegularExpression("^(SWING_CLOSE|INTRADAY)$", ErrorMessage = "Exit Mode must be SWING_CLOSE or INTRADAY.")]
    public string ExitMode { get; set; } = "SWING_CLOSE";

    [RegularExpression(@"^([01]\d|2[0-3]):[0-5]\d$", ErrorMessage = "Close Check Time must be HH:mm.")]
    public string CloseCheckTime { get; set; } = "15:15";

    [Range(typeof(decimal), "0.5", "5.0", ErrorMessage = "Stop Loss ATR multiple must be between 0.5 and 5.")]
    public decimal StopLossAtrMult { get; set; } = 1.5m;

    [Range(typeof(decimal), "0.5", "10.0", ErrorMessage = "Trailing ATR multiple must be between 0.5 and 10.")]
    public decimal TrailAtrMult { get; set; } = 3.0m;

    [Range(typeof(decimal), "0.5", "20.0", ErrorMessage = "Target ATR multiple must be between 0.5 and 20.")]
    public decimal TargetAtrMult { get; set; } = 3.0m;

    [Range(1, 365, ErrorMessage = "Max Duration must be between 1 and 365 days.")]
    public int MaxDurationDays { get; set; } = 20;

    [Range(1, 50, ErrorMessage = "Max Trades Per Day must be between 1 and 50.")]
    public int MaxTradesPerDay { get; set; } = 5;

    [Range(typeof(decimal), "100", "1000000", ErrorMessage = "Trade Amount must be between ₹100 and ₹10,00,000.")]
    public decimal FixedAmountPerTrade { get; set; } = 20000.00m;

    [RegularExpression(@"^([01]\d|2[0-3]):[0-5]\d$", ErrorMessage = "Trading Window Start must be HH:mm.")]
    public string TradingWindowStart { get; set; } = "09:15";

    [RegularExpression(@"^([01]\d|2[0-3]):[0-5]\d$", ErrorMessage = "Trading Window End must be HH:mm.")]
    public string TradingWindowEnd { get; set; } = "15:30";
}

public class ToggleManualPaperTradeRequestDto
{
    public bool Enabled { get; set; }
}

/// <summary>
/// Filter for the Manual Trading page's Active / Pending Orders tab (fn_get_manual_paper_orders_paged).
/// </summary>
public class ManualPaperOrderFilterDto
{
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 10;
    public string? Symbol { get; set; }
    public QuantEdge.Domain.Entities.TradeSide? Side { get; set; }
    public QuantEdge.Domain.Entities.PaperOrderStatus? Status { get; set; }
    public System.DateTime? FromDate { get; set; }
    public System.DateTime? ToDate { get; set; }
}

/// <summary>
/// Manual Trading page stat cards (fn_get_manual_paper_dashboard) - figures from the manual_paper_* tables.
/// </summary>
public class ManualPaperDashboardDto
{
    // Manual-only figures
    public decimal ManualCapital { get; set; }
    public decimal ManualUsedMargin { get; set; }
    public decimal ManualUnrealizedPnl { get; set; }
    public decimal ManualRealizedPnl { get; set; }
    public decimal ManualTodayRealizedPnl { get; set; }
    public decimal ManualEquity => ManualCapital + ManualRealizedPnl + ManualUnrealizedPnl;
    // Manual Trading's own cash - what a manual BUY is checked against (not the shared paper account).
    public decimal ManualAvailableMargin => ManualCapital + ManualRealizedPnl - ManualUsedMargin;
    public int OpenPositionsCount { get; set; }
    public int TotalBuyTrades { get; set; }
    public int TodayBuyTrades { get; set; }
    public int ClosedTrades { get; set; }
    public int WinningTrades { get; set; }
    public decimal WinRatePct => ClosedTrades > 0 ? System.Math.Round(WinningTrades * 100m / ClosedTrades, 1) : 0m;

    public bool IsManualTradeEnabled { get; set; }
    public int MaxTradesPerDay { get; set; }
}

/// <summary>
/// Edit Stop Loss / Trailing SL % / Target of an OPEN manual paper position (Live Open Positions "Edit").
/// These are reference levels - manual paper positions are only sold with the Close button.
/// </summary>
public class UpdateManualPositionLevelsDto
{
    [Range(typeof(decimal), "0.01", "10000000", ErrorMessage = "Stop Loss must be a positive price.")]
    public decimal StopLoss { get; set; }

    [Range(typeof(decimal), "0.1", "50.0", ErrorMessage = "Trailing Stop Loss % must be between 0.1% and 50%.")]
    public decimal TrailingSlPct { get; set; }

    [Range(typeof(decimal), "0.01", "10000000", ErrorMessage = "Target must be a positive price.")]
    public decimal TakeProfit { get; set; }
}

/// <summary>Result row of fn_close_manual_paper_position (Close button).</summary>
public class ManualPaperCloseResult
{
    public bool Success { get; set; }
    public string? Symbol { get; set; }
    public int Quantity { get; set; }
    public decimal EntryPrice { get; set; }
    public decimal ExitPrice { get; set; }
    public decimal RealizedPnl { get; set; }
}
