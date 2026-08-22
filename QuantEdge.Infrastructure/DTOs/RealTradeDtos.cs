using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using QuantEdge.Domain.Entities;

namespace QuantEdge.Infrastructure.DTOs;

public class RealTradeSettingsUpdateDto
{
    public bool IsRealTradeEnabled { get; set; } = false;

    [Range(typeof(decimal), "100", "50000000", ErrorMessage = "Available Capital must be between ₹100 and ₹5,00,00,000.")]
    public decimal AvailableCapital { get; set; } = 2000.00m;

    [Range(typeof(decimal), "0.1", "100.0", ErrorMessage = "Profit Target % must be between 0.1% and 100%.")]
    public decimal ProfitTargetPct { get; set; } = 5.00m;

    // Optional Stop Loss %
    [Range(typeof(decimal), "0.1", "100.0", ErrorMessage = "Stop Loss % must be between 0.1% and 100%.")]
    public decimal? StopLossPct { get; set; }

    // Optional Trailing SL
    public bool TrailingSlEnabled { get; set; } = false;

    [Range(typeof(decimal), "0.1", "50.0", ErrorMessage = "Trailing Stop Loss % must be between 0.1% and 50%.")]
    public decimal? TrailingSlPct { get; set; }

    [Range(1, 365, ErrorMessage = "Max Duration must be between 1 and 365 days.")]
    public int MaxDurationDays { get; set; } = 20;

    [Range(1, 50, ErrorMessage = "Max Trades Per Day must be between 1 and 50.")]
    public int MaxTradesPerDay { get; set; } = 5;

    [Range(typeof(decimal), "10", "5000000", ErrorMessage = "Trade Amount must be between ₹10 and ₹50,00,000.")]
    public decimal FixedAmountPerTrade { get; set; } = 400.00m;

    // Optional Daily Loss Circuit Breaker
    [Range(typeof(decimal), "10", "10000000", ErrorMessage = "Daily Loss Limit must be between ₹10 and ₹1,00,00,000.")]
    public decimal? MaxDailyLossLimit { get; set; }

    public string ProductType { get; set; } = "CNC"; // CNC or MIS

    [Range(1, 13, ErrorMessage = "Min Conditions Match must be between 1 and 13.")]
    public int MinConditionsMatch { get; set; } = 10;

    public string TradingWindowStart { get; set; } = "09:15";
    public string TradingWindowEnd { get; set; } = "15:30";
}

public class RealTradeDashboardDto
{
    public RealTradeSettings Settings { get; set; } = new();
    public int TodayTradeCount { get; set; }
    public decimal TodayTradeAmount { get; set; }
    public int MaxTradesPerDay => Settings.MaxTradesPerDay;
    public int ActivePositionsCount { get; set; }
    public decimal TotalUnrealizedPnl { get; set; }
    public decimal TotalRealizedPnlToday { get; set; }
    public decimal AvailableBrokerMargin { get; set; }
    public decimal UsedBrokerMargin { get; set; }
    public bool IsBrokerTokenActive { get; set; }
    public bool IsDdpiEnabled { get; set; }
    public string ClientId { get; set; } = string.Empty;
    public string AccountHolderName { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public string BrokerTokenCreatedIst { get; set; } = string.Empty;
    public string BrokerTokenExpiresIst { get; set; } = string.Empty;
    public bool TpinGuidanceRequired { get; set; } = true;
    public bool IsWebSocketConnected { get; set; }
    public bool IsRestPollingFallback { get; set; }
    public string SystemStatus { get; set; } = "IDLE"; // LIVE_ACTIVE, PAUSED, TOKEN_EXPIRED, KILL_SWITCH_TRIGGERED
    public IEnumerable<RealPosition> OpenPositions { get; set; } = new List<RealPosition>();
    public IEnumerable<RealOrder> RecentOrders { get; set; } = new List<RealOrder>();
    public IEnumerable<RealTradeExecutionLog> TodayLogs { get; set; } = new List<RealTradeExecutionLog>();
    public DateTime? NextRunTime { get; set; }
    public int NextRunSeconds { get; set; }
    public string NextRunFormatted { get; set; } = string.Empty;
    public bool IsMarketOpen { get; set; }

    // Live Zerodha Broker P&L & Positions
    public ZerodhaPositionsDto? BrokerPositions { get; set; }
    public List<ZerodhaHoldingDto>? BrokerHoldings { get; set; }
    public decimal ZerodhaTotalM2M { get; set; }
    public decimal ZerodhaRealizedPnl { get; set; }
    public decimal ZerodhaUnrealizedPnl { get; set; }
}

public class UpdateDdpiDto
{
    public int UserId { get; set; } = 1;
    public bool IsDdpiEnabled { get; set; }
}

public class ZerodhaPositionsDto
{
    public List<ZerodhaPositionItemDto> Net { get; set; } = new();
    public List<ZerodhaPositionItemDto> Day { get; set; } = new();
    public decimal TotalM2M { get; set; }
    public decimal TotalRealizedPnl { get; set; }
    public decimal TotalUnrealizedPnl { get; set; }
}

public class ZerodhaPositionItemDto
{
    public string TradingSymbol { get; set; } = string.Empty;
    public string Exchange { get; set; } = "NSE";
    public string Product { get; set; } = "CNC"; // CNC, MIS, NRML
    public int Quantity { get; set; }
    public int BuyQuantity { get; set; }
    public int SellQuantity { get; set; }
    public decimal BuyPrice { get; set; }
    public decimal SellPrice { get; set; }
    public decimal BuyValue { get; set; }
    public decimal SellValue { get; set; }
    public decimal LastPrice { get; set; }
    public decimal ClosePrice { get; set; }
    public decimal Pnl { get; set; }
    public decimal M2m { get; set; }
    public decimal Realised { get; set; }
    public decimal Unrealised { get; set; }
    public decimal Value { get; set; }
    public decimal Multiplier { get; set; } = 1;
}

public class ZerodhaHoldingDto
{
    public string TradingSymbol { get; set; } = string.Empty;
    public string Exchange { get; set; } = "NSE";
    public string Isin { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public int T1Quantity { get; set; }
    public int RealisedQuantity { get; set; }
    public decimal AveragePrice { get; set; }
    public decimal LastPrice { get; set; }
    public decimal ClosePrice { get; set; }
    public decimal Pnl { get; set; }
    public decimal DayChange { get; set; }
    public decimal DayChangePercentage { get; set; }
    public decimal Value { get; set; }
}

public class ToggleRealTradeRequestDto
{
    public bool Enabled { get; set; }
}

public class EmergencyKillSwitchRequestDto
{
    public string? Reason { get; set; }
}

public class CloseRealPositionRequestDto
{
    public int PositionId { get; set; }
    public string? Reason { get; set; }
}

public class RealTradeLivePositionsFastDto
{
    public bool Success { get; set; }
    public bool IsBrokerTokenActive { get; set; }
    public decimal AvailableBrokerMargin { get; set; }
    public decimal UsedBrokerMargin { get; set; }
    public decimal ZerodhaTotalM2M { get; set; }
    public decimal ZerodhaRealizedPnl { get; set; }
    public decimal ZerodhaUnrealizedPnl { get; set; }
    public decimal TotalUnrealizedPnl { get; set; }
    public decimal TotalRealizedPnlToday { get; set; }
    public ZerodhaPositionsDto? BrokerPositions { get; set; }
    public List<ZerodhaHoldingDto>? BrokerHoldings { get; set; }
    public IEnumerable<RealPosition> OpenPositions { get; set; } = new List<RealPosition>();
}
