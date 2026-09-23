using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using QuantEdge.Domain.Entities;

namespace QuantEdge.Infrastructure.DTOs;

public class AutoTradeSettingsUpdateDto
{
    public bool IsAutoTradeEnabled { get; set; } = false;

    [Range(typeof(decimal), "100", "10000000", ErrorMessage = "Available Capital must be between ₹100 and ₹1,00,00,000.")]
    public decimal AvailableCapital { get; set; } = 100000.00m;

    [Range(typeof(decimal), "0.1", "100.0", ErrorMessage = "Profit Target % must be between 0.1% and 100%.")]
    public decimal ProfitTargetPct { get; set; } = 5.00m;

    // Stop Loss % / Trailing SL % are no longer global settings (same as Real Trade) - SL, Target and
    // Trailing SL come from the shared swing exit policy below (SwingTradeRules).

    // Optional Daily Loss Circuit Breaker override (defaults to 10% of Available Capital)
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

    [Range(1, 13, ErrorMessage = "Min Conditions Match must be between 1 and 13.")]
    public int MinConditionsMatch { get; set; } = 10;

    public string TradingWindowStart { get; set; } = "09:15";
    public string TradingWindowEnd { get; set; } = "15:30";
}

public class AutoTradeDashboardDto
{
    public AutoTradeSettings Settings { get; set; } = new();
    public int TodayTradeCount { get; set; }
    public decimal TodayTradeAmount { get; set; }
    public int MaxTradesPerDay => Settings.MaxTradesPerDay;
    public int ActivePositionsCount { get; set; }
    public decimal TotalUnrealizedPnl { get; set; }
    public decimal TotalRealizedPnlToday { get; set; }
    public decimal AvailableMargin { get; set; }
    public decimal UsedMargin { get; set; }
    public bool IsWebSocketConnected { get; set; }
    public bool IsRestPollingFallback { get; set; }
    public string SystemStatus { get; set; } = "IDLE"; // ACTIVE, PAUSED, TOKEN_EXPIRED, STOPPED
    public IEnumerable<PaperPosition> OpenPositions { get; set; } = new List<PaperPosition>();
    public IEnumerable<AutoTradeExecutionLog> TodayLogs { get; set; } = new List<AutoTradeExecutionLog>();
    public DateTime? NextRunTime { get; set; }
    public int NextRunSeconds { get; set; }
    public string NextRunFormatted { get; set; } = string.Empty;
    public bool IsMarketOpen { get; set; }
}

public class ToggleAutoTradeRequestDto
{
    public bool Enabled { get; set; }
}
