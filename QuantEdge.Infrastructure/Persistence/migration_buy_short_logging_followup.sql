-- Migration: BUY early profit protection + SHORT regime restriction + rule-level logging follow-up
-- Safe to re-run: all columns use ADD COLUMN IF NOT EXISTS, all functions use DROP FUNCTION IF EXISTS /
-- CREATE OR REPLACE. This is a targeted extract of the new statements from schema.sql / functions.sql -
-- running the full files instead is equally safe.

-- ============================================================================
-- 1. New settings column: TrailActivationPct (0 = disabled, same behavior as before)
-- ============================================================================
ALTER TABLE auto_trade_settings ADD COLUMN IF NOT EXISTS trail_activation_pct NUMERIC(5, 2) NOT NULL DEFAULT 0;
ALTER TABLE real_trade_settings ADD COLUMN IF NOT EXISTS trail_activation_pct NUMERIC(5, 2) NOT NULL DEFAULT 0;

-- ============================================================================
-- 2. New backtest_trades columns: rule-level logging
-- ============================================================================
ALTER TABLE backtest_trades ADD COLUMN IF NOT EXISTS sub_type VARCHAR(40);
ALTER TABLE backtest_trades ADD COLUMN IF NOT EXISTS rsi_15m NUMERIC(6, 2);
ALTER TABLE backtest_trades ADD COLUMN IF NOT EXISTS rsi_60m NUMERIC(6, 2);
ALTER TABLE backtest_trades ADD COLUMN IF NOT EXISTS volume_multiple NUMERIC(8, 2);
ALTER TABLE backtest_trades ADD COLUMN IF NOT EXISTS adx_1d NUMERIC(6, 2);
ALTER TABLE backtest_trades ADD COLUMN IF NOT EXISTS signal_daily_atr NUMERIC(18, 4);

-- ============================================================================
-- 3. fn_upsert_auto_trade_settings / fn_get_auto_trade_settings / fn_get_active_auto_trade_settings
--    - add p_trail_activation_pct param + TrailActivationPct output column
-- ============================================================================
DROP FUNCTION IF EXISTS fn_upsert_auto_trade_settings CASCADE;

