using System;
using System.Collections.Generic;
using System.Linq;

namespace QuantEdge.Infrastructure.Services;

/// <summary>
/// Statutory + broker charge rates for one product, valid from <see cref="EffectiveFrom"/> (charge_rates table).
/// Percentages are in percent (0.1 = 0.1%). Rates change rarely; when Zerodha / the exchange revise them, insert a
/// new dated row instead of editing code - older trades keep the rates that applied on their date.
/// </summary>
public sealed record ChargeRates(
    string Product,               // CNC (delivery) or MIS (intraday)
    DateTime EffectiveFrom,
    decimal BrokeragePct,         // per executed order, % of order value
    decimal? BrokerageMaxPerOrder,// cap per executed order (null = no cap)
    decimal SttBuyPct,
    decimal SttSellPct,
    decimal ExchangeTxnPct,       // NSE transaction charge, both sides
    decimal SebiPerCrore,         // ₹ per ₹1 crore of turnover, both sides
    decimal StampBuyPct,          // buy side only
    decimal GstPct,               // on brokerage + exchange + SEBI
    decimal DpPerSell)            // ₹ per scrip per sell day, GST included (delivery only)
{
    // Zerodha equity rates as published on zerodha.com/charges (checked 06-Oct-2026: NSE transaction 0.00307%, DP ₹15.34/scrip).
    public static readonly ChargeRates ZerodhaCncDefault = new("CNC", new DateTime(2024, 10, 1),
        0m, null, 0.1m, 0.1m, 0.00307m, 10m, 0.015m, 18m, 15.34m);

    public static readonly ChargeRates ZerodhaMisDefault = new("MIS", new DateTime(2024, 10, 1),
        0.03m, 20m, 0m, 0.025m, 0.00307m, 10m, 0.003m, 18m, 0m);

    public static IReadOnlyList<ChargeRates> Defaults { get; } = new[] { ZerodhaCncDefault, ZerodhaMisDefault };
}

/// <summary>Itemised charges for a trade, like the contract note. Total is what separates gross from net P&amp;L.</summary>
public sealed record ChargeBreakdown(
    string Product,
    decimal Brokerage,
    decimal Stt,
    decimal ExchangeTxn,
    decimal Sebi,
    decimal Stamp,
    decimal Gst,
    decimal Dp)
{
    public decimal Total => Brokerage + Stt + ExchangeTxn + Sebi + Stamp + Gst + Dp;
    public static readonly ChargeBreakdown None = new("NONE", 0, 0, 0, 0, 0, 0, 0);
}

/// <summary>
/// Gross → Charges → Net. Pure arithmetic on order values; used by every P&amp;L view so they all agree.
/// Estimates match the contract note to within rounding (the exchange rounds STT / stamp to the rupee per
/// contract note, which can differ by a few rupees when several trades share one note).
/// </summary>
public static class ChargesCalculator
{
    /// <summary>
    /// A buy and a sell on the same IST day are charged as intraday (MIS rates) even under CNC; anything held
    /// overnight is delivery (CNC). Unknown entry date -> delivery (the swing default).
    /// </summary>
    public static string ProductFor(DateTime? entryIst, DateTime exitIst) =>
        entryIst.HasValue && entryIst.Value.Date == exitIst.Date ? "MIS" : "CNC";

    /// <summary>Rates for <paramref name="product"/> in force on <paramref name="onDate"/> (latest effective_from on or before it).</summary>
    public static ChargeRates RatesFor(IEnumerable<ChargeRates>? table, string product, DateTime onDate)
    {
        var candidates = (table ?? ChargeRates.Defaults)
            .Where(r => string.Equals(r.Product, product, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(r => r.EffectiveFrom)
            .ToList();
        return candidates.FirstOrDefault(r => r.EffectiveFrom.Date <= onDate.Date)
            ?? candidates.LastOrDefault()
            ?? (string.Equals(product, "MIS", StringComparison.OrdinalIgnoreCase) ? ChargeRates.ZerodhaMisDefault : ChargeRates.ZerodhaCncDefault);
    }

    /// <summary>
    /// Estimated round-trip charges for an OPEN position if it were closed now at <paramref name="mark"/>: same-day
    /// opens are charged at intraday rates, older ones at delivery rates. Used for "net if sold now" on live pages.
    /// </summary>
    public static decimal EstimateOpenPosition(decimal entryPrice, decimal mark, int quantity, bool isShort, DateTime openedAtUtc,
        IEnumerable<ChargeRates>? table = null)
    {
        if (quantity <= 0 || entryPrice <= 0m || mark <= 0m) return 0m;
        DateTime nowIst = DayChangeCalculator.IstDate(DateTime.UtcNow);
        string product = ProductFor(DayChangeCalculator.IstDate(openedAtUtc), nowIst);
        var rates = RatesFor(table, product, nowIst);
        decimal entryValue = entryPrice * quantity, exitValue = mark * quantity;
        return isShort
            ? RoundTrip(buyValue: exitValue, sellValue: entryValue, rates).Total
            : RoundTrip(buyValue: entryValue, sellValue: exitValue, rates).Total;
    }

    /// <summary>
    /// Charges for one round trip: <paramref name="buyValue"/> = buy price × qty, <paramref name="sellValue"/> = sell
    /// price × qty. Pass 0 for a leg that isn't known (only that leg's charges are skipped).
    /// </summary>
    public static ChargeBreakdown RoundTrip(decimal buyValue, decimal sellValue, ChargeRates rates)
    {
        buyValue = Math.Max(0m, buyValue);
        sellValue = Math.Max(0m, sellValue);
        if (buyValue == 0m && sellValue == 0m) return ChargeBreakdown.None;

        decimal Brokerage(decimal value)
        {
            if (value <= 0m) return 0m;
            decimal b = value * rates.BrokeragePct / 100m;
            return rates.BrokerageMaxPerOrder.HasValue ? Math.Min(b, rates.BrokerageMaxPerOrder.Value) : b;
        }

        decimal turnover = buyValue + sellValue;
        decimal brokerage = Brokerage(buyValue) + Brokerage(sellValue);
        decimal stt = buyValue * rates.SttBuyPct / 100m + sellValue * rates.SttSellPct / 100m;
        decimal exchange = turnover * rates.ExchangeTxnPct / 100m;
        decimal sebi = turnover * rates.SebiPerCrore / 10_000_000m;
        decimal stamp = buyValue * rates.StampBuyPct / 100m;
        decimal gst = (brokerage + exchange + sebi) * rates.GstPct / 100m;
        decimal dp = sellValue > 0m ? rates.DpPerSell : 0m;

        return new ChargeBreakdown(rates.Product,
            Math.Round(brokerage, 2), Math.Round(stt, 2), Math.Round(exchange, 2), Math.Round(sebi, 2),
            Math.Round(stamp, 2), Math.Round(gst, 2), Math.Round(dp, 2));
    }
}
