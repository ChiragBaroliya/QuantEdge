-- ============================================================================
-- QuantEdge Database Stored Procedures (PostgreSQL / TimescaleDB)
-- ============================================================================

-- ----------------------------------------------------------------------------
-- Procedure: sp_insert_market_candle
-- Dynamically creates target table based on timeframe if not exists, and UPSERTs data.
-- ----------------------------------------------------------------------------
DROP PROCEDURE IF EXISTS sp_insert_market_candle CASCADE;
DROP PROCEDURE IF EXISTS sp_insert_market_candle(INT, VARCHAR, VARCHAR, NUMERIC, NUMERIC, NUMERIC, NUMERIC, BIGINT, TIMESTAMP WITH TIME ZONE, TIMESTAMP WITH TIME ZONE) CASCADE;

CREATE OR REPLACE PROCEDURE sp_insert_market_candle(
    p_id INT,
    p_symbol VARCHAR(50),
    p_timeframe VARCHAR(20),
    p_open NUMERIC(18, 6),
    p_high NUMERIC(18, 6),
    p_low NUMERIC(18, 6),
    p_close NUMERIC(18, 6),
    p_volume BIGINT,
    p_candle_time TIMESTAMP WITH TIME ZONE,
    p_created_at TIMESTAMP WITH TIME ZONE
)
LANGUAGE plpgsql
AS $$
DECLARE
    v_table_name TEXT;
BEGIN
    v_table_name := 'market_candles_' || LOWER(p_timeframe);
    
    -- Check and create table dynamically if it doesn't exist
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.tables 
        WHERE table_schema = 'public' AND table_name = v_table_name
    ) THEN
        EXECUTE format('
            CREATE TABLE %I (
                id INT NOT NULL,
                candle_time TIMESTAMP WITH TIME ZONE NOT NULL,
                symbol VARCHAR(50) NOT NULL,
                timeframe VARCHAR(20) NOT NULL,
                open NUMERIC(18, 6) NOT NULL,
                high NUMERIC(18, 6) NOT NULL,
                low NUMERIC(18, 6) NOT NULL,
                close NUMERIC(18, 6) NOT NULL,
                volume BIGINT NOT NULL,
                created_at TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT NOW(),
                CONSTRAINT %I PRIMARY KEY (id, candle_time)
            );
            CREATE INDEX IF NOT EXISTS %I ON %I (symbol, candle_time DESC);
        ', 
        v_table_name, 
        'pk_' || v_table_name, 
        'ix_' || v_table_name || '_symbol_candle_time', 
        v_table_name);
        
        RAISE NOTICE 'Created dynamic table %', v_table_name;
    END IF;

    EXECUTE format('
        INSERT INTO %I (id, symbol, timeframe, open, high, low, close, volume, candle_time, created_at)
        VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10)
        ON CONFLICT (id, candle_time) DO UPDATE
        SET open = EXCLUDED.open,
            high = EXCLUDED.high,
            low = EXCLUDED.low,
            close = EXCLUDED.close,
            volume = EXCLUDED.volume;', v_table_name)
    USING p_id, p_symbol, p_timeframe, p_open, p_high, p_low, p_close, p_volume, p_candle_time, p_created_at;
END;
$$;


-- ----------------------------------------------------------------------------
-- Procedure: sp_insert_market_indicator
-- Dynamically creates target table based on timeframe if not exists, and UPSERTs data.
-- ----------------------------------------------------------------------------
DROP PROCEDURE IF EXISTS sp_insert_market_indicator CASCADE;
DROP PROCEDURE IF EXISTS sp_insert_market_indicator(INT, VARCHAR, VARCHAR, NUMERIC, NUMERIC, NUMERIC, NUMERIC, NUMERIC, NUMERIC, TIMESTAMP WITH TIME ZONE, TIMESTAMP WITH TIME ZONE) CASCADE;

CREATE OR REPLACE PROCEDURE sp_insert_market_indicator(
    p_id INT,
    p_symbol VARCHAR(50),
    p_timeframe VARCHAR(20),
    p_rsi NUMERIC(18, 6),
    p_ema20 NUMERIC(18, 6),
    p_ema50 NUMERIC(18, 6),
    p_macd NUMERIC(18, 6),
    p_signal_line NUMERIC(18, 6),
    p_vwap NUMERIC(18, 6),
    p_candle_time TIMESTAMP WITH TIME ZONE,
    p_created_at TIMESTAMP WITH TIME ZONE
)
LANGUAGE plpgsql
AS $$
DECLARE
    v_table_name TEXT;
