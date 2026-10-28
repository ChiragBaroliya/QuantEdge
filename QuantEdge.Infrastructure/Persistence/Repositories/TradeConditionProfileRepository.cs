using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Extensions.Logging;
using QuantEdge.Domain.Entities;

namespace QuantEdge.Infrastructure.Persistence.Repositories;

/// <summary>
/// trade_condition_profiles: which of the 11 BUY / SHORT conditions each user's Auto Paper / Auto Real bot applies.
/// Table in schema.sql; functions / procedures in functions.sql (data_fixes/2026-10-28_trade_condition_profiles.sql).
/// Not cached - the Worker and API are separate processes, and the workers read it once per 15-minute scan.
/// Reads fall back to the default profile when the database objects are missing, so the scans never stop over it.
/// </summary>
public class TradeConditionProfileRepository : ITradeConditionProfileRepository
{
    private readonly IDbConnectionFactory _connectionFactory;
    private readonly ILogger<TradeConditionProfileRepository> _logger;

    public TradeConditionProfileRepository(IDbConnectionFactory connectionFactory, ILogger<TradeConditionProfileRepository> logger)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<IReadOnlyList<TradeConditionProfile>> GetModeAsync(string userRef, string tradeMode)
    {
        var rows = new List<ProfileRow>();
        try
        {
            using var conn = _connectionFactory.CreateConnection();
            rows = (await conn.QueryAsync<ProfileRow>(
                "SELECT * FROM fn_get_trade_condition_profiles(@userRef, @tradeMode);",
                new { userRef, tradeMode })).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Loading Trade Conditions for {Mode} (user {UserRef}) failed - using the default (all 11 on). Apply data_fixes/2026-10-28_trade_condition_profiles.sql.",
                tradeMode, userRef);
        }

        return TradeConditionProfile.Sides
            .Select(side => rows.FirstOrDefault(r => r.Side == side)?.ToDomain() ?? TradeConditionProfile.Default(userRef, tradeMode, side))
            .ToList();
    }

    public async Task<IReadOnlyList<TradeConditionProfile>> GetAllForModeAsync(string tradeMode)
    {
        try
        {
            using var conn = _connectionFactory.CreateConnection();
            var rows = await conn.QueryAsync<ProfileRow>(
                "SELECT * FROM fn_get_trade_condition_profiles_by_mode(@tradeMode);", new { tradeMode });
            return rows.Select(r => r.ToDomain()).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Loading Trade Conditions for {Mode} failed - every user uses the default (all 11 on). Apply data_fixes/2026-10-28_trade_condition_profiles.sql.",
                tradeMode);
            return Array.Empty<TradeConditionProfile>();
        }
    }

    public async Task SaveAsync(TradeConditionProfile profile)
    {
        using var conn = _connectionFactory.CreateConnection();
        await conn.ExecuteAsync(
            "CALL sp_upsert_trade_condition_profile(@userRef, @tradeMode, @side, @disabledConditions, @minConditionsMatch, @riskAcceptedAt);",
            new
            {
                userRef = profile.UserRef,
                tradeMode = profile.TradeMode,
                side = profile.Side,
                disabledConditions = profile.DisabledConditions.ToArray(),
                minConditionsMatch = profile.MinConditionsMatch,
                riskAcceptedAt = profile.RiskAcceptedAt
            });
    }

    public async Task DeleteModeAsync(string userRef, string tradeMode)
    {
        using var conn = _connectionFactory.CreateConnection();
        await conn.ExecuteAsync("CALL sp_delete_trade_condition_profiles(@userRef, @tradeMode);", new { userRef, tradeMode });
    }

    private class ProfileRow
    {
        public string UserRef { get; set; } = string.Empty;
        public string TradeMode { get; set; } = string.Empty;
        public string Side { get; set; } = string.Empty;
        public string[]? DisabledConditions { get; set; }
        public int? MinConditionsMatch { get; set; }
        public DateTime? RiskAcceptedAt { get; set; }
        public DateTime UpdatedAt { get; set; }

        public TradeConditionProfile ToDomain() => new()
        {
            UserRef = UserRef,
            TradeMode = TradeMode,
            Side = Side,
            DisabledConditions = (DisabledConditions ?? Array.Empty<string>()).ToList(),
            MinConditionsMatch = MinConditionsMatch,
            RiskAcceptedAt = RiskAcceptedAt,
            UpdatedAt = UpdatedAt
        };
    }
}