CREATE OR REPLACE FUNCTION fn_upsert_auto_trade_settings(
    p_user_id VARCHAR,
    p_is_auto_trade_enabled BOOLEAN,
    p_available_capital NUMERIC,
    p_profit_target_pct NUMERIC,
    p_stop_loss_pct NUMERIC,
    p_max_duration_days INT,
    p_max_trades_per_day INT,
    p_fixed_amount_per_trade NUMERIC,
    p_min_conditions_match INT,
    p_trading_window_start VARCHAR,
    p_trading_window_end VARCHAR,
    p_trailing_sl_pct NUMERIC DEFAULT NULL,
    p_entry_delay_minutes INT DEFAULT 15,
    p_max_daily_loss_limit NUMERIC DEFAULT NULL,
    p_exit_mode VARCHAR DEFAULT 'SWING_CLOSE',
    p_close_check_time VARCHAR DEFAULT '15:15',
    p_stop_loss_atr_mult NUMERIC DEFAULT 1.50,
    p_trail_atr_mult NUMERIC DEFAULT 3.00,
    p_target_atr_mult NUMERIC DEFAULT 3.00,
    p_is_auto_short_enabled BOOLEAN DEFAULT FALSE,
    p_short_entry_cutoff VARCHAR DEFAULT '15:00',
    p_short_square_off_time VARCHAR DEFAULT '15:15',
    p_short_stop_loss_atr_mult NUMERIC DEFAULT 0.60,
    p_short_trail_atr_mult NUMERIC DEFAULT 1.20,
    p_short_target_atr_mult NUMERIC DEFAULT 1.20,
    p_trail_activation_pct NUMERIC DEFAULT 0
)
RETURNS TABLE (
    Id INT,
    UserId VARCHAR,
    IsAutoTradeEnabled BOOLEAN,
    AvailableCapital NUMERIC,
    ProfitTargetPct NUMERIC,
    StopLossPct NUMERIC,
    TrailingSlPct NUMERIC,
    MaxDurationDays INT,
    MaxTradesPerDay INT,
    FixedAmountPerTrade NUMERIC,
    MinConditionsMatch INT,
    TradingWindowStart VARCHAR,
    TradingWindowEnd VARCHAR,
    EntryDelayMinutes INT,
    MaxDailyLossLimit NUMERIC,
    ExitMode VARCHAR,
    CloseCheckTime VARCHAR,
    StopLossAtrMult NUMERIC,
    TrailAtrMult NUMERIC,
    TargetAtrMult NUMERIC,
    IsAutoShortEnabled BOOLEAN,
    ShortEntryCutoff VARCHAR,
    ShortSquareOffTime VARCHAR,
    ShortStopLossAtrMult NUMERIC,
    ShortTrailAtrMult NUMERIC,
    ShortTargetAtrMult NUMERIC,
    TrailActivationPct NUMERIC,
    UpdatedAt TIMESTAMP WITH TIME ZONE
)
LANGUAGE plpgsql
AS $$
BEGIN
    RETURN QUERY
    INSERT INTO auto_trade_settings (
        user_id, is_auto_trade_enabled, available_capital, profit_target_pct, stop_loss_pct,
        max_duration_days, max_trades_per_day, fixed_amount_per_trade, min_conditions_match,
        trading_window_start, trading_window_end, trailing_sl_pct,
        entry_delay_minutes, max_daily_loss_limit, exit_mode, close_check_time,
        stop_loss_atr_mult, trail_atr_mult, target_atr_mult,
        is_auto_short_enabled, short_entry_cutoff, short_square_off_time,
        short_stop_loss_atr_mult, short_trail_atr_mult, short_target_atr_mult, trail_activation_pct, updated_at
    )
    VALUES (
        p_user_id, p_is_auto_trade_enabled, p_available_capital, p_profit_target_pct, p_stop_loss_pct,
        p_max_duration_days, p_max_trades_per_day, p_fixed_amount_per_trade, p_min_conditions_match,
        p_trading_window_start, p_trading_window_end, p_trailing_sl_pct,
        p_entry_delay_minutes, p_max_daily_loss_limit, p_exit_mode, p_close_check_time,
        p_stop_loss_atr_mult, p_trail_atr_mult, p_target_atr_mult,
        COALESCE(p_is_auto_short_enabled, FALSE), COALESCE(p_short_entry_cutoff, '15:00'), COALESCE(p_short_square_off_time, '15:15'),
        COALESCE(p_short_stop_loss_atr_mult, 0.60), COALESCE(p_short_trail_atr_mult, 1.20), COALESCE(p_short_target_atr_mult, 1.20),
        COALESCE(p_trail_activation_pct, 0), NOW()
    )
    ON CONFLICT (user_id) DO UPDATE
    SET is_auto_trade_enabled = EXCLUDED.is_auto_trade_enabled,
        available_capital = EXCLUDED.available_capital,
        profit_target_pct = EXCLUDED.profit_target_pct,
        stop_loss_pct = EXCLUDED.stop_loss_pct,
        max_duration_days = EXCLUDED.max_duration_days,
        max_trades_per_day = EXCLUDED.max_trades_per_day,
        fixed_amount_per_trade = EXCLUDED.fixed_amount_per_trade,
        min_conditions_match = EXCLUDED.min_conditions_match,
        trading_window_start = EXCLUDED.trading_window_start,
        trading_window_end = EXCLUDED.trading_window_end,
        trailing_sl_pct = EXCLUDED.trailing_sl_pct,
        entry_delay_minutes = EXCLUDED.entry_delay_minutes,
        max_daily_loss_limit = EXCLUDED.max_daily_loss_limit,
        exit_mode = EXCLUDED.exit_mode,
        close_check_time = EXCLUDED.close_check_time,
        stop_loss_atr_mult = EXCLUDED.stop_loss_atr_mult,
        trail_atr_mult = EXCLUDED.trail_atr_mult,
        target_atr_mult = EXCLUDED.target_atr_mult,
        is_auto_short_enabled = EXCLUDED.is_auto_short_enabled,
        short_entry_cutoff = EXCLUDED.short_entry_cutoff,
        short_square_off_time = EXCLUDED.short_square_off_time,
        short_stop_loss_atr_mult = EXCLUDED.short_stop_loss_atr_mult,
        short_trail_atr_mult = EXCLUDED.short_trail_atr_mult,
        short_target_atr_mult = EXCLUDED.short_target_atr_mult,
        trail_activation_pct = EXCLUDED.trail_activation_pct,
        updated_at = NOW()
    RETURNING
        auto_trade_settings.id AS Id,
        auto_trade_settings.user_id AS UserId,
        auto_trade_settings.is_auto_trade_enabled AS IsAutoTradeEnabled,
        auto_trade_settings.available_capital AS AvailableCapital,
        auto_trade_settings.profit_target_pct AS ProfitTargetPct,
        auto_trade_settings.stop_loss_pct AS StopLossPct,
        auto_trade_settings.trailing_sl_pct AS TrailingSlPct,
        auto_trade_settings.max_duration_days AS MaxDurationDays,
        auto_trade_settings.max_trades_per_day AS MaxTradesPerDay,
        auto_trade_settings.fixed_amount_per_trade AS FixedAmountPerTrade,
        auto_trade_settings.min_conditions_match AS MinConditionsMatch,
        auto_trade_settings.trading_window_start AS TradingWindowStart,
        auto_trade_settings.trading_window_end AS TradingWindowEnd,
        auto_trade_settings.entry_delay_minutes AS EntryDelayMinutes,
        auto_trade_settings.max_daily_loss_limit AS MaxDailyLossLimit,
        auto_trade_settings.exit_mode AS ExitMode,
        auto_trade_settings.close_check_time AS CloseCheckTime,
        auto_trade_settings.stop_loss_atr_mult AS StopLossAtrMult,
        auto_trade_settings.trail_atr_mult AS TrailAtrMult,
        auto_trade_settings.target_atr_mult AS TargetAtrMult,
        auto_trade_settings.is_auto_short_enabled AS IsAutoShortEnabled,
        auto_trade_settings.short_entry_cutoff AS ShortEntryCutoff,
        auto_trade_settings.short_square_off_time AS ShortSquareOffTime,
        auto_trade_settings.short_stop_loss_atr_mult AS ShortStopLossAtrMult,
        auto_trade_settings.short_trail_atr_mult AS ShortTrailAtrMult,
        auto_trade_settings.short_target_atr_mult AS ShortTargetAtrMult,
        auto_trade_settings.trail_activation_pct AS TrailActivationPct,
        auto_trade_settings.updated_at AS UpdatedAt;
