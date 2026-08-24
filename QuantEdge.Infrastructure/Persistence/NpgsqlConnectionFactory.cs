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

        var builder = new NpgsqlConnectionStringBuilder(_config.ConnectionString);
        if (builder.Pooling)
        {
            builder.MinPoolSize = builder.MinPoolSize > 0 ? builder.MinPoolSize : 0;
            builder.MaxPoolSize = builder.MaxPoolSize > 0 ? builder.MaxPoolSize : 30;
            builder.ConnectionIdleLifetime = builder.ConnectionIdleLifetime >= 10 ? builder.ConnectionIdleLifetime : 300;
            builder.ConnectionPruningInterval = 10;
        }

        return new NpgsqlConnection(builder.ConnectionString);
    }
}

