using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Extensions.Logging;
using QuantEdge.Infrastructure.Persistence;

namespace QuantEdge.Infrastructure.Services;

/// <summary>One row of NSE's CM bhavcopy.</summary>
public sealed class NseBhavcopyRow
{
    public DateTime TradeDate { get; set; }
    public string Symbol { get; set; } = string.Empty;
    public string Series { get; set; } = string.Empty;
    public string? Isin { get; set; }
    public decimal Open { get; set; }
    public decimal High { get; set; }
    public decimal Low { get; set; }
    public decimal Close { get; set; }
    public decimal? Last { get; set; }
    public decimal? PrevClose { get; set; }
    public long Volume { get; set; }
    public decimal? Turnover { get; set; }
}

/// <summary>Outcome of storing one trading day.</summary>
public sealed record BhavcopyLoadResult(DateTime TradeDate, int Rows, int DailyCandlesUpdated, int CloseMismatches = 0);

public interface INseBhavcopyService
{
    /// <summary>Downloads and parses NSE's bhavcopy for this IST date; null when NSE has no file (holiday, or not out yet). No Zerodha call.</summary>
    Task<IReadOnlyList<NseBhavcopyRow>?> DownloadAsync(DateTime tradeDate, CancellationToken cancellationToken = default);

    /// <summary>Stores the rows in nse_bhavcopy and overwrites that day's market_candles_1d rows (EQ series) with the official OHLC / volume, all or nothing.</summary>
    Task<BhavcopyLoadResult> StoreAsync(DateTime tradeDate, IReadOnlyList<NseBhavcopyRow> rows);

    /// <summary>True when this trading day is already stored.</summary>
    Task<bool> IsLoadedAsync(DateTime tradeDate);

    /// <summary>True when this day's split / bonus check has finished, so a failed check is retried even though the day is stored.</summary>
    Task<bool> IsCorporateActionCheckDoneAsync(DateTime tradeDate);

    Task MarkCorporateActionCheckDoneAsync(DateTime tradeDate);
}

public class NseBhavcopyService : INseBhavcopyService
{
    public const string HttpClientName = "NseArchives";
    private const string UrlFormat = "https://nsearchives.nseindia.com/content/cm/BhavCopy_NSE_CM_0_0_0_{0:yyyyMMdd}_F_0000.csv.zip";

    // A whole day's write is more than the connection's default 30 seconds should have to cover over a remote link.
    private const int StoreCommandTimeoutSeconds = 180;

    private static bool _checksTableEnsured;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IDbConnectionFactory _connectionFactory;
    private readonly ILogger<NseBhavcopyService> _logger;

    public NseBhavcopyService(IHttpClientFactory httpClientFactory, IDbConnectionFactory connectionFactory, ILogger<NseBhavcopyService> logger)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<bool> IsLoadedAsync(DateTime tradeDate)
    {
        using var connection = _connectionFactory.CreateConnection();
        return await connection.ExecuteScalarAsync<bool>(
            "SELECT EXISTS (SELECT 1 FROM nse_bhavcopy WHERE trade_date = @d);", new { d = tradeDate.Date });
    }

    public async Task<bool> IsCorporateActionCheckDoneAsync(DateTime tradeDate)
    {
        using var connection = _connectionFactory.CreateConnection();
        await EnsureChecksTableAsync(connection);
        return await connection.ExecuteScalarAsync<bool>(
            "SELECT EXISTS (SELECT 1 FROM nse_corporate_action_checks WHERE trade_date = @d);", new { d = tradeDate.Date });
    }

    public async Task MarkCorporateActionCheckDoneAsync(DateTime tradeDate)
    {
        using var connection = _connectionFactory.CreateConnection();
        await EnsureChecksTableAsync(connection);
        await connection.ExecuteAsync(
            "INSERT INTO nse_corporate_action_checks (trade_date) VALUES (@d) ON CONFLICT (trade_date) DO NOTHING;", new { d = tradeDate.Date });
    }

