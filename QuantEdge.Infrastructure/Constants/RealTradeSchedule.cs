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

    /// <summary>Intraday candles (15m / 60m) loaded for each scanned stock.</summary>
    public const int CandleHistoryCount = 100;

    /// <summary>
    /// Daily candles loaded for each scanned stock and for NIFTY (Plan D1): enough for a real EMA200 (needs ~220) and
    /// a real 52-week high (250 sessions). With only 100, "EMA200" was a running average and "52W high" a 5-month high.
    /// </summary>
    public const int DailyCandleHistoryCount = 300;

    /// <summary>Wait after a 15m candle closes before scanning, so the closed candle has been synced / stored.</summary>
    public static readonly TimeSpan ScanAfterCandleClose = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Delay until the next scan: just after the next 15-minute candle close (IST :00/:15/:30/:45 + 20 s - the NSE
    /// session opens at 09:15, so these are the 15m candle boundaries). Plan D2: the scans used to run every 15 min
    /// counted from worker start, so they scored a candle that might be only 2 minutes old.
    /// </summary>
    public static TimeSpan DelayUntilNextScan(DateTime nowUtc)
    {
        // 15 min divides the +05:30 offset, so UTC and IST 15-minute boundaries are the same instants.
        long interval = ScanInterval.Ticks;
        var target = new DateTime(nowUtc.Ticks / interval * interval, DateTimeKind.Utc) + ScanAfterCandleClose;   // this boundary + 20 s
        if (target <= nowUtc) target = target.AddTicks(interval);                                                 // already past -> next one
        return target - nowUtc;
    }

    /// <summary>How many candles to load for a timeframe.</summary>
    public static int HistoryCountFor(string timeframe) =>
        string.Equals(timeframe, "1d", StringComparison.OrdinalIgnoreCase) ? DailyCandleHistoryCount : CandleHistoryCount;

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
