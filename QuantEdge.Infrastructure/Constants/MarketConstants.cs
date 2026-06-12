using System;

namespace QuantEdge.Infrastructure.Constants;

/// <summary>
/// Domain-wide market and indicator constants.
/// </summary>
public static class MarketConstants
{
    /// <summary>
    /// Supported/Sample Asset Symbols.
    /// </summary>
    public static class Symbols
    {
        public const string Nifty50 = "NIFTY";
        public const string BankNifty = "BANKNIFTY";
    }

    /// <summary>
    /// Standard Candlestick timeframe intervals.
    /// </summary>
    public static class Timeframes
    {
        public static readonly TimeSpan OneSecond = TimeSpan.FromSeconds(1);
        public static readonly TimeSpan FiveSeconds = TimeSpan.FromSeconds(5);
        public static readonly TimeSpan OneMinute = TimeSpan.FromMinutes(1);
        public static readonly TimeSpan FiveMinutes = TimeSpan.FromMinutes(5);
        public static readonly TimeSpan FifteenMinutes = TimeSpan.FromMinutes(15);
    }

    /// <summary>
    /// Default connection settings.
    /// </summary>
    public const string DefaultWebSocketFeedUrl = "wss://feed.quantedge.internal/v1/marketdata";
}
