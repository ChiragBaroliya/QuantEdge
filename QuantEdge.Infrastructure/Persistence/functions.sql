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
-- ----------------------------------------------------------------------------
-- Function: sp_activate_zerodha_token
-- ----------------------------------------------------------------------------
DROP FUNCTION IF EXISTS sp_activate_zerodha_token CASCADE;
DROP FUNCTION IF EXISTS sp_activate_zerodha_token(VARCHAR) CASCADE;
DROP FUNCTION IF EXISTS sp_activate_zerodha_token(VARCHAR, INT) CASCADE;

CREATE OR REPLACE FUNCTION sp_activate_zerodha_token(
    p_api_key VARCHAR(50),
    p_user_id INT DEFAULT 1
)
RETURNS VARCHAR
LANGUAGE plpgsql
AS $$
DECLARE
    v_cutoff_time     TIMESTAMP WITH TIME ZONE;
    v_token_created   TIMESTAMP WITH TIME ZONE;
    v_access_token    VARCHAR(255);
BEGIN
    -- 6:00 AM IST
    v_cutoff_time := (DATE_TRUNC('day', NOW() AT TIME ZONE 'Asia/Kolkata')
                     + INTERVAL '6 hours')
                     AT TIME ZONE 'Asia/Kolkata';

    SELECT access_token, created_at
    INTO v_access_token, v_token_created
    FROM zerodha_sessions
    WHERE api_key = p_api_key AND user_id = p_user_id
    ORDER BY created_at DESC
    LIMIT 1;

    IF v_access_token IS NULL THEN
        RAISE NOTICE 'sp_activate_zerodha_token: No session found for api_key % and user %', p_api_key, p_user_id;
        RETURN NULL;
    END IF;

    IF v_token_created >= v_cutoff_time THEN
        UPDATE zerodha_sessions
        SET is_active = TRUE
        WHERE api_key = p_api_key AND user_id = p_user_id;

        RAISE NOTICE 'sp_activate_zerodha_token: Token for user % activated (created_at: %)', p_user_id, v_token_created;
        RETURN v_access_token;
    ELSE
        RAISE NOTICE 'sp_activate_zerodha_token: Token for user % is stale (created_at: %, cutoff: %). Not activating.', p_user_id, v_token_created, v_cutoff_time;
        RETURN NULL;
    END IF;
END;
$$;


-- ----------------------------------------------------------------------------
-- Function: fn_get_active_zerodha_session
-- ----------------------------------------------------------------------------
DROP FUNCTION IF EXISTS fn_get_active_zerodha_session CASCADE;
DROP FUNCTION IF EXISTS fn_get_active_zerodha_session() CASCADE;
DROP FUNCTION IF EXISTS fn_get_active_zerodha_session(INT) CASCADE;
DROP FUNCTION IF EXISTS sp_get_active_zerodha_session CASCADE;
DROP FUNCTION IF EXISTS sp_get_active_zerodha_session() CASCADE;

CREATE OR REPLACE FUNCTION fn_get_active_zerodha_session(
    p_user_id INT DEFAULT 1
)
RETURNS TABLE (
    user_id         INT,
    client_id       VARCHAR(50),
    user_name       VARCHAR(100),
    user_email      VARCHAR(100),
    api_key         VARCHAR(50),
    api_secret      VARCHAR(100),
    access_token    VARCHAR(255),
    is_active       BOOLEAN,
    is_ddpi_enabled BOOLEAN,
    created_at      TIMESTAMP WITH TIME ZONE
)
LANGUAGE plpgsql
AS $$
BEGIN
    RETURN QUERY
    SELECT COALESCE(s.user_id, p_user_id), s.client_id, s.user_name, s.user_email, s.api_key, s.api_secret, s.access_token, s.is_active, COALESCE(s.is_ddpi_enabled, FALSE), s.created_at
    FROM zerodha_sessions s
    WHERE s.user_id = p_user_id
    ORDER BY s.is_active DESC, s.created_at DESC
    LIMIT 1;
END;
$$;


-- ----------------------------------------------------------------------------
-- Function: fn_get_all_active_zerodha_sessions
-- ----------------------------------------------------------------------------
DROP FUNCTION IF EXISTS fn_get_all_active_zerodha_sessions CASCADE;
DROP FUNCTION IF EXISTS fn_get_all_active_zerodha_sessions() CASCADE;

CREATE OR REPLACE FUNCTION fn_get_all_active_zerodha_sessions()
RETURNS TABLE (
    user_id         INT,
    client_id       VARCHAR(50),
    user_name       VARCHAR(100),
    user_email      VARCHAR(100),
    api_key         VARCHAR(50),
    api_secret      VARCHAR(100),
    access_token    VARCHAR(255),
    is_active       BOOLEAN,
    is_ddpi_enabled BOOLEAN,
    created_at      TIMESTAMP WITH TIME ZONE
)
LANGUAGE plpgsql
AS $$
BEGIN
    RETURN QUERY
    SELECT COALESCE(s.user_id, 1), s.client_id, s.user_name, s.user_email, s.api_key, s.api_secret, s.access_token, s.is_active, COALESCE(s.is_ddpi_enabled, FALSE), s.created_at
    FROM zerodha_sessions s
    WHERE s.is_active = TRUE
    ORDER BY s.created_at DESC;
