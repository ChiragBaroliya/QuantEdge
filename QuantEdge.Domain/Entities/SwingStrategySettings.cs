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

    /// <summary>
    /// When true (default), the NIFTY market filter is a mandatory hard filter: if NIFTY fails
    /// (or its data is missing/insufficient) every stock is REJECTED and no new entry is taken.
    /// When false, the legacy soft behaviour applies (score penalty + reduced size, never a reject).
    /// </summary>
    public bool RequireNiftyMarketFilter { get; set; } = true;

    /// <summary>Score penalty applied when the NIFTY market-context filter fails. Only used when <see cref="RequireNiftyMarketFilter"/> is false.</summary>
    public int MarketContextScorePenalty { get; set; } = 10;

    /// <summary>Position-size multiplier applied when the NIFTY market-context filter fails. Only used when <see cref="RequireNiftyMarketFilter"/> is false.</summary>
    public decimal MarketContextPositionSizeFactor { get; set; } = 0.5m;

    /// <summary>
    /// Protection band (e.g. 0.005 = 0.5%) applied around the reference price when emulating a market
    /// order as a Kite Connect LIMIT order for real-money orders (Zerodha rejects plain MARKET orders
    /// via API on the "regular" variety). BUY adds this band, SELL subtracts it.
    /// </summary>
    public decimal MarketProtectionBufferPct { get; set; } = 0.005m;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public static SwingStrategySettings Default => new();
}
