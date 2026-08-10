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
}
