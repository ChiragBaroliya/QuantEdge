using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Extensions.Logging;
using QuantEdge.Domain.Entities;
using QuantEdge.Infrastructure.Interfaces;
using QuantEdge.Infrastructure.Persistence;
using System.Text.RegularExpressions;

namespace QuantEdge.Infrastructure.Services;

public class InstrumentSyncService : IInstrumentSyncService
{
    private readonly IDbConnectionFactory _connectionFactory;
    private readonly ILogger<InstrumentSyncService> _logger;
    private readonly IBrokerApiEventRecorder? _apiEventRecorder;

    // A real NSE instruments file has thousands of rows. Fewer than this means a partial / broken download, and then
    // existing instruments are neither updated nor reported as missing (that would flag every stock).
    private const int MinNseRowsForReconcile = 1000;

    private static readonly string[] ExcludedNameKeywords = 
    {
        "TREASURY", "GOVERNMENT", "GOVT", "STATE DEVELOPMENT LOAN", "SOVEREIGN", 
        "DEBT", "BOND", "SECURITY", "SDL", "SGB", "NCD", "TBILL", "T-BILL", 
        "GSEC", "GOI"
    };

    private static readonly HashSet<string> ActiveSymbols = new(StringComparer.OrdinalIgnoreCase)
    {
        "NIFTYBEES", "INFY", "TCS", "HDFCBANK", "RELIANCE", "NIFTY 50"
    };


