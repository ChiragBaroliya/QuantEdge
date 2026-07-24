using System;
using System.Collections.Generic;
using System.Data;
using System.Threading.Tasks;
using Dapper;
using QuantEdge.Domain.Entities;

namespace QuantEdge.Infrastructure.Persistence.Repositories;

/// <summary>
/// High-performance Dapper repository implementation for indicators using Stored Procedures.
/// </summary>
public class MarketIndicatorRepository : IMarketIndicatorRepository
{
    private readonly IDbConnectionFactory _connectionFactory;

    public MarketIndicatorRepository(IDbConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
    }

    /// <summary>
    /// Executes the "sp_insert_market_indicator" stored procedure to persist indicators.
    /// </summary>
    public async Task InsertAsync(MarketIndicator indicator)
    {
        using var connection = _connectionFactory.CreateConnection();

        var parameters = new DynamicParameters();
        parameters.Add("p_id", indicator.Id);
        parameters.Add("p_symbol", indicator.Symbol);
        parameters.Add("p_timeframe", indicator.Timeframe);
        parameters.Add("p_rsi", indicator.RSI);
        parameters.Add("p_ema20", indicator.EMA20);
        parameters.Add("p_ema50", indicator.EMA50);
        parameters.Add("p_macd", indicator.MACD);
        parameters.Add("p_signal_line", indicator.SignalLine);
        parameters.Add("p_vwap", indicator.VWAP);
        parameters.Add("p_candle_time", indicator.CandleTime);
        parameters.Add("p_created_at", indicator.CreatedAt);

        try
        {
            await connection.ExecuteAsync(
                "sp_insert_market_indicator",
                parameters,
                commandType: CommandType.StoredProcedure
            );
        }
        finally
        {
            if (connection.State == ConnectionState.Open)
            {
                connection.Close();
            }
        }
    }

    /// <summary>
    /// Retrieves indicator logs using sp_get_market_indicators function.
    /// </summary>
    public async Task<IEnumerable<MarketIndicator>> GetHistoryAsync(string symbol, string timeframe, int limit)
    {
        using var connection = _connectionFactory.CreateConnection();

        try
        {
            return await connection.QueryAsync<MarketIndicator>(
                "SELECT * FROM sp_get_market_indicators(@p_symbol, @p_timeframe, @p_limit);",
                new { p_symbol = symbol, p_timeframe = timeframe, p_limit = limit }
            );
        }
        finally
        {
            if (connection.State == ConnectionState.Open)
            {
                connection.Close();
            }
        }
    }

    /// <summary>
    /// Deletes today's calculated indicators for a specific symbol and timeframe.
    /// </summary>
    public async Task DeleteTodayIndicatorsAsync(string symbol, string timeframe)
    {
        DateTime todayStart = DateTime.UtcNow.Date;
        DateTime todayEnd = todayStart.AddDays(1).AddTicks(-1);
        await DeleteIndicatorsRangeAsync(symbol, timeframe, todayStart, todayEnd);
    }

    public async Task DeleteIndicatorsRangeAsync(string? symbol, string timeframe, DateTime fromDate, DateTime toDate)
    {
        string safeTimeframe = timeframe.ToLower();
        if (!new[] { "1m", "5m", "15m", "60m", "1d" }.Contains(safeTimeframe)) return;

        using var connection = _connectionFactory.CreateConnection();
        string tableName = $"market_indicators_{safeTimeframe}";

        try
        {
            if (string.IsNullOrWhiteSpace(symbol))
            {
                await connection.ExecuteAsync(
                    $"DELETE FROM {tableName} WHERE created_at >= @FromDate AND created_at <= @ToDate;",
                    new { FromDate = fromDate, ToDate = toDate }
                );
            }
            else
            {
                await connection.ExecuteAsync(
                    $"DELETE FROM {tableName} WHERE symbol = @Symbol AND created_at >= @FromDate AND created_at <= @ToDate;",
                    new { Symbol = symbol.ToUpper(), FromDate = fromDate, ToDate = toDate }
                );
            }
        }
        finally
        {
            if (connection.State == ConnectionState.Open)
            {
                connection.Close();
            }
        }
    }
}
