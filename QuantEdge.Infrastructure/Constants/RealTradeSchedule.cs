using System;

namespace QuantEdge.Infrastructure.Constants;

/// <summary>
/// Timing and data constants of the Auto Real Trade background workers. Kept here (not private to the
/// workers) so the admin Trade Flow screen can show the exact values the workers run with.
/// </summary>
public static class RealTradeSchedule
{
    /// <summary>AutoRealTradeSignalScanWorker loop interval.</summary>
    public static readonly TimeSpan ScanInterval = TimeSpan.FromMinutes(15);

    /// <summary>Candles loaded per timeframe (1d / 15m / 60m) for each scanned stock.</summary>
    public const int CandleHistoryCount = 100;

    /// <summary>Stocks with fewer daily candles than this are skipped by the scan.</summary>
    public const int MinDailyCandles = 50;

    /// <summary>AutoRealPositionMonitorWorker loop interval.</summary>
    public static readonly TimeSpan MonitorInterval = TimeSpan.FromSeconds(20);

    /// <summary>
    /// A live position's SL/TSL check trusts only a WebSocket tick received within this window - never
    /// RealPosition.CurrentPrice (which is written once at buy time and frozen forever after).
    /// </summary>
    public static readonly TimeSpan LtpFreshnessWindow = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Consecutive stale monitor cycles (~40s) before falling back to a REST quote, so a brief WS gap
    /// never triggers an extra API call.
    /// </summary>
    public const int RestFallbackMissThreshold = 2;

    /// <summary>
    /// How often the same symbol may write an LTP_UNAVAILABLE / LTP_REST_FALLBACK / WS_RESUBSCRIBED
    /// audit entry, so a stuck symbol doesn't spam the audit stream every cycle.
    /// </summary>
    public static readonly TimeSpan AuditLogDebounceWindow = TimeSpan.FromMinutes(5);
}
