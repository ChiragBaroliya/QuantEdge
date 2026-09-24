using System;
using System.Collections.Generic;

namespace QuantEdge.Infrastructure.Services;

/// <summary>
/// The 13 pre-trade guards, numbered in the order <see cref="AutoRealTradeService"/>
/// .EvaluateAndExecuteRealBuyCoreAsync runs them (the same numbering the Real Trade Flow "Risk guards"
/// diagram shows). <see cref="Classify"/> maps a skipped-BUY audit entry back to its guard by the
/// start of the reason text written there - keep the prefixes in sync when a guard's message changes.
/// </summary>
public static class RealTradeGuards
{
    public const int TotalGuards = 13;

    public sealed record Guard(int Number, string Name);

    private static readonly IReadOnlyDictionary<int, string> Names = new Dictionary<int, string>
    {
        [1] = "Bot switched on?",
        [2] = "Zerodha login valid?",
        [3] = "In trading window?",
        [4] = "Past opening delay?",
        [5] = "Signal strong enough?",
        [6] = "Under trade cap?",
        [7] = "Under loss limit?",
        [8] = "Under portfolio cap?",
        [9] = "Not already held?",
        [10] = "No pending BUY?",
        [11] = "Enough margin?",
        [12] = "Price still close?",
        [13] = "At least one share?"
    };

    public static Guard Get(int number) => new(number, Names.TryGetValue(number, out var name) ? name : $"Check {number}");

    private static readonly (string Prefix, int Number)[] ReasonPrefixes =
    {
        ("Zerodha Token Invalid", 2),
        ("Outside Market Hours", 3),
        ("Outside trading window", 3),
        ("Opening entry delay", 4),
        ("Condition score", 5),
        ("Daily limit of", 6),
        ("Portfolio exposure cap", 8),
        ("Symbol already has an OPEN real position", 9),
        ("Insufficient Broker Capital", 11),
        ("Live price", 12),
        ("Calculated quantity 0", 13)
    };

    /// <summary>The guard behind a REAL_SIGNAL_SKIPPED / CIRCUIT_BREAKER entry, or null if unrecognised.</summary>
    public static Guard? Classify(string? actionType, string? reason)
    {
        if (string.Equals(actionType, "CIRCUIT_BREAKER", StringComparison.OrdinalIgnoreCase))
            return Get(7);

        if (string.IsNullOrWhiteSpace(reason)) return null;
        if (reason.StartsWith("Manual trade rejected", StringComparison.OrdinalIgnoreCase))
            return new Guard(13, "Trade inputs valid?");

        foreach (var (prefix, number) in ReasonPrefixes)
        {
            if (reason.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return Get(number);
        }
        return null;
    }
}
