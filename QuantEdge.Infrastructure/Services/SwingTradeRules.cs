using System;
using QuantEdge.Domain.Entities;
using QuantEdge.Infrastructure.Helpers;

namespace QuantEdge.Infrastructure.Services;

/// <summary>
/// Settings-derived parameters for <see cref="SwingTradeRules"/>. Built from either
/// <see cref="RealTradeSettings"/> or <see cref="AutoTradeSettings"/> so Auto Paper Trading and Auto
/// Real Trading always run the exact same buy/sell rules - only order execution differs.
/// </summary>
public sealed record SwingTradeParams(
    string ExitMode,
    TimeSpan CloseCheckTime,
    TimeSpan TradingWindowEnd,
    decimal StopLossAtrMult,
    decimal TrailAtrMult,
    decimal TargetAtrMult,
    decimal ProfitTargetPct)
{
    public bool IsSwingClose => !string.Equals(ExitMode, SwingTradeRules.ExitModeIntraday, StringComparison.OrdinalIgnoreCase);

    public static SwingTradeParams From(RealTradeSettings s) =>
        Create(s.ExitMode, s.CloseCheckTime, s.TradingWindowEnd, s.StopLossAtrMult, s.TrailAtrMult, s.TargetAtrMult, s.ProfitTargetPct);

    public static SwingTradeParams From(AutoTradeSettings s) =>
        Create(s.ExitMode, s.CloseCheckTime, s.TradingWindowEnd, s.StopLossAtrMult, s.TrailAtrMult, s.TargetAtrMult, s.ProfitTargetPct);

    private static SwingTradeParams Create(string? exitMode, string? closeCheckTime, string? windowEnd,
        decimal slMult, decimal trailMult, decimal targetMult, decimal profitTargetPct)
    {
        return new SwingTradeParams(
            string.IsNullOrWhiteSpace(exitMode) ? SwingTradeRules.ExitModeSwingClose : exitMode.Trim().ToUpperInvariant(),
            TimeSpan.TryParse(closeCheckTime, out var cc) ? cc : SwingTradeRules.DefaultCloseCheckTime,
            TimeSpan.TryParse(windowEnd, out var we) ? we : new TimeSpan(15, 30, 0),
            slMult > 0 ? slMult : SwingTradeRules.DefaultStopLossAtrMult,
            trailMult > 0 ? trailMult : SwingTradeRules.DefaultTrailAtrMult,
            targetMult > 0 ? targetMult : SwingTradeRules.DefaultTargetAtrMult,
            Math.Abs(profitTargetPct));
    }
}

/// <summary>The broker-agnostic parts of an open position that the exit rules need.</summary>
public sealed record ExitPositionView(
    decimal EntryPrice,
    decimal? StopLoss,
    decimal? TakeProfit,
    decimal? TrailingStopLoss,
    decimal? ManualTrailingSlPct,
    DateTime OpenedAtUtc)
{
    public static ExitPositionView From(RealPosition p) => new(
        p.AverageEntryPrice, p.StopLoss, p.TakeProfit, p.TrailingStopLoss,
        p.TradeType == TradeType.Manual ? p.TrailingSlPct : null, p.OpenedAt);

    public static ExitPositionView From(PaperPosition p) => new(
        p.AverageEntryPrice, p.StopLoss, p.TakeProfit, p.TrailingStopLoss,
        p.TradeType == TradeType.Manual ? p.TrailingSlPct : null, p.OpenedAt);
}

public sealed record EntryLevels(decimal StopLoss, decimal TakeProfit, decimal? TrailingStopLoss);

public sealed record ExitDecision(bool ShouldExit, string Reason, bool IsGap, decimal? NewTrailingStopLoss)
{
    public static readonly ExitDecision Hold = new(false, string.Empty, false, null);
}

