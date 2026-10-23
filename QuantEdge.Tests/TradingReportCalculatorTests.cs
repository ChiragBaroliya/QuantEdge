using QuantEdge.Infrastructure.DTOs;
using QuantEdge.Infrastructure.Services;
using Xunit;

namespace QuantEdge.Tests;

public class TradingReportCalculatorTests
{
    private static DateTime Utc(int y, int m, int d, int h = 5, int min = 0) => new(y, m, d, h, min, 0, DateTimeKind.Utc);

    private static TradingReportTradeDto Trade(decimal invested, decimal netPnl, DateTime closedUtc, DateTime? openedUtc = null) =>
        new(Id: 1, Symbol: "X", Mode: "Paper", Side: "SELL", Quantity: 1, EntryPrice: invested, ExecutedPrice: invested + netPnl,
            InvestedAmount: invested, RealizedPnl: netPnl, ReturnPct: Math.Round(netPnl / invested * 100m, 2), TradeType: "Auto",
            ExitReason: "x", ExecutedAt: closedUtc, HoldDays: 0, NetPnl: netPnl, OpenedAt: openedUtc);

    [Fact]
    public void Roi_UsesPeakCapitalDeployed_NotTurnover()
    {
        // The same ₹1L traded three times back to back: turnover ₹3L, but only ₹1L was ever tied up.
        var trades = new[]
        {
            Trade(100_000m, 1_000m, Utc(2026, 10, 20, 5), Utc(2026, 10, 20, 4)),
            Trade(100_000m, 1_000m, Utc(2026, 10, 21, 5), Utc(2026, 10, 21, 4)),
            Trade(100_000m, 1_000m, Utc(2026, 10, 22, 5), Utc(2026, 10, 22, 4)),
        };

        var s = TradingReportCalculator.Summary(trades);

        Assert.Equal(300_000m, s.TotalInvestedCapital);
        Assert.Equal(100_000m, s.PeakCapitalDeployed);
        Assert.Equal(3m, s.TotalRoiPct);          // 3,000 / 1,00,000 - turnover basis would show 1%
    }

    [Fact]
    public void PeakCapital_SumsOverlappingPositions()
    {
        var trades = new[]
        {
            Trade(50_000m, 0m, Utc(2026, 10, 20, 8), Utc(2026, 10, 20, 4)),
            Trade(30_000m, 0m, Utc(2026, 10, 20, 7), Utc(2026, 10, 20, 5)),   // open alongside the first
            Trade(40_000m, 0m, Utc(2026, 10, 20, 10), Utc(2026, 10, 20, 9)),  // after both closed
        };

        Assert.Equal(80_000m, TradingReportCalculator.PeakCapitalDeployed(trades));
    }

    [Fact]
    public void Drawdown_IsMeasuredAgainstPeakEquity()
    {
        var trades = new[]
        {
            Trade(100_000m, 10_000m, Utc(2026, 10, 20)),
            Trade(100_000m, -22_000m, Utc(2026, 10, 21)),
        };

        var s = TradingReportCalculator.Summary(trades);

        // Equity 1,00,000 -> 1,10,000 -> 88,000: fall of 22,000 from a 1,10,000 peak = 20%.
        Assert.Equal(20m, s.MaxDrawdownPct);
    }

    [Fact]
    public void WeeklyKey_UsesIsoWeekYear_AtYearEnd()
    {
        var trades = new[]
        {
            Trade(1_000m, 10m, Utc(2025, 12, 30)),   // ISO 2026-W01
            Trade(1_000m, 10m, Utc(2025, 1, 2)),     // ISO 2025-W01
        };

        var periods = TradingReportCalculator.Periods(trades, "weekly");

        Assert.Equal(2, periods.Count);
        Assert.Contains(periods, p => p.PeriodKey == "2026-W01");
        Assert.Contains(periods, p => p.PeriodKey == "2025-W01");
    }

    [Fact]
    public void DailyKey_UsesIstDate()
    {
        // 19:00 UTC on the 22nd is 00:30 IST on the 23rd.
        var periods = TradingReportCalculator.Periods(new[] { Trade(1_000m, 10m, Utc(2026, 10, 22, 19)) }, "daily");

        Assert.Equal("2026-10-23", Assert.Single(periods).PeriodKey);
    }

    [Fact]
    public void Periods_CumulativePnlRunsOldestToNewest()
    {
        var trades = new[]
        {
            Trade(1_000m, 30m, Utc(2026, 10, 22)),
            Trade(1_000m, -10m, Utc(2026, 10, 20)),
            Trade(1_000m, 50m, Utc(2026, 10, 21)),
        };

        var periods = TradingReportCalculator.Periods(trades, "daily");

        Assert.Equal(new[] { "2026-10-22", "2026-10-21", "2026-10-20" }, periods.Select(p => p.PeriodKey));
        Assert.Equal(new[] { 70m, 40m, -10m }, periods.Select(p => p.CumulativePnl));
    }
}
