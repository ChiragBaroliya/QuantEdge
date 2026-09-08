using System;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Extensions.Logging;
using QuantEdge.Domain.Entities;
using QuantEdge.Infrastructure.Interfaces;

namespace QuantEdge.Infrastructure.Persistence.Repositories;

public class SwingStrategySettingsRepository : ISwingStrategySettingsRepository
{
    private const string CacheKey = "swing_strategy_settings";

    private readonly IDbConnectionFactory _connectionFactory;
    private readonly ICacheService? _cacheService;
    private readonly ILogger<SwingStrategySettingsRepository> _logger;

    private static bool _tableEnsured = false;
    private static readonly object _tableLock = new();

    public SwingStrategySettingsRepository(
        IDbConnectionFactory connectionFactory,
        ILogger<SwingStrategySettingsRepository> logger,
        ICacheService? cacheService = null)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _cacheService = cacheService;
    }

    private void EnsureTableCreated()
    {
        if (_tableEnsured) return;
        lock (_tableLock)
        {
            if (_tableEnsured) return;
            try
            {
                using var connection = _connectionFactory.CreateConnection();
                connection.Execute(@"
                    CREATE TABLE IF NOT EXISTS swing_strategy_settings (
                        id INT PRIMARY KEY DEFAULT 1,
                        buy_score_threshold INT NOT NULL DEFAULT 70,
                        watch_score_threshold INT NOT NULL DEFAULT 50,
                        market_context_score_penalty INT NOT NULL DEFAULT 10,
                        market_context_position_size_factor NUMERIC(5, 2) NOT NULL DEFAULT 0.5,
                        market_protection_buffer_pct NUMERIC(6, 4) NOT NULL DEFAULT 0.005,
                        updated_at TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT NOW(),
                        CONSTRAINT chk_swing_strategy_settings_singleton CHECK (id = 1)
                    );
                    ALTER TABLE swing_strategy_settings ADD COLUMN IF NOT EXISTS market_protection_buffer_pct NUMERIC(6, 4) NOT NULL DEFAULT 0.005;
                    INSERT INTO swing_strategy_settings (id) VALUES (1) ON CONFLICT (id) DO NOTHING;
                ");
                _tableEnsured = true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to ensure swing_strategy_settings table exists.");
            }
        }
    }

    public async Task<SwingStrategySettings> GetSettingsAsync()
    {
        if (_cacheService != null)
        {
            var cached = await _cacheService.GetAsync<SwingStrategySettings>(CacheKey);
            if (cached != null) return cached;
        }

        EnsureTableCreated();

        using var conn = _connectionFactory.CreateConnection();
        var row = await conn.QueryFirstOrDefaultAsync<SwingStrategySettingsRow>(
            "SELECT * FROM swing_strategy_settings WHERE id = 1;");

        var settings = row?.ToDomain() ?? SwingStrategySettings.Default;

        if (_cacheService != null)
        {
            await _cacheService.SetAsync(CacheKey, settings, TimeSpan.FromMinutes(5));
        }

        return settings;
    }

    public async Task<SwingStrategySettings> UpdateSettingsAsync(SwingStrategySettings settings)
    {
        EnsureTableCreated();

        using var conn = _connectionFactory.CreateConnection();
        await conn.ExecuteAsync(@"
            INSERT INTO swing_strategy_settings
                (id, buy_score_threshold, watch_score_threshold, market_context_score_penalty, market_context_position_size_factor, market_protection_buffer_pct, updated_at)
            VALUES
                (1, @BuyScoreThreshold, @WatchScoreThreshold, @MarketContextScorePenalty, @MarketContextPositionSizeFactor, @MarketProtectionBufferPct, NOW())
            ON CONFLICT (id) DO UPDATE SET
                buy_score_threshold = EXCLUDED.buy_score_threshold,
                watch_score_threshold = EXCLUDED.watch_score_threshold,
                market_context_score_penalty = EXCLUDED.market_context_score_penalty,
                market_context_position_size_factor = EXCLUDED.market_context_position_size_factor,
                market_protection_buffer_pct = EXCLUDED.market_protection_buffer_pct,
                updated_at = NOW();",
            settings);

        if (_cacheService != null)
        {
            await _cacheService.RemoveAsync(CacheKey);
        }

        return await GetSettingsAsync();
    }

    private class SwingStrategySettingsRow
    {
        public int Id { get; set; }
        public int BuyScoreThreshold { get; set; }
        public int WatchScoreThreshold { get; set; }
        public int MarketContextScorePenalty { get; set; }
        public decimal MarketContextPositionSizeFactor { get; set; }
        public decimal MarketProtectionBufferPct { get; set; }
        public DateTime UpdatedAt { get; set; }

        public SwingStrategySettings ToDomain() => new()
        {
            Id = Id,
            BuyScoreThreshold = BuyScoreThreshold,
            WatchScoreThreshold = WatchScoreThreshold,
            MarketContextScorePenalty = MarketContextScorePenalty,
            MarketContextPositionSizeFactor = MarketContextPositionSizeFactor,
            MarketProtectionBufferPct = MarketProtectionBufferPct,
            UpdatedAt = UpdatedAt
        };
    }
}
