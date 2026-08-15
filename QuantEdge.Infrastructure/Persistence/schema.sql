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

-- ----------------------------------------------------------------------------
-- 5. User Authentication & Role Management Schema
-- ----------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS app_users (
    id SERIAL PRIMARY KEY,
    full_name VARCHAR(150) NOT NULL,
    email VARCHAR(255) NULL,
    mobile_no VARCHAR(20) NULL,
    username VARCHAR(100) NOT NULL UNIQUE,
    password_hash VARCHAR(255) NOT NULL,
    role VARCHAR(50) NOT NULL DEFAULT 'User',
    created_at TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT NOW()
);

-- Schema migration helper: Ensure email and mobile_no are nullable
ALTER TABLE app_users ALTER COLUMN email DROP NOT NULL;
ALTER TABLE app_users ALTER COLUMN mobile_no DROP NOT NULL;

CREATE INDEX IF NOT EXISTS ix_app_users_email ON app_users (LOWER(email));
CREATE INDEX IF NOT EXISTS ix_app_users_username ON app_users (LOWER(username));



-- ----------------------------------------------------------------------------
-- 5. Paper Trading Tables
-- ----------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS paper_accounts (
    id SERIAL PRIMARY KEY,
    user_id VARCHAR(100) NOT NULL DEFAULT 'default_user',
    account_name VARCHAR(100) NOT NULL DEFAULT 'Virtual Trading Account',
    initial_balance NUMERIC(18, 4) NOT NULL DEFAULT 100000.00,
    current_balance NUMERIC(18, 4) NOT NULL DEFAULT 100000.00,
    used_margin NUMERIC(18, 4) NOT NULL DEFAULT 0.00,
    realized_pnl NUMERIC(18, 4) NOT NULL DEFAULT 0.00,
    created_at TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS paper_orders (
    id SERIAL PRIMARY KEY,
    account_id INT NOT NULL REFERENCES paper_accounts(id) ON DELETE CASCADE,
    symbol VARCHAR(50) NOT NULL,
    order_type INT NOT NULL,
    side INT NOT NULL,
    quantity INT NOT NULL,
    price NUMERIC(18, 4) NOT NULL,
    trigger_price NUMERIC(18, 4),
    stop_loss NUMERIC(18, 4),
    take_profit NUMERIC(18, 4),
    status INT NOT NULL DEFAULT 0,
    filled_price NUMERIC(18, 4),
    filled_at TIMESTAMP WITH TIME ZONE,
    trade_type INT NOT NULL DEFAULT 0,
    created_at TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT NOW(),
    remarks VARCHAR(255)
);

CREATE TABLE IF NOT EXISTS paper_positions (
    id SERIAL PRIMARY KEY,
    account_id INT NOT NULL REFERENCES paper_accounts(id) ON DELETE CASCADE,
    symbol VARCHAR(50) NOT NULL,
    side INT NOT NULL,
    quantity INT NOT NULL,
    average_entry_price NUMERIC(18, 4) NOT NULL,
    current_price NUMERIC(18, 4) NOT NULL,
    unrealized_pnl NUMERIC(18, 4) NOT NULL DEFAULT 0.00,
    stop_loss NUMERIC(18, 4),
    take_profit NUMERIC(18, 4),
    status INT NOT NULL DEFAULT 0,
    trade_type INT NOT NULL DEFAULT 0,
    exit_reason VARCHAR(100),
    opened_at TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT NOW(),
    closed_at TIMESTAMP WITH TIME ZONE,
    realized_pnl NUMERIC(18, 4) NOT NULL DEFAULT 0.00
);

CREATE TABLE IF NOT EXISTS paper_trade_history (
    id SERIAL PRIMARY KEY,
    account_id INT NOT NULL REFERENCES paper_accounts(id) ON DELETE CASCADE,
    order_id INT NOT NULL,
    symbol VARCHAR(50) NOT NULL,
    side INT NOT NULL,
    quantity INT NOT NULL,
    entry_price NUMERIC(18, 4) NOT NULL DEFAULT 0.00,
    executed_price NUMERIC(18, 4) NOT NULL,
    realized_pnl NUMERIC(18, 4) NOT NULL DEFAULT 0.00,
    trade_type INT NOT NULL DEFAULT 0,
    exit_reason VARCHAR(100),
    executed_at TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT NOW(),
    remarks VARCHAR(255)
);

-- ----------------------------------------------------------------------------
-- 6. Auto Paper Trading Tables
-- ----------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS auto_trade_settings (
    id SERIAL PRIMARY KEY,
    user_id VARCHAR(100) NOT NULL DEFAULT 'default_user' UNIQUE,
    is_auto_trade_enabled BOOLEAN NOT NULL DEFAULT FALSE,
    available_capital NUMERIC(18, 4) NOT NULL DEFAULT 100000.00,
    profit_target_pct NUMERIC(5, 2) NOT NULL DEFAULT 5.00,
    stop_loss_pct NUMERIC(5, 2) NULL DEFAULT 3.00,
    max_duration_days INT NOT NULL DEFAULT 20,
    max_trades_per_day INT NOT NULL DEFAULT 5,
    fixed_amount_per_trade NUMERIC(18, 4) NOT NULL DEFAULT 20000.00,
    min_conditions_match INT NOT NULL DEFAULT 12,
    trading_window_start VARCHAR(10) NOT NULL DEFAULT '09:15',
    trading_window_end VARCHAR(10) NOT NULL DEFAULT '15:30',
    updated_at TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS auto_trade_execution_logs (
    id SERIAL PRIMARY KEY,
    user_id VARCHAR(100) NOT NULL DEFAULT 'default_user',
    symbol VARCHAR(50) NOT NULL,
    action_type VARCHAR(50) NOT NULL,
    price NUMERIC(18, 4),
    quantity INT,
    reason VARCHAR(255),
    executed_at TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT NOW()
);

-- Idempotent Column Additions for existing installations
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='paper_orders' AND column_name='trade_type') THEN
        ALTER TABLE paper_orders ADD COLUMN trade_type INT NOT NULL DEFAULT 0;
    END IF;

    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='paper_positions' AND column_name='trade_type') THEN
        ALTER TABLE paper_positions ADD COLUMN trade_type INT NOT NULL DEFAULT 0;
    END IF;

    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='paper_positions' AND column_name='exit_reason') THEN
        ALTER TABLE paper_positions ADD COLUMN exit_reason VARCHAR(100);
    END IF;

    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='paper_trade_history' AND column_name='entry_price') THEN
        ALTER TABLE paper_trade_history ADD COLUMN entry_price NUMERIC(18, 4) NOT NULL DEFAULT 0.00;
    END IF;

    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='paper_trade_history' AND column_name='trade_type') THEN
        ALTER TABLE paper_trade_history ADD COLUMN trade_type INT NOT NULL DEFAULT 0;
    END IF;

    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='paper_trade_history' AND column_name='exit_reason') THEN
        ALTER TABLE paper_trade_history ADD COLUMN exit_reason VARCHAR(100);
    END IF;