END;
$$;


-- ----------------------------------------------------------------------------
-- Procedure: sp_upsert_user_zerodha_session
-- ----------------------------------------------------------------------------
DROP PROCEDURE IF EXISTS sp_upsert_user_zerodha_session CASCADE;
DROP PROCEDURE IF EXISTS sp_upsert_user_zerodha_session(INT, VARCHAR, VARCHAR, VARCHAR) CASCADE;
DROP PROCEDURE IF EXISTS sp_upsert_user_zerodha_session(INT, VARCHAR, VARCHAR, VARCHAR, VARCHAR, VARCHAR, VARCHAR) CASCADE;

CREATE OR REPLACE PROCEDURE sp_upsert_user_zerodha_session(
    p_user_id INT,
    p_client_id VARCHAR(50),
    p_user_name VARCHAR(100),
    p_user_email VARCHAR(100),
    p_api_key VARCHAR(50),
    p_api_secret VARCHAR(100),
    p_access_token VARCHAR(255)
)
LANGUAGE plpgsql
AS $$
BEGIN
    INSERT INTO zerodha_sessions (user_id, client_id, user_name, user_email, api_key, api_secret, access_token, is_active, is_ddpi_enabled, created_at)
    VALUES (p_user_id, p_client_id, p_user_name, p_user_email, p_api_key, p_api_secret, p_access_token, TRUE, FALSE, NOW())
    ON CONFLICT (api_key) 
    DO UPDATE SET 
        user_id = EXCLUDED.user_id,
        client_id = COALESCE(EXCLUDED.client_id, zerodha_sessions.client_id),
        user_name = COALESCE(EXCLUDED.user_name, zerodha_sessions.user_name),
        user_email = COALESCE(EXCLUDED.user_email, zerodha_sessions.user_email),
        api_secret = COALESCE(EXCLUDED.api_secret, zerodha_sessions.api_secret),
        access_token = EXCLUDED.access_token,
        is_active = TRUE,
        created_at = NOW();
END;
$$;


-- ----------------------------------------------------------------------------
-- Procedure: sp_update_user_ddpi_status
-- ----------------------------------------------------------------------------
DROP PROCEDURE IF EXISTS sp_update_user_ddpi_status CASCADE;
DROP PROCEDURE IF EXISTS sp_update_user_ddpi_status(INT, BOOLEAN) CASCADE;

CREATE OR REPLACE PROCEDURE sp_update_user_ddpi_status(
    p_user_id INT,
    p_is_ddpi_enabled BOOLEAN
)
LANGUAGE plpgsql
AS $$
BEGIN
    UPDATE zerodha_sessions
    SET is_ddpi_enabled = p_is_ddpi_enabled
    WHERE user_id = p_user_id;
END;
$$;


-- ----------------------------------------------------------------------------
-- Function: fn_get_all_open_real_positions
-- ----------------------------------------------------------------------------
DROP FUNCTION IF EXISTS fn_get_all_open_real_positions CASCADE;
DROP FUNCTION IF EXISTS fn_get_all_open_real_positions() CASCADE;

CREATE OR REPLACE FUNCTION fn_get_all_open_real_positions()
RETURNS TABLE (
    id INT,
    user_id INT,
    symbol VARCHAR(50),
    side INT,
    quantity INT,
    average_entry_price NUMERIC(18, 4),
    current_price NUMERIC(18, 4),
    unrealized_pnl NUMERIC(18, 4),
    stop_loss NUMERIC(18, 4),
    take_profit NUMERIC(18, 4),
    trailing_stop_loss NUMERIC(18, 4),
    status INT,
    trade_type INT,
    exit_reason VARCHAR(255),
    realized_pnl NUMERIC(18, 4),
    opened_at TIMESTAMP WITH TIME ZONE,
    closed_at TIMESTAMP WITH TIME ZONE
)
LANGUAGE plpgsql
AS $$
BEGIN
    RETURN QUERY
    SELECT 
        p.id,
        p.user_id,
        p.symbol,
        p.side,
        p.quantity,
        p.average_entry_price,
        p.current_price,
        p.unrealized_pnl,
        p.stop_loss,
        p.take_profit,
        p.trailing_stop_loss,
        p.status,
        p.trade_type,
        p.exit_reason,
        p.realized_pnl,
        p.opened_at,
        p.closed_at
    FROM real_positions p
    WHERE p.status = 0 -- OPEN
    ORDER BY p.opened_at DESC;
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
DROP FUNCTION IF EXISTS sp_get_paginated_stock_coverage(VARCHAR, VARCHAR, VARCHAR, VARCHAR, INT, INT) CASCADE;

