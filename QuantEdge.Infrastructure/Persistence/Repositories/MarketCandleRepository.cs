using System;
using System.Collections.Generic;
using System.Data;
using System.Threading.Tasks;
using Dapper;
using QuantEdge.Domain.Entities;

namespace QuantEdge.Infrastructure.Persistence.Repositories;

/// <summary>
/// High-performance Dapper repository implementation for saving and querying candles using Stored Procedures.
/// </summary>
public class MarketCandleRepository : IMarketCandleRepository
{
    private readonly IDbConnectionFactory _connectionFactory;

    public MarketCandleRepository(IDbConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
    }

    /// <summary>
    /// Executes the "sp_insert_market_candle" stored procedure to persist a candlestick bar.
    /// </summary>
    public async Task InsertAsync(MarketCandle candle)
    {
        using var connection = _connectionFactory.CreateConnection();
        
        var parameters = new DynamicParameters();
        parameters.Add("p_id", candle.Id);
        parameters.Add("p_symbol", candle.Symbol);
        parameters.Add("p_timeframe", candle.Timeframe);
        parameters.Add("p_open", candle.Open);
        parameters.Add("p_high", candle.High);
        parameters.Add("p_low", candle.Low);
        parameters.Add("p_close", candle.Close);
        parameters.Add("p_volume", candle.Volume);
        parameters.Add("p_candle_time", candle.CandleTime);
        parameters.Add("p_created_at", candle.CreatedAt);

        try
        {
            await connection.ExecuteAsync(
                "sp_insert_market_candle",
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
    /// Retrieves historical candles using sp_get_market_candles function.
    /// </summary>
    public async Task<IEnumerable<MarketCandle>> GetHistoryAsync(string symbol, string timeframe, int limit)
    {
        using var connection = _connectionFactory.CreateConnection();

        try
        {
            return await connection.QueryAsync<MarketCandle>(
                "SELECT * FROM sp_get_market_candles(@p_symbol, @p_timeframe, @p_limit);",
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

    public async Task DeleteTodayHistoryAsync(string symbol, string timeframe)
    {
        DateTime todayStart = DateTime.UtcNow.Date;
        DateTime todayEnd = todayStart.AddDays(1).AddTicks(-1);
        await DeleteHistoryRangeAsync(symbol, timeframe, todayStart, todayEnd);
    }

    public async Task DeleteHistoryRangeAsync(string? symbol, string timeframe, DateTime fromDate, DateTime toDate)
    {
        string safeTimeframe = timeframe.ToLower();
        if (!new[] { "1m", "5m", "15m", "60m", "1d" }.Contains(safeTimeframe)) return;

        using var connection = _connectionFactory.CreateConnection();
        string tableName = $"market_candles_{safeTimeframe}";

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
