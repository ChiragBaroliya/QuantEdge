using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Extensions.Logging;
using QuantEdge.Infrastructure.DTOs;
using QuantEdge.Infrastructure.Helpers;
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
            StartDate = IstDayStartUtc(filter.StartDate),
            EndDate = IstDayStartUtc(filter.EndDate?.AddDays(1)),
            Symbol = !string.IsNullOrWhiteSpace(filter.Symbol) ? filter.Symbol.Trim() : null
        };

        var rawList = (await conn.QueryAsync<dynamic>(sql, parameters)).ToList();
        return await MapTradesWithChargesAsync(rawList);
    }

    // The UI sends calendar dates (yyyy-MM-dd) meaning IST trading days. The SQL range is [start, end): start = IST
    // midnight of the first day, end = IST midnight after the last day - so the last day's trades are included.
    private static DateTime? IstDayStartUtc(DateTime? istDate) => istDate.HasValue
        ? TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(istDate.Value.Date, DateTimeKind.Unspecified), TimeZoneHelper.IndianTimeZone)
        : null;

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
            StartDate = IstDayStartUtc(filter.StartDate),
            EndDate = IstDayStartUtc(filter.EndDate?.AddDays(1)),
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
        return TradingReportCalculator.Summary(trades);
    }

    public async Task<IReadOnlyList<TradingReportPeriodDto>> GetPeriodicBreakdownAsync(ReportFilterDto filter, CancellationToken cancellationToken = default)
    {
        var trades = await GetTradesAsync(filter, cancellationToken);
        return TradingReportCalculator.Periods(trades, filter.PeriodType);
    }

    public async Task<PagedResult<TradingReportPeriodDto>> GetPeriodicBreakdownPagedAsync(ReportPeriodsFilterDto filter, CancellationToken cancellationToken = default)
    {
        var baseFilter = new ReportFilterDto(
            PeriodType: filter.PeriodType,
            TradeMode: filter.TradeMode,
            UserId: filter.UserId,
            StartDate: filter.StartDate,
            EndDate: filter.EndDate,
            Symbol: filter.Symbol
        );

        var allPeriods = await GetPeriodicBreakdownAsync(baseFilter, cancellationToken);
        return TradingReportCalculator.PagePeriods(allPeriods, filter.PnlFilter, filter.Page, filter.PageSize);
    }

    public async Task<IReadOnlyList<TradingReportEquityPointDto>> GetEquityCurveAsync(ReportFilterDto filter, CancellationToken cancellationToken = default)
    {
        var trades = await GetTradesAsync(filter, cancellationToken);
        return TradingReportCalculator.EquityCurve(trades);
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
            // Each order's charges are split by this row's share of the order's quantity, so an entry closed by
            // two partial exits isn't charged twice. A row larger than its matched order (scaled-in position, so
            // the match is only the last of several entries) can't be attributed and uses the estimate.
            var row = (IDictionary<string, object>)r;
            int? sellOrderId = ReadInt(row, "sell_order_id");
            int? buyOrderId = ReadInt(row, "buy_order_id");
            decimal? sellShare = OrderShare(qty, ReadInt(row, "sell_order_qty"));
            decimal? buyShare = OrderShare(qty, ReadInt(row, "buy_order_qty"));
            bool isActual = sellOrderId.HasValue && buyOrderId.HasValue && sellShare.HasValue && buyShare.HasValue
                && actualCharges.ContainsKey(sellOrderId.Value) && actualCharges.ContainsKey(buyOrderId.Value);
            decimal chargesTotal = isActual
                ? actualCharges[sellOrderId!.Value].Total * sellShare!.Value + actualCharges[buyOrderId!.Value].Total * buyShare!.Value
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
                ChargeSource: isActual ? "ACTUAL" : "ESTIMATED",
                OpenedAt: openAt
            ));
        }
        return result;
    }

    private static int? ReadInt(IDictionary<string, object> row, string key) =>
        row.TryGetValue(key, out var v) && v != null ? Convert.ToInt32(v) : null;

    // Fraction of an order's charges that belong to a row of rowQty. Order quantity unknown (column absent before the
    // SQL update) -> whole order, the old behaviour; row larger than the order -> null (not attributable).
    private static decimal? OrderShare(int rowQty, int? orderQty)
    {
        if (!orderQty.HasValue || orderQty.Value <= 0) return 1m;
        if (rowQty <= 0 || rowQty > orderQty.Value) return null;
        return (decimal)rowQty / orderQty.Value;
    }
}
