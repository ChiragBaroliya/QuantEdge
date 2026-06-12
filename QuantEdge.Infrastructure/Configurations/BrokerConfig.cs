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

    /// <summary>
    /// Gets or sets the API token or credentials for authentication.
    /// </summary>
    public string ApiKey { get; set; } = "QE-MOCK-API-KEY-12345";

    /// <summary>
    /// Gets or sets the API Secret for request signing.
    /// </summary>
    public string ApiSecret { get; set; } = "QE-MOCK-API-SECRET-XYZ";

    /// <summary>
    /// Gets or sets the access token (required for Zerodha Kite login session).
    /// </summary>
    public string AccessToken { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the Zerodha User ID for headless login.
    /// </summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the Zerodha Password for headless login.
    /// </summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the Zerodha TOTP Secret key for headless login.
    /// </summary>
    public string TotpSecret { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets connection timeouts in milliseconds.
    /// </summary>
    public int TimeoutMs { get; set; } = 5000;

    /// <summary>
    /// Gets or sets the PostgreSQL database connection string.
    /// </summary>
    public string ConnectionString { get; set; } = "Host=localhost;Database=quantedge;Username=postgres;Password=postgres";

    /// <summary>
    /// Gets or sets the target timeframes to process/build candles for (e.g. ["1m", "5m"]).
    /// </summary>
    public string[] Timeframes { get; set; } = new[] { "1m", "5m" };
}