BEGIN
    v_table_name := 'market_indicators_' || LOWER(p_timeframe);
    
    -- Check and create table dynamically if it doesn't exist
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.tables 
        WHERE table_schema = 'public' AND table_name = v_table_name
    ) THEN
        EXECUTE format('
            CREATE TABLE %I (
                id INT NOT NULL,
                candle_time TIMESTAMP WITH TIME ZONE NOT NULL,
                symbol VARCHAR(50) NOT NULL,
                timeframe VARCHAR(20) NOT NULL,
                rsi NUMERIC(18, 6) NOT NULL,
                ema20 NUMERIC(18, 6) NOT NULL,
                ema50 NUMERIC(18, 6) NOT NULL,
                macd NUMERIC(18, 6) NOT NULL,
                signal_line NUMERIC(18, 6) NOT NULL,
                vwap NUMERIC(18, 6) NOT NULL,
                created_at TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT NOW(),
                CONSTRAINT %I PRIMARY KEY (id, candle_time)
            );
            CREATE INDEX IF NOT EXISTS %I ON %I (symbol, candle_time DESC);
        ', 
        v_table_name, 
        'pk_' || v_table_name, 
        'ix_' || v_table_name || '_symbol_candle_time', 
        v_table_name);
        
        RAISE NOTICE 'Created dynamic table %', v_table_name;
    END IF;

    EXECUTE format('
        INSERT INTO %I (id, symbol, timeframe, rsi, ema20, ema50, macd, signal_line, vwap, candle_time, created_at)
        VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11)
        ON CONFLICT (id, candle_time) DO UPDATE
        SET rsi = EXCLUDED.rsi,
            ema20 = EXCLUDED.ema20,
            ema50 = EXCLUDED.ema50,
            macd = EXCLUDED.macd,
            signal_line = EXCLUDED.signal_line,
            vwap = EXCLUDED.vwap;', v_table_name)
    USING p_id, p_symbol, p_timeframe, p_rsi, p_ema20, p_ema50, p_macd, p_signal_line, p_vwap, p_candle_time, p_created_at;
END;
$$;


-- ----------------------------------------------------------------------------
-- Procedure: sp_insert_trading_signal
-- ----------------------------------------------------------------------------
DROP PROCEDURE IF EXISTS sp_insert_trading_signal CASCADE;
DROP PROCEDURE IF EXISTS sp_insert_trading_signal(INT, VARCHAR, VARCHAR, NUMERIC, NUMERIC, VARCHAR, TIMESTAMP WITH TIME ZONE, TIMESTAMP WITH TIME ZONE) CASCADE;

CREATE OR REPLACE PROCEDURE sp_insert_trading_signal(
    p_id INT,
    p_symbol VARCHAR(50),
    p_signal_type VARCHAR(20),
    p_signal_strength NUMERIC(5, 2),
    p_entry_price NUMERIC(18, 6),
    p_reason VARCHAR(1000),
    p_candle_time TIMESTAMP WITH TIME ZONE,
    p_created_at TIMESTAMP WITH TIME ZONE
)
LANGUAGE plpgsql
AS $$
BEGIN
    INSERT INTO trading_signals (id, symbol, signal_type, signal_strength, entry_price, reason, candle_time, created_at)
    VALUES (p_id, p_symbol, p_signal_type, p_signal_strength, p_entry_price, p_reason, p_candle_time, p_created_at)
    ON CONFLICT (id, candle_time) DO NOTHING;
END;
$$;


-- ----------------------------------------------------------------------------
-- Procedure: sp_upsert_zerodha_session
-- ----------------------------------------------------------------------------
DROP PROCEDURE IF EXISTS sp_upsert_zerodha_session CASCADE;
DROP PROCEDURE IF EXISTS sp_upsert_zerodha_session(VARCHAR, VARCHAR) CASCADE;

