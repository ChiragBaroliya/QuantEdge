-- ============================================================================
-- QuantEdge Database Stored Functions (PostgreSQL / TimescaleDB)
-- ============================================================================

-- ----------------------------------------------------------------------------
-- Function: sp_get_market_candles
-- Dynamically returns query results from target timeframe table, safely handling non-existent tables.
-- ----------------------------------------------------------------------------
DROP FUNCTION IF EXISTS sp_get_market_candles CASCADE;
DROP FUNCTION IF EXISTS sp_get_market_candles(VARCHAR, VARCHAR, INTEGER) CASCADE;
DROP FUNCTION IF EXISTS sp_get_market_candles(VARCHAR, VARCHAR, INTEGER, TIMESTAMP WITH TIME ZONE) CASCADE;

CREATE OR REPLACE FUNCTION sp_get_market_candles(
    p_symbol VARCHAR(50),
    p_timeframe VARCHAR(20),
    p_limit INTEGER,
    p_before_time TIMESTAMP WITH TIME ZONE DEFAULT NULL
)
RETURNS TABLE (
    id INT,
    candle_time TIMESTAMP WITH TIME ZONE,
    symbol VARCHAR(50),
    timeframe VARCHAR(20),
    open NUMERIC(18, 6),
    high NUMERIC(18, 6),
    low NUMERIC(18, 6),
    close NUMERIC(18, 6),
    volume BIGINT,
    created_at TIMESTAMP WITH TIME ZONE
)
LANGUAGE plpgsql
AS $$
DECLARE
    v_table_name TEXT;
