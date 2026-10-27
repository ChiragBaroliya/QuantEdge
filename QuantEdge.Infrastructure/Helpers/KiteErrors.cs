using System;
using System.Linq;

namespace QuantEdge.Infrastructure.Helpers;

/// <summary>
/// Classifies Kite Connect error messages that come from the Zerodha app's own configuration rather than the order or
/// symbol - retrying them changes nothing until the user fixes the app on the Kite developer console.
/// </summary>
public static class KiteErrors
{
    private static readonly string[] AccountLevelMarkers =
    {
        "No IPs configured",        // static-IP whitelist empty (mandatory for order APIs)
        "allowed IPs",
        "unregistered IP",
        "IP is not allowed",
        "not whitelisted",
        "Insufficient permission"   // the app's Kite Connect plan does not include this API
    };

    /// <summary>True when every call of this kind will fail the same way until the Kite app is reconfigured.</summary>
    public static bool IsAccountLevelRejection(string? message) =>
        !string.IsNullOrWhiteSpace(message) &&
        AccountLevelMarkers.Any(m => message.Contains(m, StringComparison.OrdinalIgnoreCase));
}