END;
$$;


DROP FUNCTION IF EXISTS fn_get_auto_trade_settings(VARCHAR);

CREATE OR REPLACE FUNCTION fn_get_auto_trade_settings(p_user_id VARCHAR)
RETURNS TABLE (
    Id INT,
    UserId VARCHAR,
    IsAutoTradeEnabled BOOLEAN,
    AvailableCapital NUMERIC,
    ProfitTargetPct NUMERIC,
    StopLossPct NUMERIC,
    TrailingSlPct NUMERIC,
    MaxDurationDays INT,
    MaxTradesPerDay INT,
    FixedAmountPerTrade NUMERIC,
    MinConditionsMatch INT,
    TradingWindowStart VARCHAR,
    TradingWindowEnd VARCHAR,
    EntryDelayMinutes INT,
    MaxDailyLossLimit NUMERIC,
    ExitMode VARCHAR,
    CloseCheckTime VARCHAR,
    StopLossAtrMult NUMERIC,
    TrailAtrMult NUMERIC,
    TargetAtrMult NUMERIC,
    IsAutoShortEnabled BOOLEAN,
    ShortEntryCutoff VARCHAR,
    ShortSquareOffTime VARCHAR,
    ShortStopLossAtrMult NUMERIC,
    ShortTrailAtrMult NUMERIC,
    ShortTargetAtrMult NUMERIC,
    TrailActivationPct NUMERIC,
    UpdatedAt TIMESTAMP WITH TIME ZONE
)
LANGUAGE plpgsql
AS $$
BEGIN
    RETURN QUERY
    SELECT
        s.id AS Id,
        s.user_id AS UserId,
        s.is_auto_trade_enabled AS IsAutoTradeEnabled,
        s.available_capital AS AvailableCapital,
        s.profit_target_pct AS ProfitTargetPct,
        s.stop_loss_pct AS StopLossPct,
        s.trailing_sl_pct AS TrailingSlPct,
        s.max_duration_days AS MaxDurationDays,
        s.max_trades_per_day AS MaxTradesPerDay,
        s.fixed_amount_per_trade AS FixedAmountPerTrade,
        s.min_conditions_match AS MinConditionsMatch,
        s.trading_window_start AS TradingWindowStart,
        s.trading_window_end AS TradingWindowEnd,
        s.entry_delay_minutes AS EntryDelayMinutes,
        s.max_daily_loss_limit AS MaxDailyLossLimit,
        s.exit_mode AS ExitMode,
        s.close_check_time AS CloseCheckTime,
        s.stop_loss_atr_mult AS StopLossAtrMult,
        s.trail_atr_mult AS TrailAtrMult,
        s.target_atr_mult AS TargetAtrMult,
        s.is_auto_short_enabled AS IsAutoShortEnabled,
        s.short_entry_cutoff AS ShortEntryCutoff,
        s.short_square_off_time AS ShortSquareOffTime,
        s.short_stop_loss_atr_mult AS ShortStopLossAtrMult,
        s.short_trail_atr_mult AS ShortTrailAtrMult,
        s.short_target_atr_mult AS ShortTargetAtrMult,
        s.trail_activation_pct AS TrailActivationPct,
        s.updated_at AS UpdatedAt
    FROM auto_trade_settings s
    WHERE s.user_id = p_user_id;