/// <summary>
/// Single source of truth for the swing buy/sell rules shared by Auto Paper Trading
/// (<see cref="AutoTradeService"/>) and Auto Real Trading (<see cref="AutoRealTradeService"/>).
///
/// SWING_CLOSE exit mode (default) - built so ordinary intraday noise can't shake a swing trade out:
///   1. Target            - live, any time.
///   2. Emergency stop     - live, any time: entry - (SL mult + 1) x daily ATR, capped at 12% loss.
///                          The only stop that can sell during the day.
///   3. Closing-basis stop - checked only from CloseCheckTime to the window end: exits only if price
///                          is still below the Stop Loss / Trailing SL near the close.
///   4. Trailing SL        - never on the entry day; activates once price reaches entry + 1 x ATR,
///                          starting at breakeven, then trails (price - TrailAtrMult x ATR), raised
///                          only in the closing window so it follows the swing, not the ticks.
///   5. Max days hold      - evaluated in the closing window.
///
/// INTRADAY exit mode - the previous behavior (every check live, 2% trail ratcheting each cycle), kept
/// so the strategy can be switched back from Settings without a redeploy.
/// </summary>
public static class SwingTradeRules
{
    public const string ExitModeSwingClose = "SWING_CLOSE";
    public const string ExitModeIntraday = "INTRADAY";

    public static readonly TimeSpan DefaultCloseCheckTime = new(15, 15, 0);
    public const decimal DefaultStopLossAtrMult = 1.5m;
    public const decimal DefaultTrailAtrMult = 3.0m;
    public const decimal DefaultTargetAtrMult = 3.0m;

    // Entry gates shared by both engines.
    public const int MaxConcurrentPositions = 10;
    public const decimal DefaultDailyLossLimitFactor = 0.10m; // 10% of AvailableCapital
    public const decimal MaxSignalDriftPct = 2.0m;

    // Fallbacks for positions without an ATR (holdings enrollment, pre-existing positions).
    public const decimal DefaultStopLossPct = 3.00m;
    public const decimal DefaultTrailingSlPct = 2.00m; // INTRADAY mode trail distance

    // Fixed parts of the SWING_CLOSE policy (the tunable parts live on the settings).
    public const decimal EmergencyExtraAtrMult = 1.0m;
    public const decimal TrailActivationAtrMult = 1.0m;
    public const decimal MinAtrPct = 1.0m;
    public const decimal MaxStopLossPct = 8.0m;
    public const decimal MaxEmergencyStopPct = 12.0m;

    // A stop/target "hit" where price is already this far past the level (gap open, or a fast move
    // between monitor cycles) is flagged so the real engine can widen its exit order's price band.
    public const decimal GapBreachThresholdPct = 1.5m;

    // The closing window always gets at least this long before the trading window ends, even if
    // CloseCheckTime was set later than (or equal to) the window end.
    private static readonly TimeSpan MinCloseWindow = TimeSpan.FromMinutes(10);

    public static DateTime NowIst() => ToIst(DateTime.UtcNow);

    public static DateTime ToIst(DateTime time)
    {
        var utc = time.Kind switch
        {
            DateTimeKind.Local => time.ToUniversalTime(),
            DateTimeKind.Unspecified => DateTime.SpecifyKind(time, DateTimeKind.Utc),
            _ => time
        };
        return TimeZoneInfo.ConvertTimeFromUtc(utc, TimeZoneHelper.IndianTimeZone);
    }

    // ------------------------------------------------------------------------------------------
    // Entry gates
    // ------------------------------------------------------------------------------------------

    public static bool IsWithinTradingWindow(string startTime, string endTime, DateTime nowIst)
    {
        if (nowIst.DayOfWeek == DayOfWeek.Saturday || nowIst.DayOfWeek == DayOfWeek.Sunday)
            return false;

        if (!TimeSpan.TryParse(startTime, out var start)) start = new TimeSpan(9, 15, 0);
        if (!TimeSpan.TryParse(endTime, out var end)) end = new TimeSpan(15, 30, 0);

        var timeOfDay = nowIst.TimeOfDay;
        return timeOfDay >= start && timeOfDay <= end;
    }

    // Entry-only gate: true once "now" (IST) is at or past windowStartTime + entryDelayMinutes.
    // Never applied to exits - an open position must always be able to stop out.
    public static bool IsPastEntryDelay(string windowStartTime, int entryDelayMinutes, DateTime nowIst)
    {
        if (entryDelayMinutes <= 0) return true;
        if (!TimeSpan.TryParse(windowStartTime, out var start)) start = new TimeSpan(9, 15, 0);
        return nowIst.TimeOfDay >= start + TimeSpan.FromMinutes(entryDelayMinutes);
    }

    public static decimal EffectiveDailyLossLimit(decimal? configuredLimit, decimal availableCapital) =>
        configuredLimit.HasValue && configuredLimit.Value > 0
            ? Math.Abs(configuredLimit.Value)
            : Math.Max(1m, availableCapital * DefaultDailyLossLimitFactor);

