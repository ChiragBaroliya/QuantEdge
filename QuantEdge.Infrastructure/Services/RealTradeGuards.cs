using System;

namespace QuantEdge.Infrastructure.Services;

/// <summary>
/// Maps a skipped-BUY audit entry back to the pre-trade guard that stopped it, numbered in the order
/// <see cref="AutoRealTradeService"/>.EvaluateAndExecuteRealBuyCoreAsync runs them (the same numbering
/// the Real Trade Flow "Risk guards" diagram shows). Matching is on the start of the reason text
/// written there - keep the prefixes in sync when a guard's message changes.
/// </summary>
public static class RealTradeGuards
{
    public const int TotalGuards = 13;

    public sealed record Guard(int Number, string Name);

    private static readonly (string Prefix, Guard Guard)[] ReasonPrefixes =
    {
        ("Zerodha Token Invalid", new Guard(2, "Zerodha login valid?")),
        ("Outside Market Hours", new Guard(3, "In trading window?")),
        ("Outside trading window", new Guard(3, "In trading window?")),
        ("Opening entry delay", new Guard(4, "Past opening delay?")),
        ("Condition score", new Guard(5, "Signal strong enough?")),
        ("Daily limit of", new Guard(6, "Under trade cap?")),
        ("Portfolio exposure cap", new Guard(8, "Under portfolio cap?")),
        ("Symbol already has an OPEN real position", new Guard(9, "Not already held?")),
        ("Insufficient Broker Capital", new Guard(11, "Enough margin?")),
        ("Live price", new Guard(12, "Price still close?")),
        ("Calculated quantity 0", new Guard(13, "At least one share?")),
        ("Manual trade rejected", new Guard(13, "Trade inputs valid?"))
    };

    /// <summary>The guard behind a REAL_SIGNAL_SKIPPED / CIRCUIT_BREAKER entry, or null if unrecognised.</summary>
    public static Guard? Classify(string? actionType, string? reason)
    {
        if (string.Equals(actionType, "CIRCUIT_BREAKER", StringComparison.OrdinalIgnoreCase))
            return new Guard(7, "Under loss limit?");

        if (string.IsNullOrWhiteSpace(reason)) return null;
        foreach (var (prefix, guard) in ReasonPrefixes)
        {
            if (reason.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return guard;
        }
        return null;
    }
}
