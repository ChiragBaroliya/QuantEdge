using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using QuantEdge.Domain.Entities;
using QuantEdge.Infrastructure.DTOs;
using QuantEdge.Infrastructure.Helpers;
using QuantEdge.Infrastructure.Interfaces;
using QuantEdge.Infrastructure.Persistence.Repositories;

namespace QuantEdge.Infrastructure.Services;

public interface IRealOrderChargesService
{
    /// <summary>
    /// Fetches Zerodha's actual charges for filled real orders that don't have them yet (last <see cref="RealOrderChargesService.LookbackDays"/>
    /// days, so a missed day is caught up). One Kite call per user per <see cref="RealOrderChargesService.BatchSize"/> orders.
    /// Returns how many orders got charges; failures are reported to the header bell.
    /// </summary>
    Task<int> FetchPendingChargesAsync();
}

public class RealOrderChargesService : IRealOrderChargesService
{
    public const int LookbackDays = 7;
    public const int BatchSize = 50;

    private readonly IRealOrderChargesRepository _chargesRepository;
    private readonly IRealTradingRepository _realTradingRepository;
    private readonly IZerodhaKiteBrokerService _brokerService;
    private readonly IChargeRatesRepository _chargeRatesRepository;
    private readonly IBrokerApiEventRecorder _apiEventRecorder;
    private readonly ILogger<RealOrderChargesService> _logger;

    public RealOrderChargesService(
        IRealOrderChargesRepository chargesRepository,
        IRealTradingRepository realTradingRepository,
        IZerodhaKiteBrokerService brokerService,
        IChargeRatesRepository chargeRatesRepository,
        IBrokerApiEventRecorder apiEventRecorder,
        ILogger<RealOrderChargesService> logger)
    {
        _chargesRepository = chargesRepository ?? throw new ArgumentNullException(nameof(chargesRepository));
        _realTradingRepository = realTradingRepository ?? throw new ArgumentNullException(nameof(realTradingRepository));
        _brokerService = brokerService ?? throw new ArgumentNullException(nameof(brokerService));
        _chargeRatesRepository = chargeRatesRepository ?? throw new ArgumentNullException(nameof(chargeRatesRepository));
        _apiEventRecorder = apiEventRecorder ?? throw new ArgumentNullException(nameof(apiEventRecorder));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<int> FetchPendingChargesAsync()
    {
        DateTime sinceUtc = TimeZoneHelper.IstTodayStartUtc().AddDays(-LookbackDays);
        var pending = await _chargesRepository.GetFilledOrdersWithoutChargesAsync(sinceUtc);
        if (pending.Count == 0) return 0;

        var rateTable = await _chargeRatesRepository.GetAllAsync();
        int saved = 0;

        foreach (var userOrders in pending.GroupBy(o => o.UserId))
        {
            var settings = await _realTradingRepository.GetSettingsAsync(userOrders.Key);
            string product = string.IsNullOrWhiteSpace(settings?.ProductType) ? "CNC" : settings!.ProductType.Trim().ToUpperInvariant();

            foreach (var batch in userOrders.Chunk(BatchSize))
            {
                var requests = batch.Select(o => new KiteChargesOrderRequest
                {
                    OrderId = o.BrokerOrderId!,
                    TradingSymbol = o.Symbol,
                    TransactionType = o.Side == (int)TradeSide.SELL ? "SELL" : "BUY",
                    Product = product,
                    OrderType = "LIMIT",
                    Quantity = o.Quantity,
                    AveragePrice = o.FilledPrice
                }).ToList();

                var (success, charges, message) = await _brokerService.GetOrderChargesAsync(requests, userOrders.Key);
                if (!success || charges == null)
                {
                    _apiEventRecorder.RecordFailure(BrokerApiSource.Job, "actual charges (contract note)",
                        $"Could not fetch Zerodha's actual charges for {batch.Length} order(s): {message}. Reports keep using estimates; retried on the next run.",
                        userId: userOrders.Key);
                    continue;
                }

                var rows = MatchCharges(batch, charges, product, rateTable, out int mismatched);
                if (mismatched > 0)
                {
                    _apiEventRecorder.RecordFailure(BrokerApiSource.Job, "actual charges (contract note)",
                        $"{mismatched} of {batch.Length} charge rows didn't match their order (symbol/side/qty) and were skipped; they stay on estimates.",
                        userId: userOrders.Key, level: "warning");
                }

                await _chargesRepository.SaveAsync(rows);
                saved += rows.Count;
            }
        }

        _logger.LogInformation("Actual Zerodha charges stored for {Saved} of {Pending} filled real order(s).", saved, pending.Count);
        return saved;
    }

    /// <summary>
    /// Kite returns charges in request order without order ids, so each result is paired by position and accepted only
    /// when symbol, side and quantity agree. DP (not in Kite's response) is added once per scrip per IST sell day for
    /// delivery sells, from the charge_rates table.
    /// </summary>
    public static List<RealOrderCharges> MatchCharges(IReadOnlyList<RealOrderNeedingCharges> orders, IReadOnlyList<KiteOrderCharges> charges,
        string product, IReadOnlyList<ChargeRates> rateTable, out int mismatched)
    {
        var rows = new List<RealOrderCharges>();
        var dpTaken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        mismatched = 0;

        for (int i = 0; i < orders.Count; i++)
        {
            var o = orders[i];
            var c = i < charges.Count ? charges[i] : null;
            string side = o.Side == (int)TradeSide.SELL ? "SELL" : "BUY";
            bool matches = c != null
                && string.Equals(c.TradingSymbol, o.Symbol, StringComparison.OrdinalIgnoreCase)
                && string.Equals(c.TransactionType, side, StringComparison.OrdinalIgnoreCase)
                && (c.Quantity == 0 || c.Quantity == o.Quantity);
            if (!matches)
            {
                mismatched++;
                continue;
            }

            DateTime dayIst = DayChangeCalculator.IstDate(o.FilledAt ?? DateTime.UtcNow);
            decimal dp = 0m;
            if (side == "SELL" && product == "CNC" && dpTaken.Add($"{o.Symbol}|{dayIst:yyyyMMdd}"))
            {
                dp = ChargesCalculator.RatesFor(rateTable, "CNC", dayIst).DpPerSell;
            }

            rows.Add(new RealOrderCharges
            {
                OrderId = o.Id,
                BrokerOrderId = o.BrokerOrderId,
                Brokerage = c!.Brokerage,
                Stt = c.TransactionTax,
                ExchangeTxn = c.ExchangeTurnoverCharge,
                Sebi = c.SebiTurnoverCharge,
                Stamp = c.StampDuty,
                Gst = c.Gst,
                Dp = dp,
                Total = c.Total + dp
            });
        }
        return rows;
    }
}
