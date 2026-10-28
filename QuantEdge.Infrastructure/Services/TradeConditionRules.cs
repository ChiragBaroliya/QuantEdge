using System;
using System.Collections.Generic;
using System.Linq;
using QuantEdge.Domain.Entities;

namespace QuantEdge.Infrastructure.Services;

/// <summary>One of the 11 conditions both swing engines check, with the factor code each engine writes for it.</summary>
public sealed record TradeCondition(string Key, int Number, bool IsHardFilter, int MaxPoints,
    string BuyFactorCode, string ShortFactorCode, string BuyName, string ShortName)
{
    public string NameFor(bool isShort) => isShort ? ShortName : BuyName;
}

/// <summary>
/// Passed by the scan workers to the entry services when a user's Trade Conditions profile decided the candidate: the
/// condition-match guard then uses these instead of the mode settings' MinConditionsMatch out of 11.
/// </summary>
public sealed record ConditionGate(int MinConditionsMatch, int TotalConditions)
{
    public static ConditionGate From(ProfiledEvaluation e) => new(e.MinConditionsMatch, e.TotalCount);
}

/// <summary>A swing evaluation judged by one user's <see cref="TradeConditionProfile"/>.</summary>
public sealed record ProfiledEvaluation(
    bool HardFiltersPassed,
    IReadOnlyList<string> FailedHardFilters,
    int Score,
    int MetCount,
    int TotalCount,
    int MinConditionsMatch,
    int EnabledMaxPoints,
    bool IsSignal,
    bool Qualifies,
    string Reason);

/// <summary>
/// Applies a user's Trade Conditions profile (which of the 11 conditions are ON) to a swing engine result, for the
/// Auto Paper and Auto Real scan workers:
///   - only the hard filters that are ON must pass;
///   - the score is the points of the scoring conditions that are ON, rescaled to 100 (so BuyScoreThreshold keeps its meaning);
///   - the met-count and MinConditionsMatch are counted over the conditions that are ON.
/// With every condition ON this gives the engine's own score and met-count. In REGIME gate mode the BUY side's
/// NIFTY condition is not the profile's to decide - the market regime policy replaces it.
/// </summary>
public static class TradeConditionRules
{
    public const string Market = "MARKET";
    public const string Trend = "TREND";
    public const string Adx = "ADX";
    public const string Breakout = "BREAKOUT";
    public const string Volume = "VOLUME";
    public const string RelativeStrength = "REL_STRENGTH";
    public const string Mtf60m = "MTF_60M";
    public const string Rsi = "RSI";
    public const string Macd = "MACD";
    public const string Candle = "CANDLE";
    public const string RiskReward = "RISK_REWARD";

    public const int TotalConditions = 11;

    // Same order as the engines' checklists. Factor codes are the SwingFactorScore codes each engine writes.
    public static readonly IReadOnlyList<TradeCondition> All = new[]
    {
        new TradeCondition(Market, 1, true, 0, "", "", "NIFTY Market Filter", "NIFTY Bearish Filter"),
        new TradeCondition(Trend, 2, true, 0, "", "", "EMA Trend Alignment", "EMA Downtrend Alignment"),
        new TradeCondition(Adx, 3, true, 0, "", "", "ADX Trend Strength", "ADX Trend Strength"),
        new TradeCondition(Breakout, 4, false, 20, "BREAKOUT_GROUP", "BREAKDOWN_GROUP", "Breakout", "Breakdown"),
        new TradeCondition(Volume, 5, false, 15, "VOL_CONFIRMATION", "VOL_CONFIRMATION", "Volume Confirmation", "Volume Confirmation"),
        new TradeCondition(RelativeStrength, 6, false, 15, "RELATIVE_STRENGTH", "RELATIVE_WEAKNESS", "Relative Strength vs NIFTY", "Relative Weakness vs NIFTY"),
        new TradeCondition(Mtf60m, 7, false, 15, "MULTITIMEFRAME", "MULTITIMEFRAME", "60m Trend Confirmation", "60m Downtrend Confirmation"),
        new TradeCondition(Rsi, 8, false, 10, "RSI_MOMENTUM", "RSI_MOMENTUM", "RSI Momentum Zone", "RSI Bearish Zone"),
        new TradeCondition(Macd, 9, false, 10, "MACD_BULLISH", "MACD_BEARISH", "MACD Bullish", "MACD Bearish"),
        new TradeCondition(Candle, 10, false, 8, "BULLISH_CANDLE", "BEARISH_CANDLE", "Bullish Candle", "Bearish Candle"),
        new TradeCondition(RiskReward, 11, false, 7, "RISK_REWARD", "RISK_REWARD", "Risk : Reward", "Risk : Reward")
    };