    /// <summary>
    /// Guards against buying a signal whose price has already moved away from the scanned candle
    /// close - either dropped (no longer the setup that was scored) or run up (chasing a local high).
    /// Returns the skip reason, or null when the live price is close enough to trade.
    /// </summary>
    public static string? CheckSignalDrift(decimal scanPrice, decimal livePrice)
    {
        if (scanPrice <= 0m || livePrice <= 0m) return null;

        decimal movePct = (livePrice - scanPrice) / scanPrice * 100m;
        if (movePct <= -MaxSignalDriftPct)
            return $"Live price ₹{livePrice:N2} has moved {movePct:F2}% below the scanned entry ₹{scanPrice:N2} - signal stale, skipping";
        if (movePct >= MaxSignalDriftPct)
            return $"Live price ₹{livePrice:N2} has already moved {movePct:F2}% above the scanned entry ₹{scanPrice:N2} - too extended to chase, skipping";
        return null;
    }

    // ------------------------------------------------------------------------------------------
    // Entry levels
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// Stop Loss / Target / initial Trailing SL for a new position.
    /// <paramref name="dailyAtr"/> is the daily ATR(14) at signal time (SWING_CLOSE levels);
    /// <paramref name="engineStopLoss"/>/<paramref name="engineTarget"/> are the SwingDecisionEngine's
    /// own levels (INTRADAY levels). Manual %s, when given, override both.
    /// </summary>
    public static EntryLevels ComputeEntryLevels(
        decimal entryPrice,
        decimal? dailyAtr,
        decimal? engineStopLoss,
        decimal? engineTarget,
        decimal? manualStopLossPct,
        decimal? manualTrailingSlPct,
        SwingTradeParams p)
    {
        bool isManual = manualStopLossPct.HasValue && manualStopLossPct.Value > 0;
        decimal pctTarget = Math.Round(entryPrice * (1m + p.ProfitTargetPct / 100m), 2);

        if (p.IsSwingClose)
        {
            decimal stopLoss;
            decimal takeProfit;
            if (isManual)
            {
                stopLoss = Math.Round(entryPrice * (1m - Math.Abs(manualStopLossPct!.Value) / 100m), 2);
                takeProfit = pctTarget;
            }
            else if (dailyAtr.HasValue && dailyAtr.Value > 0)
            {
                decimal atr = Math.Max(dailyAtr.Value, entryPrice * MinAtrPct / 100m);
                decimal atrStop = entryPrice - p.StopLossAtrMult * atr;
                decimal cappedStop = entryPrice * (1m - MaxStopLossPct / 100m);
                stopLoss = Math.Round(Math.Max(atrStop, cappedStop), 2);
                takeProfit = Math.Round(entryPrice + p.TargetAtrMult * atr, 2);
            }
            else
            {
                stopLoss = Math.Round(entryPrice * (1m - DefaultStopLossPct / 100m), 2);
                takeProfit = pctTarget;
            }

            // No trailing SL at entry - it activates only after the trade has moved in our favor.
            return new EntryLevels(stopLoss, takeProfit, null);
        }

        // INTRADAY (previous behavior): engine levels, else fixed/manual %, with a trail from entry.
        decimal intradayTarget = !isManual && engineTarget.HasValue && engineTarget.Value > entryPrice
            ? Math.Round(engineTarget.Value, 2)
            : pctTarget;

        decimal slPct = isManual ? Math.Abs(manualStopLossPct!.Value) : DefaultStopLossPct;
        decimal intradayStop = !isManual && engineStopLoss.HasValue && engineStopLoss.Value > 0 && engineStopLoss.Value < entryPrice
            ? Math.Round(engineStopLoss.Value, 2)
            : Math.Round(entryPrice * (1m - slPct / 100m), 2);

        decimal trailPct = manualTrailingSlPct.HasValue && manualTrailingSlPct.Value > 0 ? Math.Abs(manualTrailingSlPct.Value) : DefaultTrailingSlPct;
        decimal intradayTrail = Math.Round(entryPrice * (1m - trailPct / 100m), 2);

        return new EntryLevels(intradayStop, intradayTarget, intradayTrail);
    }

