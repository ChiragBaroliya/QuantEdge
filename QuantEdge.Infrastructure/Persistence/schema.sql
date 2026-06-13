-- ============================================================================
-- QuantEdge Database Schema & Stored Procedures (PostgreSQL / TimescaleDB)
-- ============================================================================

-- ----------------------------------------------------------------------------
-- 0. Schema Migration (Automatic split from single table to timeframe tables)
-- ----------------------------------------------------------------------------

-- Migration: Copy old market_candles data to timeframe-specific tables (if old table exists)
DO $$
BEGIN
    IF EXISTS (
        SELECT 1 FROM information_schema.tables 
        WHERE table_schema = 'public' AND table_name = 'market_candles'
    ) THEN
        -- Create tables if not exists to be safe
        CREATE TABLE IF NOT EXISTS market_candles_1m (
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
            CONSTRAINT pk_market_candles_1m PRIMARY KEY (id, candle_time)
        );
        CREATE TABLE IF NOT EXISTS market_candles_5m (
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
            CONSTRAINT pk_market_candles_5m PRIMARY KEY (id, candle_time)
        );

        -- Copy data
        INSERT INTO market_candles_1m (id, candle_time, symbol, timeframe, open, high, low, close, volume, created_at)
        SELECT id, candle_time, symbol, timeframe, open, high, low, close, volume, created_at
        FROM market_candles WHERE LOWER(timeframe) = '1m' ON CONFLICT DO NOTHING;

        INSERT INTO market_candles_5m (id, candle_time, symbol, timeframe, open, high, low, close, volume, created_at)
        SELECT id, candle_time, symbol, timeframe, open, high, low, close, volume, created_at
        FROM market_candles WHERE LOWER(timeframe) = '5m' ON CONFLICT DO NOTHING;

        -- Drop old table
        DROP TABLE market_candles;
        RAISE NOTICE 'Migrated and dropped old market_candles table.';
    END IF;
END;
$$;

-- Migration: Copy old market_indicators data to timeframe-specific tables (if old table exists)
DO $$
BEGIN
    IF EXISTS (
        SELECT 1 FROM information_schema.tables 
        WHERE table_schema = 'public' AND table_name = 'market_indicators'
    ) THEN
        CREATE TABLE IF NOT EXISTS market_indicators_1m (
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
            CONSTRAINT pk_market_indicators_1m PRIMARY KEY (id, candle_time)
        );
        CREATE TABLE IF NOT EXISTS market_indicators_5m (
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
            CONSTRAINT pk_market_indicators_5m PRIMARY KEY (id, candle_time)
        );

        INSERT INTO market_indicators_1m (id, candle_time, symbol, timeframe, rsi, ema20, ema50, macd, signal_line, vwap, created_at)
        SELECT id, candle_time, symbol, timeframe, rsi, ema20, ema50, macd, signal_line, vwap, created_at
        FROM market_indicators WHERE LOWER(timeframe) = '1m' ON CONFLICT DO NOTHING;

        INSERT INTO market_indicators_5m (id, candle_time, symbol, timeframe, rsi, ema20, ema50, macd, signal_line, vwap, created_at)
        SELECT id, candle_time, symbol, timeframe, rsi, ema20, ema50, macd, signal_line, vwap, created_at
        FROM market_indicators WHERE LOWER(timeframe) = '5m' ON CONFLICT DO NOTHING;

        DROP TABLE market_indicators;
        RAISE NOTICE 'Migrated and dropped old market_indicators table.';
    END IF;
END;
$$;


-- ----------------------------------------------------------------------------
-- 1. Tables Creation
-- ----------------------------------------------------------------------------

-- Table: market_candles_1m
CREATE TABLE IF NOT EXISTS market_candles_1m (
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
    CONSTRAINT pk_market_candles_1m PRIMARY KEY (id, candle_time)
);

-- Table: market_candles_5m
CREATE TABLE IF NOT EXISTS market_candles_5m (
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
    CONSTRAINT pk_market_candles_5m PRIMARY KEY (id, candle_time)
);

