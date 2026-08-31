using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Extensions.Logging;
using QuantEdge.Infrastructure.DTOs;

namespace QuantEdge.Infrastructure.Persistence.Repositories;

/// <summary>
/// Repository implementation for aggregating trading investments, returns, and performance reports.
/// Uses the PostgreSQL stored functions fn_get_trading_report_trades and fn_get_trading_report_trades_paged.
/// </summary>
public class TradingReportRepository : ITradingReportRepository
{
    private readonly IDbConnectionFactory _connectionFactory;
    private readonly ILogger<TradingReportRepository> _logger;

    public TradingReportRepository(
        IDbConnectionFactory connectionFactory,
        ILogger<TradingReportRepository> logger)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<IReadOnlyList<TradingReportTradeDto>> GetTradesAsync(ReportFilterDto filter, CancellationToken cancellationToken = default)
    {
        using var conn = _connectionFactory.CreateConnection();

        const string sql = @"
            SELECT * FROM fn_get_trading_report_trades(
                @Mode,
                @UserId,
                @StartDate,
                @EndDate,
                @Symbol
            );";

        var parameters = new
        {
            Mode = filter.TradeMode ?? "all",
            UserId = !string.IsNullOrWhiteSpace(filter.UserId) && filter.UserId != "all" ? filter.UserId.Trim() : null,
            StartDate = filter.StartDate,
            EndDate = filter.EndDate,
            Symbol = !string.IsNullOrWhiteSpace(filter.Symbol) ? filter.Symbol.Trim() : null
        };

        var rawList = (await conn.QueryAsync<dynamic>(sql, parameters)).ToList();
        return MapTrades(rawList);
    }

    public async Task<PagedResult<TradingReportTradeDto>> GetTradesPagedAsync(ReportTradesFilterDto filter, CancellationToken cancellationToken = default)
    {
        using var conn = _connectionFactory.CreateConnection();

        const string sql = @"
            SELECT * FROM fn_get_trading_report_trades_paged(
                @Mode,
                @UserId,
                @StartDate,
                @EndDate,
                @Symbol,
                @TradeType,
                @PnlFilter,
                @Page,
                @PageSize
            );";

        int page = filter.Page < 1 ? 1 : filter.Page;
        int pageSize = filter.PageSize < 1 ? 10 : filter.PageSize;

        var parameters = new
        {
            Mode = filter.TradeMode ?? "all",
            UserId = !string.IsNullOrWhiteSpace(filter.UserId) && filter.UserId != "all" ? filter.UserId.Trim() : null,
            StartDate = filter.StartDate,
            EndDate = filter.EndDate,
            Symbol = !string.IsNullOrWhiteSpace(filter.Symbol) ? filter.Symbol.Trim() : null,
            TradeType = filter.TradeType ?? "all",
            PnlFilter = filter.PnlFilter ?? "all",
            Page = page,
            PageSize = pageSize
        };

        var rawList = (await conn.QueryAsync<dynamic>(sql, parameters)).ToList();
        long totalCount = rawList.Count > 0 && rawList[0].total_count != null ? Convert.ToInt64(rawList[0].total_count) : 0;
        var items = MapTrades(rawList);
        int totalPages = totalCount > 0 ? (int)Math.Ceiling((double)totalCount / pageSize) : 0;

        return new PagedResult<TradingReportTradeDto>(items, totalCount, page, pageSize, totalPages);
    }

