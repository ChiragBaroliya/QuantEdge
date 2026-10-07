using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Extensions.Logging;
using QuantEdge.Infrastructure.Interfaces;
using QuantEdge.Infrastructure.Persistence;

namespace QuantEdge.Infrastructure.Services;

/// <summary>A split / bonus detected for one symbol on one ex-date.</summary>
public sealed class CorporateAction
{
    public int Id { get; set; }
    public string Symbol { get; set; } = string.Empty;
    public DateTime ExDate { get; set; }
    public decimal Factor { get; set; }
    public string? RatioText { get; set; }
    public string Status { get; set; } = "NEEDS_REVIEW";
}

public interface ICorporateActionService
{
    /// <summary>
    /// Compares <paramref name="tradeDate"/>'s bhavcopy prev_close with the previous loaded day's close (EQ series) and
    /// records every ≥5% gap as a corporate action. Simple ratios are applied immediately (candles + open positions);
    /// others are left NEEDS_REVIEW. Both are reported to the header bell. No Zerodha or NSE call.
    /// </summary>
    Task<IReadOnlyList<CorporateAction>> DetectAndApplyAsync(DateTime tradeDate);
}

public class CorporateActionService : ICorporateActionService
{
    private const decimal MinGap = 0.05m;          // ignore anything closer than 5% (dividends, rounding)
    private const decimal RatioTolerance = 0.005m; // factor must be within 0.5% of p/q to auto-apply
    private static readonly string[] CandleTimeframes = { "1m", "5m", "15m", "60m", "1d" };

    private readonly IDbConnectionFactory _connectionFactory;
    private readonly IIndicatorService _indicatorService;
    private readonly IBrokerApiEventRecorder _recorder;
    private readonly ILogger<CorporateActionService> _logger;

    public CorporateActionService(IDbConnectionFactory connectionFactory, IIndicatorService indicatorService,
        IBrokerApiEventRecorder recorder, ILogger<CorporateActionService> logger)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _indicatorService = indicatorService ?? throw new ArgumentNullException(nameof(indicatorService));
        _recorder = recorder ?? throw new ArgumentNullException(nameof(recorder));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<IReadOnlyList<CorporateAction>> DetectAndApplyAsync(DateTime tradeDate)
    {
        tradeDate = tradeDate.Date;
        using var connection = _connectionFactory.CreateConnection();
        const string detectSql = @"
            SELECT t.symbol, t.prev_close / p.close AS factor
            FROM nse_bhavcopy t
            JOIN nse_bhavcopy p ON p.symbol = t.symbol AND p.series = 'EQ'
             AND p.trade_date = (SELECT MAX(trade_date) FROM nse_bhavcopy WHERE trade_date < @d)
            WHERE t.trade_date = @d AND t.series = 'EQ'
              AND t.prev_close > 0 AND p.close > 0
              AND ABS(1 - t.prev_close / p.close) >= @minGap
              AND NOT EXISTS (SELECT 1 FROM corporate_actions ca WHERE ca.symbol = t.symbol AND ca.ex_date = @d);";
        var gaps = (await connection.QueryAsync<(string Symbol, decimal Factor)>(detectSql, new { d = tradeDate, minGap = MinGap })).ToList();

        var actions = new List<CorporateAction>();
        foreach (var (symbol, rawFactor) in gaps)
        {
            var ratio = MatchSimpleRatio(rawFactor);
            var action = new CorporateAction
            {
                Symbol = symbol,
                ExDate = tradeDate,
                Factor = ratio?.Factor ?? Math.Round(rawFactor, 8),
                RatioText = ratio.HasValue ? $"price x {ratio.Value.P}/{ratio.Value.Q}" : null,
                Status = ratio.HasValue ? "APPLIED" : "NEEDS_REVIEW"
            };

            if (ratio.HasValue)
            {
                await ApplyAsync(action);
                _recorder.RecordFailure(BrokerApiSource.Nse, "corporate action",
                    $"{symbol}: split/bonus on {tradeDate:dd-MMM-yyyy} ({action.RatioText}). Older candles and open positions were adjusted (price x {action.Factor:0.####}, quantity / {action.Factor:0.####}).",
                    symbol: symbol, level: "warning");
            }
            else
            {
                _recorder.RecordFailure(BrokerApiSource.Nse, "corporate action",
                    $"{symbol}: NSE adjusted the previous close by {rawFactor:0.####} on {tradeDate:dd-MMM-yyyy} (rights issue or unusual ratio?). Not applied automatically - check and adjust manually.",
                    symbol: symbol, level: "warning");
            }

            await connection.ExecuteAsync(@"
                INSERT INTO corporate_actions (symbol, ex_date, factor, ratio_text, status, details, applied_at)
                VALUES (@Symbol, @ExDate, @Factor, @RatioText, @Status, @Details, CASE WHEN @Status = 'APPLIED' THEN NOW() END)
                ON CONFLICT (symbol, ex_date) DO NOTHING;",
                new { action.Symbol, action.ExDate, action.Factor, action.RatioText, action.Status,
                      Details = $"bhavcopy prev_close / previous close = {rawFactor:0.########}" });
            actions.Add(action);
        }
        return actions;
    }