    public static bool IsKnownKey(string? key) => key != null && All.Any(c => c.Key == key);

    /// <summary>Whether the profile applies this condition (ON, and not the BUY side's NIFTY filter in REGIME mode).</summary>
    public static bool IsApplied(TradeConditionProfile profile, TradeCondition condition, bool regimeMode) =>
        profile.IsEnabled(condition.Key) && !IsManagedByRegime(condition, profile.IsShort, regimeMode);

    /// <summary>In REGIME gate mode the market regime decides the BUY side's condition 1 (shorts keep their own NIFTY filter).</summary>
    public static bool IsManagedByRegime(TradeCondition condition, bool isShort, bool regimeMode) =>
        regimeMode && !isShort && condition.Key == Market;

    /// <summary>Conditions the profile can switch (11, or 10 for BUY in REGIME mode).</summary>
    public static int ConfigurableCount(bool isShort, bool regimeMode) =>
        All.Count(c => !IsManagedByRegime(c, isShort, regimeMode));

    public static int EnabledCount(TradeConditionProfile profile, bool regimeMode) =>
        All.Count(c => IsApplied(profile, c, regimeMode));

    public static int EnabledMaxPoints(TradeConditionProfile profile, bool regimeMode) =>
        All.Where(c => !c.IsHardFilter && IsApplied(profile, c, regimeMode)).Sum(c => c.MaxPoints);

    /// <summary>The profile's min-conditions (or <paramref name="fallbackMinMatch"/>), kept within 1..conditions ON.</summary>
    public static int EffectiveMinMatch(TradeConditionProfile profile, bool regimeMode, int fallbackMinMatch) =>
        Math.Clamp(profile.MinConditionsMatch ?? fallbackMinMatch, 1, Math.Max(1, EnabledCount(profile, regimeMode)));

    /// <summary>
    /// <see cref="Apply"/>, re-scoring the stock first when the engine stopped at a hard filter this profile switched
    /// off (its result then has no factor scores). <paramref name="scoreIgnoringHardFilters"/> runs the engine with
    /// skipHardFilterGate - called at most once, and only when needed. Returns the result to trade from (entry, stop,
    /// target, ATR).
    /// </summary>
    public static (ProfiledEvaluation Evaluation, SwingEvaluationResult Scored) Evaluate(
        TradeConditionProfile profile,
        SwingEvaluationResult result,
        Func<SwingEvaluationResult>? scoreIgnoringHardFilters,
        SwingStrategySettings settings,
        bool regimeMode,
        int fallbackMinMatch,
        RegimePolicy? regimePolicy = null)
    {
        var scored = result;
        if (result.Factors.Count == 0 && scoreIgnoringHardFilters != null
            && FailedHardFilters(profile, result, settings, regimeMode).Count == 0)
        {
            scored = scoreIgnoringHardFilters();
        }
        return (Apply(profile, scored, settings, regimeMode, fallbackMinMatch, regimePolicy), scored);
    }