END;
$$;


DROP FUNCTION IF EXISTS fn_get_active_auto_trade_settings();

CREATE OR REPLACE FUNCTION fn_get_active_auto_trade_settings()
RETURNS TABLE (
    Id INT,
    UserId VARCHAR,
    IsAutoTradeEnabled BOOLEAN,
    AvailableCapital NUMERIC,
    ProfitTargetPct NUMERIC,
    StopLossPct NUMERIC,
    TrailingSlPct NUMERIC,
    MaxDurationDays INT,
    MaxTradesPerDay INT,
    FixedAmountPerTrade NUMERIC,
    MinConditionsMatch INT,
    TradingWindowStart VARCHAR,
    TradingWindowEnd VARCHAR,
    EntryDelayMinutes INT,
    MaxDailyLossLimit NUMERIC,
    ExitMode VARCHAR,
    CloseCheckTime VARCHAR,
    StopLossAtrMult NUMERIC,
    TrailAtrMult NUMERIC,
    TargetAtrMult NUMERIC,
    IsAutoShortEnabled BOOLEAN,
    ShortEntryCutoff VARCHAR,
    ShortSquareOffTime VARCHAR,
    ShortStopLossAtrMult NUMERIC,
    ShortTrailAtrMult NUMERIC,
    ShortTargetAtrMult NUMERIC,
    TrailActivationPct NUMERIC,
    UpdatedAt TIMESTAMP WITH TIME ZONE
)
LANGUAGE plpgsql
AS $$
BEGIN
    RETURN QUERY
    SELECT
        s.id AS Id,
        s.user_id AS UserId,
        s.is_auto_trade_enabled AS IsAutoTradeEnabled,
        s.available_capital AS AvailableCapital,
        s.profit_target_pct AS ProfitTargetPct,
        s.stop_loss_pct AS StopLossPct,
        s.trailing_sl_pct AS TrailingSlPct,
        s.max_duration_days AS MaxDurationDays,
        s.max_trades_per_day AS MaxTradesPerDay,
        s.fixed_amount_per_trade AS FixedAmountPerTrade,
        s.min_conditions_match AS MinConditionsMatch,
        s.trading_window_start AS TradingWindowStart,
        s.trading_window_end AS TradingWindowEnd,
        s.entry_delay_minutes AS EntryDelayMinutes,
        s.max_daily_loss_limit AS MaxDailyLossLimit,
        s.exit_mode AS ExitMode,
        s.close_check_time AS CloseCheckTime,
        s.stop_loss_atr_mult AS StopLossAtrMult,
        s.trail_atr_mult AS TrailAtrMult,
        s.target_atr_mult AS TargetAtrMult,
        s.is_auto_short_enabled AS IsAutoShortEnabled,
        s.short_entry_cutoff AS ShortEntryCutoff,
        s.short_square_off_time AS ShortSquareOffTime,
        s.short_stop_loss_atr_mult AS ShortStopLossAtrMult,
        s.short_trail_atr_mult AS ShortTrailAtrMult,
        s.short_target_atr_mult AS ShortTargetAtrMult,
        s.trail_activation_pct AS TrailActivationPct,
        s.updated_at AS UpdatedAt
    FROM auto_trade_settings s
    WHERE s.is_auto_trade_enabled = TRUE;
END;
$$;

-- ============================================================================
-- 4. fn_get_real_trade_settings / fn_get_active_real_trade_settings / fn_upsert_real_trade_settings
--    - add p_trail_activation_pct param + TrailActivationPct output column
-- ============================================================================
DROP FUNCTION IF EXISTS fn_get_real_trade_settings(INT);
DROP FUNCTION IF EXISTS fn_get_real_trade_settings(VARCHAR);

