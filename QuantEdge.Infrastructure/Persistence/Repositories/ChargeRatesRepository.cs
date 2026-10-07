using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Extensions.Logging;
using QuantEdge.Infrastructure.Services;

namespace QuantEdge.Infrastructure.Persistence.Repositories;

/// <summary>charge_rates (schema.sql): dated brokerage + statutory charge rates per product (CNC / MIS).</summary>
public interface IChargeRatesRepository
{
    /// <summary>All dated rate rows; the built-in Zerodha defaults when the table is missing or empty.</summary>
    Task<IReadOnlyList<ChargeRates>> GetAllAsync();
}

public class ChargeRatesRepository : IChargeRatesRepository
{
    private readonly IDbConnectionFactory _connectionFactory;
    private readonly ILogger<ChargeRatesRepository> _logger;

    public ChargeRatesRepository(IDbConnectionFactory connectionFactory, ILogger<ChargeRatesRepository> logger)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<IReadOnlyList<ChargeRates>> GetAllAsync()
    {
        try
        {
            using var connection = _connectionFactory.CreateConnection();
            const string sql = @"
                SELECT product, effective_from::timestamp AS EffectiveFrom, brokerage_pct AS BrokeragePct, brokerage_max_per_order AS BrokerageMaxPerOrder,
                       stt_buy_pct AS SttBuyPct, stt_sell_pct AS SttSellPct, exchange_txn_pct AS ExchangeTxnPct,
                       sebi_per_crore AS SebiPerCrore, stamp_buy_pct AS StampBuyPct, gst_pct AS GstPct, dp_per_sell AS DpPerSell
                FROM charge_rates;";
            var rows = (await connection.QueryAsync<Row>(sql)).ToList();
            if (rows.Count == 0) return ChargeRates.Defaults;

            return rows.Select(r => new ChargeRates(r.Product, r.EffectiveFrom, r.BrokeragePct, r.BrokerageMaxPerOrder,
                r.SttBuyPct, r.SttSellPct, r.ExchangeTxnPct, r.SebiPerCrore, r.StampBuyPct, r.GstPct, r.DpPerSell)).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "charge_rates not readable (apply schema.sql) - using built-in Zerodha default rates.");
            return ChargeRates.Defaults;
        }
    }

    private sealed class Row
    {
        public string Product { get; set; } = "CNC";
        public DateTime EffectiveFrom { get; set; }
        public decimal BrokeragePct { get; set; }
        public decimal? BrokerageMaxPerOrder { get; set; }
        public decimal SttBuyPct { get; set; }
        public decimal SttSellPct { get; set; }
        public decimal ExchangeTxnPct { get; set; }
        public decimal SebiPerCrore { get; set; }
        public decimal StampBuyPct { get; set; }
        public decimal GstPct { get; set; }
        public decimal DpPerSell { get; set; }
    }
}
