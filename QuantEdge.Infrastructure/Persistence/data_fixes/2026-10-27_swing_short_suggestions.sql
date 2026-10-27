-- Swing Trading suggestions - Short Selling signals.
-- 1. Adds swing_slot_recommendations.side (LONG / SHORT): a short suggestion's WATCH has to be told apart from a long
--    one. Existing rows are all long suggestions and get the LONG default.
-- 2. Re-creates sp_get_swing_scan_slots (adds ShortCount) and sp_get_swing_slot_recommendations (adds Side) - same
--    results as functions.sql, so this works without re-applying that whole file. Safe to run more than once.
-- The app also adds the column itself on first use (SwingSlotRecommendationRepository.EnsureTableCreated).

ALTER TABLE swing_slot_recommendations ADD COLUMN IF NOT EXISTS side VARCHAR(5) NOT NULL DEFAULT 'LONG';

DROP FUNCTION IF EXISTS sp_get_swing_scan_slots(DATE);

CREATE OR REPLACE FUNCTION sp_get_swing_scan_slots(
    p_date DATE
)
RETURNS TABLE (
    SlotLabel VARCHAR,
    SlotTime TIMESTAMP WITH TIME ZONE,
    BuyCount BIGINT,
    WatchCount BIGINT,
    ShortCount BIGINT,
    TotalCount BIGINT
)
LANGUAGE plpgsql
AS $$
BEGIN
    RETURN QUERY
    SELECT
        r.slot_label::VARCHAR AS SlotLabel,
        MAX(r.slot_time) AS SlotTime,
        COUNT(*) FILTER (WHERE UPPER(r.decision) = 'BUY') AS BuyCount,
        COUNT(*) FILTER (WHERE UPPER(r.decision) = 'WATCH') AS WatchCount,
        COUNT(*) FILTER (WHERE UPPER(r.decision) = 'SHORT') AS ShortCount,
        COUNT(*) AS TotalCount
    FROM swing_slot_recommendations r
    WHERE r.scan_date = p_date
    GROUP BY r.slot_label
    ORDER BY MAX(r.slot_time) ASC;
END;
$$;

DROP FUNCTION IF EXISTS sp_get_swing_slot_recommendations(DATE, VARCHAR);

CREATE OR REPLACE FUNCTION sp_get_swing_slot_recommendations(
    p_date DATE,
    p_slot_label VARCHAR
)
RETURNS TABLE (
    Id INT,
    ScanDate DATE,
    SlotTime TIMESTAMP WITH TIME ZONE,
    SlotLabel VARCHAR,
    Symbol VARCHAR,
    Decision VARCHAR,
    Score INT,
    ConfidencePct NUMERIC,
    EntryPrice NUMERIC,
    StopLoss NUMERIC,
    Target1 NUMERIC,
    Target2 NUMERIC,
    RiskRewardRatio NUMERIC,
    VolumeMultiplier NUMERIC,
    Rsi14 NUMERIC,
    Adx14 NUMERIC,
    Ema20 NUMERIC,
    Ema50 NUMERIC,
    Ema200 NUMERIC,
    PassedRules TEXT,
    FailedRules TEXT,
    Reason TEXT,
    TimeframeUsed VARCHAR,
    ChecklistJson JSONB,
    Side VARCHAR,
    CreatedAt TIMESTAMP WITH TIME ZONE
)
LANGUAGE plpgsql
AS $$
BEGIN
    RETURN QUERY
    SELECT
        r.id AS Id,
        r.scan_date AS ScanDate,
        r.slot_time AS SlotTime,
        r.slot_label::VARCHAR AS SlotLabel,
        r.symbol::VARCHAR AS Symbol,
        r.decision::VARCHAR AS Decision,
        r.score AS Score,
        r.confidence_pct AS ConfidencePct,
        r.entry_price AS EntryPrice,
        r.stop_loss AS StopLoss,
        r.target1 AS Target1,
        r.target2 AS Target2,
        r.risk_reward_ratio AS RiskRewardRatio,
        r.volume_multiplier AS VolumeMultiplier,
        r.rsi14 AS Rsi14,
        r.adx14 AS Adx14,
        r.ema20 AS Ema20,
        r.ema50 AS Ema50,
        r.ema200 AS Ema200,
        r.passed_rules AS PassedRules,
        r.failed_rules AS FailedRules,
        r.reason AS Reason,
        r.timeframe_used::VARCHAR AS TimeframeUsed,
        r.checklist_json AS ChecklistJson,
        r.side::VARCHAR AS Side,
        r.created_at AS CreatedAt
    FROM swing_slot_recommendations r
    WHERE r.scan_date = p_date
      AND (LOWER(p_slot_label) = 'all' OR p_slot_label IS NULL OR TRIM(p_slot_label) = '' OR r.slot_label = p_slot_label)
    ORDER BY r.score DESC, r.slot_time DESC, r.symbol ASC;
END;
$$;
