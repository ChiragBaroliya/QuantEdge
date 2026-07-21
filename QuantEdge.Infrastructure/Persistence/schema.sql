-- ============================================================================
-- QuantEdge Database Schema (Tables, Indexes, & Initial Seeds)
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

-- Table: market_candles_60m
CREATE TABLE IF NOT EXISTS market_candles_60m (
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
    CONSTRAINT pk_market_candles_60m PRIMARY KEY (id, candle_time)
);

-- Table: market_candles_1d
CREATE TABLE IF NOT EXISTS market_candles_1d (
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
    CONSTRAINT pk_market_candles_1d PRIMARY KEY (id, candle_time)
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

-- Table: market_indicators_60m
CREATE TABLE IF NOT EXISTS market_indicators_60m (
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
    CONSTRAINT pk_market_indicators_60m PRIMARY KEY (id, candle_time)
);

-- Table: market_indicators_1d
CREATE TABLE IF NOT EXISTS market_indicators_1d (
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
    CONSTRAINT pk_market_indicators_1d PRIMARY KEY (id, candle_time)
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
    is_histry_stored_1m INT DEFAULT NULL,
    is_histry_stored_5m INT DEFAULT NULL,
    is_histry_stored_15m INT DEFAULT NULL,
    is_histry_stored_60m INT DEFAULT NULL,
    is_histry_stored_1d INT DEFAULT NULL,
    created_at TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT NOW()
);

-- Table: indian_holidays
CREATE TABLE IF NOT EXISTS indian_holidays (
    id SERIAL PRIMARY KEY,
    holiday_date DATE UNIQUE NOT NULL,
    description VARCHAR(255) NOT NULL,
    created_at TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT NOW()
);

-- Table: daily_stock_analysis
CREATE TABLE IF NOT EXISTS daily_stock_analysis (
    id SERIAL PRIMARY KEY,
    stock_id INT NOT NULL REFERENCES stock_master(id) ON DELETE CASCADE,
    trade_date DATE NOT NULL,
    close_price NUMERIC(18, 4) NOT NULL,
    volume BIGINT NOT NULL,
    ema20 NUMERIC(18, 4),
    ema50 NUMERIC(18, 4),
    ema200 NUMERIC(18, 4),
    rsi14 NUMERIC(18, 4),
    macd NUMERIC(18, 4),
    macd_signal NUMERIC(18, 4),
    adx14 NUMERIC(18, 4),
    atr14 NUMERIC(18, 4),
    average_volume20 NUMERIC(18, 4),
    is_52_week_high BOOLEAN NOT NULL DEFAULT FALSE,
    buy_score INT,
    sell_score INT,
    buy_signal BOOLEAN NOT NULL DEFAULT FALSE,
    sell_signal BOOLEAN NOT NULL DEFAULT FALSE,
    recommendation VARCHAR(20) NOT NULL DEFAULT 'HOLD',
    reason TEXT,
    created_on TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT NOW(),
    CONSTRAINT uq_daily_stock_analysis UNIQUE (stock_id, trade_date)
);

-- Table: swing_positions
CREATE TABLE IF NOT EXISTS swing_positions (
    id SERIAL PRIMARY KEY,
    symbol VARCHAR(50) NOT NULL,
    entry_date DATE NOT NULL,
    entry_price NUMERIC(18, 4) NOT NULL,
    quantity INT NOT NULL DEFAULT 1,
    is_closed BOOLEAN NOT NULL DEFAULT FALSE,
    exit_date DATE,
    exit_price NUMERIC(18, 4),
    exit_reason VARCHAR(100),
    created_at TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT NOW()
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
-- 3. Composite & Helper Indexes
-- ----------------------------------------------------------------------------
CREATE INDEX IF NOT EXISTS ix_market_candles_1m_symbol_candle_time
ON market_candles_1m (symbol, candle_time DESC);

CREATE INDEX IF NOT EXISTS ix_market_candles_5m_symbol_candle_time
ON market_candles_5m (symbol, candle_time DESC);

CREATE INDEX IF NOT EXISTS ix_market_candles_15m_symbol_candle_time
ON market_candles_15m (symbol, candle_time DESC);

CREATE INDEX IF NOT EXISTS ix_market_candles_60m_symbol_candle_time
ON market_candles_60m (symbol, candle_time DESC);

CREATE INDEX IF NOT EXISTS ix_market_candles_1d_symbol_candle_time
ON market_candles_1d (symbol, candle_time DESC);

CREATE INDEX IF NOT EXISTS ix_market_indicators_1m_symbol_candle_time
ON market_indicators_1m (symbol, candle_time DESC);

CREATE INDEX IF NOT EXISTS ix_market_indicators_5m_symbol_candle_time
ON market_indicators_5m (symbol, candle_time DESC);

CREATE INDEX IF NOT EXISTS ix_market_indicators_15m_symbol_candle_time
ON market_indicators_15m (symbol, candle_time DESC);

CREATE INDEX IF NOT EXISTS ix_market_indicators_60m_symbol_candle_time
ON market_indicators_60m (symbol, candle_time DESC);

CREATE INDEX IF NOT EXISTS ix_market_indicators_1d_symbol_candle_time
ON market_indicators_1d (symbol, candle_time DESC);

CREATE INDEX IF NOT EXISTS ix_trading_signals_symbol_candle_time
ON trading_signals (symbol, candle_time DESC);

CREATE INDEX IF NOT EXISTS ix_stock_master_instrument_token 
ON stock_master (instrument_token);

CREATE INDEX IF NOT EXISTS ix_indian_holidays_date 
ON indian_holidays (holiday_date);

CREATE INDEX IF NOT EXISTS ix_daily_stock_analysis_date 
ON daily_stock_analysis (trade_date DESC);

CREATE INDEX IF NOT EXISTS ix_daily_stock_analysis_stock_date 
ON daily_stock_analysis (stock_id, trade_date DESC);

CREATE INDEX IF NOT EXISTS ix_swing_positions_symbol_closed 
ON swing_positions (symbol, is_closed);


-- ----------------------------------------------------------------------------
-- 4. Initial Seed Data
-- ----------------------------------------------------------------------------
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