CREATE OR REPLACE FUNCTION fn_get_real_trade_settings(p_user_id INT)
RETURNS TABLE (
    Id INT,
    UserId INT,
    IsRealTradeEnabled BOOLEAN,
    AvailableCapital NUMERIC,
    ProfitTargetPct NUMERIC,
    StopLossPct NUMERIC,
    TrailingSlEnabled BOOLEAN,
    TrailingSlPct NUMERIC,
    MaxDurationDays INT,
    MaxTradesPerDay INT,
    FixedAmountPerTrade NUMERIC,
    MaxDailyLossLimit NUMERIC,
    ProductType VARCHAR,
    MinConditionsMatch INT,
    TradingWindowStart VARCHAR,
    TradingWindowEnd VARCHAR,
    EntryDelayMinutes INT,
    ExitMode VARCHAR,
    CloseCheckTime VARCHAR,
    StopLossAtrMult NUMERIC,
    TrailAtrMult NUMERIC,
    TargetAtrMult NUMERIC,
    IsAutoShortEnabled BOOLEAN,
    ShortEntryCutoff VARCHAR,
    ShortSquareOffTime VARCHAR,
    ShortStopLossAtrMult NUMERIC,
    ShortTrailAtrMult NUMERIC,
    ShortTargetAtrMult NUMERIC,
    TrailActivationPct NUMERIC,
    UpdatedAt TIMESTAMP WITH TIME ZONE
)
LANGUAGE plpgsql
AS $$
BEGIN
    RETURN QUERY
    SELECT
        s.id AS Id,
        s.user_id AS UserId,
        s.is_real_trade_enabled AS IsRealTradeEnabled,
        s.available_capital AS AvailableCapital,
        s.profit_target_pct AS ProfitTargetPct,
        s.stop_loss_pct AS StopLossPct,
        s.trailing_sl_enabled AS TrailingSlEnabled,
        s.trailing_sl_pct AS TrailingSlPct,
        s.max_duration_days AS MaxDurationDays,
        s.max_trades_per_day AS MaxTradesPerDay,
        s.fixed_amount_per_trade AS FixedAmountPerTrade,
        s.max_daily_loss_limit AS MaxDailyLossLimit,
        s.product_type AS ProductType,
        s.min_conditions_match AS MinConditionsMatch,
        s.trading_window_start AS TradingWindowStart,
        s.trading_window_end AS TradingWindowEnd,
        s.entry_delay_minutes AS EntryDelayMinutes,
        s.exit_mode AS ExitMode,
        s.close_check_time AS CloseCheckTime,
        s.stop_loss_atr_mult AS StopLossAtrMult,
        s.trail_atr_mult AS TrailAtrMult,
        s.target_atr_mult AS TargetAtrMult,
        s.is_auto_short_enabled AS IsAutoShortEnabled,
        s.short_entry_cutoff AS ShortEntryCutoff,
        s.short_square_off_time AS ShortSquareOffTime,
        s.short_stop_loss_atr_mult AS ShortStopLossAtrMult,
        s.short_trail_atr_mult AS ShortTrailAtrMult,
        s.short_target_atr_mult AS ShortTargetAtrMult,
        s.trail_activation_pct AS TrailActivationPct,
        s.updated_at AS UpdatedAt
    FROM real_trade_settings s
    WHERE s.user_id = p_user_id;
END;
$$;


DROP FUNCTION IF EXISTS fn_get_active_real_trade_settings();

