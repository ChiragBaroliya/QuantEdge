using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;

namespace QuantEdge.Infrastructure.Persistence.Repositories;

/// <summary>One mismatch found by a reconciliation check (data_quality_issues).</summary>
public sealed class DataQualityIssue
{
    public long Id { get; set; }
    public DateTime CheckDate { get; set; }
    public string CheckType { get; set; } = string.Empty;
    public string? Symbol { get; set; }
    public int? UserId { get; set; }
    public decimal? Ours { get; set; }
    public decimal? External { get; set; }
    public decimal? DiffPct { get; set; }
    public string? Details { get; set; }
    public DateTime CreatedAt { get; set; }
}

public interface IDataQualityRepository
{
    Task InsertAsync(IReadOnlyCollection<DataQualityIssue> issues);
    Task<IReadOnlyList<DataQualityIssue>> GetRecentAsync(int days = 7, int limit = 500);
}

public class DataQualityRepository : IDataQualityRepository
{
    private readonly IDbConnectionFactory _connectionFactory;

    public DataQualityRepository(IDbConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
    }

    public async Task InsertAsync(IReadOnlyCollection<DataQualityIssue> issues)
    {
        if (issues == null || issues.Count == 0) return;
        using var connection = _connectionFactory.CreateConnection();
        const string sql = @"
            INSERT INTO data_quality_issues (check_date, check_type, symbol, user_id, ours, external, diff_pct, details)
            VALUES (@CheckDate, @CheckType, @Symbol, @UserId, @Ours, @External, @DiffPct, LEFT(@Details, 500));";
        await connection.ExecuteAsync(sql, issues);
    }

    public async Task<IReadOnlyList<DataQualityIssue>> GetRecentAsync(int days = 7, int limit = 500)
    {
        using var connection = _connectionFactory.CreateConnection();
        const string sql = @"
            SELECT id, check_date::timestamp AS CheckDate, check_type AS CheckType, symbol, user_id AS UserId, ours, external,
                   diff_pct AS DiffPct, details, created_at AS CreatedAt
            FROM data_quality_issues
            WHERE check_date >= (NOW() AT TIME ZONE 'Asia/Kolkata')::date - @days
            ORDER BY created_at DESC
            LIMIT @limit;";
        return (await connection.QueryAsync<DataQualityIssue>(sql, new { days, limit })).ToList();
    }
}
