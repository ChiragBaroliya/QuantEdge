using System;
using System.Collections.Generic;

namespace QuantEdge.Infrastructure.DTOs;

public record SwingTradingDashboardDto(
    NiftyStatusDto NiftyStatus,
    List<SwingStockSignalDto> StockSignals,
    BacktestStatsDto BacktestStats15Days,
    BacktestStatsDto BacktestStats30Days,
    List<SwingTradeDto> RecentTrades
);

public record NiftyStatusDto(
    string Symbol,
    decimal Close,
    decimal Sma50,
    decimal Ema20,
    decimal Ema50,
    bool IsAboveSma50,
    bool IsEmaBullish,
    bool IsMarketFilterPassed
);

public record SwingStockSignalDto(
    string Symbol,
    decimal Close,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Ema20,
    decimal Ema50,
    decimal Ema200,
    decimal Rsi14,
    decimal Macd,
    decimal MacdSignal,
    decimal Adx14,
    decimal Atr14,
    long Volume,
    decimal AvgVolume20,
    decimal VolumeMultiplier,
    bool Is52WeekHigh,
    decimal High52Week,
    decimal ClosenessTo52WeekHighPct,
    bool IsLastCandleBullish,
    bool MeetsStockFilter,
    bool MeetsAllBuyRules,
    string Decision,
    string Reason
);

public record BacktestStatsDto(
    int PeriodDays,
    int TotalTrades,
    int WinTrades,
    int LossTrades,
    decimal WinRatePct,
    decimal NetProfitLossPct,
    decimal AvgProfitLossPct
);

public record SwingTradeDto(
    int Id,
    string Symbol,
    DateTime EntryDate,
    decimal EntryPrice,
    int Quantity,
    bool IsClosed,
    DateTime? ExitDate,
    decimal? ExitPrice,
    string? ExitReason,
    int HoldDays,
    decimal ProfitLossPct
);
