using QuantEdge.Domain.Entities;
using QuantEdge.Infrastructure.Constants;
using QuantEdge.Infrastructure.Services;

namespace QuantEdge.Infrastructure.DTOs;

/// <summary>
/// Read-only snapshot of every value that drives the Auto Real Trade flow, rendered by the admin
/// Trade Flow screen. Built from the live settings plus the shared rule/worker constants, so the
/// diagrams never drift from what the workers actually run with.
/// </summary>
public class TradeLogicRulesDto
{
    /// <summary>False when the Web host could not reach the API and fell back to entity defaults.</summary>
    public bool IsLive { get; set; } = true;
    public int UserId { get; set; }

    // Worker schedule
    public int ScanIntervalMinutes { get; set; }
    public int MonitorIntervalSeconds { get; set; }
    public int CandleHistoryCount { get; set; }
    public int MinDailyCandles { get; set; }
    public int LtpFreshnessSeconds { get; set; }
    public int RestFallbackMissThreshold { get; set; }

    // Per-user real trade settings
    public bool IsRealTradeEnabled { get; set; }
    public decimal AvailableCapital { get; set; }
    public decimal FixedAmountPerTrade { get; set; }
    public int MaxTradesPerDay { get; set; }
    public decimal EffectiveDailyLossLimit { get; set; }
    public bool IsDailyLossLimitDefault { get; set; }
    public int MinConditionsMatch { get; set; }
    public string TradingWindowStart { get; set; } = "09:15";
    public string TradingWindowEnd { get; set; } = "15:30";
    public int EntryDelayMinutes { get; set; }
    public string ProductType { get; set; } = "CNC";
    public string ExitMode { get; set; } = SwingTradeRules.ExitModeSwingClose;
    public string CloseCheckTime { get; set; } = "15:15";
    public decimal StopLossAtrMult { get; set; }
    public decimal TrailAtrMult { get; set; }
    public decimal TargetAtrMult { get; set; }
    public int MaxDurationDays { get; set; }

    // Global swing strategy settings
    public int BuyScoreThreshold { get; set; }
    public int WatchScoreThreshold { get; set; }
    public int MarketContextScorePenalty { get; set; }
    public decimal MarketProtectionBufferPct { get; set; }
    public decimal GapExitProtectionBufferPct { get; set; }

    // Fixed SwingTradeRules policy
    public int MaxConcurrentPositions { get; set; }
    public decimal MaxSignalDriftPct { get; set; }
    public decimal EmergencyExtraAtrMult { get; set; }
    public decimal TrailActivationAtrMult { get; set; }
    public decimal MaxStopLossPct { get; set; }
    public decimal MaxEmergencyStopPct { get; set; }
    public decimal GapBreachThresholdPct { get; set; }
    public decimal DefaultTrailingSlPct { get; set; }

    public static TradeLogicRulesDto Create(RealTradeSettings settings, SwingStrategySettings strategy, bool isLive = true)
    {
        var p = SwingTradeParams.From(settings);
        return new TradeLogicRulesDto
        {
            IsLive = isLive,
            UserId = settings.UserId,

            ScanIntervalMinutes = (int)RealTradeSchedule.ScanInterval.TotalMinutes,
            MonitorIntervalSeconds = (int)RealTradeSchedule.MonitorInterval.TotalSeconds,
            CandleHistoryCount = RealTradeSchedule.CandleHistoryCount,
            MinDailyCandles = RealTradeSchedule.MinDailyCandles,
            LtpFreshnessSeconds = (int)RealTradeSchedule.LtpFreshnessWindow.TotalSeconds,
            RestFallbackMissThreshold = RealTradeSchedule.RestFallbackMissThreshold,

            IsRealTradeEnabled = settings.IsRealTradeEnabled,
            AvailableCapital = settings.AvailableCapital,
            FixedAmountPerTrade = settings.FixedAmountPerTrade,
            MaxTradesPerDay = settings.MaxTradesPerDay,
            EffectiveDailyLossLimit = SwingTradeRules.EffectiveDailyLossLimit(settings.MaxDailyLossLimit, settings.AvailableCapital),
            IsDailyLossLimitDefault = !(settings.MaxDailyLossLimit.HasValue && settings.MaxDailyLossLimit.Value > 0),
            MinConditionsMatch = settings.MinConditionsMatch,
            TradingWindowStart = settings.TradingWindowStart,
            TradingWindowEnd = settings.TradingWindowEnd,
            EntryDelayMinutes = settings.EntryDelayMinutes,
            ProductType = settings.ProductType,
            ExitMode = p.ExitMode,
            CloseCheckTime = p.CloseCheckTime.ToString(@"hh\:mm"),
            StopLossAtrMult = p.StopLossAtrMult,
            TrailAtrMult = p.TrailAtrMult,
            TargetAtrMult = p.TargetAtrMult,
            MaxDurationDays = settings.MaxDurationDays,

            BuyScoreThreshold = strategy.BuyScoreThreshold,
            WatchScoreThreshold = strategy.WatchScoreThreshold,
            MarketContextScorePenalty = strategy.MarketContextScorePenalty,
            MarketProtectionBufferPct = strategy.MarketProtectionBufferPct,
            GapExitProtectionBufferPct = AutoRealTradeService.GapExitProtectionBufferPct,

            MaxConcurrentPositions = SwingTradeRules.MaxConcurrentPositions,
            MaxSignalDriftPct = SwingTradeRules.MaxSignalDriftPct,
            EmergencyExtraAtrMult = SwingTradeRules.EmergencyExtraAtrMult,
            TrailActivationAtrMult = SwingTradeRules.TrailActivationAtrMult,
            MaxStopLossPct = SwingTradeRules.MaxStopLossPct,
            MaxEmergencyStopPct = SwingTradeRules.MaxEmergencyStopPct,
            GapBreachThresholdPct = SwingTradeRules.GapBreachThresholdPct,
            DefaultTrailingSlPct = SwingTradeRules.DefaultTrailingSlPct
        };
    }
}