BEGIN
    v_table_name := 'market_candles_' || LOWER(p_timeframe);
    
    -- Check if table exists. If not, return empty result
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.tables 
        WHERE table_schema = 'public' AND table_name = v_table_name
    ) THEN
        RETURN;
    END IF;

    IF p_before_time IS NULL THEN
        RETURN QUERY EXECUTE format('
            SELECT c.id, c.candle_time, c.symbol, c.timeframe, c.open, c.high, c.low, c.close, c.volume, c.created_at
            FROM %I c
            WHERE c.symbol = $1
            ORDER BY c.candle_time DESC
            LIMIT $2;', v_table_name)
        USING p_symbol, p_limit;
    ELSE
        RETURN QUERY EXECUTE format('
            SELECT c.id, c.candle_time, c.symbol, c.timeframe, c.open, c.high, c.low, c.close, c.volume, c.created_at
            FROM %I c
            WHERE c.symbol = $1 AND c.candle_time < $2
            ORDER BY c.candle_time DESC
            LIMIT $3;', v_table_name)
        USING p_symbol, p_before_time, p_limit;
    END IF;
END;
$$;


-- ----------------------------------------------------------------------------
-- Function: sp_get_market_indicators
-- Dynamically returns query results from target timeframe table, safely handling non-existent tables.
-- ----------------------------------------------------------------------------
DROP FUNCTION IF EXISTS sp_get_market_indicators CASCADE;
DROP FUNCTION IF EXISTS sp_get_market_indicators(VARCHAR, VARCHAR, INTEGER) CASCADE;
DROP FUNCTION IF EXISTS sp_get_market_indicators(VARCHAR, VARCHAR, INTEGER, TIMESTAMP WITH TIME ZONE) CASCADE;

CREATE OR REPLACE FUNCTION sp_get_market_indicators(
    p_symbol VARCHAR(50),
    p_timeframe VARCHAR(20),
    p_limit INTEGER,
    p_before_time TIMESTAMP WITH TIME ZONE DEFAULT NULL
)
RETURNS TABLE (
    id INT,
    candle_time TIMESTAMP WITH TIME ZONE,
    symbol VARCHAR(50),
    timeframe VARCHAR(20),
    rsi NUMERIC(18, 6),
    ema20 NUMERIC(18, 6),
    ema50 NUMERIC(18, 6),
    macd NUMERIC(18, 6),
    signal_line NUMERIC(18, 6),
    vwap NUMERIC(18, 6),
    created_at TIMESTAMP WITH TIME ZONE
)
LANGUAGE plpgsql
AS $$
DECLARE
    v_table_name TEXT;
BEGIN
    v_table_name := 'market_indicators_' || LOWER(p_timeframe);
    
    -- Check if table exists. If not, return empty result
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.tables 
        WHERE table_schema = 'public' AND table_name = v_table_name
    ) THEN
        RETURN;
    END IF;

    IF p_before_time IS NULL THEN
        RETURN QUERY EXECUTE format('
            SELECT i.id, i.candle_time, i.symbol, i.timeframe, i.rsi, i.ema20, i.ema50, i.macd, i.signal_line, i.vwap, i.created_at
            FROM %I i
            WHERE i.symbol = $1
            ORDER BY i.candle_time DESC
            LIMIT $2;', v_table_name)
        USING p_symbol, p_limit;
    ELSE
        RETURN QUERY EXECUTE format('
            SELECT i.id, i.candle_time, i.symbol, i.timeframe, i.rsi, i.ema20, i.ema50, i.macd, i.signal_line, i.vwap, i.created_at
            FROM %I i
            WHERE i.symbol = $1 AND i.candle_time < $2
            ORDER BY i.candle_time DESC
            LIMIT $3;', v_table_name)
        USING p_symbol, p_before_time, p_limit;
    END IF;
END;
$$;


-- ----------------------------------------------------------------------------
-- Function: sp_get_recent_trading_signals
-- ----------------------------------------------------------------------------
DROP FUNCTION IF EXISTS sp_get_recent_trading_signals CASCADE;
DROP FUNCTION IF EXISTS sp_get_recent_trading_signals(INTEGER) CASCADE;

CREATE OR REPLACE FUNCTION sp_get_recent_trading_signals(
    p_limit INTEGER
)
RETURNS TABLE (
    id INT,
    candle_time TIMESTAMP WITH TIME ZONE,
    symbol VARCHAR(50),
    signal_type VARCHAR(20),
    signal_strength NUMERIC(5, 2),
    entry_price NUMERIC(18, 6),
    reason VARCHAR(1000),
    created_at TIMESTAMP WITH TIME ZONE
)
LANGUAGE plpgsql
AS $$
BEGIN
    RETURN QUERY
    SELECT s.id, s.candle_time, s.symbol, s.signal_type, s.signal_strength, s.entry_price, s.reason, s.created_at
    FROM trading_signals s
    ORDER BY s.candle_time DESC
    LIMIT p_limit;
END;
$$;


-- ----------------------------------------------------------------------------
-- Function: sp_activate_zerodha_token
-- ----------------------------------------------------------------------------
DROP FUNCTION IF EXISTS sp_activate_zerodha_token CASCADE;
DROP FUNCTION IF EXISTS sp_activate_zerodha_token(VARCHAR) CASCADE;

CREATE OR REPLACE FUNCTION sp_activate_zerodha_token(
    p_api_key VARCHAR(50)
)
RETURNS VARCHAR
LANGUAGE plpgsql
AS $$
DECLARE
    v_cutoff_time     TIMESTAMP WITH TIME ZONE;
    v_token_created   TIMESTAMP WITH TIME ZONE;
    v_access_token    VARCHAR(255);
BEGIN
    -- 6:00 AM IST = 00:30 UTC
    v_cutoff_time := (DATE_TRUNC('day', NOW() AT TIME ZONE 'Asia/Kolkata')
                     + INTERVAL '6 hours')
                     AT TIME ZONE 'Asia/Kolkata';

    SELECT access_token, created_at
    INTO v_access_token, v_token_created
    FROM zerodha_sessions
    WHERE api_key = p_api_key
    LIMIT 1;

    IF v_access_token IS NULL THEN
        RAISE NOTICE 'sp_activate_zerodha_token: No session found for api_key %', p_api_key;
        RETURN NULL;
    END IF;

    IF v_token_created >= v_cutoff_time THEN
        UPDATE zerodha_sessions
        SET is_active = TRUE
        WHERE api_key = p_api_key;

        RAISE NOTICE 'sp_activate_zerodha_token: Token for api_key % activated (created_at: %)', p_api_key, v_token_created;
        RETURN v_access_token;
    ELSE
        RAISE NOTICE 'sp_activate_zerodha_token: Token for api_key % is stale (created_at: %, cutoff: %). Not activating.', p_api_key, v_token_created, v_cutoff_time;
        RETURN NULL;
    END IF;
END;
$$;


-- ----------------------------------------------------------------------------
-- Function: sp_get_active_zerodha_session
-- ----------------------------------------------------------------------------
DROP FUNCTION IF EXISTS sp_get_active_zerodha_session CASCADE;
DROP FUNCTION IF EXISTS sp_get_active_zerodha_session() CASCADE;

CREATE OR REPLACE FUNCTION sp_get_active_zerodha_session()
RETURNS TABLE (
    api_key      VARCHAR(50),
    access_token VARCHAR(255),
    is_active    BOOLEAN,
    created_at   TIMESTAMP WITH TIME ZONE
)
LANGUAGE plpgsql
AS $$
BEGIN
    RETURN QUERY
    SELECT s.api_key, s.access_token, s.is_active, s.created_at
    FROM zerodha_sessions s
    WHERE s.is_active = TRUE
    LIMIT 1;
END;
$$;


-- ----------------------------------------------------------------------------
-- Function: sp_get_active_stocks
-- ----------------------------------------------------------------------------
DROP FUNCTION IF EXISTS sp_get_active_stocks CASCADE;
DROP FUNCTION IF EXISTS sp_get_active_stocks() CASCADE;

CREATE OR REPLACE FUNCTION sp_get_active_stocks()
RETURNS TABLE (
    id INT,
    symbol VARCHAR(50),
    instrument_token INT,
    is_active BOOLEAN,
    exchange_token VARCHAR(50),
    name VARCHAR(100),
    last_price NUMERIC(18, 4),
    expiry TIMESTAMP WITH TIME ZONE,
    strike NUMERIC(18, 4),
    tick_size NUMERIC(18, 4),
    lot_size INT,
    instrument_type VARCHAR(20),
    segment VARCHAR(20),
    exchange VARCHAR(20),
    is_histry_stored_1m INT,
    is_histry_stored_5m INT,
    is_histry_stored_15m INT,
    is_histry_stored_60m INT,
    is_histry_stored_1d INT,
    created_at TIMESTAMP WITH TIME ZONE
)
LANGUAGE plpgsql
AS $$
BEGIN
    RETURN QUERY
    SELECT s.id, s.symbol, s.instrument_token, s.is_active,
           s.exchange_token, s.name, s.last_price, s.expiry,
           s.strike, s.tick_size, s.lot_size, s.instrument_type,
           s.segment, s.exchange, 
           s.is_histry_stored_1m, s.is_histry_stored_5m, s.is_histry_stored_15m, s.is_histry_stored_60m, s.is_histry_stored_1d,
           s.created_at
    FROM stock_master s
    WHERE s.is_active = TRUE;
END;
$$;


-- ----------------------------------------------------------------------------
-- Function: sp_get_stock_by_symbol
-- ----------------------------------------------------------------------------
DROP FUNCTION IF EXISTS sp_get_stock_by_symbol CASCADE;
DROP FUNCTION IF EXISTS sp_get_stock_by_symbol(VARCHAR) CASCADE;

CREATE OR REPLACE FUNCTION sp_get_stock_by_symbol(
    p_symbol VARCHAR(50)
)
RETURNS TABLE (
    id INT,
    symbol VARCHAR(50),
    instrument_token INT,
    is_active BOOLEAN,
    exchange_token VARCHAR(50),
    name VARCHAR(100),
    last_price NUMERIC(18, 4),
    expiry TIMESTAMP WITH TIME ZONE,
    strike NUMERIC(18, 4),
    tick_size NUMERIC(18, 4),
    lot_size INT,
    instrument_type VARCHAR(20),
    segment VARCHAR(20),
    exchange VARCHAR(20),
    is_histry_stored_1m INT,
    is_histry_stored_5m INT,
    is_histry_stored_15m INT,
    is_histry_stored_60m INT,
    is_histry_stored_1d INT,
    created_at TIMESTAMP WITH TIME ZONE
)
LANGUAGE plpgsql
AS $$
BEGIN
    RETURN QUERY
    SELECT s.id, s.symbol, s.instrument_token, s.is_active,
           s.exchange_token, s.name, s.last_price, s.expiry,
           s.strike, s.tick_size, s.lot_size, s.instrument_type,
           s.segment, s.exchange, 
           s.is_histry_stored_1m, s.is_histry_stored_5m, s.is_histry_stored_15m, s.is_histry_stored_60m, s.is_histry_stored_1d,
           s.created_at
    FROM stock_master s
    WHERE UPPER(s.symbol) = UPPER(p_symbol)
    LIMIT 1;
END;
$$;


-- ----------------------------------------------------------------------------
-- Function: sp_upsert_instruments
-- ----------------------------------------------------------------------------
DROP FUNCTION IF EXISTS sp_upsert_instruments CASCADE;
DROP FUNCTION IF EXISTS sp_upsert_instruments(JSONB) CASCADE;

CREATE OR REPLACE FUNCTION sp_upsert_instruments(p_instruments JSONB)
RETURNS VOID
LANGUAGE plpgsql
AS $$
BEGIN
    INSERT INTO stock_master (
        symbol, instrument_token, is_active, exchange_token, name, 
        last_price, expiry, strike, tick_size, lot_size, 
        instrument_type, segment, exchange
    )
    SELECT 
        (rec->>'Symbol')::VARCHAR(50),
        (rec->>'InstrumentToken')::INT,
        (rec->>'IsActive')::BOOLEAN,
        (rec->>'ExchangeToken')::VARCHAR(50),
        (rec->>'Name')::VARCHAR(100),
        (rec->>'LastPrice')::NUMERIC(18, 4),
        CASE 
            WHEN rec->>'Expiry' IS NOT NULL AND (rec->>'Expiry') <> '' 
            THEN (rec->>'Expiry')::TIMESTAMP WITH TIME ZONE 
            ELSE NULL 
        END,
        (rec->>'Strike')::NUMERIC(18, 4),
        (rec->>'TickSize')::NUMERIC(18, 4),
        (rec->>'LotSize')::INT,
        (rec->>'InstrumentType')::VARCHAR(20),
        (rec->>'Segment')::VARCHAR(20),
        (rec->>'Exchange')::VARCHAR(20)
    FROM jsonb_array_elements(p_instruments) AS rec
    ON CONFLICT (symbol) DO NOTHING;
END;
$$;


-- ----------------------------------------------------------------------------
-- Function: sp_get_data_coverage_summary
-- ----------------------------------------------------------------------------
DROP FUNCTION IF EXISTS sp_get_data_coverage_summary CASCADE;
DROP FUNCTION IF EXISTS sp_get_data_coverage_summary() CASCADE;

CREATE OR REPLACE FUNCTION sp_get_data_coverage_summary()
RETURNS TABLE (
    "TotalStocks" INT,
    "ActiveCount" INT,
    "InactiveCount" INT,
    "HistoryMissingCount" INT
)
LANGUAGE plpgsql
AS $$
BEGIN
    RETURN QUERY
    SELECT 
        COUNT(*)::INT AS "TotalStocks",
        COUNT(*) FILTER (WHERE s.is_active = TRUE)::INT AS "ActiveCount",
        COUNT(*) FILTER (WHERE s.is_active = FALSE)::INT AS "InactiveCount",
        COUNT(*) FILTER (WHERE COALESCE(s.is_histry_stored_1m, 0) = 0 
                            OR COALESCE(s.is_histry_stored_5m, 0) = 0 
                            OR COALESCE(s.is_histry_stored_15m, 0) = 0 
                            OR COALESCE(s.is_histry_stored_60m, 0) = 0 
                            OR COALESCE(s.is_histry_stored_1d, 0) = 0)::INT AS "HistoryMissingCount"
    FROM stock_master s;
END;
$$;


-- ----------------------------------------------------------------------------
-- Function: sp_get_paginated_stock_coverage
-- ----------------------------------------------------------------------------
DROP FUNCTION IF EXISTS sp_get_paginated_stock_coverage CASCADE;
DROP FUNCTION IF EXISTS sp_get_paginated_stock_coverage(VARCHAR, VARCHAR, VARCHAR, INT, INT) CASCADE;

CREATE OR REPLACE FUNCTION sp_get_paginated_stock_coverage(
    p_search VARCHAR DEFAULT NULL,
    p_status_filter VARCHAR DEFAULT NULL,
    p_history_filter VARCHAR DEFAULT NULL,
    p_page_number INT DEFAULT 1,
    p_page_size INT DEFAULT 25
)
RETURNS TABLE (
    "Id" INT,
    "Symbol" VARCHAR(50),
    "Name" VARCHAR(100),
    "Exchange" VARCHAR(20),
    "InstrumentToken" INT,
    "IsActive" BOOLEAN,
    "IsHistryStored1m" INT,
    "IsHistryStored5m" INT,
    "IsHistryStored15m" INT,
    "IsHistryStored60m" INT,
    "IsHistryStored1d" INT,
    "CreatedAt" TIMESTAMP WITH TIME ZONE,
    "Count1d" BIGINT,
    "Count60m" BIGINT,
    "LastCandleDate" TIMESTAMP WITH TIME ZONE,
    "TotalRecords" INT
)
LANGUAGE plpgsql
AS $$
DECLARE
    v_offset INT;
BEGIN
    v_offset := (GREATEST(1, p_page_number) - 1) * GREATEST(1, p_page_size);

    RETURN QUERY
    WITH filtered_stocks AS (
        SELECT s.*
        FROM stock_master s
        WHERE 
            (p_search IS NULL OR p_search = '' OR UPPER(s.symbol) LIKE '%' || UPPER(p_search) || '%' OR UPPER(COALESCE(s.name, '')) LIKE '%' || UPPER(p_search) || '%')
            AND (
                p_status_filter IS NULL OR p_status_filter = '' OR LOWER(p_status_filter) = 'all'
                OR (LOWER(p_status_filter) = 'active' AND s.is_active = TRUE)
                OR (LOWER(p_status_filter) = 'inactive' AND s.is_active = FALSE)
            )
            AND (
                p_history_filter IS NULL OR p_history_filter = '' OR LOWER(p_history_filter) = 'all'
                OR (LOWER(p_history_filter) IN ('today_created', 'created_today') AND DATE(s.created_at AT TIME ZONE 'Asia/Kolkata') = (CURRENT_TIMESTAMP AT TIME ZONE 'Asia/Kolkata')::DATE)
                OR (LOWER(p_history_filter) = 'missing' AND (
                    COALESCE(s.is_histry_stored_1m, 0) = 0 
                    OR COALESCE(s.is_histry_stored_5m, 0) = 0 
                    OR COALESCE(s.is_histry_stored_15m, 0) = 0 
                    OR COALESCE(s.is_histry_stored_60m, 0) = 0 
                    OR COALESCE(s.is_histry_stored_1d, 0) = 0
                ))
                OR (LOWER(p_history_filter) = '1m_missing' AND COALESCE(s.is_histry_stored_1m, 0) = 0)
                OR (LOWER(p_history_filter) = '5m_missing' AND COALESCE(s.is_histry_stored_5m, 0) = 0)
                OR (LOWER(p_history_filter) = '15m_missing' AND COALESCE(s.is_histry_stored_15m, 0) = 0)
                OR (LOWER(p_history_filter) = '60m_missing' AND COALESCE(s.is_histry_stored_60m, 0) = 0)
                OR (LOWER(p_history_filter) = '1d_missing' AND COALESCE(s.is_histry_stored_1d, 0) = 0)
                OR (LOWER(p_history_filter) = 'has_1m' AND COALESCE(s.is_histry_stored_1m, 0) = 1)
                OR (LOWER(p_history_filter) = 'has_5m' AND COALESCE(s.is_histry_stored_5m, 0) = 1)
                OR (LOWER(p_history_filter) = 'has_15m' AND COALESCE(s.is_histry_stored_15m, 0) = 1)
                OR (LOWER(p_history_filter) = 'has_60m' AND COALESCE(s.is_histry_stored_60m, 0) = 1)
                OR (LOWER(p_history_filter) = 'has_1d' AND COALESCE(s.is_histry_stored_1d, 0) = 1)
            )
    ),
    counted AS (
        SELECT fs.*, COUNT(*) OVER()::INT AS full_count
        FROM filtered_stocks fs
        ORDER BY fs.symbol ASC
        LIMIT GREATEST(1, p_page_size) OFFSET v_offset
    )
    SELECT 
        c.id AS "Id",
        c.symbol AS "Symbol",
        c.name AS "Name",
        c.exchange AS "Exchange",
        c.instrument_token AS "InstrumentToken",
        c.is_active AS "IsActive",
        c.is_histry_stored_1m AS "IsHistryStored1m",
        c.is_histry_stored_5m AS "IsHistryStored5m",
        c.is_histry_stored_15m AS "IsHistryStored15m",
        c.is_histry_stored_60m AS "IsHistryStored60m",
        c.is_histry_stored_1d AS "IsHistryStored1d",
        c.created_at AS "CreatedAt",
        COALESCE(c.is_histry_stored_1d, 0)::BIGINT AS "Count1d",
        COALESCE(c.is_histry_stored_60m, 0)::BIGINT AS "Count60m",
        (SELECT MAX(candle_time) FROM market_candles_1d c1d WHERE c1d.symbol = c.symbol) AS "LastCandleDate",
        c.full_count AS "TotalRecords"
    FROM counted c
    ORDER BY c.symbol ASC;
END;
$$;


-- ----------------------------------------------------------------------------
-- Function: sp_update_stock_coverage_flags
-- ----------------------------------------------------------------------------
DROP FUNCTION IF EXISTS sp_update_stock_coverage_flags CASCADE;
DROP FUNCTION IF EXISTS sp_update_stock_coverage_flags(INT, BOOLEAN, INT, INT, INT, INT, INT) CASCADE;

CREATE OR REPLACE FUNCTION sp_update_stock_coverage_flags(
    p_id INT,
    p_is_active BOOLEAN,
    p_histry_1m INT,
    p_histry_5m INT,
    p_histry_15m INT,
    p_histry_60m INT,
    p_histry_1d INT
)
RETURNS VOID
LANGUAGE plpgsql
AS $$
BEGIN
    UPDATE stock_master
    SET 
        is_active = p_is_active,
        is_histry_stored_1m = p_histry_1m,
        is_histry_stored_5m = p_histry_5m,
        is_histry_stored_15m = p_histry_15m,
        is_histry_stored_60m = p_histry_60m,
        is_histry_stored_1d = p_histry_1d
    WHERE id = p_id;
END;
$$;


-- ----------------------------------------------------------------------------
-- Function: sp_delete_stock_master
-- ----------------------------------------------------------------------------
DROP FUNCTION IF EXISTS sp_delete_stock_master CASCADE;
DROP FUNCTION IF EXISTS sp_delete_stock_master(INT) CASCADE;

CREATE OR REPLACE FUNCTION sp_delete_stock_master(
    p_id INT
)
RETURNS VOID
LANGUAGE plpgsql
AS $$
BEGIN
    -- Delete associated candles if any
    DELETE FROM market_candles_1d WHERE symbol IN (SELECT symbol FROM stock_master WHERE id = p_id);
    DELETE FROM market_candles_60m WHERE symbol IN (SELECT symbol FROM stock_master WHERE id = p_id);
    DELETE FROM market_candles_15m WHERE symbol IN (SELECT symbol FROM stock_master WHERE id = p_id);
    DELETE FROM market_candles_5m WHERE symbol IN (SELECT symbol FROM stock_master WHERE id = p_id);
    DELETE FROM market_candles_1m WHERE symbol IN (SELECT symbol FROM stock_master WHERE id = p_id);

    -- Delete main record from stock_master
    DELETE FROM stock_master WHERE id = p_id;
END;
$$;


-- ----------------------------------------------------------------------------
-- Function: sp_bulk_delete_stock_master
-- ----------------------------------------------------------------------------
DROP FUNCTION IF EXISTS sp_bulk_delete_stock_master CASCADE;
DROP FUNCTION IF EXISTS sp_bulk_delete_stock_master(INT[]) CASCADE;

CREATE OR REPLACE FUNCTION sp_bulk_delete_stock_master(
    p_ids INT[]
)
RETURNS VOID
LANGUAGE plpgsql
AS $$
BEGIN
    -- Delete associated candles if any
    DELETE FROM market_candles_1d WHERE symbol IN (SELECT symbol FROM stock_master WHERE id = ANY(p_ids));
    DELETE FROM market_candles_60m WHERE symbol IN (SELECT symbol FROM stock_master WHERE id = ANY(p_ids));
    DELETE FROM market_candles_15m WHERE symbol IN (SELECT symbol FROM stock_master WHERE id = ANY(p_ids));
    DELETE FROM market_candles_5m WHERE symbol IN (SELECT symbol FROM stock_master WHERE id = ANY(p_ids));
    DELETE FROM market_candles_1m WHERE symbol IN (SELECT symbol FROM stock_master WHERE id = ANY(p_ids));

    -- Delete main records from stock_master
    DELETE FROM stock_master WHERE id = ANY(p_ids);
END;
$$;


-- ----------------------------------------------------------------------------
-- Function: sp_get_indian_holidays
-- ----------------------------------------------------------------------------
DROP FUNCTION IF EXISTS sp_get_indian_holidays CASCADE;
DROP FUNCTION IF EXISTS sp_get_indian_holidays() CASCADE;

CREATE OR REPLACE FUNCTION sp_get_indian_holidays()
RETURNS TABLE (
    id INT,
    holiday_date TIMESTAMP,
    description VARCHAR(255),
    created_at TIMESTAMP WITH TIME ZONE
)
LANGUAGE plpgsql
AS $$
BEGIN
    RETURN QUERY
    SELECT h.id, h.holiday_date::timestamp, h.description, h.created_at
    FROM indian_holidays h
    ORDER BY h.holiday_date ASC;
END;
$$;


-- ----------------------------------------------------------------------------
-- Function: sp_is_indian_holiday
-- ----------------------------------------------------------------------------
DROP FUNCTION IF EXISTS sp_is_indian_holiday CASCADE;
DROP FUNCTION IF EXISTS sp_is_indian_holiday(DATE) CASCADE;

CREATE OR REPLACE FUNCTION sp_is_indian_holiday(
    p_date DATE
)
RETURNS BOOLEAN
LANGUAGE plpgsql
AS $$
BEGIN
    RETURN EXISTS (
        SELECT 1 FROM indian_holidays WHERE holiday_date = p_date
    );
END;
$$;


-- ----------------------------------------------------------------------------
-- Function: fn_get_user_by_identifier
-- Returns user record matching given email or username.
-- ----------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION fn_get_user_by_identifier(
    p_identifier VARCHAR(255)
)
RETURNS TABLE (
    id INT,
    full_name VARCHAR(150),
    email VARCHAR(255),
    mobile_no VARCHAR(20),
    username VARCHAR(100),
    password_hash VARCHAR(255),
    role VARCHAR(50),
    created_at TIMESTAMP WITH TIME ZONE,
    updated_at TIMESTAMP WITH TIME ZONE
)
LANGUAGE plpgsql
AS $$
BEGIN
    RETURN QUERY
    SELECT u.id, u.full_name, u.email, u.mobile_no, u.username, u.password_hash, u.role, u.created_at, u.updated_at
    FROM app_users u
    WHERE LOWER(u.username) = LOWER(p_identifier) 
       OR (u.email IS NOT NULL AND LOWER(u.email) = LOWER(p_identifier))
    LIMIT 1;
END;
$$;


-- ----------------------------------------------------------------------------
-- Function: fn_get_user_by_id
-- Returns user record matching given user ID.
-- ----------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION fn_get_user_by_id(
    p_id INT
)
RETURNS TABLE (
    id INT,
    full_name VARCHAR(150),
    email VARCHAR(255),
    mobile_no VARCHAR(20),
    username VARCHAR(100),
    password_hash VARCHAR(255),
    role VARCHAR(50),
    created_at TIMESTAMP WITH TIME ZONE,
    updated_at TIMESTAMP WITH TIME ZONE
)
LANGUAGE plpgsql
AS $$
BEGIN
    RETURN QUERY
    SELECT u.id, u.full_name, u.email, u.mobile_no, u.username, u.password_hash, u.role, u.created_at, u.updated_at
    FROM app_users u
    WHERE u.id = p_id;
END;
$$;


-- ----------------------------------------------------------------------------
-- Function: fn_check_email_exists
-- ----------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION fn_check_email_exists(
    p_email VARCHAR(255)
)
RETURNS BOOLEAN
LANGUAGE plpgsql
AS $$
BEGIN
    IF p_email IS NULL OR p_email = '' THEN
        RETURN FALSE;
    END IF;

    RETURN EXISTS (
        SELECT 1 FROM app_users WHERE LOWER(email) = LOWER(p_email)
    );
END;
$$;


-- ----------------------------------------------------------------------------
-- Function: fn_check_username_exists
-- ----------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION fn_check_username_exists(
    p_username VARCHAR(100)
)
RETURNS BOOLEAN
LANGUAGE plpgsql
AS $$
BEGIN
    RETURN EXISTS (
        SELECT 1 FROM app_users WHERE LOWER(username) = LOWER(p_username)
    );
END;
$$;


-- ----------------------------------------------------------------------------
-- Function: fn_register_user
-- Registers a new user and returns the generated user ID.
-- ----------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION fn_register_user(
    p_full_name VARCHAR(150),
    p_email VARCHAR(255),
    p_mobile_no VARCHAR(20),
    p_username VARCHAR(100),
    p_password_hash VARCHAR(255),
    p_role VARCHAR(50) DEFAULT 'User'
)
RETURNS INT
LANGUAGE plpgsql
AS $$
DECLARE
    v_user_id INT;
BEGIN
    INSERT INTO app_users (
        full_name, 
        email, 
        mobile_no, 
        username, 
        password_hash, 
        role, 
        created_at, 
        updated_at
    )
    VALUES (
        p_full_name, 
        NULLIF(LOWER(p_email), ''), 
        NULLIF(p_mobile_no, ''), 
        p_username, 
        p_password_hash, 
        COALESCE(p_role, 'User'), 
        NOW(), 
        NOW()
    )
    RETURNING id INTO v_user_id;

    RETURN v_user_id;
END;
$$;


-- ----------------------------------------------------------------------------
-- Function: fn_update_user_password
-- Updates the password hash of a user.
-- ----------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION fn_update_user_password(
    p_user_id INT,
    p_new_password_hash VARCHAR(255)
)
RETURNS BOOLEAN
LANGUAGE plpgsql
AS $$
BEGIN
    UPDATE app_users
    SET password_hash = p_new_password_hash,
        updated_at = NOW()
    WHERE id = p_user_id;

    RETURN FOUND;
END;
$$;


-- ----------------------------------------------------------------------------
-- Function: fn_has_admin_user
-- ----------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION fn_has_admin_user()
RETURNS BOOLEAN
LANGUAGE plpgsql
AS $$
BEGIN
    RETURN EXISTS (
        SELECT 1 FROM app_users WHERE LOWER(role) = 'admin'
    );
END;
$$;


-- ----------------------------------------------------------------------------
-- Function: sp_get_paginated_users
-- Returns paginated users with filtering on search string and role.
-- ----------------------------------------------------------------------------
DROP FUNCTION IF EXISTS sp_get_paginated_users CASCADE;
DROP FUNCTION IF EXISTS sp_get_paginated_users(VARCHAR, VARCHAR, INT, INT) CASCADE;

CREATE OR REPLACE FUNCTION sp_get_paginated_users(
    p_search VARCHAR DEFAULT NULL,
    p_role_filter VARCHAR DEFAULT NULL,
    p_page_number INT DEFAULT 1,
    p_page_size INT DEFAULT 25
)
RETURNS TABLE (
    "Id" INT,
    "FullName" VARCHAR(150),
    "Email" VARCHAR(255),
    "MobileNo" VARCHAR(20),
    "Username" VARCHAR(100),
    "Role" VARCHAR(50),
    "CreatedAt" TIMESTAMP WITH TIME ZONE,
    "UpdatedAt" TIMESTAMP WITH TIME ZONE,
    "TotalRecords" INT
)
LANGUAGE plpgsql
AS $$
DECLARE
    v_offset INT;
BEGIN
    v_offset := (GREATEST(1, p_page_number) - 1) * GREATEST(1, p_page_size);

    RETURN QUERY
    WITH filtered_users AS (
        SELECT u.*
        FROM app_users u
        WHERE 
            (p_search IS NULL OR p_search = '' 
             OR UPPER(u.username) LIKE '%' || UPPER(p_search) || '%' 
             OR UPPER(u.full_name) LIKE '%' || UPPER(p_search) || '%'
             OR UPPER(COALESCE(u.email, '')) LIKE '%' || UPPER(p_search) || '%'
             OR UPPER(COALESCE(u.mobile_no, '')) LIKE '%' || UPPER(p_search) || '%')
            AND (
                p_role_filter IS NULL OR p_role_filter = '' OR LOWER(p_role_filter) = 'all'
                OR LOWER(u.role) = LOWER(p_role_filter)
            )
    ),
    counted AS (
        SELECT fu.*, COUNT(*) OVER()::INT AS full_count
        FROM filtered_users fu
        ORDER BY fu.id ASC
        LIMIT GREATEST(1, p_page_size) OFFSET v_offset
    )
    SELECT 
        c.id AS "Id",
        c.full_name AS "FullName",
        c.email AS "Email",
        c.mobile_no AS "MobileNo",
        c.username AS "Username",
        c.role AS "Role",
        c.created_at AS "CreatedAt",
        c.updated_at AS "UpdatedAt",
        c.full_count AS "TotalRecords"
    FROM counted c
    ORDER BY c.id ASC;
END;
$$;


-- ----------------------------------------------------------------------------
-- Function: sp_purge_history_by_date
-- Deletes market candles and indicators strictly matching created_at::date = p_target_date
-- and updates stock_master history flags based on remaining data.
-- ----------------------------------------------------------------------------
DROP FUNCTION IF EXISTS sp_purge_history_by_date CASCADE;
DROP FUNCTION IF EXISTS sp_purge_history_by_date(DATE, VARCHAR) CASCADE;

CREATE OR REPLACE FUNCTION sp_purge_history_by_date(
    p_target_date DATE,
    p_symbol VARCHAR DEFAULT NULL
)
RETURNS TABLE (
    "DeletedCandles" BIGINT,
    "DeletedIndicators" BIGINT,
    "AffectedStocks" INT
)
LANGUAGE plpgsql
AS $$
DECLARE
    v_deleted_candles BIGINT := 0;
    v_deleted_indicators BIGINT := 0;
    v_affected_stocks INT := 0;
    v_count BIGINT := 0;
    v_sym VARCHAR(50);
    r_stock RECORD;
BEGIN
    v_sym := NULLIF(TRIM(p_symbol), '');

    -- Loop through active stock symbols (either single specified symbol or ALL active symbols one by one)
    FOR r_stock IN 
        SELECT symbol 
        FROM stock_master 
        WHERE is_active = TRUE 
          AND (v_sym IS NULL OR UPPER(symbol) = UPPER(v_sym))
        ORDER BY symbol ASC
    LOOP
        -- 1. Delete from all market_candles_* tables for current active symbol where created_at::date = p_target_date
        -- market_candles_1m
        EXECUTE 'WITH d AS (DELETE FROM market_candles_1m WHERE UPPER(symbol) = UPPER($2) AND created_at::date = $1 RETURNING 1) SELECT COUNT(*) FROM d' INTO v_count USING p_target_date, r_stock.symbol;
        v_deleted_candles := v_deleted_candles + v_count;

        -- market_candles_5m
        EXECUTE 'WITH d AS (DELETE FROM market_candles_5m WHERE UPPER(symbol) = UPPER($2) AND created_at::date = $1 RETURNING 1) SELECT COUNT(*) FROM d' INTO v_count USING p_target_date, r_stock.symbol;
        v_deleted_candles := v_deleted_candles + v_count;

        -- market_candles_15m
        EXECUTE 'WITH d AS (DELETE FROM market_candles_15m WHERE UPPER(symbol) = UPPER($2) AND created_at::date = $1 RETURNING 1) SELECT COUNT(*) FROM d' INTO v_count USING p_target_date, r_stock.symbol;
        v_deleted_candles := v_deleted_candles + v_count;

        -- market_candles_60m
        EXECUTE 'WITH d AS (DELETE FROM market_candles_60m WHERE UPPER(symbol) = UPPER($2) AND created_at::date = $1 RETURNING 1) SELECT COUNT(*) FROM d' INTO v_count USING p_target_date, r_stock.symbol;
        v_deleted_candles := v_deleted_candles + v_count;

        -- market_candles_1d
        EXECUTE 'WITH d AS (DELETE FROM market_candles_1d WHERE UPPER(symbol) = UPPER($2) AND created_at::date = $1 RETURNING 1) SELECT COUNT(*) FROM d' INTO v_count USING p_target_date, r_stock.symbol;
        v_deleted_candles := v_deleted_candles + v_count;

        -- 2. Delete from all market_indicators_* tables for current active symbol where created_at::date = p_target_date
        -- market_indicators_1m
        EXECUTE 'WITH d AS (DELETE FROM market_indicators_1m WHERE UPPER(symbol) = UPPER($2) AND created_at::date = $1 RETURNING 1) SELECT COUNT(*) FROM d' INTO v_count USING p_target_date, r_stock.symbol;
        v_deleted_indicators := v_deleted_indicators + v_count;

        -- market_indicators_5m
        EXECUTE 'WITH d AS (DELETE FROM market_indicators_5m WHERE UPPER(symbol) = UPPER($2) AND created_at::date = $1 RETURNING 1) SELECT COUNT(*) FROM d' INTO v_count USING p_target_date, r_stock.symbol;
        v_deleted_indicators := v_deleted_indicators + v_count;

        -- market_indicators_15m
        EXECUTE 'WITH d AS (DELETE FROM market_indicators_15m WHERE UPPER(symbol) = UPPER($2) AND created_at::date = $1 RETURNING 1) SELECT COUNT(*) FROM d' INTO v_count USING p_target_date, r_stock.symbol;
        v_deleted_indicators := v_deleted_indicators + v_count;

        -- market_indicators_60m
        EXECUTE 'WITH d AS (DELETE FROM market_indicators_60m WHERE UPPER(symbol) = UPPER($2) AND created_at::date = $1 RETURNING 1) SELECT COUNT(*) FROM d' INTO v_count USING p_target_date, r_stock.symbol;
        v_deleted_indicators := v_deleted_indicators + v_count;

        -- market_indicators_1d
        EXECUTE 'WITH d AS (DELETE FROM market_indicators_1d WHERE UPPER(symbol) = UPPER($2) AND created_at::date = $1 RETURNING 1) SELECT COUNT(*) FROM d' INTO v_count USING p_target_date, r_stock.symbol;
        v_deleted_indicators := v_deleted_indicators + v_count;

        -- 3. Update stock_master history flags for current symbol based on remaining candles
        UPDATE stock_master s
        SET is_histry_stored_1m = CASE WHEN EXISTS (SELECT 1 FROM market_candles_1m c WHERE UPPER(c.symbol) = UPPER(s.symbol)) THEN 1 ELSE 0 END,
            is_histry_stored_5m = CASE WHEN EXISTS (SELECT 1 FROM market_candles_5m c WHERE UPPER(c.symbol) = UPPER(s.symbol)) THEN 1 ELSE 0 END,
            is_histry_stored_15m = CASE WHEN EXISTS (SELECT 1 FROM market_candles_15m c WHERE UPPER(c.symbol) = UPPER(s.symbol)) THEN 1 ELSE 0 END,
            is_histry_stored_60m = CASE WHEN EXISTS (SELECT 1 FROM market_candles_60m c WHERE UPPER(c.symbol) = UPPER(s.symbol)) THEN 1 ELSE 0 END,
            is_histry_stored_1d = CASE WHEN EXISTS (SELECT 1 FROM market_candles_1d c WHERE UPPER(c.symbol) = UPPER(s.symbol)) THEN 1 ELSE 0 END
        WHERE UPPER(s.symbol) = UPPER(r_stock.symbol);

        v_affected_stocks := v_affected_stocks + 1;
    END LOOP;

    RETURN QUERY SELECT v_deleted_candles, v_deleted_indicators, v_affected_stocks;
END;
$$;


-- ----------------------------------------------------------------------------
-- Function: sp_get_candle_timeframe_summary
-- Aggregates timeframe-wise candle counts for stocks between specified dates.
-- ----------------------------------------------------------------------------
DROP FUNCTION IF EXISTS sp_get_candle_timeframe_summary CASCADE;
DROP FUNCTION IF EXISTS sp_get_candle_timeframe_summary(TIMESTAMP WITH TIME ZONE, TIMESTAMP WITH TIME ZONE, VARCHAR) CASCADE;
DROP FUNCTION IF EXISTS sp_get_candle_timeframe_summary(TIMESTAMP WITH TIME ZONE, TIMESTAMP WITH TIME ZONE, VARCHAR, VARCHAR, INT, INT) CASCADE;

CREATE OR REPLACE FUNCTION sp_get_candle_timeframe_summary(
    p_from_date TIMESTAMP WITH TIME ZONE,
    p_to_date TIMESTAMP WITH TIME ZONE,
    p_symbol VARCHAR(50) DEFAULT 'ALL',
    p_timeframe VARCHAR(20) DEFAULT 'ALL',
    p_page INT DEFAULT 1,
    p_page_size INT DEFAULT 25
)
RETURNS TABLE (
    symbol VARCHAR(50),
    stock_name VARCHAR(255),
    candles_1d INT,
    candles_60m INT,
    candles_15m INT,
    candles_5m INT,
    candles_1m INT,
    total_candles INT,
    latest_candle_time TIMESTAMP WITH TIME ZONE,
    total_records BIGINT
)
AS $BODY$
DECLARE
    v_offset INT;
    v_limit INT;
    v_tf VARCHAR(20);
BEGIN
    v_offset := (GREATEST(1, p_page) - 1) * GREATEST(1, p_page_size);
    v_limit := GREATEST(1, p_page_size);
    v_tf := LOWER(COALESCE(p_timeframe, 'ALL'));

    RETURN QUERY
    WITH c_1d AS (
        SELECT c.symbol, COUNT(*)::INT AS count_1d, MAX(c.candle_time) AS max_time
        FROM market_candles_1d c
        WHERE c.candle_time >= p_from_date AND c.candle_time <= p_to_date
        GROUP BY c.symbol
    ),
    c_60m AS (
        SELECT c.symbol, COUNT(*)::INT AS count_60m, MAX(c.candle_time) AS max_time
        FROM market_candles_60m c
        WHERE c.candle_time >= p_from_date AND c.candle_time <= p_to_date
        GROUP BY c.symbol
    ),
    c_15m AS (
        SELECT c.symbol, COUNT(*)::INT AS count_15m, MAX(c.candle_time) AS max_time
        FROM market_candles_15m c
        WHERE c.candle_time >= p_from_date AND c.candle_time <= p_to_date
        GROUP BY c.symbol
    ),
    c_5m AS (
        SELECT c.symbol, COUNT(*)::INT AS count_5m, MAX(c.candle_time) AS max_time
        FROM market_candles_5m c
        WHERE c.candle_time >= p_from_date AND c.candle_time <= p_to_date
        GROUP BY c.symbol
    ),
    c_1m AS (
        SELECT c.symbol, COUNT(*)::INT AS count_1m, MAX(c.candle_time) AS max_time
        FROM market_candles_1m c
        WHERE c.candle_time >= p_from_date AND c.candle_time <= p_to_date
        GROUP BY c.symbol
    ),
    all_summary AS (
        SELECT 
            s.symbol::VARCHAR(50) AS symbol,
            COALESCE(s.name, s.symbol)::VARCHAR(255) AS stock_name,
            COALESCE(d.count_1d, 0)::INT AS candles_1d,
            COALESCE(h.count_60m, 0)::INT AS candles_60m,
            COALESCE(m15.count_15m, 0)::INT AS candles_15m,
            COALESCE(m5.count_5m, 0)::INT AS candles_5m,
            COALESCE(m1.count_1m, 0)::INT AS candles_1m,
            (COALESCE(d.count_1d, 0) + COALESCE(h.count_60m, 0) + COALESCE(m15.count_15m, 0) + COALESCE(m5.count_5m, 0) + COALESCE(m1.count_1m, 0))::INT AS total_candles,
            GREATEST(d.max_time, h.max_time, m15.max_time, m5.max_time, m1.max_time) AS latest_candle_time
        FROM stock_master s
        LEFT JOIN c_1d d ON s.symbol = d.symbol
        LEFT JOIN c_60m h ON s.symbol = h.symbol
        LEFT JOIN c_15m m15 ON s.symbol = m15.symbol
        LEFT JOIN c_5m m5 ON s.symbol = m5.symbol
        LEFT JOIN c_1m m1 ON s.symbol = m1.symbol
        WHERE s.is_active = TRUE
          AND (p_symbol IS NULL OR p_symbol = '' OR p_symbol = 'ALL' OR UPPER(s.symbol) = UPPER(p_symbol))
    ),
    filtered_summary AS (
        SELECT 
            a.*,
            COUNT(*) OVER() AS total_records
        FROM all_summary a
        WHERE (v_tf = 'all' OR v_tf = '')
           OR (v_tf = '1d' AND a.candles_1d > 0)
           OR (v_tf = '60m' AND a.candles_60m > 0)
           OR (v_tf = '15m' AND a.candles_15m > 0)
           OR (v_tf = '5m' AND a.candles_5m > 0)
           OR (v_tf = '1m' AND a.candles_1m > 0)
    )
    SELECT 
        f.symbol,
        f.stock_name,
        f.candles_1d,
        f.candles_60m,
        f.candles_15m,
        f.candles_5m,
        f.candles_1m,
        f.total_candles,
        f.latest_candle_time,
        f.total_records
    FROM filtered_summary f
    ORDER BY f.total_candles DESC, f.symbol ASC
    LIMIT v_limit OFFSET v_offset;
END;
$BODY$
LANGUAGE plpgsql;


-- ----------------------------------------------------------------------------
-- Function: fn_get_paper_trade_history_paged
-- Returns database-side paged paper trade execution history with multi-column filtering.
-- ----------------------------------------------------------------------------
DROP FUNCTION IF EXISTS fn_get_paper_trade_history_paged CASCADE;

CREATE OR REPLACE FUNCTION fn_get_paper_trade_history_paged(
    p_account_id INT,
    p_symbol VARCHAR DEFAULT NULL,
    p_side INT DEFAULT NULL,
    p_from_date TIMESTAMP WITH TIME ZONE DEFAULT NULL,
    p_to_date TIMESTAMP WITH TIME ZONE DEFAULT NULL,
    p_page_size INT DEFAULT 10,
    p_offset INT DEFAULT 0
)
RETURNS TABLE (
    Id INT,
    AccountId INT,
    OrderId INT,
    Symbol VARCHAR,
    Side INT,
    Quantity INT,
    EntryPrice NUMERIC,
    ExecutedPrice NUMERIC,
    RealizedPnl NUMERIC,
    TradeType INT,
    ExitReason VARCHAR,
    ExecutedAt TIMESTAMP WITH TIME ZONE,
    Remarks VARCHAR,
    TotalCount BIGINT
) 
LANGUAGE plpgsql
AS $$
BEGIN
    RETURN QUERY
    WITH deduplicated_history AS (
        SELECT DISTINCT ON (h.account_id, h.symbol, h.side, h.quantity, h.executed_price, date_trunc('second', h.executed_at))
            h.id AS Id,
            h.account_id AS AccountId,
            h.order_id AS OrderId,
            h.symbol AS Symbol,
            h.side AS Side,
            h.quantity AS Quantity,
            COALESCE(h.entry_price, 0.00) AS EntryPrice,
            h.executed_price AS ExecutedPrice,
            h.realized_pnl AS RealizedPnl,
            h.trade_type AS TradeType,
            h.exit_reason AS ExitReason,
            h.executed_at AS ExecutedAt,
            h.remarks AS Remarks
        FROM paper_trade_history h
        WHERE h.account_id = p_account_id
          AND (p_symbol IS NULL OR p_symbol = '' OR UPPER(h.symbol) = UPPER(p_symbol))
          AND (p_side IS NULL OR h.side = p_side)
          AND (p_from_date IS NULL OR h.executed_at >= p_from_date)
          AND (p_to_date IS NULL OR h.executed_at <= p_to_date)
        ORDER BY h.account_id, h.symbol, h.side, h.quantity, h.executed_price, date_trunc('second', h.executed_at), h.id ASC
    ),
    paged_result AS (
        SELECT 
            dh.Id,
            dh.AccountId,
            dh.OrderId,
            dh.Symbol,
            dh.Side,
            dh.Quantity,
            dh.EntryPrice,
            dh.ExecutedPrice,
            dh.RealizedPnl,
            dh.TradeType,
            dh.ExitReason,
            dh.ExecutedAt,
            dh.Remarks,
            COUNT(*) OVER() AS TotalCount
        FROM deduplicated_history dh
    )
    SELECT *
    FROM paged_result p
    ORDER BY p.ExecutedAt DESC, p.Id DESC
    LIMIT p_page_size OFFSET p_offset;
END;
$$;


-- ----------------------------------------------------------------------------
-- Function: fn_get_paper_orders
-- Returns active/pending paper orders with created_at and filled_at converted to IST.
-- ----------------------------------------------------------------------------
DROP FUNCTION IF EXISTS fn_get_paper_orders CASCADE;

CREATE OR REPLACE FUNCTION fn_get_paper_orders(
    p_account_id INT,
    p_active_only BOOLEAN DEFAULT FALSE
)
RETURNS TABLE (
    Id INT,
    AccountId INT,
    Symbol VARCHAR,
    OrderType INT,
    Side INT,
    Quantity INT,
    Price NUMERIC,
    TriggerPrice NUMERIC,
    StopLoss NUMERIC,
    TakeProfit NUMERIC,
    Status INT,
    FilledPrice NUMERIC,
    FilledAt TIMESTAMP,
    TradeType INT,
    CreatedAt TIMESTAMP,
    Remarks VARCHAR
) 
LANGUAGE plpgsql
AS $$
BEGIN
    RETURN QUERY
    SELECT 
        o.id AS Id,
        o.account_id AS AccountId,
        o.symbol AS Symbol,
        o.order_type AS OrderType,
        o.side AS Side,
        o.quantity AS Quantity,
        o.price AS Price,
        o.trigger_price AS TriggerPrice,
        o.stop_loss AS StopLoss,
        o.take_profit AS TakeProfit,
        o.status AS Status,
        o.filled_price AS FilledPrice,
        (o.filled_at AT TIME ZONE 'Asia/Kolkata')::TIMESTAMP AS FilledAt,
        o.trade_type AS TradeType,
        (o.created_at AT TIME ZONE 'Asia/Kolkata')::TIMESTAMP AS CreatedAt,
        o.remarks AS Remarks
    FROM paper_orders o
    WHERE o.account_id = p_account_id
      AND (p_active_only = FALSE OR o.status = 0)
    ORDER BY o.created_at DESC;
END;
$$;


-- ----------------------------------------------------------------------------
-- Function: fn_upsert_auto_trade_settings
-- Upserts auto trade settings for a user and returns the updated record.
-- ----------------------------------------------------------------------------
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
    p_trading_window_end VARCHAR
)
RETURNS TABLE (
    Id INT,
    UserId VARCHAR,
    IsAutoTradeEnabled BOOLEAN,
    AvailableCapital NUMERIC,
    ProfitTargetPct NUMERIC,
    StopLossPct NUMERIC,
    MaxDurationDays INT,
    MaxTradesPerDay INT,
    FixedAmountPerTrade NUMERIC,
    MinConditionsMatch INT,
    TradingWindowStart VARCHAR,
    TradingWindowEnd VARCHAR,
    UpdatedAt TIMESTAMP WITH TIME ZONE
)
LANGUAGE plpgsql
AS $$
BEGIN
    RETURN QUERY
    INSERT INTO auto_trade_settings (
        user_id, is_auto_trade_enabled, available_capital, profit_target_pct, stop_loss_pct,
        max_duration_days, max_trades_per_day, fixed_amount_per_trade, min_conditions_match,
        trading_window_start, trading_window_end, updated_at
    )
    VALUES (
        p_user_id, p_is_auto_trade_enabled, p_available_capital, p_profit_target_pct, p_stop_loss_pct,
        p_max_duration_days, p_max_trades_per_day, p_fixed_amount_per_trade, p_min_conditions_match,
        p_trading_window_start, p_trading_window_end, NOW()
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
        updated_at = NOW()
    RETURNING 
        auto_trade_settings.id AS Id,
        auto_trade_settings.user_id AS UserId,
        auto_trade_settings.is_auto_trade_enabled AS IsAutoTradeEnabled,
        auto_trade_settings.available_capital AS AvailableCapital,
        auto_trade_settings.profit_target_pct AS ProfitTargetPct,
        auto_trade_settings.stop_loss_pct AS StopLossPct,
        auto_trade_settings.max_duration_days AS MaxDurationDays,
        auto_trade_settings.max_trades_per_day AS MaxTradesPerDay,
        auto_trade_settings.fixed_amount_per_trade AS FixedAmountPerTrade,
        auto_trade_settings.min_conditions_match AS MinConditionsMatch,
        auto_trade_settings.trading_window_start AS TradingWindowStart,
        auto_trade_settings.trading_window_end AS TradingWindowEnd,
        auto_trade_settings.updated_at AS UpdatedAt;
END;
$$;


-- ----------------------------------------------------------------------------
-- Function: fn_get_auto_trade_settings
-- ----------------------------------------------------------------------------
DROP FUNCTION IF EXISTS fn_get_auto_trade_settings(VARCHAR);

CREATE OR REPLACE FUNCTION fn_get_auto_trade_settings(p_user_id VARCHAR)
RETURNS TABLE (
    Id INT,
    UserId VARCHAR,
    IsAutoTradeEnabled BOOLEAN,
    AvailableCapital NUMERIC,
    ProfitTargetPct NUMERIC,
    StopLossPct NUMERIC,
    MaxDurationDays INT,
    MaxTradesPerDay INT,
    FixedAmountPerTrade NUMERIC,
    MinConditionsMatch INT,
    TradingWindowStart VARCHAR,
    TradingWindowEnd VARCHAR,
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
        s.max_duration_days AS MaxDurationDays,
        s.max_trades_per_day AS MaxTradesPerDay,
        s.fixed_amount_per_trade AS FixedAmountPerTrade,
        s.min_conditions_match AS MinConditionsMatch,
        s.trading_window_start AS TradingWindowStart,
        s.trading_window_end AS TradingWindowEnd,
        s.updated_at AS UpdatedAt
    FROM auto_trade_settings s
    WHERE s.user_id = p_user_id;
END;
$$;


-- ----------------------------------------------------------------------------
-- Function: fn_get_active_auto_trade_settings
-- ----------------------------------------------------------------------------
DROP FUNCTION IF EXISTS fn_get_active_auto_trade_settings();

CREATE OR REPLACE FUNCTION fn_get_active_auto_trade_settings()
RETURNS TABLE (
    Id INT,
    UserId VARCHAR,
    IsAutoTradeEnabled BOOLEAN,
    AvailableCapital NUMERIC,
    ProfitTargetPct NUMERIC,
    StopLossPct NUMERIC,
    MaxDurationDays INT,
    MaxTradesPerDay INT,
    FixedAmountPerTrade NUMERIC,
    MinConditionsMatch INT,
    TradingWindowStart VARCHAR,
    TradingWindowEnd VARCHAR,
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
        s.max_duration_days AS MaxDurationDays,
        s.max_trades_per_day AS MaxTradesPerDay,
        s.fixed_amount_per_trade AS FixedAmountPerTrade,
        s.min_conditions_match AS MinConditionsMatch,
        s.trading_window_start AS TradingWindowStart,
        s.trading_window_end AS TradingWindowEnd,
        s.updated_at AS UpdatedAt
    FROM auto_trade_settings s
    WHERE s.is_auto_trade_enabled = TRUE;
END;
$$;


-- ----------------------------------------------------------------------------
-- Function: fn_toggle_auto_trade
-- ----------------------------------------------------------------------------
DROP FUNCTION IF EXISTS fn_toggle_auto_trade(VARCHAR, BOOLEAN);

CREATE OR REPLACE FUNCTION fn_toggle_auto_trade(p_user_id VARCHAR, p_enabled BOOLEAN)
RETURNS VOID
LANGUAGE plpgsql
AS $$
BEGIN
    UPDATE auto_trade_settings
    SET is_auto_trade_enabled = p_enabled,
        updated_at = NOW()
    WHERE user_id = p_user_id;
END;
$$;


-- ----------------------------------------------------------------------------
-- Function: fn_get_today_auto_trade_count
-- ----------------------------------------------------------------------------
DROP FUNCTION IF EXISTS fn_get_today_auto_trade_count(VARCHAR, TIMESTAMP WITH TIME ZONE);

CREATE OR REPLACE FUNCTION fn_get_today_auto_trade_count(p_user_id VARCHAR, p_today_start TIMESTAMP WITH TIME ZONE)
RETURNS INT
LANGUAGE plpgsql
AS $$
DECLARE
    v_count INT;
BEGIN
    SELECT COUNT(*) INTO v_count
    FROM auto_trade_execution_logs
    WHERE user_id = p_user_id
      AND action_type = 'AUTO_BUY'
      AND executed_at >= p_today_start;

    RETURN v_count;
END;
$$;


-- ----------------------------------------------------------------------------
-- Function: fn_log_auto_trade_execution
-- ----------------------------------------------------------------------------
DROP FUNCTION IF EXISTS fn_log_auto_trade_execution(VARCHAR, VARCHAR, VARCHAR, NUMERIC, INT, VARCHAR);

CREATE OR REPLACE FUNCTION fn_log_auto_trade_execution(
    p_user_id VARCHAR,
    p_symbol VARCHAR,
    p_action_type VARCHAR,
    p_price NUMERIC,
    p_quantity INT,
    p_reason VARCHAR
)
RETURNS VOID
LANGUAGE plpgsql
AS $$
BEGIN
    INSERT INTO auto_trade_execution_logs (user_id, symbol, action_type, price, quantity, reason, executed_at)
    VALUES (p_user_id, p_symbol, p_action_type, p_price, p_quantity, p_reason, NOW());
END;
$$;


-- ----------------------------------------------------------------------------
-- Function: fn_get_today_auto_trade_logs
-- ----------------------------------------------------------------------------
DROP FUNCTION IF EXISTS fn_get_today_auto_trade_logs(VARCHAR, TIMESTAMP WITH TIME ZONE, INT);

CREATE OR REPLACE FUNCTION fn_get_today_auto_trade_logs(
    p_user_id VARCHAR,
    p_today_start TIMESTAMP WITH TIME ZONE,
    p_limit INT
)
RETURNS TABLE (
    Id INT,
    UserId VARCHAR,
    Symbol VARCHAR,
    ActionType VARCHAR,
    Price NUMERIC,
    Quantity INT,
    Reason VARCHAR,
    ExecutedAt TIMESTAMP WITH TIME ZONE
)
LANGUAGE plpgsql
AS $$
BEGIN
    RETURN QUERY
    SELECT 
        l.id AS Id,
        l.user_id AS UserId,
        l.symbol AS Symbol,
        l.action_type AS ActionType,
        l.price AS Price,
        l.quantity AS Quantity,
        l.reason AS Reason,
        l.executed_at AS ExecutedAt
    FROM auto_trade_execution_logs l
    WHERE l.user_id = p_user_id AND l.executed_at >= p_today_start
    ORDER BY l.executed_at DESC
    LIMIT p_limit;
END;
$$;