    public async Task<TradingReportSummaryDto> GetSummaryAsync(ReportFilterDto filter, CancellationToken cancellationToken = default)
    {
        var trades = await GetTradesAsync(filter, cancellationToken);
        if (trades.Count == 0)
        {
            return new TradingReportSummaryDto(
                TotalInvestedCapital: 0m,
                NetRealizedPnl: 0m,
                TotalRoiPct: 0m,
                TotalTrades: 0,
                WinningTrades: 0,
                LosingTrades: 0,
                WinRatePct: 0m,
                GrossProfit: 0m,
                GrossLoss: 0m,
                ProfitFactor: 0m,
                AvgTradePnl: 0m,
                AvgTradeRoiPct: 0m,
                MaxDrawdownPct: 0m,
                BestTradePnl: 0m,
                WorstTradePnl: 0m
            );
        }

        decimal totalInvested = trades.Sum(t => t.InvestedAmount);
        decimal netPnl = trades.Sum(t => t.RealizedPnl);
        decimal totalRoi = totalInvested > 0m ? Math.Round((netPnl / totalInvested) * 100m, 2) : 0m;

        int wins = trades.Count(t => t.RealizedPnl > 0);
        int losses = trades.Count(t => t.RealizedPnl < 0);
        decimal winRate = trades.Count > 0 ? Math.Round((decimal)wins / trades.Count * 100m, 2) : 0m;

        decimal grossProfit = trades.Where(t => t.RealizedPnl > 0).Sum(t => t.RealizedPnl);
        decimal grossLoss = Math.Abs(trades.Where(t => t.RealizedPnl < 0).Sum(t => t.RealizedPnl));
        decimal profitFactor = grossLoss > 0m ? Math.Round(grossProfit / grossLoss, 2) : (grossProfit > 0m ? 99.99m : 0m);

        decimal avgPnl = Math.Round(netPnl / trades.Count, 2);
        decimal avgRoi = Math.Round(trades.Average(t => t.ReturnPct), 2);
        decimal bestPnl = trades.Max(t => t.RealizedPnl);
        decimal worstPnl = trades.Min(t => t.RealizedPnl);

        decimal peak = 0m;
        decimal maxDrawdown = 0m;
        decimal currentEquity = 0m;

        foreach (var t in trades.OrderBy(x => x.ExecutedAt))
        {
            currentEquity += t.RealizedPnl;
            if (currentEquity > peak)
            {
                peak = currentEquity;
            }
            decimal drawdown = peak - currentEquity;
            if (drawdown > maxDrawdown)
            {
                maxDrawdown = drawdown;
            }
        }

        decimal maxDrawdownPct = totalInvested > 0m ? Math.Round((maxDrawdown / totalInvested) * 100m, 2) : 0m;

        return new TradingReportSummaryDto(
            TotalInvestedCapital: Math.Round(totalInvested, 2),
            NetRealizedPnl: Math.Round(netPnl, 2),
            TotalRoiPct: totalRoi,
            TotalTrades: trades.Count,
            WinningTrades: wins,
            LosingTrades: losses,
            WinRatePct: winRate,
            GrossProfit: Math.Round(grossProfit, 2),
            GrossLoss: Math.Round(grossLoss, 2),
            ProfitFactor: profitFactor,
            AvgTradePnl: avgPnl,
            AvgTradeRoiPct: avgRoi,
            MaxDrawdownPct: maxDrawdownPct,
            BestTradePnl: Math.Round(bestPnl, 2),
            WorstTradePnl: Math.Round(worstPnl, 2)
        );
    }

    public async Task<IReadOnlyList<TradingReportPeriodDto>> GetPeriodicBreakdownAsync(ReportFilterDto filter, CancellationToken cancellationToken = default)
    {
        var trades = (await GetTradesAsync(filter, cancellationToken)).OrderBy(t => t.ExecutedAt).ToList();
        return CalculatePeriods(trades, filter.PeriodType);
    }

    public async Task<PagedResult<TradingReportPeriodDto>> GetPeriodicBreakdownPagedAsync(ReportPeriodsFilterDto filter, CancellationToken cancellationToken = default)
    {
        var baseFilter = new ReportFilterDto(
            PeriodType: filter.PeriodType,
            TradeMode: filter.TradeMode,
            UserId: filter.UserId,
            StartDate: filter.StartDate,
            EndDate: filter.EndDate
        );

        var allPeriods = (await GetPeriodicBreakdownAsync(baseFilter, cancellationToken)).ToList();

        // Apply PnL filter
        if (string.Equals(filter.PnlFilter, "profit", StringComparison.OrdinalIgnoreCase))
        {
            allPeriods = allPeriods.Where(p => p.NetPnl > 0).ToList();
        }
        else if (string.Equals(filter.PnlFilter, "loss", StringComparison.OrdinalIgnoreCase))
        {
            allPeriods = allPeriods.Where(p => p.NetPnl < 0).ToList();
        }

        int page = filter.Page < 1 ? 1 : filter.Page;
        int pageSize = filter.PageSize < 1 ? 10 : filter.PageSize;
        long totalCount = allPeriods.Count;
        int totalPages = totalCount > 0 ? (int)Math.Ceiling((double)totalCount / pageSize) : 0;

        var pagedItems = allPeriods
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToList();

        return new PagedResult<TradingReportPeriodDto>(pagedItems, totalCount, page, pageSize, totalPages);
    }

