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

namespace QuantEdge.Infrastructure.Services;

public class InstrumentSyncService : IInstrumentSyncService
{
    private readonly IDbConnectionFactory _connectionFactory;
    private readonly ILogger<InstrumentSyncService> _logger;

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
        using var connection = _connectionFactory.CreateConnection();
        if (connection.State != ConnectionState.Open)
        {
            connection.Open();
        }

        using var transaction = connection.BeginTransaction();
        try
        {
            var json = System.Text.Json.JsonSerializer.Serialize(instrumentsToSave);
            await connection.ExecuteAsync(
                "SELECT sp_upsert_instruments(@p_instruments::jsonb);",
                new { p_instruments = json },
                transaction: transaction
            );
            transaction.Commit();
            _logger.LogInformation("Successfully synced {Count} instruments to stock_master via stored function.", instrumentsToSave.Count);
        }
        catch (Exception ex)
        {
            transaction.Rollback();
            _logger.LogError(ex, "Error occurred during bulk saving of instruments to database via stored function.");
            throw;
        }
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