CREATE OR REPLACE FUNCTION fn_get_active_real_trade_settings()
RETURNS TABLE (
    Id INT,
    UserId INT,
    IsRealTradeEnabled BOOLEAN,
    AvailableCapital NUMERIC,
    ProfitTargetPct NUMERIC,
    StopLossPct NUMERIC,
    TrailingSlEnabled BOOLEAN,
    TrailingSlPct NUMERIC,
    MaxDurationDays INT,
    MaxTradesPerDay INT,
    FixedAmountPerTrade NUMERIC,
    MaxDailyLossLimit NUMERIC,
    ProductType VARCHAR,
    MinConditionsMatch INT,
    TradingWindowStart VARCHAR,
    TradingWindowEnd VARCHAR,
    EntryDelayMinutes INT,
    ExitMode VARCHAR,
    CloseCheckTime VARCHAR,
    StopLossAtrMult NUMERIC,
    TrailAtrMult NUMERIC,
    TargetAtrMult NUMERIC,
    IsAutoShortEnabled BOOLEAN,
    ShortEntryCutoff VARCHAR,
    ShortSquareOffTime VARCHAR,
    ShortStopLossAtrMult NUMERIC,
    ShortTrailAtrMult NUMERIC,
    ShortTargetAtrMult NUMERIC,
    TrailActivationPct NUMERIC,
    UpdatedAt TIMESTAMP WITH TIME ZONE
)
LANGUAGE plpgsql
AS $$
BEGIN
    RETURN QUERY
    SELECT
        s.id AS Id,
        s.user_id AS UserId,
        s.is_real_trade_enabled AS IsRealTradeEnabled,
        s.available_capital AS AvailableCapital,
        s.profit_target_pct AS ProfitTargetPct,
        s.stop_loss_pct AS StopLossPct,
        s.trailing_sl_enabled AS TrailingSlEnabled,
        s.trailing_sl_pct AS TrailingSlPct,
        s.max_duration_days AS MaxDurationDays,
        s.max_trades_per_day AS MaxTradesPerDay,
        s.fixed_amount_per_trade AS FixedAmountPerTrade,
        s.max_daily_loss_limit AS MaxDailyLossLimit,
        s.product_type AS ProductType,
        s.min_conditions_match AS MinConditionsMatch,
        s.trading_window_start AS TradingWindowStart,
        s.trading_window_end AS TradingWindowEnd,
        s.entry_delay_minutes AS EntryDelayMinutes,
        s.exit_mode AS ExitMode,
        s.close_check_time AS CloseCheckTime,
        s.stop_loss_atr_mult AS StopLossAtrMult,
        s.trail_atr_mult AS TrailAtrMult,
        s.target_atr_mult AS TargetAtrMult,
        s.is_auto_short_enabled AS IsAutoShortEnabled,
        s.short_entry_cutoff AS ShortEntryCutoff,
        s.short_square_off_time AS ShortSquareOffTime,
        s.short_stop_loss_atr_mult AS ShortStopLossAtrMult,
        s.short_trail_atr_mult AS ShortTrailAtrMult,
        s.short_target_atr_mult AS ShortTargetAtrMult,
        s.trail_activation_pct AS TrailActivationPct,
        s.updated_at AS UpdatedAt
    FROM real_trade_settings s
    WHERE s.is_real_trade_enabled = TRUE;
END;
$$;


DROP FUNCTION IF EXISTS fn_upsert_real_trade_settings(INT, BOOLEAN, NUMERIC, NUMERIC, NUMERIC, BOOLEAN, NUMERIC, INT, INT, NUMERIC, NUMERIC, VARCHAR, INT, VARCHAR, VARCHAR);
DROP FUNCTION IF EXISTS fn_upsert_real_trade_settings(VARCHAR, BOOLEAN, NUMERIC, NUMERIC, NUMERIC, BOOLEAN, NUMERIC, INT, INT, NUMERIC, NUMERIC, VARCHAR, INT, VARCHAR, VARCHAR);
DROP FUNCTION IF EXISTS fn_upsert_real_trade_settings(INT, BOOLEAN, NUMERIC, NUMERIC, NUMERIC, BOOLEAN, NUMERIC, INT, INT, NUMERIC, NUMERIC, VARCHAR, INT, VARCHAR, VARCHAR, INT);
DROP FUNCTION IF EXISTS fn_upsert_real_trade_settings(INT, BOOLEAN, NUMERIC, NUMERIC, NUMERIC, BOOLEAN, NUMERIC, INT, INT, NUMERIC, NUMERIC, VARCHAR, INT, VARCHAR, VARCHAR, INT, VARCHAR, VARCHAR, NUMERIC, NUMERIC, NUMERIC);
DROP FUNCTION IF EXISTS fn_upsert_real_trade_settings(INT, BOOLEAN, NUMERIC, NUMERIC, NUMERIC, BOOLEAN, NUMERIC, INT, INT, NUMERIC, NUMERIC, VARCHAR, INT, VARCHAR, VARCHAR, INT, VARCHAR, VARCHAR, NUMERIC, NUMERIC, NUMERIC, BOOLEAN, VARCHAR, VARCHAR);
DROP FUNCTION IF EXISTS fn_upsert_real_trade_settings(INT, BOOLEAN, NUMERIC, NUMERIC, NUMERIC, BOOLEAN, NUMERIC, INT, INT, NUMERIC, NUMERIC, VARCHAR, INT, VARCHAR, VARCHAR, INT, VARCHAR, VARCHAR, NUMERIC, NUMERIC, NUMERIC, BOOLEAN, VARCHAR, VARCHAR, NUMERIC, NUMERIC, NUMERIC);