-- Table: market_candles_15m
CREATE TABLE IF NOT EXISTS market_candles_15m (
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
    CONSTRAINT pk_market_candles_15m PRIMARY KEY (id, candle_time)
);

-- Table: market_indicators_1m
CREATE TABLE IF NOT EXISTS market_indicators_1m (
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
    CONSTRAINT pk_market_indicators_1m PRIMARY KEY (id, candle_time)
);

-- Table: market_indicators_5m
CREATE TABLE IF NOT EXISTS market_indicators_5m (
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
    CONSTRAINT pk_market_indicators_5m PRIMARY KEY (id, candle_time)
);

-- Table: market_indicators_15m
CREATE TABLE IF NOT EXISTS market_indicators_15m (
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
    CONSTRAINT pk_market_indicators_15m PRIMARY KEY (id, candle_time)
);

-- Table: trading_signals
CREATE TABLE IF NOT EXISTS trading_signals (
    id INT NOT NULL,
    candle_time TIMESTAMP WITH TIME ZONE NOT NULL,
    symbol VARCHAR(50) NOT NULL,
    signal_type VARCHAR(20) NOT NULL,
    signal_strength NUMERIC(5, 2) NOT NULL,
    entry_price NUMERIC(18, 6) NOT NULL,
    reason VARCHAR(1000) NOT NULL,
    created_at TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT NOW(),
    CONSTRAINT pk_trading_signals PRIMARY KEY (id, candle_time)
);

-- Table: zerodha_sessions
CREATE TABLE IF NOT EXISTS zerodha_sessions (
    api_key      VARCHAR(50)  PRIMARY KEY,
    access_token VARCHAR(255) NOT NULL,
    is_active    BOOLEAN      NOT NULL DEFAULT FALSE,
    created_at   TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT NOW()
);

-- ----------------------------------------------------------------------------
-- 2. Optional: TimescaleDB Hypertables Configuration
-- ----------------------------------------------------------------------------
-- SELECT create_hypertable('market_candles_1m', 'candle_time', if_not_exists => TRUE);
-- SELECT create_hypertable('market_candles_5m', 'candle_time', if_not_exists => TRUE);
-- SELECT create_hypertable('market_indicators_1m', 'candle_time', if_not_exists => TRUE);
-- SELECT create_hypertable('market_indicators_5m', 'candle_time', if_not_exists => TRUE);
-- SELECT create_hypertable('trading_signals', 'candle_time', if_not_exists => TRUE);

-- ----------------------------------------------------------------------------
-- 3. Composite Optimization Indexes
-- ----------------------------------------------------------------------------
CREATE INDEX IF NOT EXISTS ix_market_candles_1m_symbol_candle_time
ON market_candles_1m (symbol, candle_time DESC);

CREATE INDEX IF NOT EXISTS ix_market_candles_5m_symbol_candle_time
ON market_candles_5m (symbol, candle_time DESC);

CREATE INDEX IF NOT EXISTS ix_market_candles_15m_symbol_candle_time
ON market_candles_15m (symbol, candle_time DESC);

CREATE INDEX IF NOT EXISTS ix_market_indicators_1m_symbol_candle_time
ON market_indicators_1m (symbol, candle_time DESC);

CREATE INDEX IF NOT EXISTS ix_market_indicators_5m_symbol_candle_time
ON market_indicators_5m (symbol, candle_time DESC);

CREATE INDEX IF NOT EXISTS ix_market_indicators_15m_symbol_candle_time
ON market_indicators_15m (symbol, candle_time DESC);

CREATE INDEX IF NOT EXISTS ix_trading_signals_symbol_candle_time
ON trading_signals (symbol, candle_time DESC);

-- ----------------------------------------------------------------------------
-- 4. Stored Procedures (Dynamic Routing Data Ingestion & Extraction APIs)
-- ----------------------------------------------------------------------------

-- Procedure: sp_insert_market_candle
-- Dynamically creates target table based on timeframe if not exists, and UPSERTs data.
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

-- Function: sp_get_market_candles
-- Dynamically returns query results from target timeframe table, safely handling non-existent tables.
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

-- Procedure: sp_insert_market_indicator
-- Dynamically creates target table based on timeframe if not exists, and UPSERTs data.
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

