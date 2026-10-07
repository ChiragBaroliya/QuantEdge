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
using QuantEdge.Infrastructure.Services;

namespace QuantEdge.Infrastructure.Persistence.Repositories;

/// <summary>
/// Repository implementation for aggregating trading investments, returns, and performance reports.
/// Uses the PostgreSQL stored functions fn_get_trading_report_trades and fn_get_trading_report_trades_paged.
/// </summary>
public class TradingReportRepository : ITradingReportRepository
{
    private readonly IDbConnectionFactory _connectionFactory;
    private readonly ILogger<TradingReportRepository> _logger;
    private readonly IChargeRatesRepository _chargeRatesRepository;
    private readonly IRealOrderChargesRepository _realOrderChargesRepository;

    public TradingReportRepository(
        IDbConnectionFactory connectionFactory,
        ILogger<TradingReportRepository> logger,
        IChargeRatesRepository chargeRatesRepository,
        IRealOrderChargesRepository realOrderChargesRepository)
    {
        _realOrderChargesRepository = realOrderChargesRepository ?? throw new ArgumentNullException(nameof(realOrderChargesRepository));
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _chargeRatesRepository = chargeRatesRepository ?? throw new ArgumentNullException(nameof(chargeRatesRepository));
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
        return await MapTradesWithChargesAsync(rawList);
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
        string pnlFilter = (filter.PnlFilter ?? "all").Trim().ToLowerInvariant();

        // Profit / Loss means NET of charges, which the SQL function can't see (it only has gross P&L). For those
        // filters, fetch every matching row (pnl 'all'), compute net here with the same ChargesCalculator, then
        // filter and page in memory. "all" keeps the SQL-side paging.
        bool filterOnNet = pnlFilter is "profit" or "loss";

        var parameters = new
        {
            Mode = filter.TradeMode ?? "all",
            UserId = !string.IsNullOrWhiteSpace(filter.UserId) && filter.UserId != "all" ? filter.UserId.Trim() : null,
            StartDate = filter.StartDate,
            EndDate = filter.EndDate,
            Symbol = !string.IsNullOrWhiteSpace(filter.Symbol) ? filter.Symbol.Trim() : null,
            TradeType = filter.TradeType ?? "all",
            PnlFilter = filterOnNet ? "all" : pnlFilter,
            Page = filterOnNet ? 1 : page,
            PageSize = filterOnNet ? int.MaxValue : pageSize
        };

        var rawList = (await conn.QueryAsync<dynamic>(sql, parameters)).ToList();
        var items = await MapTradesWithChargesAsync(rawList);

        long totalCount;
        if (filterOnNet)
        {
            var matching = items.Where(t => pnlFilter == "profit" ? t.NetPnl > 0 : t.NetPnl < 0).ToList();
            totalCount = matching.Count;
            items = matching.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        }
        else
        {
            totalCount = rawList.Count > 0 && rawList[0].total_count != null ? Convert.ToInt64(rawList[0].total_count) : 0;
        }
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

        // All performance stats below are on NET P&L (after estimated charges) - what actually reaches the account.
        decimal totalInvested = trades.Sum(t => t.InvestedAmount);
        decimal grossPnl = trades.Sum(t => t.RealizedPnl);
        decimal totalCharges = trades.Sum(t => t.Charges);
        decimal netPnl = trades.Sum(t => t.NetPnl);
        decimal totalRoi = totalInvested > 0m ? Math.Round((netPnl / totalInvested) * 100m, 2) : 0m;

        int wins = trades.Count(t => t.NetPnl > 0);
        int losses = trades.Count(t => t.NetPnl < 0);
        decimal winRate = trades.Count > 0 ? Math.Round((decimal)wins / trades.Count * 100m, 2) : 0m;

        decimal grossProfit = trades.Where(t => t.NetPnl > 0).Sum(t => t.NetPnl);
        decimal grossLoss = Math.Abs(trades.Where(t => t.NetPnl < 0).Sum(t => t.NetPnl));
        decimal profitFactor = grossLoss > 0m ? Math.Round(grossProfit / grossLoss, 2) : (grossProfit > 0m ? 99.99m : 0m);

        decimal avgPnl = Math.Round(netPnl / trades.Count, 2);
        decimal avgRoi = Math.Round(trades.Average(t => t.ReturnPct), 2);
        decimal bestPnl = trades.Max(t => t.NetPnl);
        decimal worstPnl = trades.Min(t => t.NetPnl);

        decimal peak = 0m;
        decimal maxDrawdown = 0m;
        decimal currentEquity = 0m;

        foreach (var t in trades.OrderBy(x => x.ExecutedAt))
        {
            currentEquity += t.NetPnl;
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
            WorstTradePnl: Math.Round(worstPnl, 2),
            GrossRealizedPnl: Math.Round(grossPnl, 2),
            TotalCharges: Math.Round(totalCharges, 2)
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
            runningPnl += t.NetPnl;
            runningInvested += t.InvestedAmount;
            decimal roi = runningInvested > 0m ? Math.Round((runningPnl / runningInvested) * 100m, 2) : 0m;

            result.Add(new TradingReportEquityPointDto(
                Timestamp: t.ExecutedAt,
                Label: t.ExecutedAt.ToString("dd MMM yyyy HH:mm"),
                TradePnl: t.NetPnl,
                CumulativePnl: Math.Round(runningPnl, 2),
                InvestedCapital: Math.Round(runningInvested, 2),
                CumulativeRoiPct: roi
            ));
        }

        return result;
    }