    public async Task<IReadOnlyList<TradingReportEquityPointDto>> GetEquityCurveAsync(ReportFilterDto filter, CancellationToken cancellationToken = default)
    {
        var trades = (await GetTradesAsync(filter, cancellationToken)).OrderBy(t => t.ExecutedAt).ToList();
        if (trades.Count == 0) return Array.Empty<TradingReportEquityPointDto>();

        var result = new List<TradingReportEquityPointDto>();
        decimal runningPnl = 0m;
        decimal runningInvested = 0m;

        foreach (var t in trades)
        {
            runningPnl += t.RealizedPnl;
            runningInvested += t.InvestedAmount;
            decimal roi = runningInvested > 0m ? Math.Round((runningPnl / runningInvested) * 100m, 2) : 0m;

            result.Add(new TradingReportEquityPointDto(
                Timestamp: t.ExecutedAt,
                Label: t.ExecutedAt.ToString("dd MMM yyyy HH:mm"),
                TradePnl: t.RealizedPnl,
                CumulativePnl: Math.Round(runningPnl, 2),
                InvestedCapital: Math.Round(runningInvested, 2),
                CumulativeRoiPct: roi
            ));
        }

        return result;
    }

    private static List<TradingReportTradeDto> MapTrades(IEnumerable<dynamic> rawList)
    {
        var result = new List<TradingReportTradeDto>();
        foreach (var r in rawList)
        {
            decimal entryPrice = r.entry_price != null ? Convert.ToDecimal(r.entry_price) : 0m;
            decimal execPrice = r.executed_price != null ? Convert.ToDecimal(r.executed_price) : 0m;
            int qty = r.quantity != null ? Convert.ToInt32(r.quantity) : 0;
            decimal pnl = r.realized_pnl != null ? Convert.ToDecimal(r.realized_pnl) : 0m;

            decimal invested = (entryPrice > 0m ? entryPrice : execPrice) * qty;
            decimal returnPct = invested > 0m ? Math.Round((pnl / invested) * 100m, 2) : 0m;

            DateTime execAt = r.executed_at != null ? Convert.ToDateTime(r.executed_at) : DateTime.UtcNow;
            DateTime? openAt = r.opened_at != null ? Convert.ToDateTime(r.opened_at) : null;
            int holdDays = openAt.HasValue 
                ? Math.Max(0, (execAt.Date - openAt.Value.Date).Days) 
                : (r.hold_days != null ? Convert.ToInt32(r.hold_days) : 0);

            int sideVal = r.side != null ? Convert.ToInt32(r.side) : 0;
            string sideText = sideVal == 0 ? "BUY" : "SELL";

            string modeStr = Convert.ToString(r.mode) ?? "Paper";
            int tradeTypeVal = r.trade_type != null ? Convert.ToInt32(r.trade_type) : 0;
            string tradeTypeText = modeStr == "Swing Sim" ? "Swing" : tradeTypeVal switch
            {
                0 => "Manual",
                1 => "Auto",
                _ => "Auto"
            };

            result.Add(new TradingReportTradeDto(
                Id: Convert.ToInt64(r.id),
                Symbol: Convert.ToString(r.symbol) ?? "UNKNOWN",
                Mode: modeStr,
                Side: sideText,
                Quantity: qty,
                EntryPrice: Math.Round(entryPrice, 2),
                ExecutedPrice: Math.Round(execPrice, 2),
                InvestedAmount: Math.Round(invested, 2),
                RealizedPnl: Math.Round(pnl, 2),
                ReturnPct: returnPct,
                TradeType: tradeTypeText,
                ExitReason: Convert.ToString(r.exit_reason) ?? "Manual Exit",
                ExecutedAt: execAt,
                HoldDays: holdDays,
                Username: Convert.ToString(r.username) ?? "User"
            ));
        }
        return result;
    }