    public InstrumentSyncService(
        IDbConnectionFactory connectionFactory,
        ILogger<InstrumentSyncService> logger,
        IBrokerApiEventRecorder? apiEventRecorder = null)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _apiEventRecorder = apiEventRecorder;
    }

    public async Task SyncInstrumentsAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting instruments sync from Zerodha...");

        // 0. Query all existing symbols from stock_master: new instruments skip them, and their tokens are re-checked
        // against Zerodha's current list after the download (see ReconcileExistingAsync).
        var existingSymbolsSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var existingInstruments = new List<ExistingInstrument>();
        try
        {
            using var conn = _connectionFactory.CreateConnection();
            try
            {
                var existing = await conn.QueryAsync<ExistingInstrument>(
                    "SELECT symbol AS Symbol, instrument_token AS InstrumentToken, is_active AS IsActive FROM stock_master;");
                foreach (var row in existing)
                {
                    if (!string.IsNullOrWhiteSpace(row.Symbol))
                    {
                        existingSymbolsSet.Add(row.Symbol);
                        existingInstruments.Add(row);
                    }
                }
            }
            finally
            {
                if (conn.State == ConnectionState.Open)
                {
                    conn.Close();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to query existing stock symbols prior to instrument sync.");
        }
        _logger.LogInformation("Loaded {Count} existing stock symbols from database to skip during sync.", existingSymbolsSet.Count);

        using var httpClient = new HttpClient();
        httpClient.Timeout = TimeSpan.FromMinutes(2);
        
        var response = await httpClient.GetAsync("https://api.kite.trade/instruments", cancellationToken);
        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        string? header = await reader.ReadLineAsync(cancellationToken);
        if (string.IsNullOrEmpty(header))
        {
            throw new InvalidOperationException("Zerodha instruments CSV is empty.");
        }

        var rawInstruments = new List<StockMaster>();
        // Every NSE instrument in today's file (symbol -> token), before any filtering - used to re-check existing rows.
        var zerodhaNseTokens = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        string? line;
        int lineNumber = 1;

        while ((line = await reader.ReadLineAsync(cancellationToken)) != null)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line)) continue;

            var cols = SplitCsvLine(line);
            if (cols.Length < 12)
            {
                continue;
            }

            try
            {
                var exchange = cols[11].Trim();
                // Filter only NSE instruments
                if (!exchange.Equals("NSE", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var symbol = cols[2].Trim();
                if (string.IsNullOrEmpty(symbol)) continue;

                var instToken = int.Parse(cols[0].Trim());
                zerodhaNseTokens[symbol] = instToken;

                // Existing stocks (active and inactive) are not re-inserted; their token is re-checked after the loop.
                if (existingSymbolsSet.Contains(symbol))
                {
                    continue;
                }
                var exchangeToken = cols[1].Trim();
                var name = cols[3].Trim().Replace("\"", "");
                
                decimal lastPrice = 0;
                if (!string.IsNullOrWhiteSpace(cols[4]))
                {
                    decimal.TryParse(cols[4].Trim(), out lastPrice);
                }

                DateTime? expiry = null;
                if (!string.IsNullOrWhiteSpace(cols[5]))
                {
                    if (DateTime.TryParse(cols[5].Trim(), out var expDate))
                    {
                        expiry = DateTime.SpecifyKind(expDate, DateTimeKind.Utc);
                    }
                }

                decimal strike = 0;
                if (!string.IsNullOrWhiteSpace(cols[6]))
                {
                    decimal.TryParse(cols[6].Trim(), out strike);
                }

                decimal tickSize = 0;
                if (!string.IsNullOrWhiteSpace(cols[7]))
                {
                    decimal.TryParse(cols[7].Trim(), out tickSize);
                }

                int lotSize = 1;
                if (!string.IsNullOrWhiteSpace(cols[8]))
                {
                    int.TryParse(cols[8].Trim(), out lotSize);
                }

                var instType = cols[9].Trim();
                var segment = cols[10].Trim();

                // 1. Inclusion criteria
                // 1. Inclusion criteria
                bool isNSEEquity = exchange.Equals("NSE", StringComparison.OrdinalIgnoreCase) && 
                                   segment.Equals("NSE", StringComparison.OrdinalIgnoreCase) && 
                                   instType.Equals("EQ", StringComparison.OrdinalIgnoreCase);

                bool isNSEETF = exchange.Equals("NSE", StringComparison.OrdinalIgnoreCase) && 
                                segment.Equals("NSE", StringComparison.OrdinalIgnoreCase) && 
                                instType.Equals("ETF", StringComparison.OrdinalIgnoreCase);

                bool isNSEIndex = segment.Equals("INDICES", StringComparison.OrdinalIgnoreCase);

                if (!isNSEEquity && !isNSEETF && !isNSEIndex)
                {
                    continue;
                }

                // 2. Exclusion criteria

                // Reject if name is empty
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                // Reject symbols that start with a digit
                if (symbol.Length > 0 && char.IsDigit(symbol[0]))
                {
                    continue;
                }

                // Reject symbols ending with -N0 to -N9
                if (Regex.IsMatch(symbol, @"-N\d$"))
                {
                    continue;
                }

                // Reject government securities based on symbol
                if (Regex.IsMatch(symbol, @"-(SG|GS|GB)$", RegexOptions.IgnoreCase))
                {
                    continue;
                }

                // Keep existing exclusions for SME, Warrants, Rights, Suspended
                var upperSymbol = symbol.ToUpperInvariant();
                if (upperSymbol.EndsWith("-SM") ||
                    upperSymbol.EndsWith("-ST") ||
                    upperSymbol.EndsWith("-RE") ||
                    upperSymbol.EndsWith("-RT") ||
                    upperSymbol.EndsWith("-W") ||
                    upperSymbol.EndsWith("-W1") ||
                    upperSymbol.EndsWith("-BE") ||
                    upperSymbol.EndsWith("NAV"))
                {
                    continue;
                }

                var upperName = name.ToUpperInvariant();
                if (upperName.Contains("SUSPENDED") || upperName.Contains("WARRANT"))
                {
                    continue;
                }

                // Reject names matching coupon rates (e.g. 7.18%, 6.95%)
                if (Regex.IsMatch(name, @"\d+(\.\d+)?%"))
                {
                    continue;
                }

                // Reject names containing excluded keywords
                bool hasExcludedKeyword = false;
                foreach (var keyword in ExcludedNameKeywords)
                {
                    if (upperName.Contains(keyword))
                    {
                        hasExcludedKeyword = true;
                        break;
                    }
                }

                if (hasExcludedKeyword)
                {
                    continue;
                }


                bool isActive = ActiveSymbols.Contains(symbol);

                rawInstruments.Add(new StockMaster
                {
                    Symbol = symbol,
                    InstrumentToken = instToken,
                    IsActive = isActive,
                    ExchangeToken = exchangeToken,
                    Name = name,
                    LastPrice = lastPrice,
                    Expiry = expiry,
                    Strike = strike,
                    TickSize = tickSize,
                    LotSize = lotSize,
                    InstrumentType = instType,
                    Segment = segment,
                    Exchange = exchange
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to parse instrument CSV line {LineNumber}: {Line}", lineNumber, line);
            }
        }

        _logger.LogInformation("Parsed {Count} NSE instruments. De-duplicating by symbol...", rawInstruments.Count);

        // De-duplicate by symbol
        var deduplicated = new Dictionary<string, StockMaster>(StringComparer.OrdinalIgnoreCase);
        foreach (var inst in rawInstruments)
        {
            deduplicated[inst.Symbol] = inst;
        }

        var instrumentsToSave = deduplicated.Values.ToList();
        _logger.LogInformation("Saving {Count} de-duplicated instruments to database...", instrumentsToSave.Count);

        // Perform batch upsert in a single transaction for performance
        if (instrumentsToSave.Count > 0)
        {
            using var connection = _connectionFactory.CreateConnection();
            if (connection.State != ConnectionState.Open)
            {
                connection.Open();
            }

            try
            {
                using var transaction = connection.BeginTransaction();
                try
                {
                    var json = System.Text.Json.JsonSerializer.Serialize(instrumentsToSave);
                    await connection.ExecuteScalarAsync(
                        "SELECT public.sp_upsert_instruments(@p_instruments::jsonb);",
                        new { p_instruments = json },
                        transaction);
                    transaction.Commit();
                    _logger.LogInformation("Successfully inserted {Count} missing instruments into stock_master via stored function.", instrumentsToSave.Count);
                }
                catch (Exception ex)
                {
                    transaction.Rollback();
                    _logger.LogError(ex, "Error occurred during bulk saving of instruments to database via stored function.");
                    throw;
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

        await ReconcileExistingAsync(zerodhaNseTokens, existingInstruments);

        //var csvPath = Path.Combine(Directory.GetCurrentDirectory(), "instruments_output.csv");
        //_logger.LogInformation("Writing {Count} instruments to CSV for testing at {Path}", instrumentsToSave.Count, csvPath);

        //var csvLines = new List<string>
        //{
        //    "Symbol,Name,InstrumentToken,ExchangeToken,InstrumentType,Segment,Exchange,IsActive"
        //};

        //foreach (var inst in instrumentsToSave)
        //{
        //    // Escape any existing quotes in the name
        //    var safeName = inst.Name?.Replace("\"", "\"\"") ?? "";
        //    csvLines.Add($"{inst.Symbol},\"{safeName}\",{inst.InstrumentToken},{inst.ExchangeToken},{inst.InstrumentType},{inst.Segment},{inst.Exchange},{inst.IsActive}");
        //}

        //if (File.Exists(csvPath))
        //{
        //    File.Delete(csvPath);
        //}
        //await File.WriteAllLinesAsync(csvPath, csvLines, cancellationToken);
        _logger.LogInformation("Successfully cleared old file and wrote new instruments to CSV.");
    }

    // ------------------------------------------------------------------------------------------
    // Existing instruments: keep their Zerodha token current
    // ------------------------------------------------------------------------------------------

    public sealed class ExistingInstrument
    {
        public string Symbol { get; set; } = string.Empty;
        public int InstrumentToken { get; set; }
        public bool IsActive { get; set; }
    }

    public sealed record TokenChange(string Symbol, int OldToken, int NewToken);

    /// <summary>An active stock Zerodha no longer lists under its symbol, with any same-name listings (e.g. HFCL-BE).</summary>
    public sealed record MissingInstrument(string Symbol, IReadOnlyList<string> ListedAs);

    public sealed record InstrumentReconciliation(IReadOnlyList<TokenChange> TokenChanges, IReadOnlyList<MissingInstrument> MissingActive);

    /// <summary>
    /// Compares stock_master with today's Zerodha NSE list. A symbol still listed with a different token gets the new
    /// token (Zerodha re-issues tokens, e.g. after a series move; candles are keyed by symbol, so history is unaffected).
    /// An ACTIVE symbol no longer listed is reported - never deactivated automatically - with any "SYMBOL-xx" listings.
    /// Indices and ETFs ride along: they are in the same file.
    /// </summary>
    public static InstrumentReconciliation ReconcileExisting(IReadOnlyDictionary<string, int> zerodhaNseTokens, IEnumerable<ExistingInstrument> existing)
    {
        var changes = new List<TokenChange>();
        var missing = new List<MissingInstrument>();

        foreach (var row in existing)
        {
            if (zerodhaNseTokens.TryGetValue(row.Symbol, out int token))
            {
                if (token != row.InstrumentToken) changes.Add(new TokenChange(row.Symbol, row.InstrumentToken, token));
            }
            else if (row.IsActive)
            {
                string baseSymbol = row.Symbol.Split('-')[0];
                var listedAs = zerodhaNseTokens.Keys
                    .Where(k => !k.Equals(row.Symbol, StringComparison.OrdinalIgnoreCase)
                        && (k.Equals(baseSymbol, StringComparison.OrdinalIgnoreCase) || k.StartsWith(baseSymbol + "-", StringComparison.OrdinalIgnoreCase)))
                    .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                missing.Add(new MissingInstrument(row.Symbol, listedAs));
            }
        }

        return new InstrumentReconciliation(changes, missing);
    }

    private async Task ReconcileExistingAsync(Dictionary<string, int> zerodhaNseTokens, List<ExistingInstrument> existing)
    {
        if (existing.Count == 0) return;
        if (zerodhaNseTokens.Count < MinNseRowsForReconcile)
        {
            _logger.LogWarning("Zerodha instruments file has only {Count} NSE rows - looks incomplete, existing instrument tokens left unchanged.", zerodhaNseTokens.Count);
            return;
        }

        var result = ReconcileExisting(zerodhaNseTokens, existing);

        if (result.TokenChanges.Count > 0)
        {
            try
            {
                using var connection = _connectionFactory.CreateConnection();
                await connection.ExecuteAsync(@"
                    UPDATE stock_master m
                    SET instrument_token = u.token
                    FROM UNNEST(@Symbols::VARCHAR[], @Tokens::INT[]) AS u(symbol, token)
                    WHERE m.symbol = u.symbol;",
                    new
                    {
                        Symbols = result.TokenChanges.Select(c => c.Symbol).ToArray(),
                        Tokens = result.TokenChanges.Select(c => c.NewToken).ToArray()
                    });

                foreach (var c in result.TokenChanges)
                {
                    _logger.LogWarning("Instrument token changed for {Symbol}: {Old} -> {New} (updated in stock_master).", c.Symbol, c.OldToken, c.NewToken);
                }
                _apiEventRecorder?.RecordFailure(BrokerApiSource.Job, "instrument sync",
                    $"Zerodha instrument token updated for {result.TokenChanges.Count} stock(s): {Summarise(result.TokenChanges.Select(c => c.Symbol))}. " +
                    "Their candle sync uses the new token from now on - backfill any gap from the Data Coverage page.",
                    level: "info");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update {Count} changed instrument tokens in stock_master.", result.TokenChanges.Count);
            }
        }

        if (result.MissingActive.Count > 0)
        {
            foreach (var m in result.MissingActive)
            {
                _logger.LogWarning("Active stock {Symbol} is not in Zerodha's NSE instrument list{ListedAs} - no new candles will arrive for it.",
                    m.Symbol, m.ListedAs.Count > 0 ? $" (now listed as {string.Join(", ", m.ListedAs)})" : string.Empty);
            }
            _apiEventRecorder?.RecordFailure(BrokerApiSource.Job, "instrument sync",
                $"{result.MissingActive.Count} active stock(s) are no longer listed by Zerodha (suspended, delisted or moved series) and get no new candles: " +
                Summarise(result.MissingActive.Select(m => m.ListedAs.Count > 0 ? $"{m.Symbol} → {string.Join("/", m.ListedAs)}" : m.Symbol)) +
                ". Review them on the Data Coverage page and deactivate or replace them.",
                level: "warning");
        }

        _logger.LogInformation("Instrument reconcile: {Changed} token(s) updated, {Missing} active stock(s) no longer listed by Zerodha.",
            result.TokenChanges.Count, result.MissingActive.Count);
    }

    private static string Summarise(IEnumerable<string> items, int max = 15)
    {
        var list = items.ToList();
        return list.Count <= max ? string.Join(", ", list) : $"{string.Join(", ", list.Take(max))} +{list.Count - max} more";
    }

    private static string[] SplitCsvLine(string line)
    {
        var list = new List<string>();
        bool inQuotes = false;
        var currentStr = new System.Text.StringBuilder();
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (c == ',' && !inQuotes)
            {
                list.Add(currentStr.ToString());
                currentStr.Clear();
            }
            else
            {
                currentStr.Append(c);
            }
        }
        list.Add(currentStr.ToString());
        return list.ToArray();
    }
}