END;
$$;

CREATE INDEX IF NOT EXISTS ix_paper_orders_account ON paper_orders(account_id, status);
CREATE INDEX IF NOT EXISTS ix_paper_positions_account ON paper_positions(account_id, status);
CREATE INDEX IF NOT EXISTS ix_paper_trade_history_account ON paper_trade_history(account_id);
CREATE INDEX IF NOT EXISTS ix_auto_trade_logs_user ON auto_trade_execution_logs(user_id, executed_at);

-- ----------------------------------------------------------------------------
-- 7. Auto Real Trading Tables (Live Broker Money)
-- ----------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS real_trade_settings (
    id SERIAL PRIMARY KEY,
    user_id INT NOT NULL DEFAULT 1 UNIQUE,
    is_real_trade_enabled BOOLEAN NOT NULL DEFAULT FALSE,
    available_capital NUMERIC(18, 4) NOT NULL DEFAULT 2000.00,
    profit_target_pct NUMERIC(5, 2) NOT NULL DEFAULT 5.00,
    stop_loss_pct NUMERIC(5, 2) NULL,
    trailing_sl_enabled BOOLEAN NOT NULL DEFAULT FALSE,
    trailing_sl_pct NUMERIC(5, 2) NULL,
    max_duration_days INT NOT NULL DEFAULT 20,
    max_trades_per_day INT NOT NULL DEFAULT 5,
    fixed_amount_per_trade NUMERIC(18, 4) NOT NULL DEFAULT 400.00,
    max_daily_loss_limit NUMERIC(18, 4) NULL,
    product_type VARCHAR(10) NOT NULL DEFAULT 'CNC',
    min_conditions_match INT NOT NULL DEFAULT 10,
    trading_window_start VARCHAR(10) NOT NULL DEFAULT '09:15',
    trading_window_end VARCHAR(10) NOT NULL DEFAULT '15:30',
    updated_at TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS real_orders (
    id SERIAL PRIMARY KEY,
    user_id INT NOT NULL DEFAULT 1,
    broker_order_id VARCHAR(100),
    symbol VARCHAR(50) NOT NULL,
    side INT NOT NULL,
    quantity INT NOT NULL,
    order_type INT NOT NULL DEFAULT 0,
    price NUMERIC(18, 4) NOT NULL,
    stop_loss NUMERIC(18, 4),
    take_profit NUMERIC(18, 4),
    status INT NOT NULL DEFAULT 0,
    filled_price NUMERIC(18, 4) NOT NULL DEFAULT 0.00,
    filled_at TIMESTAMP WITH TIME ZONE,
    rejection_reason VARCHAR(255),
    trade_type INT NOT NULL DEFAULT 1,
    remarks VARCHAR(255),
    created_at TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS real_positions (
    id SERIAL PRIMARY KEY,
    user_id INT NOT NULL DEFAULT 1,
    symbol VARCHAR(50) NOT NULL,
    side INT NOT NULL,
    quantity INT NOT NULL,
    average_entry_price NUMERIC(18, 4) NOT NULL,
    current_price NUMERIC(18, 4) NOT NULL,
    unrealized_pnl NUMERIC(18, 4) NOT NULL DEFAULT 0.00,
    stop_loss NUMERIC(18, 4),
    take_profit NUMERIC(18, 4),
    trailing_stop_loss NUMERIC(18, 4),
    status INT NOT NULL DEFAULT 0,
    trade_type INT NOT NULL DEFAULT 1,
    exit_reason VARCHAR(100),
    opened_at TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT NOW(),
    closed_at TIMESTAMP WITH TIME ZONE,
    realized_pnl NUMERIC(18, 4) NOT NULL DEFAULT 0.00
);

CREATE TABLE IF NOT EXISTS real_trade_history (
    id SERIAL PRIMARY KEY,
    user_id INT NOT NULL DEFAULT 1,
    order_id INT,
    broker_order_id VARCHAR(100),
    symbol VARCHAR(50) NOT NULL,
    side INT NOT NULL,
    quantity INT NOT NULL,
    entry_price NUMERIC(18, 4) NOT NULL DEFAULT 0.00,
    executed_price NUMERIC(18, 4) NOT NULL,
    realized_pnl NUMERIC(18, 4) NOT NULL DEFAULT 0.00,
    trade_type INT NOT NULL DEFAULT 1,
    exit_reason VARCHAR(100),
    executed_at TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT NOW(),
    remarks VARCHAR(255)
);

CREATE TABLE IF NOT EXISTS real_trade_execution_logs (
    id SERIAL PRIMARY KEY,
    user_id INT NOT NULL DEFAULT 1,
    symbol VARCHAR(50) NOT NULL,
    action_type VARCHAR(50) NOT NULL,
    price NUMERIC(18, 4),
    quantity INT,
    reason VARCHAR(255),
    executed_at TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS ix_real_orders_user ON real_orders(user_id, status);
CREATE INDEX IF NOT EXISTS ix_real_positions_user ON real_positions(user_id, status);
CREATE INDEX IF NOT EXISTS ix_real_trade_history_user ON real_trade_history(user_id);
CREATE INDEX IF NOT EXISTS ix_real_trade_logs_user ON real_trade_execution_logs(user_id, executed_at);



