using System;
using System.Collections.Generic;

namespace QuantEdge.Infrastructure.DTOs;

/// <summary>
/// Generic paginated response envelope.
/// </summary>
public record PagedResult<T>(
    IReadOnlyList<T> Items,
    long TotalCount,
    int Page,
    int PageSize,
    int TotalPages
)
{
    public bool HasPrevious => Page > 1;
    public bool HasNext => Page < TotalPages;
}

/// <summary>
/// Filter criteria for querying overall performance reports and dashboard charts.
/// </summary>
public record ReportFilterDto(
    string PeriodType = "daily",     // "daily", "weekly", "fortnightly", "monthly", "yearly"
    string TradeMode = "all",        // "all", "real", "paper"
    string? UserId = null,           // User filter (null or "all" for system-wide)
    DateTime? StartDate = null,
    DateTime? EndDate = null,
    string? Symbol = null,
    int PeriodsPage = 1,
    int PeriodsPageSize = 10,
    string PeriodsPnlFilter = "all", // "all", "profit", "loss"
    int TradesPage = 1,
    int TradesPageSize = 10,
    string TradesType = "all",       // "all", "intraday", "swing", "auto"
    string TradesPnlFilter = "all"   // "all", "profit", "loss"
);

/// <summary>
/// Dedicated query filter for paginated trades log table.
/// </summary>
public record ReportTradesFilterDto(
    string TradeMode = "all",
    string? UserId = null,
    DateTime? StartDate = null,
    DateTime? EndDate = null,
    string? Symbol = null,
    string TradeType = "all",        // "all", "intraday", "swing", "auto"
    string PnlFilter = "all",         // "all", "profit", "loss"
    int Page = 1,
    int PageSize = 10
);

/// <summary>
/// Dedicated query filter for paginated periodic breakdown table.
/// </summary>
public record ReportPeriodsFilterDto(
    string PeriodType = "daily",
    string TradeMode = "all",
    string? UserId = null,
    DateTime? StartDate = null,
    DateTime? EndDate = null,
    string PnlFilter = "all",         // "all", "profit", "loss"
    int Page = 1,
    int PageSize = 10,
    string? Symbol = null             // same symbol filter as the summary, so the period table agrees with the KPIs
);

/// <summary>
/// Overall summary performance KPI metrics.
/// </summary>
public record TradingReportSummaryDto(
    // Turnover: sum of every closed trade's entry value. The same capital reused N times counts N times.
    decimal TotalInvestedCapital,
    // Net P&L after estimated charges (GrossRealizedPnl − TotalCharges). All win/loss/drawdown stats use net.
    decimal NetRealizedPnl,
    // Net P&L ÷ PeakCapitalDeployed - return on the capital actually tied up, not on turnover.
    decimal TotalRoiPct,
    int TotalTrades,
    int WinningTrades,
    int LosingTrades,
    decimal WinRatePct,
    decimal GrossProfit,
    decimal GrossLoss,
    decimal ProfitFactor,
    decimal AvgTradePnl,
    decimal AvgTradeRoiPct,
    // Largest peak-to-trough fall of equity (PeakCapitalDeployed + cumulative net P&L), as % of the peak.
    decimal MaxDrawdownPct,
    decimal BestTradePnl,
    decimal WorstTradePnl,
    // Price-difference P&L before any charges.
    decimal GrossRealizedPnl = 0m,
    // Estimated brokerage + STT + exchange + SEBI + stamp + GST + DP for the closed trades (ChargesCalculator).
    decimal TotalCharges = 0m,
    // Most entry value held open at one time (overlapping trades summed) - the capital base for ROI and drawdown.
    decimal PeakCapitalDeployed = 0m
);

/// <summary>
/// Performance metrics aggregated for a specific time period bucket (Day, Week, 15-Day, Month, Year).
/// </summary>
public record TradingReportPeriodDto(
    string PeriodKey,            // e.g. "2026-08-30", "2026-W35", "2026-08-H2", "2026-08", "2026"
    string PeriodLabel,          // User friendly label e.g. "30 Aug 2026", "Week 35 (25-30 Aug)", "16-31 Aug 2026", "August 2026", "Year 2026"
    DateTime StartDate,
    DateTime EndDate,
    int TotalTrades,
    int WinTrades,
    int LossTrades,
    decimal WinRatePct,
    decimal InvestedCapital,     // turnover in the period
    decimal GrossProfit,
    decimal GrossLoss,
    decimal NetPnl,
    decimal RoiPct,              // NetPnl ÷ peak capital deployed in the period
    decimal CumulativePnl
);

/// <summary>
/// Data point for the equity & cumulative PnL growth line chart.
/// </summary>
public record TradingReportEquityPointDto(
    DateTime Timestamp,
    string Label,
    decimal TradePnl,
    decimal CumulativePnl,
    decimal InvestedCapital,     // cumulative turnover
    decimal CumulativeRoiPct     // CumulativePnl ÷ peak capital deployed so far
);

/// <summary>
/// Detailed individual trade record for report table drilldown and export.
/// </summary>
public record TradingReportTradeDto(
    long Id,
    string Symbol,
    string Mode,                 // "Real", "Paper", "Manual Paper" or "Swing Sim"
    string Side,                 // "BUY" or "SELL"
    int Quantity,
    decimal EntryPrice,
    decimal ExecutedPrice,
    decimal InvestedAmount,
    // Gross P&L: (exit − entry) × qty, before charges.
    decimal RealizedPnl,
    // Net return % (NetPnl ÷ InvestedAmount).
    decimal ReturnPct,
    string TradeType,            // "Manual", "Auto" or "Swing"
    string ExitReason,
    DateTime ExecutedAt,
    int HoldDays,
    string? Username = null,
    // Estimated round-trip charges (ChargesCalculator) - CNC delivery rates, or MIS for a same-day round trip.
    decimal Charges = 0m,
    // RealizedPnl − Charges.
    decimal NetPnl = 0m,
    // Rate set used: CNC or MIS.
    string ChargeProduct = "CNC",
    // ACTUAL = Zerodha's contract-note charges for both legs (real trades); ESTIMATED = ChargesCalculator.
    string ChargeSource = "ESTIMATED",
    // Entry time when known (null = unknown; treated as opened at ExecutedAt for capital-deployed maths).
    DateTime? OpenedAt = null
);

/// <summary>
/// Complete aggregated response payload for the Trading Reports Dashboard.
/// </summary>
public record TradingReportResponseDto(
    ReportFilterDto Filter,
    TradingReportSummaryDto Summary,
    PagedResult<TradingReportPeriodDto> Periods,
    IReadOnlyList<TradingReportEquityPointDto> EquityCurve,
    PagedResult<TradingReportTradeDto> RecentTrades
);
