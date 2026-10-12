using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace QuantEdge.Infrastructure.DTOs;

/// <summary>
/// Manual Paper Trade BUY (and Short SELL) request from the Manual Trading page - same shape and validation as
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

    /// <summary>Market (default) fills now at the latest stored price; Limit rests as a Pending order until the
    /// stored 1-minute candles trade through <see cref="LimitPrice"/>.</summary>
    public QuantEdge.Domain.Entities.PaperOrderType OrderType { get; set; } = QuantEdge.Domain.Entities.PaperOrderType.Market;

    /// <summary>Limit orders only: BUY at or below / SHORT SELL at or above this price.</summary>
    [Range(typeof(decimal), "0.01", "10000000", ErrorMessage = "Limit Price must be a positive price.")]
    public decimal? LimitPrice { get; set; }

    /// <summary>True: the position is closed automatically from stored 1-minute candles when its Target, Stop Loss or
    /// trailed stop is hit. False: the levels are reference only (closed with the Close button).</summary>
    public bool AutoExit { get; set; } = true;
}

/// <summary>An OPEN Manual Paper position for the Manual Trading page - a paper position plus its Auto Exit switch.</summary>
public class ManualPaperPosition : QuantEdge.Domain.Entities.PaperPosition
{
    public bool AutoExit { get; set; }
}

/// <summary>An OPEN auto-exit position with its trail state (fn_get_manual_paper_auto_exit_positions).</summary>
public class ManualPaperAutoExitPositionDto
{
    public int Id { get; set; }
    public int AccountId { get; set; }
    public string Symbol { get; set; } = string.Empty;
    public QuantEdge.Domain.Entities.TradeSide Side { get; set; }
    public int Quantity { get; set; }
    public decimal AverageEntryPrice { get; set; }
    public decimal? StopLoss { get; set; }
    public decimal? TakeProfit { get; set; }
    public decimal? TrailingSlPct { get; set; }
    public decimal? TrailingStopLoss { get; set; }
    /// <summary>Best price since entry: highest high for a long, lowest low for a short.</summary>
    public decimal? BestPrice { get; set; }
    /// <summary>Start time (UTC) of the last stored candle already checked; null before the first pass.</summary>
    public DateTime? ExitCheckedUntil { get; set; }
    public DateTime OpenedAt { get; set; }
}

/// <summary>A stored 1-minute candle (fn_get_manual_paper_candles). CandleTime is the candle's start (UTC).</summary>
public class ManualPaperCandleDto
{
    public DateTime CandleTime { get; set; }
    public decimal CandleOpen { get; set; }
    public decimal CandleHigh { get; set; }
    public decimal CandleLow { get; set; }
    public decimal CandleClose { get; set; }
}

/// <summary>
/// A Pending Manual Paper limit order with the first stored 1-minute candle that traded through its price
/// (fn_get_manual_paper_pending_orders). TriggerCandleTime is null while the price hasn't reached the limit.
/// </summary>
public class ManualPaperPendingOrderDto
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public string Symbol { get; set; } = string.Empty;
    public QuantEdge.Domain.Entities.TradeSide Side { get; set; }
    public int Quantity { get; set; }
    public decimal LimitPrice { get; set; }
    public decimal? StopLossPct { get; set; }
    public decimal? TrailingSlPct { get; set; }
    public bool AutoExit { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? TriggerCandleTime { get; set; }
    public decimal? TriggerCandleOpen { get; set; }
}

/// <summary>
/// Editable Manual Paper Trading settings (manual_paper_trade_settings) - same rule set and validation
/// ranges as AutoTradeSettingsUpdateDto, minus the auto-only fields (condition score).
/// </summary>
public class ManualPaperTradeSettingsUpdateDto : IValidatableObject
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

    [RegularExpression(@"^([01]\d|2[0-3]):[0-5]\d$", ErrorMessage = "Short Entry Cut-off must be HH:mm.")]
    public string ShortEntryCutoff { get; set; } = "15:00";

    [RegularExpression(@"^([01]\d|2[0-3]):[0-5]\d$", ErrorMessage = "Short Square-off Time must be HH:mm.")]
    public string ShortSquareOffTime { get; set; } = "15:15";

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (TimeSpan.TryParse(ShortEntryCutoff, out var cutoff) && TimeSpan.TryParse(ShortSquareOffTime, out var squareOff)
            && cutoff >= squareOff)
        {
            yield return new ValidationResult("Short Entry Cut-off must be earlier than the Short Square-off Time.",
                new[] { nameof(ShortEntryCutoff) });
        }
    }
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
    /// <summary>Capital held by Pending limit orders (quantity × limit price) until they fill, cancel or expire.</summary>
    public decimal ManualBlockedMargin { get; set; }
    public int PendingOrdersCount { get; set; }
    public decimal ManualUnrealizedPnl { get; set; }
    /// <summary>Estimated round-trip charges of the open positions if sold now; net = ManualUnrealizedPnl − this.</summary>
    public decimal ManualEstimatedCharges { get; set; }
    public decimal ManualRealizedPnl { get; set; }
    public decimal ManualTodayRealizedPnl { get; set; }
    public decimal ManualEquity => ManualCapital + ManualRealizedPnl + ManualUnrealizedPnl;
    // Manual Trading's own cash - what a manual BUY is checked against (not the shared paper account).
    public decimal ManualAvailableMargin => ManualCapital + ManualRealizedPnl - ManualUsedMargin - ManualBlockedMargin;
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
/// With Auto Exit ON they are live exit levels (ManualAutoExitWorker); with it OFF they are reference only and the
/// position is closed with the Close button.
/// </summary>
public class UpdateManualPositionLevelsDto
{
    [Range(typeof(decimal), "0.01", "10000000", ErrorMessage = "Stop Loss must be a positive price.")]
    public decimal StopLoss { get; set; }

    [Range(typeof(decimal), "0.1", "50.0", ErrorMessage = "Trailing Stop Loss % must be between 0.1% and 50%.")]
    public decimal TrailingSlPct { get; set; }

    [Range(typeof(decimal), "0.01", "10000000", ErrorMessage = "Target must be a positive price.")]
    public decimal TakeProfit { get; set; }

    /// <summary>Turns Auto Exit on / off for this position; null leaves it unchanged.</summary>
    public bool? AutoExit { get; set; }
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

/// <summary>Latest stored price for a symbol (fn_get_manual_paper_latest_prices - market_candles_1m close).</summary>
public class ManualPaperPriceDto
{
    public string Symbol { get; set; } = string.Empty;
    public decimal Ltp { get; set; }
    public System.DateTime PriceTime { get; set; }
}