    /// <summary>
    /// Judges <paramref name="r"/> by the profile. <paramref name="regimePolicy"/> (BUY in REGIME mode) replaces the
    /// signal / min-conditions test with the policy's: score ≥ MinStockScore and, where required, beating NIFTY.
    /// </summary>
    public static ProfiledEvaluation Apply(
        TradeConditionProfile profile,
        SwingEvaluationResult r,
        SwingStrategySettings settings,
        bool regimeMode,
        int fallbackMinMatch,
        RegimePolicy? regimePolicy = null)
    {
        bool isShort = profile.IsShort;
        bool marketIsSoft = !isShort && !settings.RequireNiftyMarketFilter;
        var failedHard = new List<string>();
        int met = 0, total = 0, maxPoints = 0, earned = 0, penalty = 0;

        foreach (var c in All)
        {
            if (!IsApplied(profile, c, regimeMode)) continue;
            total++;

            if (c.IsHardFilter)
            {
                if (HardFilterPassed(c, r)) met++;
                else if (c.Key == Market && marketIsSoft) penalty = settings.MarketContextScorePenalty; // legacy soft mode: penalty, not a reject
                else failedHard.Add(c.NameFor(isShort));
                continue;
            }

            maxPoints += c.MaxPoints;
            int points = FactorPoints(r, isShort ? c.ShortFactorCode : c.BuyFactorCode);
            earned += points;
            if (points > 0) met++;
        }

        bool scorable = r.Factors.Count > 0 && maxPoints > 0;
        int score = scorable
            ? Math.Clamp((int)Math.Round(earned * 100m / maxPoints, MidpointRounding.AwayFromZero) - penalty, 0, 100)
            : 0;
        int minMatch = Math.Clamp(profile.MinConditionsMatch ?? fallbackMinMatch, 1, Math.Max(1, total));
        bool hardPassed = failedHard.Count == 0;
        bool isSignal = hardPassed && scorable && score >= settings.BuyScoreThreshold;

        bool qualifies;
        string reason;
        if (!hardPassed)
        {
            qualifies = false;
            reason = $"hard filter failed: {string.Join(", ", failedHard)}";
        }
        else if (!scorable)
        {
            qualifies = false;
            reason = maxPoints == 0 ? "no scoring condition is on" : "stock could not be scored (insufficient data)";
        }
        else if (regimePolicy != null)
        {
            bool beatsNifty = FactorPoints(r, "RELATIVE_STRENGTH") > 0;
            qualifies = score >= regimePolicy.MinStockScore && (!regimePolicy.RequireRelativeStrength || beatsNifty);
            reason = qualifies
                ? $"score {score} ≥ {regimePolicy.MinStockScore} in {regimePolicy.Regime}"
                : score < regimePolicy.MinStockScore
                    ? $"score {score} < {regimePolicy.MinStockScore} needed in {regimePolicy.Regime}"
                    : $"not outperforming NIFTY (required in {regimePolicy.Regime})";
        }
        else
        {
            qualifies = isSignal || met >= minMatch;
            reason = isSignal
                ? $"score {score} ≥ {settings.BuyScoreThreshold}"
                : qualifies
                    ? $"{met}/{total} conditions met ≥ {minMatch}"
                    : $"score {score} < {settings.BuyScoreThreshold} and {met}/{total} met < {minMatch}";
        }

        return new ProfiledEvaluation(hardPassed, failedHard, score, met, total, minMatch, maxPoints, isSignal, qualifies, reason);
    }

    private static List<string> FailedHardFilters(TradeConditionProfile profile, SwingEvaluationResult r,
        SwingStrategySettings settings, bool regimeMode)
    {
        bool marketIsSoft = !profile.IsShort && !settings.RequireNiftyMarketFilter;
        return All
            .Where(c => c.IsHardFilter && IsApplied(profile, c, regimeMode) && !(c.Key == Market && marketIsSoft) && !HardFilterPassed(c, r))
            .Select(c => c.Key)
            .ToList();
    }

    // Both engines fill these flags before their hard-filter gate (the short engine's "market" flag is NIFTY bearish).
    private static bool HardFilterPassed(TradeCondition c, SwingEvaluationResult r) => c.Key switch
    {
        Market => r.IsMarketFilterPassed,
        Trend => r.EmaTrendPassed,
        _ => r.AdxPassed
    };

    private static int FactorPoints(SwingEvaluationResult r, string code) =>
        r.Factors.FirstOrDefault(f => f.Code == code)?.Points ?? 0;
}
