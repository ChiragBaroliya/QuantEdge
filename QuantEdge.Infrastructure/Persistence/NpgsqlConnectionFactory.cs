using System;
using System.Data;
using Microsoft.Extensions.Options;
using Npgsql;
using QuantEdge.Infrastructure.Configurations;

namespace QuantEdge.Infrastructure.Persistence;

/// <summary>
/// PostgreSQL connection factory implementing IDbConnectionFactory.
/// </summary>
public class NpgsqlConnectionFactory : IDbConnectionFactory
{
    private readonly BrokerConfig _config;

    public NpgsqlConnectionFactory(IOptions<BrokerConfig> config)
    {
        _config = config?.Value ?? throw new ArgumentNullException(nameof(config));
    }

    /// <summary>
    /// Creates and returns a new NpgsqlConnection instance configured with connection pooling boundaries.
    /// </summary>
    public IDbConnection CreateConnection()
    {
        if (string.IsNullOrWhiteSpace(_config.ConnectionString))
        {
            throw new InvalidOperationException("PostgreSQL ConnectionString is not configured.");
        }

        var builder = new NpgsqlConnectionStringBuilder(_config.ConnectionString)
        {
            Pooling = true,
            MinPoolSize = 0,
            MaxPoolSize = 30,
            ConnectionIdleLifetime = 15,
            Timeout = 15,
            CommandTimeout = 30
        };

        return new NpgsqlConnection(builder.ConnectionString);
    }
}