CREATE OR REPLACE PROCEDURE sp_upsert_zerodha_session(
    p_api_key VARCHAR(50),
    p_access_token VARCHAR(255)
)
LANGUAGE plpgsql
AS $$
BEGIN
    INSERT INTO zerodha_sessions (api_key, access_token, is_active, created_at)
    VALUES (p_api_key, p_access_token, FALSE, NOW())
    ON CONFLICT (api_key)
    DO UPDATE SET
        access_token = EXCLUDED.access_token,
        is_active    = FALSE,
        created_at   = NOW();
END;
$$;


-- ----------------------------------------------------------------------------
-- Procedure: sp_insert_indian_holiday
-- ----------------------------------------------------------------------------
DROP PROCEDURE IF EXISTS sp_insert_indian_holiday CASCADE;
DROP PROCEDURE IF EXISTS sp_insert_indian_holiday(DATE, VARCHAR) CASCADE;

CREATE OR REPLACE PROCEDURE sp_insert_indian_holiday(
    p_holiday_date DATE,
    p_description VARCHAR(255)
)
LANGUAGE plpgsql
AS $$
BEGIN
    INSERT INTO indian_holidays (holiday_date, description)
    VALUES (p_holiday_date, p_description)
    ON CONFLICT (holiday_date) DO UPDATE
    SET description = EXCLUDED.description;
END;
$$;


-- ----------------------------------------------------------------------------
-- Procedure: sp_delete_indian_holiday
-- ----------------------------------------------------------------------------
DROP PROCEDURE IF EXISTS sp_delete_indian_holiday CASCADE;
DROP PROCEDURE IF EXISTS sp_delete_indian_holiday(INT) CASCADE;

CREATE OR REPLACE PROCEDURE sp_delete_indian_holiday(
    p_id INT
)
LANGUAGE plpgsql
AS $$
BEGIN
    DELETE FROM indian_holidays WHERE id = p_id;
END;
$$;


-- ----------------------------------------------------------------------------
-- Procedure: sp_register_user
-- Registers a new user with default 'User' role and returns generated ID.
-- ----------------------------------------------------------------------------
CREATE OR REPLACE PROCEDURE sp_register_user(
    p_full_name VARCHAR(150),
    p_email VARCHAR(255),
    p_mobile_no VARCHAR(20),
    p_username VARCHAR(100),
    p_password_hash VARCHAR(255),
    INOUT p_user_id INT DEFAULT NULL
)
LANGUAGE plpgsql
AS $$
BEGIN
    INSERT INTO app_users (full_name, email, mobile_no, username, password_hash, role, created_at, updated_at)
    VALUES (p_full_name, LOWER(p_email), p_mobile_no, p_username, p_password_hash, 'User', NOW(), NOW())
    RETURNING id INTO p_user_id;
END;
$$;


-- ----------------------------------------------------------------------------
-- Procedure: sp_update_real_order_status
-- ----------------------------------------------------------------------------
DROP PROCEDURE IF EXISTS sp_update_real_order_status(INT, INT, NUMERIC, VARCHAR, VARCHAR) CASCADE;

CREATE OR REPLACE PROCEDURE sp_update_real_order_status(
    p_order_id INT,
    p_status INT,
    p_filled_price NUMERIC,
    p_broker_order_id VARCHAR,
    p_rejection_reason VARCHAR
)
LANGUAGE plpgsql
AS $$
BEGIN
    UPDATE real_orders
    SET status = p_status,
        filled_price = p_filled_price,
        broker_order_id = COALESCE(p_broker_order_id, broker_order_id),
        rejection_reason = p_rejection_reason,
        filled_at = CASE WHEN p_status = 1 THEN NOW() ELSE filled_at END
    WHERE id = p_order_id;
END;
$$;


-- ----------------------------------------------------------------------------
-- Procedure: sp_close_real_position
-- ----------------------------------------------------------------------------
DROP PROCEDURE IF EXISTS sp_close_real_position(INT, NUMERIC, NUMERIC, VARCHAR) CASCADE;

