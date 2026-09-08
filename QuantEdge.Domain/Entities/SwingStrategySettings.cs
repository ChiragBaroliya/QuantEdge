namespace QuantEdge.Domain.Entities;

/// <summary>
/// Tunable parameters for the shared swing scoring engine (<see cref="QuantEdge.Infrastructure.Services.SwingDecisionEngine"/>),
/// used by the Swing Trading dashboard, Auto Paper Trade, and Auto Real Trade scanners alike.
/// </summary>
public class SwingStrategySettings
{
    public int Id { get; set; } = 1;

    /// <summary>Minimum score (0-100) for a BUY decision.</summary>
    public int BuyScoreThreshold { get; set; } = 70;

    /// <summary>Minimum score (0-100) for a WATCH decision.</summary>
    public int WatchScoreThreshold { get; set; } = 50;

    /// <summary>Score penalty applied when the NIFTY market-context filter fails (risk adjustment, never a hard reject).</summary>
    public int MarketContextScorePenalty { get; set; } = 10;

    /// <summary>Position-size multiplier applied when the NIFTY market-context filter fails.</summary>
    public decimal MarketContextPositionSizeFactor { get; set; } = 0.5m;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public static SwingStrategySettings Default => new();
}
