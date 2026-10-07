using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using QuantEdge.Infrastructure.DTOs;

namespace QuantEdge.Infrastructure.Persistence.Repositories;

public class LiveQuoteRepository : ILiveQuoteRepository
{
    private readonly IDbConnectionFactory _connectionFactory;

    public LiveQuoteRepository(IDbConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
    }

    public async Task UpsertBatchAsync(IReadOnlyCollection<LiveQuote> quotes)
    {
        if (quotes == null || quotes.Count == 0) return;

        // An out-of-order flush never overwrites a newer quote.
        const string sql = @"
            INSERT INTO live_quotes (symbol, ltp, prev_close, day_open, day_high, day_low, as_of, updated_at)
            VALUES (@Symbol, @Ltp, @PrevClose, @DayOpen, @DayHigh, @DayLow, @AsOfUtc, NOW())
            ON CONFLICT (symbol) DO UPDATE
            SET ltp = EXCLUDED.ltp,
                prev_close = CASE WHEN EXCLUDED.prev_close > 0 THEN EXCLUDED.prev_close ELSE live_quotes.prev_close END,
                day_open = EXCLUDED.day_open,
                day_high = EXCLUDED.day_high,
                day_low = EXCLUDED.day_low,
                as_of = EXCLUDED.as_of,
                updated_at = NOW()
            WHERE live_quotes.as_of <= EXCLUDED.as_of;";

        using var connection = _connectionFactory.CreateConnection();
        await connection.ExecuteAsync(sql, quotes.Select(q => new
        {
            Symbol = q.Symbol.ToUpperInvariant(),
            q.Ltp,
            q.PrevClose,
            q.DayOpen,
            q.DayHigh,
            q.DayLow,
            AsOfUtc = q.AsOfUtc.Kind == DateTimeKind.Utc ? q.AsOfUtc : DateTime.SpecifyKind(q.AsOfUtc, DateTimeKind.Utc)
        }));
    }

    public async Task<Dictionary<string, LiveQuote>> GetAsync(IReadOnlyCollection<string> symbols)
    {
        var result = new Dictionary<string, LiveQuote>(StringComparer.OrdinalIgnoreCase);
        if (symbols == null || symbols.Count == 0) return result;

        const string sql = @"
            SELECT symbol, ltp, prev_close AS PrevClose, day_open AS DayOpen, day_high AS DayHigh, day_low AS DayLow, as_of AS AsOfUtc
            FROM live_quotes
            WHERE symbol = ANY(@Symbols);";

        using var connection = _connectionFactory.CreateConnection();
        var rows = await connection.QueryAsync<LiveQuoteRow>(sql, new { Symbols = symbols.Select(s => s.ToUpperInvariant()).Distinct().ToArray() });
        foreach (var r in rows)
        {
            result[r.Symbol] = new LiveQuote(r.Symbol, r.Ltp, r.PrevClose, r.DayOpen, r.DayHigh, r.DayLow,
                r.AsOfUtc.Kind == DateTimeKind.Utc ? r.AsOfUtc : r.AsOfUtc.ToUniversalTime());
        }
        return result;
    }

    private sealed class LiveQuoteRow
    {
        public string Symbol { get; set; } = string.Empty;
        public decimal Ltp { get; set; }
        public decimal PrevClose { get; set; }
        public decimal DayOpen { get; set; }
        public decimal DayHigh { get; set; }
        public decimal DayLow { get; set; }
        public DateTime AsOfUtc { get; set; }
    }
}