    // ------------------------------------------------------------------------------------------
    // Exit evaluation
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// Decides whether an open position should be sold at <paramref name="ltp"/>, and whether its
    /// trailing SL should move. Pure function - callers execute the sell / persist the new trail.
    /// </summary>
    public static ExitDecision EvaluateExit(
        ExitPositionView pos,
        decimal ltp,
        DateTime nowIst,
        int tradingDaysOpen,
        int maxDurationDays,
        SwingTradeParams p)
    {
        if (ltp <= 0m || pos.EntryPrice <= 0m) return ExitDecision.Hold;

        return p.IsSwingClose
            ? EvaluateSwingClose(pos, ltp, nowIst, tradingDaysOpen, maxDurationDays, p)
            : EvaluateIntraday(pos, ltp, tradingDaysOpen, maxDurationDays);
    }

    private static ExitDecision EvaluateSwingClose(ExitPositionView pos, decimal ltp, DateTime nowIst,
        int tradingDaysOpen, int maxDurationDays, SwingTradeParams p)
    {
        decimal entry = pos.EntryPrice;
        decimal atr = InferAtr(pos, p);

        // 1. Target - live.
        if (pos.TakeProfit.HasValue && ltp >= pos.TakeProfit.Value)
        {
            decimal gapPct = (ltp - pos.TakeProfit.Value) / pos.TakeProfit.Value * 100m;
            return gapPct >= GapBreachThresholdPct
                ? new ExitDecision(true, $"Target Hit (Gap - price already {gapPct:F1}% above target)", false, null)
                : new ExitDecision(true, "Target Hit", false, null);
        }

        // 2. Emergency stop - live. Never above the closing-basis Stop Loss.
        decimal emergency = GetEmergencyStop(pos, p);
        if (ltp <= emergency)
        {
            decimal gapPct = (emergency - ltp) / emergency * 100m;
            bool isGap = gapPct >= GapBreachThresholdPct;
            string reason = isGap
                ? $"Emergency Stop Hit @ ₹{emergency:F2} (Gap - price already {gapPct:F1}% below trigger)"
                : $"Emergency Stop Hit @ ₹{emergency:F2}";
            return new ExitDecision(true, reason, isGap, null);
        }

        if (!IsInClosingWindow(nowIst, p))
            return ExitDecision.Hold;

        // A trailing SL below entry is a leftover from INTRADAY mode (swing trails always start at
        // breakeven) - it is ignored here and replaced once the trade qualifies for trailing.
        bool trailActive = pos.TrailingStopLoss.HasValue && pos.TrailingStopLoss.Value >= entry;

        // 3a. Trailing SL - closing basis.
        if (trailActive && ltp <= pos.TrailingStopLoss!.Value)
            return new ExitDecision(true, $"Trailing SL Hit @ ₹{pos.TrailingStopLoss.Value:F2} (Closing Basis)", false, null);

        // 3b. Stop Loss - closing basis.
        if (pos.StopLoss.HasValue && pos.StopLoss.Value > 0 && ltp <= pos.StopLoss.Value)
            return new ExitDecision(true, $"Stop Loss Hit @ ₹{pos.StopLoss.Value:F2} (Closing Basis)", false, null);

        // 4. Max days hold - at the close of the last allowed day.
        if (maxDurationDays > 0 && tradingDaysOpen >= maxDurationDays)
            return new ExitDecision(true, $"Max Duration ({maxDurationDays} Trading Days) Exit", false, null);

        // 5. Trailing SL management - never on the entry day.
        if (ToIst(pos.OpenedAtUtc).Date == nowIst.Date)
            return ExitDecision.Hold;

        decimal candidate = pos.ManualTrailingSlPct.HasValue && pos.ManualTrailingSlPct.Value > 0
            ? ltp * (1m - Math.Abs(pos.ManualTrailingSlPct.Value) / 100m)
            : ltp - p.TrailAtrMult * atr;
        candidate = Math.Round(Math.Max(entry, candidate), 2); // breakeven floor

        if (!trailActive)
        {
            decimal activationPrice = entry + TrailActivationAtrMult * atr;
            return ltp >= activationPrice
                ? new ExitDecision(false, string.Empty, false, candidate)
                : ExitDecision.Hold;
        }

        return candidate > pos.TrailingStopLoss!.Value
            ? new ExitDecision(false, string.Empty, false, candidate)
            : ExitDecision.Hold;
    }