    private static List<TradingReportPeriodDto> CalculatePeriods(List<TradingReportTradeDto> trades, string periodType)
    {
        if (trades.Count == 0) return new List<TradingReportPeriodDto>();

        periodType = (periodType ?? "daily").ToLowerInvariant();
        var grouped = trades.GroupBy(t => GetPeriodKey(t.ExecutedAt, periodType));

        var periodList = new List<TradingReportPeriodDto>();
        decimal runningPnl = 0m;

        foreach (var g in grouped)
        {
            var periodTrades = g.ToList();
            var minDate = periodTrades.Min(t => t.ExecutedAt).Date;
            var maxDate = periodTrades.Max(t => t.ExecutedAt).Date;

            decimal periodInvested = periodTrades.Sum(t => t.InvestedAmount);
            decimal periodGrossProfit = periodTrades.Where(t => t.RealizedPnl > 0).Sum(t => t.RealizedPnl);
            decimal periodGrossLoss = Math.Abs(periodTrades.Where(t => t.RealizedPnl < 0).Sum(t => t.RealizedPnl));
            decimal periodNetPnl = periodTrades.Sum(t => t.RealizedPnl);
            decimal periodRoi = periodInvested > 0m ? Math.Round((periodNetPnl / periodInvested) * 100m, 2) : 0m;

            int winCount = periodTrades.Count(t => t.RealizedPnl > 0);
            int lossCount = periodTrades.Count(t => t.RealizedPnl < 0);
            decimal winRate = periodTrades.Count > 0 ? Math.Round((decimal)winCount / periodTrades.Count * 100m, 2) : 0m;

            runningPnl += periodNetPnl;
            string label = FormatPeriodLabel(g.Key, minDate, maxDate, periodType);

            periodList.Add(new TradingReportPeriodDto(
                PeriodKey: g.Key,
                PeriodLabel: label,
                StartDate: minDate,
                EndDate: maxDate,
                TotalTrades: periodTrades.Count,
                WinTrades: winCount,
                LossTrades: lossCount,
                WinRatePct: winRate,
                InvestedCapital: Math.Round(periodInvested, 2),
                GrossProfit: Math.Round(periodGrossProfit, 2),
                GrossLoss: Math.Round(periodGrossLoss, 2),
                NetPnl: Math.Round(periodNetPnl, 2),
                RoiPct: periodRoi,
                CumulativePnl: Math.Round(runningPnl, 2)
            ));
        }

        return periodList.OrderByDescending(p => p.StartDate).ToList();
    }

    private static string GetPeriodKey(DateTime date, string periodType)
    {
        return periodType switch
        {
            "daily" => date.ToString("yyyy-MM-dd"),
            "weekly" => $"{date.Year}-W{ISOWeek.GetWeekOfYear(date):D2}",
            "fortnightly" => date.Day <= 15 ? $"{date:yyyy-MM}-H1" : $"{date:yyyy-MM}-H2",
            "monthly" => date.ToString("yyyy-MM"),
            "yearly" => date.ToString("yyyy"),
            _ => date.ToString("yyyy-MM-dd")
        };
    }

    private static string FormatPeriodLabel(string key, DateTime minDate, DateTime maxDate, string periodType)
    {
        return periodType switch
        {
            "daily" => minDate.ToString("dd MMM yyyy (ddd)"),
            "weekly" => $"Week {ISOWeek.GetWeekOfYear(minDate)} ({minDate:dd MMM} – {maxDate:dd MMM yyyy})",
            "fortnightly" => key.EndsWith("-H1") 
                ? $"01–15 {minDate:MMM yyyy}" 
                : $"16–{DateTime.DaysInMonth(minDate.Year, minDate.Month):D2} {minDate:MMM yyyy}",
            "monthly" => minDate.ToString("MMMM yyyy"),
            "yearly" => $"Year {minDate.Year}",
            _ => minDate.ToString("dd MMM yyyy")
        };
    }
}