    // Loads the rate table and any stored actual charges for the real orders in these rows, then maps them.
    private async Task<List<TradingReportTradeDto>> MapTradesWithChargesAsync(List<dynamic> rawList)
    {
        var rates = await _chargeRatesRepository.GetAllAsync();
        var orderIds = new List<int>();
        foreach (var r in rawList)
        {
            var row = (IDictionary<string, object>)r;
            foreach (var key in new[] { "sell_order_id", "buy_order_id" })
            {
                if (row.TryGetValue(key, out var v) && v != null) orderIds.Add(Convert.ToInt32(v));
            }
        }

        Dictionary<int, RealOrderCharges> actual = new();
        if (orderIds.Count > 0)
        {
            try
            {
                actual = await _realOrderChargesRepository.GetByOrderIdsAsync(orderIds);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "real_order_charges not readable (apply schema.sql) - report uses estimated charges.");
            }
        }
        return MapTrades(rawList, rates, actual);
    }

    private static List<TradingReportTradeDto> MapTrades(IEnumerable<dynamic> rawList, IReadOnlyList<ChargeRates> chargeRates,
        IReadOnlyDictionary<int, RealOrderCharges> actualCharges)
    {
        var result = new List<TradingReportTradeDto>();
        foreach (var r in rawList)
        {
            decimal entryPrice = r.entry_price != null ? Convert.ToDecimal(r.entry_price) : 0m;
            decimal execPrice = r.executed_price != null ? Convert.ToDecimal(r.executed_price) : 0m;
            int qty = r.quantity != null ? Convert.ToInt32(r.quantity) : 0;
            decimal pnl = r.realized_pnl != null ? Convert.ToDecimal(r.realized_pnl) : 0m;

            decimal invested = (entryPrice > 0m ? entryPrice : execPrice) * qty;

            DateTime execAt = r.executed_at != null ? Convert.ToDateTime(r.executed_at) : DateTime.UtcNow;
            DateTime? openAt = r.opened_at != null ? Convert.ToDateTime(r.opened_at) : null;
            int holdDays = openAt.HasValue
                ? Math.Max(0, (execAt.Date - openAt.Value.Date).Days)
                : (r.hold_days != null ? Convert.ToInt32(r.hold_days) : 0);

            int sideVal = r.side != null ? Convert.ToInt32(r.side) : 0;
            string sideText = sideVal == 0 ? "BUY" : "SELL";

            // Gross -> Charges -> Net. The row closes a round trip: a SELL closes a long (bought at entry, sold at
            // exit); a BUY closes a short (sold at entry, bought back at exit). An unknown entry price (0) only
            // skips that leg's charges. Same-IST-day round trips are charged at intraday rates.
            DateTime exitIst = DayChangeCalculator.IstDate(execAt);
            DateTime? entryIst = openAt.HasValue ? DayChangeCalculator.IstDate(openAt.Value) : null;
            string chargeProduct = ChargesCalculator.ProductFor(entryIst, exitIst);
            var rates = ChargesCalculator.RatesFor(chargeRates, chargeProduct, exitIst);
            decimal entryValue = entryPrice * qty;
            decimal exitValue = execPrice * qty;
            var estimate = sideVal == 0
                ? ChargesCalculator.RoundTrip(buyValue: exitValue, sellValue: entryValue, rates)
                : ChargesCalculator.RoundTrip(buyValue: entryValue, sellValue: exitValue, rates);

            // Real trades: Zerodha's own charges (Kite contract note, stored daily by RealOrderChargesWorker) when
            // BOTH legs have them; otherwise the estimate. Never a mix, so the figure is either fully actual or not.
            var row = (IDictionary<string, object>)r;
            int? sellOrderId = row.TryGetValue("sell_order_id", out var so) && so != null ? Convert.ToInt32(so) : null;
            int? buyOrderId = row.TryGetValue("buy_order_id", out var bo) && bo != null ? Convert.ToInt32(bo) : null;
            bool isActual = sellOrderId.HasValue && buyOrderId.HasValue
                && actualCharges.ContainsKey(sellOrderId.Value) && actualCharges.ContainsKey(buyOrderId.Value);
            decimal chargesTotal = isActual
                ? actualCharges[sellOrderId!.Value].Total + actualCharges[buyOrderId!.Value].Total
                : estimate.Total;
            decimal netPnl = pnl - chargesTotal;
            decimal returnPct = invested > 0m ? Math.Round((netPnl / invested) * 100m, 2) : 0m;

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
                Username: Convert.ToString(r.username) ?? "User",
                Charges: Math.Round(chargesTotal, 2),
                NetPnl: Math.Round(netPnl, 2),
                ChargeProduct: chargeProduct,
                ChargeSource: isActual ? "ACTUAL" : "ESTIMATED"
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
            decimal periodGrossProfit = periodTrades.Where(t => t.NetPnl > 0).Sum(t => t.NetPnl);
            decimal periodGrossLoss = Math.Abs(periodTrades.Where(t => t.NetPnl < 0).Sum(t => t.NetPnl));
            decimal periodNetPnl = periodTrades.Sum(t => t.NetPnl);
            decimal periodRoi = periodInvested > 0m ? Math.Round((periodNetPnl / periodInvested) * 100m, 2) : 0m;

            int winCount = periodTrades.Count(t => t.NetPnl > 0);
            int lossCount = periodTrades.Count(t => t.NetPnl < 0);
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
