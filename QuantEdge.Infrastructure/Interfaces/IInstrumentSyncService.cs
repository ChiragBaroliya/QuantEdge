using System.Threading;
using System.Threading.Tasks;

namespace QuantEdge.Infrastructure.Interfaces;

/// <summary>
/// Service interface for fetching instruments list from Zerodha and syncing them to the database.
/// </summary>
public interface IInstrumentSyncService
{
    /// <summary>
    /// Fetches all NSE instruments from Zerodha and upserts them into stock_master.
    /// </summary>
    Task SyncInstrumentsAsync(CancellationToken cancellationToken);
}
