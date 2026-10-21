using System;
using System.Data;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Extensions.Logging;
using QuantEdge.Infrastructure.Interfaces;
using QuantEdge.Infrastructure.Persistence;

namespace QuantEdge.Infrastructure.Services;

/// <summary>
/// IRealTradeLockService backed by a Postgres session-level advisory lock (pg_try_advisory_lock).
/// Session-level, not transaction-level, because the lock has to span the Zerodha HTTP call. The
/// lock lives on a dedicated connection held for the handle's lifetime; if the process dies the
/// connection drops and Postgres releases the lock by itself.
/// </summary>
public class PostgresRealTradeLockService : IRealTradeLockService
{
    private readonly IDbConnectionFactory _connectionFactory;
    private readonly ILogger<PostgresRealTradeLockService> _logger;

    public PostgresRealTradeLockService(IDbConnectionFactory connectionFactory, ILogger<PostgresRealTradeLockService> logger)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<IAsyncDisposable?> TryAcquireAsync(string key, CancellationToken cancellationToken = default)
    {
        var connection = _connectionFactory.CreateConnection();
        try
        {
            if (connection is DbConnection dbConnection)
                await dbConnection.OpenAsync(cancellationToken);
            else
                connection.Open();

            bool acquired = await connection.ExecuteScalarAsync<bool>(
                "SELECT pg_try_advisory_lock(hashtext(@key));", new { key });
            if (!acquired)
            {
                connection.Dispose();
                return null;
            }

            return new Handle(connection, key, _logger);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private sealed class Handle : IAsyncDisposable
    {
        private readonly IDbConnection _connection;
        private readonly string _key;
        private readonly ILogger _logger;
        private int _disposed;

        public Handle(IDbConnection connection, string key, ILogger logger)
        {
            _connection = connection;
            _key = key;
            _logger = logger;
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try
            {
                await _connection.ExecuteAsync("SELECT pg_advisory_unlock(hashtext(@key));", new { key = _key });
            }
            catch (Exception ex)
            {
                // Closing the connection below still ends the session, which releases the lock.
                _logger.LogWarning(ex, "Could not explicitly release real-trade lock {Key}; it is released when its connection closes.", _key);
            }
            finally
            {
                _connection.Dispose();
            }
        }
    }
}