CREATE OR REPLACE FUNCTION fn_upsert_real_trade_settings(
    p_user_id INT,
    p_is_real_trade_enabled BOOLEAN,
    p_available_capital NUMERIC,
    p_profit_target_pct NUMERIC,
    p_stop_loss_pct NUMERIC,
    p_trailing_sl_enabled BOOLEAN,
    p_trailing_sl_pct NUMERIC,
    p_max_duration_days INT,
    p_max_trades_per_day INT,
    p_fixed_amount_per_trade NUMERIC,
    p_max_daily_loss_limit NUMERIC,
    p_product_type VARCHAR,
    p_min_conditions_match INT,
    p_trading_window_start VARCHAR,
    p_trading_window_end VARCHAR,
    p_entry_delay_minutes INT,
    p_exit_mode VARCHAR,
    p_close_check_time VARCHAR,
    p_stop_loss_atr_mult NUMERIC,
    p_trail_atr_mult NUMERIC,
    p_target_atr_mult NUMERIC,
    p_is_auto_short_enabled BOOLEAN DEFAULT FALSE,
    p_short_entry_cutoff VARCHAR DEFAULT '15:00',
    p_short_square_off_time VARCHAR DEFAULT '15:15',
    p_short_stop_loss_atr_mult NUMERIC DEFAULT 0.60,
    p_short_trail_atr_mult NUMERIC DEFAULT 1.20,
    p_short_target_atr_mult NUMERIC DEFAULT 1.20,
    p_trail_activation_pct NUMERIC DEFAULT 0
)
RETURNS TABLE (
    Id INT,
    UserId INT,
    IsRealTradeEnabled BOOLEAN,
    AvailableCapital NUMERIC,
    ProfitTargetPct NUMERIC,
    StopLossPct NUMERIC,
    TrailingSlEnabled BOOLEAN,
    TrailingSlPct NUMERIC,
    MaxDurationDays INT,
    MaxTradesPerDay INT,
    FixedAmountPerTrade NUMERIC,
    MaxDailyLossLimit NUMERIC,
    ProductType VARCHAR,
    MinConditionsMatch INT,
    TradingWindowStart VARCHAR,
    TradingWindowEnd VARCHAR,
    EntryDelayMinutes INT,
    ExitMode VARCHAR,
    CloseCheckTime VARCHAR,
    StopLossAtrMult NUMERIC,
    TrailAtrMult NUMERIC,
    TargetAtrMult NUMERIC,
    IsAutoShortEnabled BOOLEAN,
    ShortEntryCutoff VARCHAR,
    ShortSquareOffTime VARCHAR,
    ShortStopLossAtrMult NUMERIC,
    ShortTrailAtrMult NUMERIC,
    ShortTargetAtrMult NUMERIC,
    TrailActivationPct NUMERIC,
    UpdatedAt TIMESTAMP WITH TIME ZONE
)
LANGUAGE plpgsql
AS $$
BEGIN
    RETURN QUERY
    INSERT INTO real_trade_settings (
        user_id, is_real_trade_enabled, available_capital, profit_target_pct,
        stop_loss_pct, trailing_sl_enabled, trailing_sl_pct, max_duration_days,
        max_trades_per_day, fixed_amount_per_trade, max_daily_loss_limit, product_type,
        min_conditions_match, trading_window_start, trading_window_end, entry_delay_minutes,
        exit_mode, close_check_time, stop_loss_atr_mult, trail_atr_mult, target_atr_mult,
        is_auto_short_enabled, short_entry_cutoff, short_square_off_time,
        short_stop_loss_atr_mult, short_trail_atr_mult, short_target_atr_mult, trail_activation_pct, updated_at
    )
    VALUES (
        p_user_id, p_is_real_trade_enabled, p_available_capital, p_profit_target_pct,
        p_stop_loss_pct, p_trailing_sl_enabled, p_trailing_sl_pct, p_max_duration_days,
        p_max_trades_per_day, p_fixed_amount_per_trade, p_max_daily_loss_limit, p_product_type,
        p_min_conditions_match, p_trading_window_start, p_trading_window_end, p_entry_delay_minutes,
        p_exit_mode, p_close_check_time, p_stop_loss_atr_mult, p_trail_atr_mult, p_target_atr_mult,
        COALESCE(p_is_auto_short_enabled, FALSE), COALESCE(p_short_entry_cutoff, '15:00'), COALESCE(p_short_square_off_time, '15:15'),
        COALESCE(p_short_stop_loss_atr_mult, 0.60), COALESCE(p_short_trail_atr_mult, 1.20), COALESCE(p_short_target_atr_mult, 1.20),
        COALESCE(p_trail_activation_pct, 0), NOW()
    )
    ON CONFLICT (user_id) DO UPDATE SET
        is_real_trade_enabled = EXCLUDED.is_real_trade_enabled,
        available_capital = EXCLUDED.available_capital,
        profit_target_pct = EXCLUDED.profit_target_pct,
        stop_loss_pct = EXCLUDED.stop_loss_pct,
        trailing_sl_enabled = EXCLUDED.trailing_sl_enabled,
        trailing_sl_pct = EXCLUDED.trailing_sl_pct,
        max_duration_days = EXCLUDED.max_duration_days,
        max_trades_per_day = EXCLUDED.max_trades_per_day,
        fixed_amount_per_trade = EXCLUDED.fixed_amount_per_trade,
        max_daily_loss_limit = EXCLUDED.max_daily_loss_limit,
        product_type = EXCLUDED.product_type,
        min_conditions_match = EXCLUDED.min_conditions_match,
        trading_window_start = EXCLUDED.trading_window_start,
        trading_window_end = EXCLUDED.trading_window_end,
        entry_delay_minutes = EXCLUDED.entry_delay_minutes,
        exit_mode = EXCLUDED.exit_mode,
        close_check_time = EXCLUDED.close_check_time,
        stop_loss_atr_mult = EXCLUDED.stop_loss_atr_mult,
        trail_atr_mult = EXCLUDED.trail_atr_mult,
        target_atr_mult = EXCLUDED.target_atr_mult,
        is_auto_short_enabled = EXCLUDED.is_auto_short_enabled,
        short_entry_cutoff = EXCLUDED.short_entry_cutoff,
        short_square_off_time = EXCLUDED.short_square_off_time,
        short_stop_loss_atr_mult = EXCLUDED.short_stop_loss_atr_mult,
        short_trail_atr_mult = EXCLUDED.short_trail_atr_mult,
        short_target_atr_mult = EXCLUDED.short_target_atr_mult,
        trail_activation_pct = EXCLUDED.trail_activation_pct,
        updated_at = NOW()
    RETURNING
        real_trade_settings.id AS Id,
        real_trade_settings.user_id AS UserId,
        real_trade_settings.is_real_trade_enabled AS IsRealTradeEnabled,
        real_trade_settings.available_capital AS AvailableCapital,
        real_trade_settings.profit_target_pct AS ProfitTargetPct,
        real_trade_settings.stop_loss_pct AS StopLossPct,
        real_trade_settings.trailing_sl_enabled AS TrailingSlEnabled,
        real_trade_settings.trailing_sl_pct AS TrailingSlPct,
        real_trade_settings.max_duration_days AS MaxDurationDays,
        real_trade_settings.max_trades_per_day AS MaxTradesPerDay,
        real_trade_settings.fixed_amount_per_trade AS FixedAmountPerTrade,
        real_trade_settings.max_daily_loss_limit AS MaxDailyLossLimit,
        real_trade_settings.product_type AS ProductType,
        real_trade_settings.min_conditions_match AS MinConditionsMatch,
        real_trade_settings.trading_window_start AS TradingWindowStart,
        real_trade_settings.trading_window_end AS TradingWindowEnd,
        real_trade_settings.entry_delay_minutes AS EntryDelayMinutes,
        real_trade_settings.exit_mode AS ExitMode,
        real_trade_settings.close_check_time AS CloseCheckTime,
        real_trade_settings.stop_loss_atr_mult AS StopLossAtrMult,
        real_trade_settings.trail_atr_mult AS TrailAtrMult,
        real_trade_settings.target_atr_mult AS TargetAtrMult,
        real_trade_settings.is_auto_short_enabled AS IsAutoShortEnabled,
        real_trade_settings.short_entry_cutoff AS ShortEntryCutoff,
        real_trade_settings.short_square_off_time AS ShortSquareOffTime,
        real_trade_settings.short_stop_loss_atr_mult AS ShortStopLossAtrMult,
        real_trade_settings.short_trail_atr_mult AS ShortTrailAtrMult,
        real_trade_settings.short_target_atr_mult AS ShortTargetAtrMult,
        real_trade_settings.trail_activation_pct AS TrailActivationPct,
        real_trade_settings.updated_at AS UpdatedAt;
END;
$$;
