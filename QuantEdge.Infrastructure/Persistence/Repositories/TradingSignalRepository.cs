using System;
using System.Collections.Generic;
using System.Data;
using System.Threading.Tasks;
using Dapper;
using QuantEdge.Domain.Entities;

namespace QuantEdge.Infrastructure.Persistence.Repositories;

/// <summary>
/// High-performance Dapper repository implementation for signals using Stored Procedures.
/// </summary>
public class TradingSignalRepository : ITradingSignalRepository
{
    private readonly IDbConnectionFactory _connectionFactory;

    public TradingSignalRepository(IDbConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
    }

    /// <summary>
    /// Executes the "sp_insert_trading_signal" stored procedure to persist a trade signal.
    /// </summary>
    public async Task InsertAsync(TradingSignal signal)
    {
        using var connection = _connectionFactory.CreateConnection();

        var parameters = new DynamicParameters();
        parameters.Add("p_id", signal.Id);
        parameters.Add("p_symbol", signal.Symbol);
        parameters.Add("p_signal_type", signal.SignalType);
        parameters.Add("p_signal_strength", signal.SignalStrength);
        parameters.Add("p_entry_price", signal.EntryPrice);
        parameters.Add("p_reason", signal.Reason);
        parameters.Add("p_candle_time", signal.CandleTime);
        parameters.Add("p_created_at", signal.CreatedAt);

        await connection.ExecuteAsync(
            "sp_insert_trading_signal",
            parameters,
            commandType: CommandType.StoredProcedure
        );
    }

    /// <summary>
    /// Retrieves trade signal outputs using sp_get_recent_trading_signals function.
    /// </summary>
    public async Task<IEnumerable<TradingSignal>> GetRecentSignalsAsync(int limit)
    {
        using var connection = _connectionFactory.CreateConnection();

        return await connection.QueryAsync<TradingSignal>(
            "SELECT * FROM sp_get_recent_trading_signals(@p_limit);",
            new { p_limit = limit }
        );
    }
}
