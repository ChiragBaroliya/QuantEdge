-- ============================================================================
-- QuantEdge Database Stored Functions (PostgreSQL / TimescaleDB)
-- ============================================================================

-- ----------------------------------------------------------------------------
-- Function: sp_get_market_candles
-- Dynamically returns query results from target timeframe table, safely handling non-existent tables.
-- ----------------------------------------------------------------------------
DROP FUNCTION IF EXISTS sp_get_market_candles CASCADE;
DROP FUNCTION IF EXISTS sp_get_market_candles(VARCHAR, VARCHAR, INTEGER) CASCADE;

CREATE OR REPLACE FUNCTION sp_get_market_candles(
    p_symbol VARCHAR(50),
    p_timeframe VARCHAR(20),
    p_limit INTEGER
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

    RETURN QUERY EXECUTE format('
        SELECT c.id, c.candle_time, c.symbol, c.timeframe, c.open, c.high, c.low, c.close, c.volume, c.created_at
        FROM %I c
        WHERE c.symbol = $1
        ORDER BY c.candle_time DESC
        LIMIT $2;', v_table_name)
    USING p_symbol, p_limit;
END;
$$;


-- ----------------------------------------------------------------------------
-- Function: sp_get_market_indicators
-- Dynamically returns query results from target timeframe table, safely handling non-existent tables.
-- ----------------------------------------------------------------------------
DROP FUNCTION IF EXISTS sp_get_market_indicators CASCADE;
DROP FUNCTION IF EXISTS sp_get_market_indicators(VARCHAR, VARCHAR, INTEGER) CASCADE;

CREATE OR REPLACE FUNCTION sp_get_market_indicators(
    p_symbol VARCHAR(50),
    p_timeframe VARCHAR(20),
    p_limit INTEGER
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

    RETURN QUERY EXECUTE format('
        SELECT i.id, i.candle_time, i.symbol, i.timeframe, i.rsi, i.ema20, i.ema50, i.macd, i.signal_line, i.vwap, i.created_at
        FROM %I i
        WHERE i.symbol = $1
        ORDER BY i.candle_time DESC
        LIMIT $2;', v_table_name)
    USING p_symbol, p_limit;
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
    ON CONFLICT (symbol) DO UPDATE SET
        instrument_token = EXCLUDED.instrument_token,
        exchange_token = EXCLUDED.exchange_token,
        name = EXCLUDED.name,
        last_price = EXCLUDED.last_price,
        expiry = EXCLUDED.expiry,
        strike = EXCLUDED.strike,
        tick_size = EXCLUDED.tick_size,
        lot_size = EXCLUDED.lot_size,
        instrument_type = EXCLUDED.instrument_type,
        segment = EXCLUDED.segment,
        exchange = EXCLUDED.exchange
    WHERE stock_master.is_active = FALSE;
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
        COUNT(*) FILTER (WHERE COALESCE(s.is_histry_stored_1d, 0) = 0 OR COALESCE(s.is_histry_stored_60m, 0) = 0)::INT AS "HistoryMissingCount"
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
                OR (LOWER(p_history_filter) = 'missing' AND (COALESCE(s.is_histry_stored_1d, 0) = 0 OR COALESCE(s.is_histry_stored_60m, 0) = 0))
                OR (LOWER(p_history_filter) = '1d_missing' AND COALESCE(s.is_histry_stored_1d, 0) = 0)
                OR (LOWER(p_history_filter) = '60m_missing' AND COALESCE(s.is_histry_stored_60m, 0) = 0)
                OR (LOWER(p_history_filter) = 'has_1d' AND COALESCE(s.is_histry_stored_1d, 0) = 1)
                OR (LOWER(p_history_filter) = 'has_60m' AND COALESCE(s.is_histry_stored_60m, 0) = 1)
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
