namespace QuantEdge.Infrastructure.Configurations;

/// <summary>
/// Configuration parameters representing broker integration details for the market data feed.
/// </summary>
public class BrokerConfig
{
    /// <summary>
    /// Gets or sets the Active Broker Identifier (e.g., "SIMULATOR", "ZERODHA", "ANGELONE", "IB").
    /// </summary>
    public string ActiveBroker { get; set; } = "SIMULATOR";

    /// <summary>
    /// Gets or sets the base WebSocket endpoint URL for market feed connections.
    /// </summary>
    public string WebSocketUrl { get; set; } = "wss://feed.quantedge.internal/v1/marketdata";

    // Zerodha API key/secret and access tokens are not configured here: each user saves their own Kite Connect
    // credentials in zerodha_sessions (Token page), and market data runs on an Admin user's session.

    /// <summary>
    /// Gets or sets connection timeouts in milliseconds.
    /// </summary>
    public int TimeoutMs { get; set; } = 5000;

    /// <summary>
    /// Gets or sets the PostgreSQL database connection string.
    /// </summary>
    public string ConnectionString { get; set; } = "Host=localhost;Database=quantedge;Username=postgres;Password=postgres";

    /// <summary>
    /// Gets or sets the target timeframes to process/build candles for (e.g. ["1m", "5m", "15m", "60m", "1d"]).
    /// </summary>
    public string[] Timeframes { get; set; } = new[] { "1m", "5m", "15m", "60m", "1d" };

    /// <summary>
    /// Gets or sets a value indicating whether to run the instruments sync immediately on startup for testing/manual triggering.
    /// </summary>
    public bool RunInstrumentsSyncImmediately { get; set; } = false;
}
