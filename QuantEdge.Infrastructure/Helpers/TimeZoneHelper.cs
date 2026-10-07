using System;

namespace QuantEdge.Infrastructure.Helpers;

/// <summary>
/// Cross-platform TimeZone resolution helper for Indian Standard Time (IST).
/// Works seamlessly across Windows, Linux, macOS, and minimal Docker environments.
/// </summary>
public static class TimeZoneHelper
{
    private static readonly Lazy<TimeZoneInfo> _indianTimeZone = new(() =>
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("India Standard Time");
        }
        catch
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata");
            }
            catch
            {
                try
                {
                    return TimeZoneInfo.FindSystemTimeZoneById("Asia/Calcutta");
                }
                catch
                {
                    // Fallback to UTC +05:30 custom timezone if OS tzdata package is missing
                    return TimeZoneInfo.CreateCustomTimeZone("IST", TimeSpan.FromMinutes(330), "India Standard Time", "India Standard Time");
                }
            }
        }
    });

    /// <summary>
    /// Gets the Indian Standard Time (IST) TimeZoneInfo instance safely on any OS.
    /// </summary>
    public static TimeZoneInfo IndianTimeZone => _indianTimeZone.Value;

    /// <summary>
    /// Start of today's IST trading day as UTC (18:30 UTC the previous day). Use this, not DateTime.UtcNow.Date,
    /// for "today's candles": Kite stamps daily candles 00:00 IST, which UtcNow.Date (05:30 IST) misses.
    /// </summary>
    public static DateTime IstTodayStartUtc()
    {
        DateTime todayIst = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, IndianTimeZone).Date;
        return TimeZoneInfo.ConvertTimeToUtc(todayIst, IndianTimeZone);
    }
}
