using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using QuantEdge.Infrastructure.Interfaces;

namespace QuantEdge.Worker.Workers;

/// <summary>
/// Single-instance lease for the real-money workers. Two Worker processes running the same real-money loop
/// (a restart overlap, or a debug run next to the service) would place every real order twice, so only the
/// process holding the lease runs it. Others wait and retry every minute - automatic fail-over if the holder dies.
/// The lease is a Postgres advisory lock (IRealTradeLockService), released when it is disposed or the process exits.
/// </summary>
internal static class RealMoneyWorkerLease
{
    private static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(1);

    public static async Task<IAsyncDisposable> AcquireAsync(IServiceProvider serviceProvider, string key, string workerName,
        ILogger logger, CancellationToken stoppingToken)
    {
        var lockService = serviceProvider.GetService<IRealTradeLockService>();
        if (lockService == null) return NoLease.Instance;

        bool warned = false;
        while (true)
        {
            try
            {
                var lease = await lockService.TryAcquireAsync(key, stoppingToken);
                if (lease != null)
                {
                    if (warned)
                    {
                        logger.LogWarning("{Worker}: the other instance released the real-money lease - this instance takes over now.", workerName);
                    }
                    return lease;
                }

                if (!warned)
                {
                    logger.LogError("{Worker}: another QuantEdge worker is already running this real-money job - this instance stays idle " +
                                    "(no real orders) and re-checks every {Minutes} min. Stop the duplicate process.", workerName, RetryInterval.TotalMinutes);
                    warned = true;
                }
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "{Worker}: could not take the real-money single-instance lease; retrying in {Minutes} min.", workerName, RetryInterval.TotalMinutes);
            }

            await Task.Delay(RetryInterval, stoppingToken);
        }
    }

    private sealed class NoLease : IAsyncDisposable
    {
        public static readonly NoLease Instance = new();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