    /// <summary>Closest p/q (1..10) to the factor when within 0.5%; null otherwise.</summary>
    public static (int P, int Q, decimal Factor)? MatchSimpleRatio(decimal factor)
    {
        if (factor <= 0m) return null;
        (int P, int Q, decimal Factor)? best = null;
        decimal bestErr = decimal.MaxValue;
        for (int q = 1; q <= 10; q++)
        {
            for (int p = 1; p <= 10; p++)
            {
                if (p == q) continue;
                decimal candidate = (decimal)p / q;
                decimal err = Math.Abs(factor - candidate) / candidate;
                if (err < bestErr)
                {
                    bestErr = err;
                    best = (p, q, Math.Round(candidate, 8));
                }
            }
        }
        return bestErr <= RatioTolerance ? best : null;
    }

    /// <summary>Adjusts everything recorded before the ex-date so it's comparable with prices after it.</summary>
    private async Task ApplyAsync(CorporateAction action)
    {
        DateTime exStartUtc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(action.ExDate, DateTimeKind.Unspecified),
            Helpers.TimeZoneHelper.IndianTimeZone);
        var args = new { s = action.Symbol, f = action.Factor, t = exStartUtc };

        using var connection = _connectionFactory.CreateConnection();
        connection.Open();
        using var tx = connection.BeginTransaction();
        foreach (var tf in CandleTimeframes)
        {
            await connection.ExecuteAsync($@"
                UPDATE market_candles_{tf}
                SET open = open * @f, high = high * @f, low = low * @f, close = close * @f, volume = ROUND(volume / @f)
                WHERE UPPER(symbol) = UPPER(@s) AND candle_time < @t;", args, tx);
        }

        // Open positions bought before the ex-date: same value, more (or fewer) shares at a lower (or higher) price.
        foreach (var (table, hasTrailingLevel) in new[] { ("paper_positions", true), ("manual_paper_positions", false), ("real_positions", true) })
        {
            string trailing = hasTrailingLevel ? ", trailing_stop_loss = trailing_stop_loss * @f" : string.Empty;
            await connection.ExecuteAsync($@"
                UPDATE {table}
                SET average_entry_price = average_entry_price * @f,
                    quantity = ROUND(quantity / @f),
                    stop_loss = stop_loss * @f,
                    take_profit = take_profit * @f{trailing}
                WHERE UPPER(symbol) = UPPER(@s) AND status = 0 AND opened_at < @t;", args, tx);
        }
        tx.Commit();

        // Indicators were computed on unadjusted prices - recompute them from the adjusted candles.
        foreach (var tf in CandleTimeframes)
        {
            try
            {
                await _indicatorService.BackfillHistoricalIndicatorsAsync(action.Symbol, tf);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Indicator recompute after corporate action failed for {Symbol} {Tf}.", action.Symbol, tf);
            }
        }
        _logger.LogWarning("Corporate action applied: {Symbol} ex {ExDate:yyyy-MM-dd} factor {Factor}.", action.Symbol, action.ExDate, action.Factor);
    }
}
