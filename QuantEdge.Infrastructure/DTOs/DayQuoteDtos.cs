using System;

namespace QuantEdge.Infrastructure.DTOs;

/// <summary>
/// Latest live quote for a symbol as written by the market-data feed (live_quotes table). PrevClose is the
/// previous session's official close that Kite sends in every quote tick (ohlc.close) - the same reference
/// NSE and TradingView use for the day's change.
/// </summary>
public sealed record LiveQuote(
    string Symbol,
    decimal Ltp,
    decimal PrevClose,
    decimal DayOpen,
    decimal DayHigh,
    decimal DayLow,
    DateTime AsOfUtc);

/// <summary>
/// A stock's day change exactly as NSE / TradingView show it: Change = Ltp - PrevClose, ChangePct = Change / PrevClose x 100.
/// </summary>
public sealed class DayQuoteDto
{
    public string Symbol { get; set; } = string.Empty;
    public decimal Ltp { get; set; }
    public decimal PrevClose { get; set; }
    /// <summary>Null when no previous close is known - shown as "—", never as a fake 0%.</summary>
    public decimal? Change { get; set; }
    public decimal? ChangePct { get; set; }
    /// <summary>When Ltp was observed (UTC).</summary>
    public DateTime AsOfUtc { get; set; }
    /// <summary>LIVE = market-data feed tick; CANDLES = stored candles (feed not running or symbol not subscribed).</summary>
    public string Source { get; set; } = string.Empty;
}
