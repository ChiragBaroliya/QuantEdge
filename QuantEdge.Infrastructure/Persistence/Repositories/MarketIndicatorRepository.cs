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
    /// High-performance bulk insert for market indicators reusing a single database connection.
    /// </summary>
    public async Task InsertBatchAsync(IEnumerable<MarketIndicator> indicators)
    {
        if (indicators == null || !indicators.Any()) return;

        var groups = indicators.GroupBy(i => i.Timeframe.ToLower());
        using var connection = _connectionFactory.CreateConnection();

        foreach (var group in groups)
        {
            string safeTimeframe = group.Key;
            if (!new[] { "1m", "5m", "15m", "60m", "1d" }.Contains(safeTimeframe))
            {
                safeTimeframe = "1m";
            }
            string tableName = $"market_indicators_{safeTimeframe}";

            string sql = $@"
                INSERT INTO {tableName} (id, symbol, timeframe, rsi, ema20, ema50, macd, signal_line, vwap, candle_time, created_at)
                VALUES (@Id, @Symbol, @Timeframe, @RSI, @EMA20, @EMA50, @MACD, @SignalLine, @VWAP, @CandleTime, @CreatedAt)
                ON CONFLICT (id, candle_time) DO UPDATE
                SET rsi = EXCLUDED.rsi,
                    ema20 = EXCLUDED.ema20,
                    ema50 = EXCLUDED.ema50,
                    macd = EXCLUDED.macd,
                    signal_line = EXCLUDED.signal_line,
                    vwap = EXCLUDED.vwap;";

            const int chunkSize = 1000;
            var list = group.ToList();
            for (int i = 0; i < list.Count; i += chunkSize)
            {
                var chunk = list.Skip(i).Take(chunkSize);
                await connection.ExecuteAsync(sql, chunk);
            }
        }
    }

    /// <summary>
    /// Retrieves indicator logs using direct SQL query on timeframe table for maximum performance and reliability.
    /// </summary>
    public async Task<IEnumerable<MarketIndicator>> GetHistoryAsync(string symbol, string timeframe, int limit, DateTime? beforeTime = null)
    {
        string safeTimeframe = timeframe.ToLower();
        if (!new[] { "1m", "5m", "15m", "60m", "1d" }.Contains(safeTimeframe))
        {
            safeTimeframe = "1m";
        }
        string tableName = $"market_indicators_{safeTimeframe}";

        using var connection = _connectionFactory.CreateConnection();

        string upperSymbol = symbol.ToUpper();

        try
        {
            if (beforeTime.HasValue)
            {
                string sql = $"SELECT id, candle_time AS CandleTime, symbol, timeframe, rsi, ema20, ema50, macd, signal_line AS SignalLine, vwap, created_at AS CreatedAt FROM {tableName} WHERE symbol = @Symbol AND candle_time < @BeforeTime ORDER BY candle_time DESC LIMIT @Limit;";
                return await connection.QueryAsync<MarketIndicator>(sql, new { Symbol = upperSymbol, BeforeTime = beforeTime.Value, Limit = limit });
            }
            else
            {
                string sql = $"SELECT id, candle_time AS CandleTime, symbol, timeframe, rsi, ema20, ema50, macd, signal_line AS SignalLine, vwap, created_at AS CreatedAt FROM {tableName} WHERE symbol = @Symbol ORDER BY candle_time DESC LIMIT @Limit;";
                return await connection.QueryAsync<MarketIndicator>(sql, new { Symbol = upperSymbol, Limit = limit });
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