CREATE OR REPLACE FUNCTION sp_get_paginated_stock_coverage(
    p_search VARCHAR DEFAULT NULL,
    p_status_filter VARCHAR DEFAULT NULL,
    p_history_filter VARCHAR DEFAULT NULL,
    p_alphabet_filter VARCHAR DEFAULT NULL,
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
            AND (
                p_alphabet_filter IS NULL OR p_alphabet_filter = '' OR LOWER(p_alphabet_filter) = 'all'
                OR (p_alphabet_filter = '0-9' AND s.symbol ~ '^[0-9]')
                OR (UPPER(s.symbol) LIKE UPPER(p_alphabet_filter) || '%')
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


-- ----------------------------------------------------------------------------
-- 7. Auto Real Trading Functions (Live Broker Money)
-- ----------------------------------------------------------------------------

-- Function: fn_get_real_trade_settings
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
        s.updated_at AS UpdatedAt
    FROM real_trade_settings s
    WHERE s.user_id = p_user_id;
END;
$$;


-- Function: fn_get_active_real_trade_settings
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
        s.updated_at AS UpdatedAt
    FROM real_trade_settings s
    WHERE s.is_real_trade_enabled = TRUE;
END;
$$;


-- Function: fn_upsert_real_trade_settings
DROP FUNCTION IF EXISTS fn_upsert_real_trade_settings(INT, BOOLEAN, NUMERIC, NUMERIC, NUMERIC, BOOLEAN, NUMERIC, INT, INT, NUMERIC, NUMERIC, VARCHAR, INT, VARCHAR, VARCHAR);
DROP FUNCTION IF EXISTS fn_upsert_real_trade_settings(VARCHAR, BOOLEAN, NUMERIC, NUMERIC, NUMERIC, BOOLEAN, NUMERIC, INT, INT, NUMERIC, NUMERIC, VARCHAR, INT, VARCHAR, VARCHAR);

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
    p_trading_window_end VARCHAR
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
        min_conditions_match, trading_window_start, trading_window_end, updated_at
    )
    VALUES (
        p_user_id, p_is_real_trade_enabled, p_available_capital, p_profit_target_pct,
        p_stop_loss_pct, p_trailing_sl_enabled, p_trailing_sl_pct, p_max_duration_days,
        p_max_trades_per_day, p_fixed_amount_per_trade, p_max_daily_loss_limit, p_product_type,
        p_min_conditions_match, p_trading_window_start, p_trading_window_end, NOW()
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
        real_trade_settings.updated_at AS UpdatedAt;
END;
$$;


-- Function: fn_toggle_real_trade
DROP FUNCTION IF EXISTS fn_toggle_real_trade(INT, BOOLEAN);
DROP FUNCTION IF EXISTS fn_toggle_real_trade(VARCHAR, BOOLEAN);

CREATE OR REPLACE FUNCTION fn_toggle_real_trade(p_user_id INT, p_enabled BOOLEAN)
RETURNS VOID
LANGUAGE plpgsql
AS $$
BEGIN
    INSERT INTO real_trade_settings (user_id, is_real_trade_enabled, updated_at)
    VALUES (p_user_id, p_enabled, NOW())
    ON CONFLICT (user_id) DO UPDATE SET
        is_real_trade_enabled = p_enabled,
        updated_at = NOW();
END;
$$;


-- Function: fn_create_real_order
DROP FUNCTION IF EXISTS fn_create_real_order(INT, VARCHAR, VARCHAR, INT, INT, INT, NUMERIC, NUMERIC, NUMERIC, INT, NUMERIC, TIMESTAMP WITH TIME ZONE, VARCHAR, INT, VARCHAR);
DROP FUNCTION IF EXISTS fn_create_real_order(VARCHAR, VARCHAR, VARCHAR, INT, INT, INT, NUMERIC, NUMERIC, NUMERIC, INT, NUMERIC, TIMESTAMP WITH TIME ZONE, VARCHAR, INT, VARCHAR);

CREATE OR REPLACE FUNCTION fn_create_real_order(
    p_user_id INT,
    p_broker_order_id VARCHAR,
    p_symbol VARCHAR,
    p_side INT,
    p_quantity INT,
    p_order_type INT,
    p_price NUMERIC,
    p_stop_loss NUMERIC,
    p_take_profit NUMERIC,
    p_status INT,
    p_filled_price NUMERIC,
    p_filled_at TIMESTAMP WITH TIME ZONE,
    p_rejection_reason VARCHAR,
    p_trade_type INT,
    p_remarks VARCHAR
)
RETURNS TABLE (
    Id INT,
    UserId INT,
    BrokerOrderId VARCHAR,
    Symbol VARCHAR,
    Side INT,
    Quantity INT,
    OrderType INT,
    Price NUMERIC,
    StopLoss NUMERIC,
    TakeProfit NUMERIC,
    Status INT,
    FilledPrice NUMERIC,
    FilledAt TIMESTAMP WITH TIME ZONE,
    RejectionReason VARCHAR,
    TradeType INT,
    Remarks VARCHAR,
    CreatedAt TIMESTAMP WITH TIME ZONE
)
LANGUAGE plpgsql
AS $$
BEGIN
    RETURN QUERY
    INSERT INTO real_orders (
        user_id, broker_order_id, symbol, side, quantity, order_type,
        price, stop_loss, take_profit, status, filled_price, filled_at,
        rejection_reason, trade_type, remarks, created_at
    ) VALUES (
        p_user_id, p_broker_order_id, p_symbol, p_side, p_quantity, p_order_type,
        p_price, p_stop_loss, p_take_profit, p_status, p_filled_price, p_filled_at,
        p_rejection_reason, p_trade_type, p_remarks, NOW()
    )
    RETURNING 
        real_orders.id AS Id,
        real_orders.user_id AS UserId,
        real_orders.broker_order_id AS BrokerOrderId,
        real_orders.symbol AS Symbol,
        real_orders.side AS Side,
        real_orders.quantity AS Quantity,
        real_orders.order_type AS OrderType,
        real_orders.price AS Price,
        real_orders.stop_loss AS StopLoss,
        real_orders.take_profit AS TakeProfit,
        real_orders.status AS Status,
        real_orders.filled_price AS FilledPrice,
        real_orders.filled_at AS FilledAt,
        real_orders.rejection_reason AS RejectionReason,
        real_orders.trade_type AS TradeType,
        real_orders.remarks AS Remarks,
        real_orders.created_at AS CreatedAt;
END;
$$;


-- Function: fn_get_real_order_by_id
DROP FUNCTION IF EXISTS fn_get_real_order_by_id(INT);

CREATE OR REPLACE FUNCTION fn_get_real_order_by_id(p_order_id INT)
RETURNS TABLE (
    Id INT,
    UserId INT,
    BrokerOrderId VARCHAR,
    Symbol VARCHAR,
    Side INT,
    Quantity INT,
    OrderType INT,
    Price NUMERIC,
    StopLoss NUMERIC,
    TakeProfit NUMERIC,
    Status INT,
    FilledPrice NUMERIC,
    FilledAt TIMESTAMP WITH TIME ZONE,
    RejectionReason VARCHAR,
    TradeType INT,
    Remarks VARCHAR,
    CreatedAt TIMESTAMP WITH TIME ZONE
)
LANGUAGE plpgsql
AS $$
BEGIN
    RETURN QUERY
    SELECT 
        o.id AS Id,
        o.user_id AS UserId,
        o.broker_order_id AS BrokerOrderId,
        o.symbol AS Symbol,
        o.side AS Side,
        o.quantity AS Quantity,
        o.order_type AS OrderType,
        o.price AS Price,
        o.stop_loss AS StopLoss,
        o.take_profit AS TakeProfit,
        o.status AS Status,
        o.filled_price AS FilledPrice,
        o.filled_at AS FilledAt,
        o.rejection_reason AS RejectionReason,
        o.trade_type AS TradeType,
        o.remarks AS Remarks,
        o.created_at AS CreatedAt
    FROM real_orders o
    WHERE o.id = p_order_id;
END;
$$;


-- Function: fn_get_real_order_by_broker_id
DROP FUNCTION IF EXISTS fn_get_real_order_by_broker_id(VARCHAR);

CREATE OR REPLACE FUNCTION fn_get_real_order_by_broker_id(p_broker_order_id VARCHAR)
RETURNS TABLE (
    Id INT,
    UserId INT,
    BrokerOrderId VARCHAR,
    Symbol VARCHAR,
    Side INT,
    Quantity INT,
    OrderType INT,
    Price NUMERIC,
    StopLoss NUMERIC,
    TakeProfit NUMERIC,
    Status INT,
    FilledPrice NUMERIC,
    FilledAt TIMESTAMP WITH TIME ZONE,
    RejectionReason VARCHAR,
    TradeType INT,
    Remarks VARCHAR,
    CreatedAt TIMESTAMP WITH TIME ZONE
)
LANGUAGE plpgsql
AS $$
BEGIN
    RETURN QUERY
    SELECT 
        o.id AS Id,
        o.user_id AS UserId,
        o.broker_order_id AS BrokerOrderId,
        o.symbol AS Symbol,
        o.side AS Side,
        o.quantity AS Quantity,
        o.order_type AS OrderType,
        o.price AS Price,
        o.stop_loss AS StopLoss,
        o.take_profit AS TakeProfit,
        o.status AS Status,
        o.filled_price AS FilledPrice,
        o.filled_at AS FilledAt,
        o.rejection_reason AS RejectionReason,
        o.trade_type AS TradeType,
        o.remarks AS Remarks,
        o.created_at AS CreatedAt
    FROM real_orders o
    WHERE o.broker_order_id = p_broker_order_id;
END;
$$;


-- Function: fn_get_recent_real_orders
DROP FUNCTION IF EXISTS fn_get_recent_real_orders(INT, INT);
DROP FUNCTION IF EXISTS fn_get_recent_real_orders(VARCHAR, INT);

CREATE OR REPLACE FUNCTION fn_get_recent_real_orders(p_user_id INT, p_limit INT)
RETURNS TABLE (
    Id INT,
    UserId INT,
    BrokerOrderId VARCHAR,
    Symbol VARCHAR,
    Side INT,
    Quantity INT,
    OrderType INT,
    Price NUMERIC,
    StopLoss NUMERIC,
    TakeProfit NUMERIC,
    Status INT,
    FilledPrice NUMERIC,
    FilledAt TIMESTAMP WITH TIME ZONE,
    RejectionReason VARCHAR,
    TradeType INT,
    Remarks VARCHAR,
    CreatedAt TIMESTAMP WITH TIME ZONE
)
LANGUAGE plpgsql
AS $$
BEGIN
    RETURN QUERY
    SELECT 
        o.id AS Id,
        o.user_id AS UserId,
        o.broker_order_id AS BrokerOrderId,
        o.symbol AS Symbol,
        o.side AS Side,
        o.quantity AS Quantity,
        o.order_type AS OrderType,
        o.price AS Price,
        o.stop_loss AS StopLoss,
        o.take_profit AS TakeProfit,
        o.status AS Status,
        o.filled_price AS FilledPrice,
        o.filled_at AS FilledAt,
        o.rejection_reason AS RejectionReason,
        o.trade_type AS TradeType,
        o.remarks AS Remarks,
        o.created_at AS CreatedAt
    FROM real_orders o
    WHERE o.user_id = p_user_id
    ORDER BY o.created_at DESC
    LIMIT p_limit;
END;
$$;


-- Function: fn_upsert_real_position
DROP FUNCTION IF EXISTS fn_upsert_real_position(INT, VARCHAR, INT, INT, NUMERIC, NUMERIC, NUMERIC, NUMERIC, NUMERIC, NUMERIC, INT, INT, VARCHAR, NUMERIC);
DROP FUNCTION IF EXISTS fn_upsert_real_position(VARCHAR, VARCHAR, INT, INT, NUMERIC, NUMERIC, NUMERIC, NUMERIC, NUMERIC, NUMERIC, INT, INT, VARCHAR, NUMERIC);

CREATE OR REPLACE FUNCTION fn_upsert_real_position(
    p_user_id INT,
    p_symbol VARCHAR,
    p_side INT,
    p_quantity INT,
    p_average_entry_price NUMERIC,
    p_current_price NUMERIC,
    p_unrealized_pnl NUMERIC,
    p_stop_loss NUMERIC,
    p_take_profit NUMERIC,
    p_trailing_stop_loss NUMERIC,
    p_status INT,
    p_trade_type INT,
    p_exit_reason VARCHAR,
    p_realized_pnl NUMERIC
)
RETURNS TABLE (
    Id INT,
    UserId INT,
    Symbol VARCHAR,
    Side INT,
    Quantity INT,
    AverageEntryPrice NUMERIC,
    CurrentPrice NUMERIC,
    UnrealizedPnl NUMERIC,
    StopLoss NUMERIC,
    TakeProfit NUMERIC,
    TrailingStopLoss NUMERIC,
    Status INT,
    TradeType INT,
    ExitReason VARCHAR,
    OpenedAt TIMESTAMP WITH TIME ZONE,
    ClosedAt TIMESTAMP WITH TIME ZONE,
    RealizedPnl NUMERIC
)
LANGUAGE plpgsql
AS $$
BEGIN
    RETURN QUERY
    INSERT INTO real_positions (
        user_id, symbol, side, quantity, average_entry_price,
        current_price, unrealized_pnl, stop_loss, take_profit,
        trailing_stop_loss, status, trade_type, exit_reason, opened_at, realized_pnl
    ) VALUES (
        p_user_id, p_symbol, p_side, p_quantity, p_average_entry_price,
        p_current_price, p_unrealized_pnl, p_stop_loss, p_take_profit,
        p_trailing_stop_loss, p_status, p_trade_type, p_exit_reason, NOW(), p_realized_pnl
    )
    RETURNING 
        real_positions.id AS Id,
        real_positions.user_id AS UserId,
        real_positions.symbol AS Symbol,
        real_positions.side AS Side,
        real_positions.quantity AS Quantity,
        real_positions.average_entry_price AS AverageEntryPrice,
        real_positions.current_price AS CurrentPrice,
        real_positions.unrealized_pnl AS UnrealizedPnl,
        real_positions.stop_loss AS StopLoss,
        real_positions.take_profit AS TakeProfit,
        real_positions.trailing_stop_loss AS TrailingStopLoss,
        real_positions.status AS Status,
        real_positions.trade_type AS TradeType,
        real_positions.exit_reason AS ExitReason,
        real_positions.opened_at AS OpenedAt,
        real_positions.closed_at AS ClosedAt,
        real_positions.realized_pnl AS RealizedPnl;
END;
$$;


-- Function: fn_get_open_real_position_by_id
DROP FUNCTION IF EXISTS fn_get_open_real_position_by_id(INT);

CREATE OR REPLACE FUNCTION fn_get_open_real_position_by_id(p_position_id INT)
RETURNS TABLE (
    Id INT,
    UserId INT,
    Symbol VARCHAR,
    Side INT,
    Quantity INT,
    AverageEntryPrice NUMERIC,
    CurrentPrice NUMERIC,
    UnrealizedPnl NUMERIC,
    StopLoss NUMERIC,
    TakeProfit NUMERIC,
    TrailingStopLoss NUMERIC,
    Status INT,
    TradeType INT,
    ExitReason VARCHAR,
    OpenedAt TIMESTAMP WITH TIME ZONE,
    ClosedAt TIMESTAMP WITH TIME ZONE,
    RealizedPnl NUMERIC
)
LANGUAGE plpgsql
AS $$
BEGIN
    RETURN QUERY
    SELECT 
        p.id AS Id,
        p.user_id AS UserId,
        p.symbol AS Symbol,
        p.side AS Side,
        p.quantity AS Quantity,
        p.average_entry_price AS AverageEntryPrice,
        p.current_price AS CurrentPrice,
        p.unrealized_pnl AS UnrealizedPnl,
        p.stop_loss AS StopLoss,
        p.take_profit AS TakeProfit,
        p.trailing_stop_loss AS TrailingStopLoss,
        p.status AS Status,
        p.trade_type AS TradeType,
        p.exit_reason AS ExitReason,
        p.opened_at AS OpenedAt,
        p.closed_at AS ClosedAt,
        p.realized_pnl AS RealizedPnl
    FROM real_positions p
    WHERE p.id = p_position_id AND p.status = 0;
END;
$$;


-- Function: fn_get_open_real_position_by_symbol
DROP FUNCTION IF EXISTS fn_get_open_real_position_by_symbol(INT, VARCHAR);
DROP FUNCTION IF EXISTS fn_get_open_real_position_by_symbol(VARCHAR, VARCHAR);

CREATE OR REPLACE FUNCTION fn_get_open_real_position_by_symbol(p_user_id INT, p_symbol VARCHAR)
RETURNS TABLE (
    Id INT,
    UserId INT,
    Symbol VARCHAR,
    Side INT,
    Quantity INT,
    AverageEntryPrice NUMERIC,
    CurrentPrice NUMERIC,
    UnrealizedPnl NUMERIC,
    StopLoss NUMERIC,
    TakeProfit NUMERIC,
    TrailingStopLoss NUMERIC,
    Status INT,
    TradeType INT,
    ExitReason VARCHAR,
    OpenedAt TIMESTAMP WITH TIME ZONE,
    ClosedAt TIMESTAMP WITH TIME ZONE,
    RealizedPnl NUMERIC
)
LANGUAGE plpgsql
AS $$
BEGIN
    RETURN QUERY
    SELECT 
        p.id AS Id,
        p.user_id AS UserId,
        p.symbol AS Symbol,
        p.side AS Side,
        p.quantity AS Quantity,
        p.average_entry_price AS AverageEntryPrice,
        p.current_price AS CurrentPrice,
        p.unrealized_pnl AS UnrealizedPnl,
        p.stop_loss AS StopLoss,
        p.take_profit AS TakeProfit,
        p.trailing_stop_loss AS TrailingStopLoss,
        p.status AS Status,
        p.trade_type AS TradeType,
        p.exit_reason AS ExitReason,
        p.opened_at AS OpenedAt,
        p.closed_at AS ClosedAt,
        p.realized_pnl AS RealizedPnl
    FROM real_positions p
    WHERE p.user_id = p_user_id AND p.symbol = p_symbol AND p.status = 0
    LIMIT 1;
END;
$$;


-- Function: fn_get_open_real_positions
DROP FUNCTION IF EXISTS fn_get_open_real_positions(INT);
DROP FUNCTION IF EXISTS fn_get_open_real_positions(VARCHAR);

CREATE OR REPLACE FUNCTION fn_get_open_real_positions(p_user_id INT)
RETURNS TABLE (
    Id INT,
    UserId INT,
    Symbol VARCHAR,
    Side INT,
    Quantity INT,
    AverageEntryPrice NUMERIC,
    CurrentPrice NUMERIC,
    UnrealizedPnl NUMERIC,
    StopLoss NUMERIC,
    TakeProfit NUMERIC,
    TrailingStopLoss NUMERIC,
    Status INT,
    TradeType INT,
    ExitReason VARCHAR,
    OpenedAt TIMESTAMP WITH TIME ZONE,
    ClosedAt TIMESTAMP WITH TIME ZONE,
    RealizedPnl NUMERIC
)
LANGUAGE plpgsql
AS $$
BEGIN
    RETURN QUERY
    SELECT 
        p.id AS Id,
        p.user_id AS UserId,
        p.symbol AS Symbol,
        p.side AS Side,
        p.quantity AS Quantity,
        p.average_entry_price AS AverageEntryPrice,
        p.current_price AS CurrentPrice,
        p.unrealized_pnl AS UnrealizedPnl,
        p.stop_loss AS StopLoss,
        p.take_profit AS TakeProfit,
        p.trailing_stop_loss AS TrailingStopLoss,
        p.status AS Status,
        p.trade_type AS TradeType,
        p.exit_reason AS ExitReason,
        p.opened_at AS OpenedAt,
        p.closed_at AS ClosedAt,
        p.realized_pnl AS RealizedPnl
    FROM real_positions p
    WHERE p.user_id = p_user_id AND p.status = 0
    ORDER BY p.opened_at DESC;
END;
$$;


-- Function: fn_record_real_trade_history
DROP FUNCTION IF EXISTS fn_record_real_trade_history(INT, INT, VARCHAR, VARCHAR, INT, INT, NUMERIC, NUMERIC, NUMERIC, INT, VARCHAR, VARCHAR);
DROP FUNCTION IF EXISTS fn_record_real_trade_history(VARCHAR, INT, VARCHAR, VARCHAR, INT, INT, NUMERIC, NUMERIC, NUMERIC, INT, VARCHAR, VARCHAR);

CREATE OR REPLACE FUNCTION fn_record_real_trade_history(
    p_user_id INT,
    p_order_id INT,
    p_broker_order_id VARCHAR,
    p_symbol VARCHAR,
    p_side INT,
    p_quantity INT,
    p_entry_price NUMERIC,
    p_executed_price NUMERIC,
    p_realized_pnl NUMERIC,
    p_trade_type INT,
    p_exit_reason VARCHAR,
    p_remarks VARCHAR
)
RETURNS TABLE (
    Id INT,
    UserId INT,
    OrderId INT,
    BrokerOrderId VARCHAR,
    Symbol VARCHAR,
    Side INT,
    Quantity INT,
    EntryPrice NUMERIC,
    ExecutedPrice NUMERIC,
    RealizedPnl NUMERIC,
    TradeType INT,
    ExitReason VARCHAR,
    ExecutedAt TIMESTAMP WITH TIME ZONE,
    Remarks VARCHAR
)
LANGUAGE plpgsql
AS $$
BEGIN
    RETURN QUERY
    INSERT INTO real_trade_history (
        user_id, order_id, broker_order_id, symbol, side,
        quantity, entry_price, executed_price, realized_pnl,
        trade_type, exit_reason, executed_at, remarks
    ) VALUES (
        p_user_id, p_order_id, p_broker_order_id, p_symbol, p_side,
        p_quantity, p_entry_price, p_executed_price, p_realized_pnl,
        p_trade_type, p_exit_reason, NOW(), p_remarks
    )
    RETURNING 
        real_trade_history.id AS Id,
        real_trade_history.user_id AS UserId,
        real_trade_history.order_id AS OrderId,
        real_trade_history.broker_order_id AS BrokerOrderId,
        real_trade_history.symbol AS Symbol,
        real_trade_history.side AS Side,
        real_trade_history.quantity AS Quantity,
        real_trade_history.entry_price AS EntryPrice,
        real_trade_history.executed_price AS ExecutedPrice,
        real_trade_history.realized_pnl AS RealizedPnl,
        real_trade_history.trade_type AS TradeType,
        real_trade_history.exit_reason AS ExitReason,
        real_trade_history.executed_at AS ExecutedAt,
        real_trade_history.remarks AS Remarks;
END;
$$;


-- Function: fn_get_real_trade_history
DROP FUNCTION IF EXISTS fn_get_real_trade_history(INT, INT);
DROP FUNCTION IF EXISTS fn_get_real_trade_history(VARCHAR, INT);

CREATE OR REPLACE FUNCTION fn_get_real_trade_history(p_user_id INT, p_limit INT)
RETURNS TABLE (
    Id INT,
    UserId INT,
    OrderId INT,
    BrokerOrderId VARCHAR,
    Symbol VARCHAR,
    Side INT,
    Quantity INT,
    EntryPrice NUMERIC,
    ExecutedPrice NUMERIC,
    RealizedPnl NUMERIC,
    TradeType INT,
    ExitReason VARCHAR,
    ExecutedAt TIMESTAMP WITH TIME ZONE,
    Remarks VARCHAR
)
LANGUAGE plpgsql
AS $$
BEGIN
    RETURN QUERY
    SELECT 
        h.id AS Id,
        h.user_id AS UserId,
        h.order_id AS OrderId,
        h.broker_order_id AS BrokerOrderId,
        h.symbol AS Symbol,
        h.side AS Side,
        h.quantity AS Quantity,
        h.entry_price AS EntryPrice,
        h.executed_price AS ExecutedPrice,
        h.realized_pnl AS RealizedPnl,
        h.trade_type AS TradeType,
        h.exit_reason AS ExitReason,
        h.executed_at AS ExecutedAt,
        h.remarks AS Remarks
    FROM real_trade_history h
    WHERE h.user_id = p_user_id
    ORDER BY h.executed_at DESC
    LIMIT p_limit;
END;
$$;


-- Function: fn_get_today_real_trade_count
DROP FUNCTION IF EXISTS fn_get_today_real_trade_count(INT, TIMESTAMP WITH TIME ZONE);
DROP FUNCTION IF EXISTS fn_get_today_real_trade_count(VARCHAR, TIMESTAMP WITH TIME ZONE);

CREATE OR REPLACE FUNCTION fn_get_today_real_trade_count(p_user_id INT, p_today_start TIMESTAMP WITH TIME ZONE)
RETURNS INT
LANGUAGE plpgsql
AS $$
DECLARE
    v_count INT;
BEGIN
    SELECT COUNT(*) INTO v_count
    FROM real_orders
    WHERE user_id = p_user_id 
      AND status = 1 
      AND side = 0 
      AND created_at >= p_today_start;

    RETURN v_count;
END;
$$;


-- Function: fn_get_today_realized_pnl
DROP FUNCTION IF EXISTS fn_get_today_realized_pnl(INT, TIMESTAMP WITH TIME ZONE);
DROP FUNCTION IF EXISTS fn_get_today_realized_pnl(VARCHAR, TIMESTAMP WITH TIME ZONE);

CREATE OR REPLACE FUNCTION fn_get_today_realized_pnl(p_user_id INT, p_today_start TIMESTAMP WITH TIME ZONE)
RETURNS NUMERIC
LANGUAGE plpgsql
AS $$
DECLARE
    v_pnl NUMERIC;
BEGIN
    SELECT COALESCE(SUM(realized_pnl), 0.00) INTO v_pnl
    FROM real_trade_history
    WHERE user_id = p_user_id 
      AND side = 1 
      AND executed_at >= p_today_start;

    RETURN v_pnl;
END;
$$;


-- Function: fn_get_today_real_trade_logs
DROP FUNCTION IF EXISTS fn_get_today_real_trade_logs(INT, TIMESTAMP WITH TIME ZONE, INT);
DROP FUNCTION IF EXISTS fn_get_today_real_trade_logs(VARCHAR, TIMESTAMP WITH TIME ZONE, INT);

CREATE OR REPLACE FUNCTION fn_get_today_real_trade_logs(
    p_user_id INT,
    p_today_start TIMESTAMP WITH TIME ZONE,
    p_limit INT
)
RETURNS TABLE (
    Id INT,
    UserId INT,
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
    FROM real_trade_execution_logs l
    WHERE l.user_id = p_user_id AND l.executed_at >= p_today_start
    ORDER BY l.executed_at DESC
    LIMIT p_limit;
END;
$$;


-- ----------------------------------------------------------------------------
-- Function: sp_get_swing_scan_slots
-- Returns all distinct 30-minute scan slots for a given date with signal counts.
-- ----------------------------------------------------------------------------
DROP FUNCTION IF EXISTS sp_get_swing_scan_slots(DATE);

CREATE OR REPLACE FUNCTION sp_get_swing_scan_slots(
    p_date DATE
)
RETURNS TABLE (
    SlotLabel VARCHAR,
    SlotTime TIMESTAMP WITH TIME ZONE,
    BuyCount BIGINT,
    WatchCount BIGINT,
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
        COUNT(*) AS TotalCount
    FROM swing_slot_recommendations r
    WHERE r.scan_date = p_date
    GROUP BY r.slot_label
    ORDER BY MAX(r.slot_time) ASC;
END;
$$;


-- ----------------------------------------------------------------------------
-- Function: sp_get_swing_slot_recommendations
-- Returns all stock recommendations for a given date and slot label ('all' for all slots).
-- ----------------------------------------------------------------------------
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
    CreatedAt TIMESTAMP WITH TIME ZONE
)
LANGUAGE plpgsql
AS $$
BEGIN
    IF LOWER(p_slot_label) = 'all' OR p_slot_label IS NULL OR TRIM(p_slot_label) = '' THEN
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
            r.created_at AS CreatedAt
        FROM swing_slot_recommendations r
        WHERE r.scan_date = p_date
        ORDER BY r.score DESC, r.slot_time DESC, r.symbol ASC;
    ELSE
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
            r.created_at AS CreatedAt
        FROM swing_slot_recommendations r
        WHERE r.scan_date = p_date AND r.slot_label = p_slot_label
        ORDER BY r.score DESC, r.symbol ASC;
    END IF;
END;
$$;






