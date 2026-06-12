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
    /// Creates and returns a new NpgsqlConnection instance.
    /// </summary>
    public IDbConnection CreateConnection()
    {
        if (string.IsNullOrWhiteSpace(_config.ConnectionString))
        {
            throw new InvalidOperationException("PostgreSQL ConnectionString is not configured.");
        }
        return new NpgsqlConnection(_config.ConnectionString);
    }
}