CREATE OR REPLACE PROCEDURE sp_close_real_position(
    p_position_id INT,
    p_exit_price NUMERIC,
    p_realized_pnl NUMERIC,
    p_exit_reason VARCHAR
)
LANGUAGE plpgsql
AS $$
BEGIN
    UPDATE real_positions
    SET status = 1,
        current_price = p_exit_price,
        realized_pnl = p_realized_pnl,
        unrealized_pnl = 0.00,
        exit_reason = p_exit_reason,
        closed_at = NOW()
    WHERE id = p_position_id;
END;
$$;


-- ----------------------------------------------------------------------------
-- Procedure: sp_update_real_trailing_sl
-- ----------------------------------------------------------------------------
DROP PROCEDURE IF EXISTS sp_update_real_trailing_sl(INT, NUMERIC) CASCADE;

CREATE OR REPLACE PROCEDURE sp_update_real_trailing_sl(
    p_position_id INT,
    p_new_trailing_sl NUMERIC
)
LANGUAGE plpgsql
AS $$
BEGIN
    UPDATE real_positions
    SET trailing_stop_loss = p_new_trailing_sl
    WHERE id = p_position_id AND status = 0;
END;
$$;


-- ----------------------------------------------------------------------------
-- Procedure: sp_log_real_trade_execution
-- ----------------------------------------------------------------------------
DROP PROCEDURE IF EXISTS sp_log_real_trade_execution(INT, VARCHAR, VARCHAR, NUMERIC, INT, VARCHAR) CASCADE;
DROP PROCEDURE IF EXISTS sp_log_real_trade_execution(VARCHAR, VARCHAR, VARCHAR, NUMERIC, INT, VARCHAR) CASCADE;

CREATE OR REPLACE PROCEDURE sp_log_real_trade_execution(
    p_user_id INT,
    p_symbol VARCHAR,
    p_action_type VARCHAR,
    p_price NUMERIC,
    p_quantity INT,
    p_reason VARCHAR
)
LANGUAGE plpgsql
AS $$
BEGIN
    INSERT INTO real_trade_execution_logs (
        user_id, symbol, action_type, price, quantity, reason, executed_at
    ) VALUES (
        p_user_id, p_symbol, p_action_type, p_price, p_quantity, p_reason, NOW()
    );
END;
$$;


-- ----------------------------------------------------------------------------
-- Procedure: sp_save_swing_slot_recommendations
-- ----------------------------------------------------------------------------
DROP PROCEDURE IF EXISTS sp_save_swing_slot_recommendations CASCADE;