-- Function: sp_get_market_indicators
-- Dynamically returns query results from target timeframe table, safely handling non-existent tables.
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

-- Procedure: sp_insert_trading_signal
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

-- Function: sp_get_recent_trading_signals
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

-- Procedure: sp_upsert_zerodha_session
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

-- Function: sp_activate_zerodha_token
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

-- Function: sp_get_active_zerodha_session
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
-- 5. Stock Master Tables Configuration
-- ----------------------------------------------------------------------------

-- Table: stock_master
CREATE TABLE IF NOT EXISTS stock_master (
    id SERIAL PRIMARY KEY,
    symbol VARCHAR(50) UNIQUE NOT NULL,
    instrument_token INT NOT NULL,
    is_active BOOLEAN NOT NULL DEFAULT FALSE,
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
    created_at TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS ix_stock_master_instrument_token 
ON stock_master (instrument_token);

INSERT INTO stock_master (symbol, instrument_token, is_active)
VALUES 
    ('NIFTYBEES', 3771393, TRUE),
    ('INFY', 408065, TRUE),
    ('TCS', 2953217, TRUE),
    ('HDFCBANK', 341249, TRUE),
    ('RELIANCE', 738561, TRUE),
    ('SBIN', 779521, FALSE),
    ('ICICIBANK', 417281, FALSE),
    ('AXISBANK', 1510401, FALSE),
    ('LT', 2939649, FALSE),
    ('ITC', 424961, FALSE),
    ('TATAMOTORS', 884737, FALSE)
ON CONFLICT (symbol) DO NOTHING;

DROP FUNCTION IF EXISTS sp_get_active_stocks();
DROP FUNCTION IF EXISTS sp_get_stock_by_symbol(VARCHAR);
DROP FUNCTION IF EXISTS sp_upsert_instruments(JSONB);

-- Function: sp_get_active_stocks
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
    created_at TIMESTAMP WITH TIME ZONE
)
LANGUAGE plpgsql
AS $$
BEGIN
    RETURN QUERY
    SELECT s.id, s.symbol, s.instrument_token, s.is_active,
           s.exchange_token, s.name, s.last_price, s.expiry,
           s.strike, s.tick_size, s.lot_size, s.instrument_type,
           s.segment, s.exchange, s.created_at
    FROM stock_master s
    WHERE s.is_active = TRUE;
END;
$$;

-- Function: sp_get_stock_by_symbol
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
    created_at TIMESTAMP WITH TIME ZONE
)
LANGUAGE plpgsql
AS $$
BEGIN
    RETURN QUERY
    SELECT s.id, s.symbol, s.instrument_token, s.is_active,
           s.exchange_token, s.name, s.last_price, s.expiry,
           s.strike, s.tick_size, s.lot_size, s.instrument_type,
           s.segment, s.exchange, s.created_at
    FROM stock_master s
    WHERE UPPER(s.symbol) = UPPER(p_symbol)
    LIMIT 1;
END;
$$;

-- Function: sp_upsert_instruments
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
        exchange = EXCLUDED.exchange;
END;
$$;

-- ----------------------------------------------------------------------------
-- 6. Indian Holidays Configuration
-- ----------------------------------------------------------------------------

-- Table: indian_holidays
CREATE TABLE IF NOT EXISTS indian_holidays (
    id SERIAL PRIMARY KEY,
    holiday_date DATE UNIQUE NOT NULL,
    description VARCHAR(255) NOT NULL,
    created_at TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS ix_indian_holidays_date ON indian_holidays (holiday_date);

-- Drop old function first to change return type from DATE to TIMESTAMP
DROP FUNCTION IF EXISTS sp_get_indian_holidays();

-- Function: sp_get_indian_holidays
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

-- Procedure: sp_insert_indian_holiday
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

-- Procedure: sp_delete_indian_holiday
CREATE OR REPLACE PROCEDURE sp_delete_indian_holiday(
    p_id INT
)
LANGUAGE plpgsql
AS $$
BEGIN
    DELETE FROM indian_holidays WHERE id = p_id;
END;
$$;

-- Function: sp_is_indian_holiday
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