    private static async Task EnsureChecksTableAsync(System.Data.IDbConnection connection)
    {
        if (_checksTableEnsured) return;
        await connection.ExecuteAsync(@"
            CREATE TABLE IF NOT EXISTS nse_corporate_action_checks (
                trade_date DATE PRIMARY KEY,
                checked_at TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT NOW()
            );");
        _checksTableEnsured = true;
    }

    public async Task<IReadOnlyList<NseBhavcopyRow>?> DownloadAsync(DateTime tradeDate, CancellationToken cancellationToken = default)
    {
        tradeDate = tradeDate.Date;
        var client = _httpClientFactory.CreateClient(HttpClientName);
        using var response = await client.GetAsync(string.Format(CultureInfo.InvariantCulture, UrlFormat, tradeDate), cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;   // holiday / not published yet
        }
        response.EnsureSuccessStatusCode();

        await using var zipStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);
        var entry = archive.Entries.FirstOrDefault(e => e.Name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException("NSE bhavcopy zip has no CSV inside.");
        using var reader = new StreamReader(entry.Open());
        var rows = Parse(reader).ToList();
        if (rows.Count == 0) throw new InvalidDataException("NSE bhavcopy CSV has no rows.");
        return rows;
    }

    public async Task<BhavcopyLoadResult> StoreAsync(DateTime tradeDate, IReadOnlyList<NseBhavcopyRow> rows)
    {
        tradeDate = tradeDate.Date;
        var (dayStartUtc, dayEndUtc) = IstDayBoundsUtc(tradeDate);

        // One statement can't touch the same key twice, so a repeated line in the file must collapse to one row.
        var unique = rows.Where(r => r.TradeDate.Date == tradeDate)
            .GroupBy(r => (r.Symbol, r.Series)).Select(g => g.Last()).ToList();

        using var connection = _connectionFactory.CreateConnection();
        connection.Open();
        using var tx = connection.BeginTransaction();

        // All rows in one statement: row-by-row meant thousands of round trips to the database inside this transaction.
        const string upsert = @"
            INSERT INTO nse_bhavcopy (trade_date, symbol, series, isin, open, high, low, close, last, prev_close, volume, turnover, loaded_at)
            SELECT @d::date, x.symbol, x.series, x.isin, x.open, x.high, x.low, x.close, x.last, x.prev_close, x.volume, x.turnover, NOW()
            FROM UNNEST(@Symbols::VARCHAR[], @Series::VARCHAR[], @Isins::VARCHAR[], @Opens::NUMERIC[], @Highs::NUMERIC[], @Lows::NUMERIC[], @Closes::NUMERIC[],
                        @Lasts::NUMERIC[], @PrevCloses::NUMERIC[], @Volumes::BIGINT[], @Turnovers::NUMERIC[])
                 AS x(symbol, series, isin, open, high, low, close, last, prev_close, volume, turnover)
            ON CONFLICT (trade_date, symbol, series) DO UPDATE
            SET isin = EXCLUDED.isin, open = EXCLUDED.open, high = EXCLUDED.high, low = EXCLUDED.low, close = EXCLUDED.close,
                last = EXCLUDED.last, prev_close = EXCLUDED.prev_close, volume = EXCLUDED.volume, turnover = EXCLUDED.turnover, loaded_at = NOW();";
        await connection.ExecuteAsync(upsert, new
        {
            d = tradeDate,
            Symbols = unique.Select(r => r.Symbol).ToArray(),
            Series = unique.Select(r => r.Series).ToArray(),
            Isins = unique.Select(r => r.Isin).ToArray(),
            Opens = unique.Select(r => r.Open).ToArray(),
            Highs = unique.Select(r => r.High).ToArray(),
            Lows = unique.Select(r => r.Low).ToArray(),
            Closes = unique.Select(r => r.Close).ToArray(),
            Lasts = unique.Select(r => r.Last).ToArray(),
            PrevCloses = unique.Select(r => r.PrevClose).ToArray(),
            Volumes = unique.Select(r => r.Volume).ToArray(),
            Turnovers = unique.Select(r => r.Turnover).ToArray()
        }, tx, StoreCommandTimeoutSeconds);

        // The two statements below use the (symbol, candle_time) index: a function on either column would scan the whole
        // table instead, past the command timeout. Stored symbols are always upper case (every read depends on that too).
        var day = new { d = tradeDate, dayStartUtc, dayEndUtc };

        // Reconciliation (Plan L.7): before overwriting, log every stored daily close more than 0.5% away from NSE's.
        const string recordMismatches = @"
            INSERT INTO data_quality_issues (check_date, check_type, symbol, ours, external, diff_pct, details)
            SELECT @d, 'DAILY_CLOSE_VS_BHAVCOPY', b.symbol, c.close, b.close,
                   ROUND((c.close - b.close) / b.close * 100, 4),
                   'Stored daily close vs NSE official close (replaced with the official value)'
            FROM nse_bhavcopy b
            JOIN market_candles_1d c ON c.symbol = b.symbol AND c.candle_time >= @dayStartUtc AND c.candle_time < @dayEndUtc
            WHERE b.trade_date = @d AND b.series = 'EQ' AND b.close > 0
              AND ABS(c.close - b.close) / b.close > 0.005;";
        int mismatches = await connection.ExecuteAsync(recordMismatches, day, tx, StoreCommandTimeoutSeconds);

        // The day's stored daily candles become NSE's official values (close = official close, not the last trade).
        const string applyToCandles = @"
            UPDATE market_candles_1d c
            SET open = b.open, high = b.high, low = b.low, close = b.close, volume = b.volume
            FROM nse_bhavcopy b
            WHERE b.trade_date = @d AND b.series = 'EQ'
              AND c.symbol = b.symbol
              AND c.candle_time >= @dayStartUtc AND c.candle_time < @dayEndUtc;";
        int updated = await connection.ExecuteAsync(applyToCandles, day, tx, StoreCommandTimeoutSeconds);
        tx.Commit();

        _logger.LogInformation("NSE bhavcopy {Date:yyyy-MM-dd}: {Rows} rows stored, {Updated} daily candles set to official values, {Mismatches} differed by >0.5%.",
            tradeDate, unique.Count, updated, mismatches);
        return new BhavcopyLoadResult(tradeDate, unique.Count, updated, mismatches);
    }

    /// <summary>The start of this IST date and of the next one, in UTC.</summary>
    public static (DateTime StartUtc, DateTime EndUtc) IstDayBoundsUtc(DateTime tradeDate)
    {
        DateTime Utc(DateTime day) => TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(day, DateTimeKind.Unspecified), Helpers.TimeZoneHelper.IndianTimeZone);
        return (Utc(tradeDate.Date), Utc(tradeDate.Date.AddDays(1)));
    }

