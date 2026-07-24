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
        ILogger<InstrumentSyncService> logger)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task SyncInstrumentsAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting instruments sync from Zerodha...");
        
        // 0. Query existing active symbols from stock_master to skip them during sync
        var activeSymbolsSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var conn = _connectionFactory.CreateConnection();
            try
            {
                var activeSymbols = await conn.QueryAsync<string>("SELECT symbol FROM stock_master WHERE is_active = TRUE;");
                if (activeSymbols != null)
                {
                    foreach (var sym in activeSymbols)
                    {
                        if (!string.IsNullOrWhiteSpace(sym))
                        {
                            activeSymbolsSet.Add(sym);
                        }
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
            _logger.LogWarning(ex, "Failed to query active stock symbols prior to instrument sync.");
        }
        _logger.LogInformation("Loaded {Count} currently active stock symbols from database to skip during sync.", activeSymbolsSet.Count);

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

                // Skip active stocks — active stock data should not be changed during instrument sync
                if (activeSymbolsSet.Contains(symbol))
                {
                    continue;
                }

                var instToken = int.Parse(cols[0].Trim());
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
                    upperSymbol.EndsWith("-W1"))
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
                    _logger.LogInformation("Successfully synced {Count} non-active instruments to stock_master via stored function.", instrumentsToSave.Count);
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

        var csvPath = Path.Combine(Directory.GetCurrentDirectory(), "instruments_output.csv");
        _logger.LogInformation("Writing {Count} instruments to CSV for testing at {Path}", instrumentsToSave.Count, csvPath);
        
        var csvLines = new List<string>
        {
            "Symbol,Name,InstrumentToken,ExchangeToken,InstrumentType,Segment,Exchange,IsActive"
        };

        foreach(var inst in instrumentsToSave)
        {
            // Escape any existing quotes in the name
            var safeName = inst.Name?.Replace("\"", "\"\"") ?? "";
            csvLines.Add($"{inst.Symbol},\"{safeName}\",{inst.InstrumentToken},{inst.ExchangeToken},{inst.InstrumentType},{inst.Segment},{inst.Exchange},{inst.IsActive}");
        }

        if (File.Exists(csvPath))
        {
            File.Delete(csvPath);
        }
        await File.WriteAllLinesAsync(csvPath, csvLines, cancellationToken);
        _logger.LogInformation("Successfully cleared old file and wrote new instruments to CSV.");
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