CREATE OR REPLACE PROCEDURE sp_save_swing_slot_recommendations(

    p_scan_date DATE,
    p_slot_time TIMESTAMP WITH TIME ZONE,
    p_slot_label VARCHAR(20),
    p_symbol VARCHAR(50),
    p_decision VARCHAR(20),
    p_score INT,
    p_confidence_pct NUMERIC,
    p_entry_price NUMERIC,
    p_stop_loss NUMERIC,
    p_target1 NUMERIC,
    p_target2 NUMERIC,
    p_risk_reward_ratio NUMERIC,
    p_volume_multiplier NUMERIC,
    p_rsi14 NUMERIC,
    p_adx14 NUMERIC,
    p_ema20 NUMERIC,
    p_ema50 NUMERIC,
    p_ema200 NUMERIC,
    p_passed_rules TEXT,
    p_failed_rules TEXT,
    p_reason TEXT,
    p_timeframe_used VARCHAR(50),
    p_checklist_json JSONB
)
LANGUAGE plpgsql
AS $$
BEGIN
    INSERT INTO swing_slot_recommendations (
        scan_date, slot_time, slot_label, symbol, decision, score, confidence_pct,
        entry_price, stop_loss, target1, target2, risk_reward_ratio, volume_multiplier,
        rsi14, adx14, ema20, ema50, ema200, passed_rules, failed_rules, reason,
        timeframe_used, checklist_json, created_at
    )
    VALUES (
        p_scan_date, p_slot_time, p_slot_label, p_symbol, p_decision, p_score, p_confidence_pct,
        p_entry_price, p_stop_loss, p_target1, p_target2, p_risk_reward_ratio, p_volume_multiplier,
        p_rsi14, p_adx14, p_ema20, p_ema50, p_ema200, p_passed_rules, p_failed_rules, p_reason,
        p_timeframe_used, p_checklist_json, NOW()
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
        created_at = NOW();
END;
$$;

-- ----------------------------------------------------------------------------
-- Function: fn_get_trading_report_trades
-- Aggregates real and paper trading history and simulated swing positions
-- for performance and investment reporting.
-- ----------------------------------------------------------------------------
DROP FUNCTION IF EXISTS fn_get_trading_report_trades(TEXT, TEXT, TIMESTAMP WITH TIME ZONE, TIMESTAMP WITH TIME ZONE, TEXT);

CREATE OR REPLACE FUNCTION fn_get_trading_report_trades(
    p_mode TEXT DEFAULT 'all',
    p_user_id TEXT DEFAULT NULL,
    p_start_date TIMESTAMP WITH TIME ZONE DEFAULT NULL,
    p_end_date TIMESTAMP WITH TIME ZONE DEFAULT NULL,
    p_symbol TEXT DEFAULT NULL
)
RETURNS TABLE (
    id BIGINT,
    mode TEXT,
    symbol VARCHAR(50),
    side INT,
    quantity INT,
    entry_price NUMERIC(18, 4),
    executed_price NUMERIC(18, 4),
    realized_pnl NUMERIC(18, 4),
    trade_type INT,
    exit_reason VARCHAR(100),
    executed_at TIMESTAMP WITH TIME ZONE,
    opened_at TIMESTAMP WITH TIME ZONE,
    hold_days INT,
    username VARCHAR(100)
)
LANGUAGE plpgsql
AS $$
DECLARE
    v_mode TEXT := LOWER(COALESCE(p_mode, 'all'));
    v_symbol TEXT := CASE WHEN p_symbol IS NOT NULL AND TRIM(p_symbol) <> '' THEN '%' || TRIM(p_symbol) || '%' ELSE NULL END;
BEGIN
    RETURN QUERY
    WITH all_trades AS (
        -- 1. Paper Trade History
        SELECT 
            th.id::BIGINT AS id,
            'Paper'::TEXT AS mode,
            th.symbol,
            th.side,
            th.quantity,
            th.entry_price,
            th.executed_price,
            th.realized_pnl,
            th.trade_type,
            th.exit_reason,
            th.executed_at,
            NULL::TIMESTAMP WITH TIME ZONE AS opened_at,
            0::INT AS hold_days,
            COALESCE(a.user_id, 'default_user')::VARCHAR(100) AS username
        FROM paper_trade_history th
        LEFT JOIN paper_accounts a ON th.account_id = a.id
        WHERE (v_mode = 'all' OR v_mode = 'paper')
          AND (p_start_date IS NULL OR th.executed_at >= p_start_date)
          AND (p_end_date IS NULL OR th.executed_at <= p_end_date)
          AND (v_symbol IS NULL OR th.symbol ILIKE v_symbol)
          AND (p_user_id IS NULL OR p_user_id = 'all' OR a.user_id = p_user_id)

        UNION ALL

        -- 2. Real Trade History
        SELECT 
            rth.id::BIGINT AS id,
            'Real'::TEXT AS mode,
            rth.symbol,
            rth.side,
            rth.quantity,
            rth.entry_price,
            rth.executed_price,
            rth.realized_pnl,
            rth.trade_type,
            rth.exit_reason,
            rth.executed_at,
            NULL::TIMESTAMP WITH TIME ZONE AS opened_at,
            0::INT AS hold_days,
            COALESCE(u.username, 'admin')::VARCHAR(100) AS username
        FROM real_trade_history rth
        LEFT JOIN app_users u ON rth.user_id = u.id
        WHERE (v_mode = 'all' OR v_mode = 'real')
          AND (p_start_date IS NULL OR rth.executed_at >= p_start_date)
          AND (p_end_date IS NULL OR rth.executed_at <= p_end_date)
          AND (v_symbol IS NULL OR rth.symbol ILIKE v_symbol)
          AND (p_user_id IS NULL OR p_user_id = 'all' OR u.id::TEXT = p_user_id OR u.username = p_user_id)

        UNION ALL

        -- 3. Swing Positions (Simulated closed swing trades)
        SELECT 
            (sp.id + 100000)::BIGINT AS id,
            'Swing Sim'::TEXT AS mode,
            sp.symbol,
            1::INT AS side,
            sp.quantity,
            sp.entry_price,
            COALESCE(sp.exit_price, 0)::NUMERIC(18, 4) AS executed_price,
            COALESCE((sp.exit_price - sp.entry_price) * sp.quantity, 0)::NUMERIC(18, 4) AS realized_pnl,
            1::INT AS trade_type,
            COALESCE(sp.exit_reason, 'Exit Triggered')::VARCHAR(100) AS exit_reason,
            COALESCE(sp.exit_date, sp.entry_date) AS executed_at,
            sp.entry_date AS opened_at,
            COALESCE((sp.exit_date - sp.entry_date), 0)::INT AS hold_days,
            'System'::VARCHAR(100) AS username
        FROM swing_positions sp
        WHERE sp.is_closed = TRUE
          AND (v_mode = 'all' OR v_mode = 'paper')
          AND (p_start_date IS NULL OR sp.exit_date >= p_start_date)
          AND (p_end_date IS NULL OR sp.exit_date <= p_end_date)
          AND (v_symbol IS NULL OR sp.symbol ILIKE v_symbol)
    )
    SELECT * FROM all_trades ORDER BY executed_at DESC;
END;
$$;

-- ----------------------------------------------------------------------------
-- Function: fn_get_trading_report_trades_paged
-- Returns paginated closed trades log with total count and server-side filters.
-- ----------------------------------------------------------------------------
DROP FUNCTION IF EXISTS fn_get_trading_report_trades_paged(TEXT, TEXT, TIMESTAMP WITH TIME ZONE, TIMESTAMP WITH TIME ZONE, TEXT, TEXT, TEXT, INT, INT);

CREATE OR REPLACE FUNCTION fn_get_trading_report_trades_paged(
    p_mode TEXT DEFAULT 'all',
    p_user_id TEXT DEFAULT NULL,
    p_start_date TIMESTAMP WITH TIME ZONE DEFAULT NULL,
    p_end_date TIMESTAMP WITH TIME ZONE DEFAULT NULL,
    p_symbol TEXT DEFAULT NULL,
    p_trade_type TEXT DEFAULT 'all',
    p_pnl_filter TEXT DEFAULT 'all',
    p_page INT DEFAULT 1,
    p_page_size INT DEFAULT 10
)
RETURNS TABLE (
    total_count BIGINT,
    id BIGINT,
    mode TEXT,
    symbol VARCHAR(50),
    side INT,
    quantity INT,
    entry_price NUMERIC(18, 4),
    executed_price NUMERIC(18, 4),
    realized_pnl NUMERIC(18, 4),
    trade_type INT,
    exit_reason VARCHAR(100),
    executed_at TIMESTAMP WITH TIME ZONE,
    opened_at TIMESTAMP WITH TIME ZONE,
    hold_days INT,
    username VARCHAR(100)
)
LANGUAGE plpgsql
AS $$
DECLARE
    v_mode TEXT := LOWER(COALESCE(p_mode, 'all'));
    v_symbol TEXT := CASE WHEN p_symbol IS NOT NULL AND TRIM(p_symbol) <> '' THEN '%' || TRIM(p_symbol) || '%' ELSE NULL END;
    v_trade_type TEXT := LOWER(COALESCE(p_trade_type, 'all'));
    v_pnl_filter TEXT := LOWER(COALESCE(p_pnl_filter, 'all'));
    v_limit INT := GREATEST(1, COALESCE(p_page_size, 10));
    v_offset INT := GREATEST(0, (GREATEST(1, COALESCE(p_page, 1)) - 1) * v_limit);
BEGIN
    RETURN QUERY
    WITH all_trades AS (
        -- 1. Paper Trade History
        SELECT 
            th.id::BIGINT AS id,
            'Paper'::TEXT AS mode,
            th.symbol,
            th.side,
            th.quantity,
            th.entry_price,
            th.executed_price,
            th.realized_pnl,
            th.trade_type,
            th.exit_reason,
            th.executed_at,
            NULL::TIMESTAMP WITH TIME ZONE AS opened_at,
            0::INT AS hold_days,
            COALESCE(a.user_id, 'default_user')::VARCHAR(100) AS username
        FROM paper_trade_history th
        LEFT JOIN paper_accounts a ON th.account_id = a.id
        WHERE (v_mode = 'all' OR v_mode = 'paper')
          AND (p_start_date IS NULL OR th.executed_at >= p_start_date)
          AND (p_end_date IS NULL OR th.executed_at <= p_end_date)
          AND (v_symbol IS NULL OR th.symbol ILIKE v_symbol)
          AND (p_user_id IS NULL OR p_user_id = 'all' OR a.user_id = p_user_id)

        UNION ALL

        -- 2. Real Trade History
        SELECT 
            rth.id::BIGINT AS id,
            'Real'::TEXT AS mode,
            rth.symbol,
            rth.side,
            rth.quantity,
            rth.entry_price,
            rth.executed_price,
            rth.realized_pnl,
            rth.trade_type,
            rth.exit_reason,
            rth.executed_at,
            NULL::TIMESTAMP WITH TIME ZONE AS opened_at,
            0::INT AS hold_days,
            COALESCE(u.username, 'admin')::VARCHAR(100) AS username
        FROM real_trade_history rth
        LEFT JOIN app_users u ON rth.user_id = u.id
        WHERE (v_mode = 'all' OR v_mode = 'real')
          AND (p_start_date IS NULL OR rth.executed_at >= p_start_date)
          AND (p_end_date IS NULL OR rth.executed_at <= p_end_date)
          AND (v_symbol IS NULL OR rth.symbol ILIKE v_symbol)
          AND (p_user_id IS NULL OR p_user_id = 'all' OR u.id::TEXT = p_user_id OR u.username = p_user_id)

        UNION ALL

        -- 3. Swing Positions (Simulated closed swing trades)
        SELECT 
            (sp.id + 100000)::BIGINT AS id,
            'Swing Sim'::TEXT AS mode,
            sp.symbol,
            1::INT AS side,
            sp.quantity,
            sp.entry_price,
            COALESCE(sp.exit_price, 0)::NUMERIC(18, 4) AS executed_price,
            COALESCE((sp.exit_price - sp.entry_price) * sp.quantity, 0)::NUMERIC(18, 4) AS realized_pnl,
            1::INT AS trade_type,
            COALESCE(sp.exit_reason, 'Exit Triggered')::VARCHAR(100) AS exit_reason,
            COALESCE(sp.exit_date, sp.entry_date) AS executed_at,
            sp.entry_date AS opened_at,
            COALESCE((sp.exit_date - sp.entry_date), 0)::INT AS hold_days,
            'System'::VARCHAR(100) AS username
        FROM swing_positions sp
        WHERE sp.is_closed = TRUE
          AND (v_mode = 'all' OR v_mode = 'paper')
          AND (p_start_date IS NULL OR sp.exit_date >= p_start_date)
          AND (p_end_date IS NULL OR sp.exit_date <= p_end_date)
          AND (v_symbol IS NULL OR sp.symbol ILIKE v_symbol)
    ),
    filtered_trades AS (
        SELECT t.*
        FROM all_trades t
        WHERE (
            v_trade_type = 'all' OR 
            (v_trade_type = 'intraday' AND t.trade_type = 0) OR
            (v_trade_type = 'swing' AND t.trade_type = 1) OR
            (v_trade_type = 'auto' AND t.trade_type = 2)
        )
        AND (
            v_pnl_filter = 'all' OR
            (v_pnl_filter = 'profit' AND t.realized_pnl > 0) OR
            (v_pnl_filter = 'loss' AND t.realized_pnl < 0)
        )
    ),
    counted AS (
        SELECT COUNT(*)::BIGINT AS total_rows FROM filtered_trades
    )
    SELECT 
        COALESCE(c.total_rows, 0::BIGINT) AS total_count,
        f.id,
        f.mode,
        f.symbol,
        f.side,
        f.quantity,
        f.entry_price,
        f.executed_price,
        f.realized_pnl,
        f.trade_type,
        f.exit_reason,
        f.executed_at,
        f.opened_at,
        f.hold_days,
        f.username
    FROM filtered_trades f
    CROSS JOIN counted c
    ORDER BY f.executed_at DESC
    LIMIT v_limit OFFSET v_offset;
END;
$$;





