using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Extensions.Logging;
using QuantEdge.Infrastructure.DTOs;

namespace QuantEdge.Infrastructure.Persistence.Repositories;

public class SwingSlotRecommendationRepository : ISwingSlotRecommendationRepository
{
    private readonly IDbConnectionFactory _connectionFactory;
    private readonly ILogger<SwingSlotRecommendationRepository> _logger;
    private static bool _tableEnsured = false;
    private static readonly object _tableLock = new();

    public SwingSlotRecommendationRepository(
        IDbConnectionFactory connectionFactory,
        ILogger<SwingSlotRecommendationRepository> logger)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
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
                    CREATE TABLE IF NOT EXISTS swing_slot_recommendations (
                        id SERIAL PRIMARY KEY,
                        scan_date DATE NOT NULL,
                        slot_time TIMESTAMP WITH TIME ZONE NOT NULL,
                        slot_label VARCHAR(20) NOT NULL,
                        symbol VARCHAR(50) NOT NULL,
                        decision VARCHAR(20) NOT NULL,
                        score INT NOT NULL,
                        confidence_pct NUMERIC(5, 2),
                        entry_price NUMERIC(18, 4),
                        stop_loss NUMERIC(18, 4),
                        target1 NUMERIC(18, 4),
                        target2 NUMERIC(18, 4),
                        risk_reward_ratio NUMERIC(18, 4),
                        volume_multiplier NUMERIC(18, 4),
                        rsi14 NUMERIC(18, 4),
                        adx14 NUMERIC(18, 4),
                        ema20 NUMERIC(18, 4),
                        ema50 NUMERIC(18, 4),
                        ema200 NUMERIC(18, 4),
                        passed_rules TEXT,
                        failed_rules TEXT,
                        reason TEXT,
                        timeframe_used VARCHAR(50) DEFAULT '1D + 15M + 60M',
                        checklist_json JSONB,
                        created_at TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT NOW(),
                        CONSTRAINT uq_swing_slot_rec UNIQUE (scan_date, slot_label, symbol)
                    );
                    CREATE INDEX IF NOT EXISTS ix_swing_slot_rec_date_slot ON swing_slot_recommendations (scan_date, slot_label);
                    CREATE INDEX IF NOT EXISTS ix_swing_slot_rec_symbol ON swing_slot_recommendations (symbol, scan_date);
                ");
                _tableEnsured = true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not ensure swing_slot_recommendations table creation. Assuming table exists.");
            }
        }
    }

    public async Task SaveSlotRecommendationsAsync(
        DateTime scanDate,
        DateTime slotTime,
        string slotLabel,
        IEnumerable<SwingStockSignalDto> signals,
        CancellationToken cancellationToken = default)
    {
        EnsureTableCreated();
        if (signals == null || !signals.Any()) return;

        using var connection = _connectionFactory.CreateConnection();
        if (connection.State != ConnectionState.Open)
        {
            connection.Open();
        }

        using var tx = connection.BeginTransaction();
        try
        {
            const string upsertSql = @"
                INSERT INTO swing_slot_recommendations (
                    scan_date, slot_time, slot_label, symbol, decision, score, confidence_pct,
                    entry_price, stop_loss, target1, target2, risk_reward_ratio, volume_multiplier,
                    rsi14, adx14, ema20, ema50, ema200, passed_rules, failed_rules, reason,
                    timeframe_used, checklist_json, created_at
                )
                VALUES (
                    @ScanDate, @SlotTime, @SlotLabel, @Symbol, @Decision, @Score, @ConfidencePct,
                    @EntryPrice, @StopLoss, @Target1, @Target2, @RiskRewardRatio, @VolumeMultiplier,
                    @Rsi14, @Adx14, @Ema20, @Ema50, @Ema200, @PassedRules, @FailedRules, @Reason,
                    @TimeframeUsed, @ChecklistJson::jsonb, NOW()
                )
                ON CONFLICT (scan_date, slot_label, symbol)
                DO UPDATE SET
                    slot_time = EXCLUDED.slot_time,
                    decision = EXCLUDED.decision,
                    score = EXCLUDED.score,
                    confidence_pct = EXCLUDED.confidence_pct,
                    entry_price = EXCLUDED.entry_price,
                    stop_loss = EXCLUDED.stop_loss,
                    target1 = EXCLUDED.target1,
                    target2 = EXCLUDED.target2,
                    risk_reward_ratio = EXCLUDED.risk_reward_ratio,
                    volume_multiplier = EXCLUDED.volume_multiplier,
                    rsi14 = EXCLUDED.rsi14,
                    adx14 = EXCLUDED.adx14,
                    ema20 = EXCLUDED.ema20,
                    ema50 = EXCLUDED.ema50,
                    ema200 = EXCLUDED.ema200,
                    passed_rules = EXCLUDED.passed_rules,
                    failed_rules = EXCLUDED.failed_rules,
                    reason = EXCLUDED.reason,
                    timeframe_used = EXCLUDED.timeframe_used,
                    checklist_json = EXCLUDED.checklist_json,
                    created_at = NOW();";

            foreach (var sig in signals)
            {
                if (cancellationToken.IsCancellationRequested) break;

                string checklistJson = sig.Checklist != null ? JsonSerializer.Serialize(sig.Checklist) : "{}";
                string passedRulesStr = sig.PassedRules != null && sig.PassedRules.Any() ? string.Join(" | ", sig.PassedRules) : string.Empty;
                string failedRulesStr = sig.FailedRules != null && sig.FailedRules.Any() ? string.Join(" | ", sig.FailedRules) : string.Empty;

                await connection.ExecuteAsync(upsertSql, new
                {
                    ScanDate = scanDate.Date,
                    SlotTime = slotTime,
                    SlotLabel = slotLabel,
                    Symbol = sig.Symbol,
                    Decision = sig.Decision ?? "WATCH",
                    Score = sig.Score,
                    ConfidencePct = sig.ConfidencePct,
                    EntryPrice = sig.EntryPrice > 0m ? sig.EntryPrice : sig.Close,
                    StopLoss = sig.StopLoss,
                    Target1 = sig.Target1,
                    Target2 = sig.Target2,
                    RiskRewardRatio = sig.RiskRewardRatio,
                    VolumeMultiplier = sig.VolumeMultiplier,
                    Rsi14 = sig.Rsi14,
                    Adx14 = sig.Adx14,
                    Ema20 = sig.Ema20,
                    Ema50 = sig.Ema50,
                    Ema200 = sig.Ema200,
                    PassedRules = passedRulesStr,
                    FailedRules = failedRulesStr,
                    Reason = sig.Reason,
                    TimeframeUsed = string.IsNullOrWhiteSpace(sig.TimeframeUsed) ? "1D + 15M + 60M" : sig.TimeframeUsed,
                    ChecklistJson = checklistJson
                }, tx);
            }

            tx.Commit();
            _logger.LogInformation("Saved {Count} swing recommendations for slot {SlotLabel} on {Date:yyyy-MM-dd}.", signals.Count(), slotLabel, scanDate);
        }
        catch (Exception ex)
        {
            tx.Rollback();
            _logger.LogError(ex, "Failed to save slot recommendations for {SlotLabel} on {Date:yyyy-MM-dd}.", slotLabel, scanDate);
            throw;
        }
    }

    public async Task<IReadOnlyList<SwingScanSlotDto>> GetScanSlotsAsync(DateTime scanDate, CancellationToken cancellationToken = default)
    {
        EnsureTableCreated();
        using var connection = _connectionFactory.CreateConnection();

        try
        {
            const string sql = @"
                SELECT 
                    slot_label AS SlotLabel,
                    MAX(slot_time) AS SlotTime,
                    COUNT(*) FILTER (WHERE UPPER(decision) = 'BUY') AS BuyCount,
                    COUNT(*) FILTER (WHERE UPPER(decision) = 'WATCH') AS WatchCount,
                    COUNT(*) AS TotalCount
                FROM swing_slot_recommendations
                WHERE scan_date = @ScanDate
                GROUP BY slot_label
                ORDER BY MAX(slot_time) ASC;";

            var slots = (await connection.QueryAsync<dynamic>(sql, new { ScanDate = scanDate.Date })).ToList();

            var result = new List<SwingScanSlotDto>();
            for (int i = 0; i < slots.Count; i++)
            {
                var s = slots[i];
                bool isLatest = (i == slots.Count - 1);
                result.Add(new SwingScanSlotDto(
                    SlotLabel: (string)s.slotlabel,
                    SlotTime: (DateTime)s.slottime,
                    BuyCount: Convert.ToInt32(s.buycount),
                    WatchCount: Convert.ToInt32(s.watchcount),
                    TotalCount: Convert.ToInt32(s.totalcount),
                    IsLatest: isLatest
                ));
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving scan slots for date {Date:yyyy-MM-dd}.", scanDate);
            return Array.Empty<SwingScanSlotDto>();
        }
    }

    public async Task<IReadOnlyList<SwingStockSignalDto>> GetSlotRecommendationsAsync(
        DateTime scanDate,
        string slotLabel,
        CancellationToken cancellationToken = default)
    {
        EnsureTableCreated();
        using var connection = _connectionFactory.CreateConnection();

        try
        {
            string sql;
            object parameters;

            if (string.IsNullOrWhiteSpace(slotLabel) || slotLabel.Trim().Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                sql = @"
                    SELECT * FROM swing_slot_recommendations
                    WHERE scan_date = @ScanDate
                    ORDER BY score DESC, slot_time DESC, symbol ASC;";
                parameters = new { ScanDate = scanDate.Date };
            }
            else
            {
                sql = @"
                    SELECT * FROM swing_slot_recommendations
                    WHERE scan_date = @ScanDate AND slot_label = @SlotLabel
                    ORDER BY score DESC, symbol ASC;";
                parameters = new { ScanDate = scanDate.Date, SlotLabel = slotLabel.Trim() };
            }

            var rows = (await connection.QueryAsync<dynamic>(sql, parameters)).ToList();
            var result = new List<SwingStockSignalDto>();

            var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

            foreach (var r in rows)
            {
                ConditionChecklistDto checklist = null!;
                try
                {
                    string jsonStr = Convert.ToString(r.checklist_json);
                    if (!string.IsNullOrWhiteSpace(jsonStr))
                    {
                        checklist = JsonSerializer.Deserialize<ConditionChecklistDto>(jsonStr, jsonOptions)!;
                    }
                }
                catch
                {
                    // Fallback to null checklist
                }

                if (checklist == null)
                {
                    checklist = new ConditionChecklistDto(0, 0, new List<ConditionItemDto>());
                }

                List<string> passedRules = new();
                string passedStr = Convert.ToString(r.passed_rules);
                if (!string.IsNullOrWhiteSpace(passedStr))
                {
                    passedRules = passedStr.Split(" | ", StringSplitOptions.RemoveEmptyEntries).ToList();
                }

                List<string> failedRules = new();
                string failedStr = Convert.ToString(r.failed_rules);
                if (!string.IsNullOrWhiteSpace(failedStr))
                {
                    failedRules = failedStr.Split(" | ", StringSplitOptions.RemoveEmptyEntries).ToList();
                }

                decimal entryPrice = r.entry_price != null ? Convert.ToDecimal(r.entry_price) : 0m;
                decimal stopLoss = r.stop_loss != null ? Convert.ToDecimal(r.stop_loss) : 0m;
                decimal target1 = r.target1 != null ? Convert.ToDecimal(r.target1) : 0m;
                decimal target2 = r.target2 != null ? Convert.ToDecimal(r.target2) : 0m;
                decimal rrRatio = r.risk_reward_ratio != null ? Convert.ToDecimal(r.risk_reward_ratio) : 0m;
                decimal volMult = r.volume_multiplier != null ? Convert.ToDecimal(r.volume_multiplier) : 0m;
                decimal rsi = r.rsi14 != null ? Convert.ToDecimal(r.rsi14) : 0m;
                decimal adx = r.adx14 != null ? Convert.ToDecimal(r.adx14) : 0m;
                decimal ema20 = r.ema20 != null ? Convert.ToDecimal(r.ema20) : 0m;
                decimal ema50 = r.ema50 != null ? Convert.ToDecimal(r.ema50) : 0m;
                decimal ema200 = r.ema200 != null ? Convert.ToDecimal(r.ema200) : 0m;
                int score = r.score != null ? Convert.ToInt32(r.score) : 0;
                decimal conf = r.confidence_pct != null ? Convert.ToDecimal(r.confidence_pct) : 0m;
                string decision = Convert.ToString(r.decision) ?? "WATCH";
                string symbol = Convert.ToString(r.symbol) ?? "";
                string reason = Convert.ToString(r.reason) ?? "";
                string timeframe = Convert.ToString(r.timeframe_used) ?? "1D + 15M + 60M";

                bool isBuy = decision.Equals("BUY", StringComparison.OrdinalIgnoreCase);

                result.Add(new SwingStockSignalDto(
                    Symbol: symbol,
                    Close: entryPrice,
                    Open: 0m,
                    High: 0m,
                    Low: 0m,
                    Ema20: ema20,
                    Ema50: ema50,
                    Ema200: ema200,
                    Rsi14: rsi,
                    Macd: 0m,
                    MacdSignal: 0m,
                    Adx14: adx,
                    Atr14: 0m,
                    Volume: 0L,
                    AvgVolume20: 0m,
                    VolumeMultiplier: volMult,
                    Is52WeekHigh: false,
                    High52Week: 0m,
                    ClosenessTo52WeekHighPct: 0m,
                    IsLastCandleBullish: true,
                    MeetsStockFilter: isBuy,
                    MeetsAllBuyRules: isBuy,
                    Decision: decision,
                    Reason: reason,
                    Checklist: checklist,
                    Score: score,
                    ConfidencePct: conf,
                    EntryPrice: entryPrice,
                    StopLoss: stopLoss,
                    Target1: target1,
                    Target2: target2,
                    RiskRewardRatio: rrRatio,
                    PassedRules: passedRules,
                    FailedRules: failedRules,
                    Sector: "",
                    HardFiltersPassed: true,
                    IsAlreadyOpen: false,
                    RecommendedQty: 0,
                    CalculatedRiskAmount: 0m,
                    TimeframeUsed: timeframe,
                    ExitSignalReason: ""
                ));
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving slot recommendations for date {Date:yyyy-MM-dd}, slot {Slot}.", scanDate, slotLabel);
            return Array.Empty<SwingStockSignalDto>();
        }
    }
}
