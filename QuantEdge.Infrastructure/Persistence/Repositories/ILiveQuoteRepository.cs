using System.Collections.Generic;
using System.Threading.Tasks;
using QuantEdge.Infrastructure.DTOs;

namespace QuantEdge.Infrastructure.Persistence.Repositories;

/// <summary>
/// live_quotes (schema.sql): one row per symbol with the latest tick. Written by the market-data feed
/// process, read by the API so every process sees the same live price and previous close.
/// </summary>
public interface ILiveQuoteRepository
{
    Task UpsertBatchAsync(IReadOnlyCollection<LiveQuote> quotes);

    /// <summary>Latest quotes keyed by upper-case symbol; symbols without a row are absent.</summary>
    Task<Dictionary<string, LiveQuote>> GetAsync(IReadOnlyCollection<string> symbols);
}