    /// <summary>Parses NSE's UDiFF CM bhavcopy CSV (columns located by header name, so column order changes don't break it).</summary>
    public static IEnumerable<NseBhavcopyRow> Parse(TextReader reader)
    {
        string? header = reader.ReadLine();
        if (header == null) yield break;
        var cols = header.Split(',').Select((name, i) => (name: name.Trim(), i)).ToDictionary(x => x.name, x => x.i, StringComparer.OrdinalIgnoreCase);
        int Col(string name) => cols.TryGetValue(name, out var i) ? i : throw new InvalidDataException($"Bhavcopy column '{name}' missing.");

        int cDate = Col("TradDt"), cSym = Col("TckrSymb"), cSeries = Col("SctySrs"), cOpen = Col("OpnPric"), cHigh = Col("HghPric"),
            cLow = Col("LwPric"), cClose = Col("ClsPric"), cLast = Col("LastPric"), cPrev = Col("PrvsClsgPric"), cVol = Col("TtlTradgVol");
        int cIsin = cols.TryGetValue("ISIN", out var ci) ? ci : -1;
        int cTurn = cols.TryGetValue("TtlTrfVal", out var ct) ? ct : -1;

        static decimal? Dec(string s) => decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var v) ? v : null;

        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var f = line.Split(',');
            if (f.Length <= cVol) continue;
            if (!DateTime.TryParse(f[cDate], CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) continue;
            decimal? close = Dec(f[cClose]);
            if (close is null or <= 0m) continue;

            yield return new NseBhavcopyRow
            {
                TradeDate = date.Date,
                Symbol = f[cSym].Trim().ToUpperInvariant(),
                Series = f[cSeries].Trim().ToUpperInvariant(),
                Isin = cIsin >= 0 ? f[cIsin].Trim() : null,
                Open = Dec(f[cOpen]) ?? close.Value,
                High = Dec(f[cHigh]) ?? close.Value,
                Low = Dec(f[cLow]) ?? close.Value,
                Close = close.Value,
                Last = Dec(f[cLast]),
                PrevClose = Dec(f[cPrev]),
                Volume = long.TryParse(f[cVol], NumberStyles.Integer, CultureInfo.InvariantCulture, out var vol) ? vol : 0,
                Turnover = cTurn >= 0 ? Dec(f[cTurn]) : null
            };
        }
    }
}