    private static ExitDecision EvaluateIntraday(ExitPositionView pos, decimal ltp, int tradingDaysOpen, int maxDurationDays)
    {
        if (pos.TakeProfit.HasValue && ltp >= pos.TakeProfit.Value)
        {
            decimal gapPct = (ltp - pos.TakeProfit.Value) / pos.TakeProfit.Value * 100m;
            return gapPct >= GapBreachThresholdPct
                ? new ExitDecision(true, $"Target Hit (Gap - price already {gapPct:F1}% above target)", false, null)
                : new ExitDecision(true, "Target Hit", false, null);
        }

        if (pos.TrailingStopLoss.HasValue && ltp <= pos.TrailingStopLoss.Value)
        {
            decimal gapPct = (pos.TrailingStopLoss.Value - ltp) / pos.TrailingStopLoss.Value * 100m;
            bool isGap = gapPct >= GapBreachThresholdPct;
            string reason = isGap
                ? $"Trailing SL Hit @ ₹{pos.TrailingStopLoss.Value:F2} (Gap - price already {gapPct:F1}% below trigger)"
                : $"Trailing SL Hit @ ₹{pos.TrailingStopLoss.Value:F2}";
            return new ExitDecision(true, reason, isGap, null);
        }

        if (pos.StopLoss.HasValue && pos.StopLoss.Value > 0 && ltp <= pos.StopLoss.Value)
        {
            decimal gapPct = (pos.StopLoss.Value - ltp) / pos.StopLoss.Value * 100m;
            bool isGap = gapPct >= GapBreachThresholdPct;
            string reason = isGap
                ? $"Stop Loss Hit (Gap - price already {gapPct:F1}% below trigger)"
                : "Stop Loss Hit";
            return new ExitDecision(true, reason, isGap, null);
        }

        if (maxDurationDays > 0 && tradingDaysOpen >= maxDurationDays)
            return new ExitDecision(true, $"Max Duration ({maxDurationDays} Trading Days) Exit", false, null);

        // Ratchet every cycle.
        decimal trailPct = pos.ManualTrailingSlPct.HasValue && pos.ManualTrailingSlPct.Value > 0
            ? Math.Abs(pos.ManualTrailingSlPct.Value)
            : DefaultTrailingSlPct;
        decimal candidate = Math.Round(ltp * (1m - trailPct / 100m), 2);
        return !pos.TrailingStopLoss.HasValue || candidate > pos.TrailingStopLoss.Value
            ? new ExitDecision(false, string.Empty, false, candidate)
            : ExitDecision.Hold;
    }

    public static bool IsInClosingWindow(DateTime nowIst, SwingTradeParams p)
    {
        TimeSpan start = p.CloseCheckTime;
        TimeSpan latestStart = p.TradingWindowEnd - MinCloseWindow;
        if (start > latestStart) start = latestStart;
        return nowIst.TimeOfDay >= start;
    }

    /// <summary>
    /// The position's volatility unit. The Stop Loss was placed StopLossAtrMult x ATR below entry, so
    /// the ATR is recovered from that distance (no extra column needed, and it works for positions
    /// opened before this policy existed). Floored at <see cref="MinAtrPct"/> of entry.
    /// </summary>
    public static decimal InferAtr(ExitPositionView pos, SwingTradeParams p)
    {
        decimal entry = pos.EntryPrice;
        decimal slDistance = pos.StopLoss.HasValue && pos.StopLoss.Value > 0 && pos.StopLoss.Value < entry
            ? entry - pos.StopLoss.Value
            : entry * DefaultStopLossPct / 100m;
        return Math.Max(slDistance / p.StopLossAtrMult, entry * MinAtrPct / 100m);
    }

    public static decimal GetEmergencyStop(ExitPositionView pos, SwingTradeParams p)
    {
        decimal entry = pos.EntryPrice;
        decimal atr = InferAtr(pos, p);
        decimal emergency = Math.Max(
            entry - (p.StopLossAtrMult + EmergencyExtraAtrMult) * atr,
            entry * (1m - MaxEmergencyStopPct / 100m));
        if (pos.StopLoss.HasValue && pos.StopLoss.Value > 0)
            emergency = Math.Min(emergency, pos.StopLoss.Value);
        return Math.Round(emergency, 2);
    }
}
