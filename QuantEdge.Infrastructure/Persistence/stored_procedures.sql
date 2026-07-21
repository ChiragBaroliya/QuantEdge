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
