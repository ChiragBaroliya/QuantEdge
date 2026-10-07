using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;

namespace QuantEdge.Infrastructure.Persistence.Repositories;

/// <summary>One failed / rate-limited / skipped Zerodha call (broker_api_events).</summary>
public sealed class BrokerApiEvent
{
    public long Id { get; set; }
    public DateTime OccurredAt { get; set; } = DateTime.UtcNow;
    public string Source { get; set; } = "REST";
    public string Operation { get; set; } = string.Empty;
    public string Level { get; set; } = "error";
    public int? HttpStatus { get; set; }
    public string? Symbol { get; set; }
    public int? UserId { get; set; }
    public string? Message { get; set; }
    public int RepeatCount { get; set; } = 1;
    public string? ProcessName { get; set; }
}

public interface IBrokerApiEventRepository
{
    Task InsertAsync(BrokerApiEvent evt);
    Task<IReadOnlyList<BrokerApiEvent>> GetSinceAsync(DateTime sinceUtc, int limit = 200);
}

public class BrokerApiEventRepository : IBrokerApiEventRepository
{
    private readonly IDbConnectionFactory _connectionFactory;

    public BrokerApiEventRepository(IDbConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
    }

    public async Task InsertAsync(BrokerApiEvent evt)
    {
        using var connection = _connectionFactory.CreateConnection();
        const string sql = @"
            INSERT INTO broker_api_events (occurred_at, source, operation, level, http_status, symbol, user_id, message, repeat_count, process_name)
            VALUES (@OccurredAt, @Source, LEFT(@Operation, 120), @Level, @HttpStatus, LEFT(@Symbol, 50), @UserId, LEFT(@Message, 500), @RepeatCount, LEFT(@ProcessName, 60));";
        await connection.ExecuteAsync(sql, evt);
    }

    public async Task<IReadOnlyList<BrokerApiEvent>> GetSinceAsync(DateTime sinceUtc, int limit = 200)
    {
        using var connection = _connectionFactory.CreateConnection();
        const string sql = @"
            SELECT id, occurred_at AS OccurredAt, source, operation, level, http_status AS HttpStatus, symbol, user_id AS UserId,
                   message, repeat_count AS RepeatCount, process_name AS ProcessName
            FROM broker_api_events
            WHERE occurred_at >= @sinceUtc
            ORDER BY occurred_at DESC
            LIMIT @limit;";
        return (await connection.QueryAsync<BrokerApiEvent>(sql, new { sinceUtc, limit })).ToList();
    }
}
