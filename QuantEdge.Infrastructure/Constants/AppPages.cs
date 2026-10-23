using System;
using System.Collections.Generic;
using System.Linq;

namespace QuantEdge.Infrastructure.Constants;

/// <summary>
/// How a page's access can be changed from the Role Permissions screen.
/// </summary>
public enum PageAccessLock
{
    /// <summary>Admin decides per role.</summary>
    None,
    /// <summary>Every signed-in user can open it (e.g. the landing page) - cannot be turned off.</summary>
    AlwaysAllowed,
    /// <summary>Security pages that only the Admin role may ever open - cannot be granted.</summary>
    AdminOnly
}

/// <summary>
/// One Web page (sidebar entry) whose access is controlled by role_page_permissions.
/// </summary>
public sealed record AppPageDefinition(
    string Key,
    string Name,
    string Group,
    string Description,
    bool DefaultForUser,
    PageAccessLock Lock = PageAccessLock.None);

/// <summary>
/// Catalog of every permission-controlled Web page. A page with no row in role_page_permissions
/// falls back to <see cref="AppPageDefinition.DefaultForUser"/>, which mirrors the access that was
/// hard-coded before the Role Permissions screen existed. The Admin role always has every page.
/// </summary>
public static class AppPages
{
    public const string AdminRole = "Admin";
    public const string UserRole = "User";

    public const string GroupTrading = "Trading & Signals";
    public const string GroupData = "Data Management";
    public const string GroupSystem = "System Administration";

    public const string SignalDashboard = "SignalDashboard";
    public const string SectorDashboard = "SectorDashboard";
    public const string Reconciliation = "Reconciliation";
    public const string Notifications = "Notifications";
    public const string Backtest = "Backtest";
    public const string AutoPaperTrading = "AutoPaperTrading";
    public const string AutoRealTrading = "AutoRealTrading";
    public const string ManualTrading = "ManualTrading";
    public const string SwingTrading = "SwingTrading";
    public const string EtfList = "EtfList";
    public const string TradingReports = "TradingReports";
    public const string TokenManager = "TokenManager";
    public const string DataCoverage = "DataCoverage";
    public const string CandleSummary = "CandleSummary";
    public const string ManageHistory = "ManageHistory";
    public const string HolidayManager = "HolidayManager";
    public const string UserManagement = "UserManagement";
    public const string RolePermissions = "RolePermissions";
    public const string RealTradeFlow = "RealTradeFlow";
    public const string Logs = "Logs";

    public static readonly IReadOnlyList<AppPageDefinition> All = new List<AppPageDefinition>
    {
        new(SignalDashboard, "Signal Dashboard", GroupTrading, "Live market data, indicators and trade signals (landing page).", true, PageAccessLock.AlwaysAllowed),
        new(SectorDashboard, "Sector Dashboard", GroupTrading, "Strong sectors today and their stocks with a BUY setup.", true),
        new(Reconciliation, "Reconciliation", GroupTrading, "Positions vs Zerodha and daily prices vs NSE close.", false),
        new(Notifications, "Notifications", GroupTrading, "Bell notification history for any date range.", true),
        new(Backtest, "Backtest", GroupTrading, "Replay the live strategy on stored candles.", false),
        new(AutoPaperTrading, "Auto Paper Trading", GroupTrading, "Automated strategy simulation with virtual capital. Master switch and settings stay Admin-only.", false),
        new(AutoRealTrading, "Auto Real Trading", GroupTrading, "Real-money automated execution via Zerodha. Master switch and settings stay Admin-only.", false),
        new(ManualTrading, "Manual Trading", GroupTrading, "Manual paper orders with virtual capital against live prices.", true),
        new(SwingTrading, "Swing Trading", GroupTrading, "Multi-day entry/exit scanner and breakout setups.", true),
        new(EtfList, "ETF List", GroupTrading, "ETFs from the instrument master. Real Trade actions stay Admin-only.", true),
        new(TradingReports, "Trading Reports", GroupTrading, "Daily to yearly turnover and ROI analytics (own trades).", true),
        new(TokenManager, "Token Manager", GroupTrading, "The user's own Zerodha API credentials and daily access token.", true),
        new(DataCoverage, "Data Coverage", GroupData, "1-minute candle completeness and missing candles.", false),
        new(CandleSummary, "Candle Summary", GroupData, "Today's candle counts per timeframe.", false),
        new(ManageHistory, "Manage History", GroupData, "Download and backfill historical candles.", false),
        new(HolidayManager, "Holiday Manager", GroupData, "Stock market trading calendar and holidays.", false),
        new(UserManagement, "User Management", GroupSystem, "Create users, change roles and reset passwords.", false, PageAccessLock.AdminOnly),
        new(RolePermissions, "Role Permissions", GroupSystem, "This screen - decide which pages each role can open.", false, PageAccessLock.AdminOnly),
        new(RealTradeFlow, "Real Trade Flow", GroupSystem, "Flow diagrams of the Auto Real Trade background jobs.", false),
        new(Logs, "Logs", GroupSystem, "Web / API / Worker logs and server memory usage.", false),
    };

    private static readonly Dictionary<string, AppPageDefinition> ByKey =
        All.ToDictionary(p => p.Key, StringComparer.OrdinalIgnoreCase);

    public static AppPageDefinition? Find(string pageKey) =>
        ByKey.TryGetValue(pageKey ?? string.Empty, out var page) ? page : null;

    public static bool IsAdminRole(string? role) =>
        string.Equals(role, AdminRole, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Final access for one page: locks win, then the stored value, then the catalog default.
    /// </summary>
    public static bool Resolve(AppPageDefinition page, string role, bool? storedValue)
    {
        if (IsAdminRole(role)) return true;
        return page.Lock switch
        {
            PageAccessLock.AlwaysAllowed => true,
            PageAccessLock.AdminOnly => false,
            _ => storedValue ?? page.DefaultForUser
        };
    }
}
