using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using QuantEdge.Domain.Entities;

namespace QuantEdge.Infrastructure.DTOs;

public class AutoTradeSettingsUpdateDto
{
    public bool IsAutoTradeEnabled { get; set; } = false;

    [Range(1000, 10000000, ErrorMessage = "Available Capital must be between ₹1,000 and ₹1,00,00,000.")]
    public decimal AvailableCapital { get; set; } = 100000.00m;

    [Range(0.1, 100.0, ErrorMessage = "Profit Target % must be between 0.1% and 100%.")]
    public decimal ProfitTargetPct { get; set; } = 5.00m;

    [Range(0.1, 100.0, ErrorMessage = "Stop Loss % must be between 0.1% and 100%.")]
    public decimal StopLossPct { get; set; } = 3.00m;

    [Range(1, 365, ErrorMessage = "Max Duration must be between 1 and 365 days.")]
    public int MaxDurationDays { get; set; } = 20;

    [Range(1, 50, ErrorMessage = "Max Trades Per Day must be between 1 and 50.")]
    public int MaxTradesPerDay { get; set; } = 5;

    [Range(100, 1000000, ErrorMessage = "Fixed Amount Per Trade must be between ₹100 and ₹10,00,000.")]
    public decimal FixedAmountPerTrade { get; set; } = 20000.00m;

    [Range(1, 13, ErrorMessage = "Min Conditions Match must be between 1 and 13.")]
    public int MinConditionsMatch { get; set; } = 12;

    public string TradingWindowStart { get; set; } = "09:15";
    public string TradingWindowEnd { get; set; } = "15:30";
}

public class AutoTradeDashboardDto
{
    public AutoTradeSettings Settings { get; set; } = new();
    public int TodayTradeCount { get; set; }
    public int MaxTradesPerDay => Settings.MaxTradesPerDay;
    public int ActivePositionsCount { get; set; }
    public decimal TotalUnrealizedPnl { get; set; }
    public decimal TotalRealizedPnlToday { get; set; }
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
