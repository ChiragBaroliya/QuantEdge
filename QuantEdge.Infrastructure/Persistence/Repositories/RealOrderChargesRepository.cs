using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;

namespace QuantEdge.Infrastructure.Persistence.Repositories;

/// <summary>Zerodha's actual charges for one real order (real_order_charges).</summary>
public sealed class RealOrderCharges
{
    public int OrderId { get; set; }
    public string? BrokerOrderId { get; set; }
    public decimal Brokerage { get; set; }
    public decimal Stt { get; set; }
    public decimal ExchangeTxn { get; set; }
    public decimal Sebi { get; set; }
    public decimal Stamp { get; set; }
    public decimal Gst { get; set; }
    public decimal Dp { get; set; }
    public decimal Total { get; set; }
}

/// <summary>A filled real order that still needs its charges from Zerodha.</summary>
public sealed class RealOrderNeedingCharges
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public string? BrokerOrderId { get; set; }
    public string Symbol { get; set; } = string.Empty;
    public int Side { get; set; }
    public int Quantity { get; set; }
    public decimal FilledPrice { get; set; }
    public DateTime? FilledAt { get; set; }
}

public interface IRealOrderChargesRepository
{
    /// <summary>Filled real orders with a broker id, filled at or after <paramref name="sinceUtc"/>, without a charges row yet.</summary>
    Task<IReadOnlyList<RealOrderNeedingCharges>> GetFilledOrdersWithoutChargesAsync(DateTime sinceUtc);
    Task SaveAsync(IReadOnlyCollection<RealOrderCharges> charges);
    Task<Dictionary<int, RealOrderCharges>> GetByOrderIdsAsync(IReadOnlyCollection<int> orderIds);
}

public class RealOrderChargesRepository : IRealOrderChargesRepository
{
    private readonly IDbConnectionFactory _connectionFactory;

    public RealOrderChargesRepository(IDbConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
    }

    public async Task<IReadOnlyList<RealOrderNeedingCharges>> GetFilledOrdersWithoutChargesAsync(DateTime sinceUtc)
    {
        using var connection = _connectionFactory.CreateConnection();
        // status 1 = Filled. filled_quantity (when recorded) is what actually traded.
        const string sql = @"
            SELECT o.id, o.user_id AS UserId, o.broker_order_id AS BrokerOrderId, o.symbol, o.side,
                   COALESCE(NULLIF(o.filled_quantity, 0), o.quantity) AS Quantity,
                   o.filled_price AS FilledPrice, o.filled_at AS FilledAt
            FROM real_orders o
            LEFT JOIN real_order_charges c ON c.order_id = o.id
            WHERE o.status = 1
              AND o.broker_order_id IS NOT NULL
              AND o.filled_price > 0
              AND COALESCE(o.filled_at, o.created_at) >= @sinceUtc
              AND c.order_id IS NULL
            ORDER BY o.user_id, o.filled_at;";
        return (await connection.QueryAsync<RealOrderNeedingCharges>(sql, new { sinceUtc })).ToList();
    }

    public async Task SaveAsync(IReadOnlyCollection<RealOrderCharges> charges)
    {
        if (charges == null || charges.Count == 0) return;
        using var connection = _connectionFactory.CreateConnection();
        const string sql = @"
            INSERT INTO real_order_charges (order_id, broker_order_id, brokerage, stt, exchange_txn, sebi, stamp, gst, dp, total, fetched_at)
            VALUES (@OrderId, @BrokerOrderId, @Brokerage, @Stt, @ExchangeTxn, @Sebi, @Stamp, @Gst, @Dp, @Total, NOW())
            ON CONFLICT (order_id) DO UPDATE
            SET brokerage = EXCLUDED.brokerage, stt = EXCLUDED.stt, exchange_txn = EXCLUDED.exchange_txn, sebi = EXCLUDED.sebi,
                stamp = EXCLUDED.stamp, gst = EXCLUDED.gst, dp = EXCLUDED.dp, total = EXCLUDED.total, fetched_at = NOW();";
        await connection.ExecuteAsync(sql, charges);
    }

    public async Task<Dictionary<int, RealOrderCharges>> GetByOrderIdsAsync(IReadOnlyCollection<int> orderIds)
    {
        var result = new Dictionary<int, RealOrderCharges>();
        if (orderIds == null || orderIds.Count == 0) return result;
        using var connection = _connectionFactory.CreateConnection();
        const string sql = @"
            SELECT order_id AS OrderId, broker_order_id AS BrokerOrderId, brokerage, stt, exchange_txn AS ExchangeTxn, sebi,
                   stamp, gst, dp, total
            FROM real_order_charges
            WHERE order_id = ANY(@ids);";
        foreach (var row in await connection.QueryAsync<RealOrderCharges>(sql, new { ids = orderIds.Distinct().ToArray() }))
        {
            result[row.OrderId] = row;
        }
        return result;
    }
}
