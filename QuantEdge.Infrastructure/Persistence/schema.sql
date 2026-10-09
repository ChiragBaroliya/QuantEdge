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
    user_id         INT          NOT NULL DEFAULT 1,
    client_id       VARCHAR(50),
    user_name       VARCHAR(100),
    user_email      VARCHAR(100),
    api_key         VARCHAR(50)  PRIMARY KEY,
    api_secret      VARCHAR(100),
    access_token    VARCHAR(255) NOT NULL,
    is_active       BOOLEAN      NOT NULL DEFAULT FALSE,
    is_ddpi_enabled BOOLEAN      NOT NULL DEFAULT FALSE,
    created_at      TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT NOW()
);

-- Migrations for existing databases
ALTER TABLE zerodha_sessions ADD COLUMN IF NOT EXISTS user_id INT DEFAULT 1;
ALTER TABLE zerodha_sessions ADD COLUMN IF NOT EXISTS client_id VARCHAR(50);
ALTER TABLE zerodha_sessions ADD COLUMN IF NOT EXISTS user_name VARCHAR(100);
ALTER TABLE zerodha_sessions ADD COLUMN IF NOT EXISTS user_email VARCHAR(100);
ALTER TABLE zerodha_sessions ADD COLUMN IF NOT EXISTS is_ddpi_enabled BOOLEAN DEFAULT FALSE;
ALTER TABLE zerodha_sessions ADD COLUMN IF NOT EXISTS api_secret VARCHAR(100);

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

-- Table: swing_slot_recommendations (30-Minute Interval Swing Recommendations)
CREATE TABLE IF NOT EXISTS swing_slot_recommendations (
    id SERIAL PRIMARY KEY,
    scan_date DATE NOT NULL,
    slot_time TIMESTAMP WITH TIME ZONE NOT NULL,
    slot_label VARCHAR(20) NOT NULL,
    symbol VARCHAR(50) NOT NULL,
    decision VARCHAR(20) NOT NULL,
    score INT NOT NULL,
    confidence_pct NUMERIC(5, 2),
    entry_price NUMERIC(18, 4),
    stop_loss NUMERIC(18, 4),
    target1 NUMERIC(18, 4),
    target2 NUMERIC(18, 4),
    risk_reward_ratio NUMERIC(18, 4),
    volume_multiplier NUMERIC(18, 4),
    rsi14 NUMERIC(18, 4),
    adx14 NUMERIC(18, 4),
    ema20 NUMERIC(18, 4),
    ema50 NUMERIC(18, 4),
    ema200 NUMERIC(18, 4),
    passed_rules TEXT,
    failed_rules TEXT,
    reason TEXT,
    timeframe_used VARCHAR(50) DEFAULT '1D + 15M + 60M',
    checklist_json JSONB,
    created_at TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT NOW(),
    CONSTRAINT uq_swing_slot_rec UNIQUE (scan_date, slot_label, symbol)
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

CREATE INDEX IF NOT EXISTS ix_swing_slot_rec_date_slot 
ON swing_slot_recommendations (scan_date, slot_label);

CREATE INDEX IF NOT EXISTS ix_swing_slot_rec_symbol
ON swing_slot_recommendations (symbol, scan_date);

-- Table: swing_strategy_settings (single-row global tuning for SwingDecisionEngine)
CREATE TABLE IF NOT EXISTS swing_strategy_settings (
    id INT PRIMARY KEY DEFAULT 1,
    buy_score_threshold INT NOT NULL DEFAULT 70,
    watch_score_threshold INT NOT NULL DEFAULT 50,
    market_context_score_penalty INT NOT NULL DEFAULT 10,
    market_context_position_size_factor NUMERIC(5, 2) NOT NULL DEFAULT 0.5,
    market_protection_buffer_pct NUMERIC(6, 4) NOT NULL DEFAULT 0.005,
    -- TRUE: NIFTY market filter is a mandatory hard filter (no new entries when NIFTY fails or data is missing)
    require_nifty_market_filter BOOLEAN NOT NULL DEFAULT TRUE,
    updated_at TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT NOW(),
    CONSTRAINT chk_swing_strategy_settings_singleton CHECK (id = 1)
);
ALTER TABLE swing_strategy_settings ADD COLUMN IF NOT EXISTS require_nifty_market_filter BOOLEAN NOT NULL DEFAULT TRUE;

INSERT INTO swing_strategy_settings (id) VALUES (1) ON CONFLICT (id) DO NOTHING;

-- Table: market_structure_settings (single-row global tuning for MarketStructureAnalysisService -
-- Support/Resistance, Buyer/Seller Strength, Volume Confirmation analysis on the Signal Dashboard chart)
CREATE TABLE IF NOT EXISTS market_structure_settings (
    id INT PRIMARY KEY DEFAULT 1,
    swing_left_bars INT NOT NULL DEFAULT 3,
    swing_right_bars INT NOT NULL DEFAULT 3,
    zone_proximity_pct NUMERIC(6, 4) NOT NULL DEFAULT 0.003,
    min_touches INT NOT NULL DEFAULT 2,
    near_zone_proximity_pct NUMERIC(6, 4) NOT NULL DEFAULT 0.005,
    max_zones_per_side INT NOT NULL DEFAULT 2,
    volume_lookback_period INT NOT NULL DEFAULT 20,
    strong_volume_threshold NUMERIC(5, 2) NOT NULL DEFAULT 1.5,
    very_strong_volume_threshold NUMERIC(5, 2) NOT NULL DEFAULT 2.0,
    rejection_volume_threshold NUMERIC(5, 2) NOT NULL DEFAULT 1.3,
    price_action_weight INT NOT NULL DEFAULT 35,
    volume_weight INT NOT NULL DEFAULT 30,
    candle_pattern_weight INT NOT NULL DEFAULT 15,
    support_resistance_weight INT NOT NULL DEFAULT 20,
    touches_weight INT NOT NULL DEFAULT 40,
    volume_reaction_weight INT NOT NULL DEFAULT 35,
    recency_weight INT NOT NULL DEFAULT 25,
    strong_zone_score_threshold INT NOT NULL DEFAULT 70,
    medium_zone_score_threshold INT NOT NULL DEFAULT 40,
    updated_at TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT NOW(),
    CONSTRAINT chk_market_structure_settings_singleton CHECK (id = 1)
);

INSERT INTO market_structure_settings (id) VALUES (1) ON CONFLICT (id) DO NOTHING;



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
    trailing_stop_loss NUMERIC(18, 4),
    stop_loss_pct NUMERIC(9, 4),
    trailing_sl_pct NUMERIC(9, 4),
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
    trailing_sl_pct NUMERIC(5, 2) NULL DEFAULT 2.00,
    max_duration_days INT NOT NULL DEFAULT 20,
    max_trades_per_day INT NOT NULL DEFAULT 5,
    fixed_amount_per_trade NUMERIC(18, 4) NOT NULL DEFAULT 20000.00,
    min_conditions_match INT NOT NULL DEFAULT 12,
    trading_window_start VARCHAR(10) NOT NULL DEFAULT '09:15',
    trading_window_end VARCHAR(10) NOT NULL DEFAULT '15:30',
    entry_delay_minutes INT NOT NULL DEFAULT 15,
    max_daily_loss_limit NUMERIC(18, 4) NULL,
    exit_mode VARCHAR(20) NOT NULL DEFAULT 'SWING_CLOSE',
    close_check_time VARCHAR(10) NOT NULL DEFAULT '15:15',
    stop_loss_atr_mult NUMERIC(5, 2) NOT NULL DEFAULT 1.50,
    trail_atr_mult NUMERIC(5, 2) NOT NULL DEFAULT 3.00,
    target_atr_mult NUMERIC(5, 2) NOT NULL DEFAULT 3.00,
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
    entry_delay_minutes INT NOT NULL DEFAULT 15,
    exit_mode VARCHAR(20) NOT NULL DEFAULT 'SWING_CLOSE',
    close_check_time VARCHAR(10) NOT NULL DEFAULT '15:15',
    stop_loss_atr_mult NUMERIC(5, 2) NOT NULL DEFAULT 1.50,
    trail_atr_mult NUMERIC(5, 2) NOT NULL DEFAULT 3.00,
    target_atr_mult NUMERIC(5, 2) NOT NULL DEFAULT 3.00,
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
    stop_loss_pct NUMERIC(9, 4),
    trailing_sl_pct NUMERIC(9, 4),
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

-- Migrations for existing databases
-- Trade-wise SL % / Trailing SL % for Manual Real Trade positions (NULL for Auto/engine-driven positions).
ALTER TABLE real_positions ADD COLUMN IF NOT EXISTS stop_loss_pct NUMERIC(9, 4);
ALTER TABLE real_positions ADD COLUMN IF NOT EXISTS trailing_sl_pct NUMERIC(9, 4);

-- Auto Paper Trade: Stop Loss % is now mandatory-with-fallback (like Real Trade) and Trailing Stop
-- Loss % is a new mandatory global setting; the effective %s used are recorded on the position itself.
ALTER TABLE auto_trade_settings ADD COLUMN IF NOT EXISTS trailing_sl_pct NUMERIC(5, 2) DEFAULT 2.00;
ALTER TABLE paper_positions ADD COLUMN IF NOT EXISTS trailing_stop_loss NUMERIC(18, 4);
ALTER TABLE paper_positions ADD COLUMN IF NOT EXISTS stop_loss_pct NUMERIC(9, 4);
ALTER TABLE paper_positions ADD COLUMN IF NOT EXISTS trailing_sl_pct NUMERIC(9, 4);

-- Real Trade: opening entry delay - new BUY signals are held back this many minutes after
-- trading_window_start to let the opening auction's gap/volatility resolve. Exits are unaffected.
ALTER TABLE real_trade_settings ADD COLUMN IF NOT EXISTS entry_delay_minutes INT NOT NULL DEFAULT 15;

-- Swing exit policy (SwingTradeRules) - shared by Auto Paper and Auto Real Trading so both run the
-- same buy/sell rules: stops judged on a closing basis, emergency stop live, trailing SL activated
-- only after +1 ATR. exit_mode 'INTRADAY' restores the previous every-tick behavior.
ALTER TABLE real_trade_settings ADD COLUMN IF NOT EXISTS exit_mode VARCHAR(20) NOT NULL DEFAULT 'SWING_CLOSE';
ALTER TABLE real_trade_settings ADD COLUMN IF NOT EXISTS close_check_time VARCHAR(10) NOT NULL DEFAULT '15:15';
ALTER TABLE real_trade_settings ADD COLUMN IF NOT EXISTS stop_loss_atr_mult NUMERIC(5, 2) NOT NULL DEFAULT 1.50;
ALTER TABLE real_trade_settings ADD COLUMN IF NOT EXISTS trail_atr_mult NUMERIC(5, 2) NOT NULL DEFAULT 3.00;
ALTER TABLE real_trade_settings ADD COLUMN IF NOT EXISTS target_atr_mult NUMERIC(5, 2) NOT NULL DEFAULT 3.00;
ALTER TABLE auto_trade_settings ADD COLUMN IF NOT EXISTS entry_delay_minutes INT NOT NULL DEFAULT 15;
ALTER TABLE auto_trade_settings ADD COLUMN IF NOT EXISTS max_daily_loss_limit NUMERIC(18, 4) NULL;
ALTER TABLE auto_trade_settings ADD COLUMN IF NOT EXISTS exit_mode VARCHAR(20) NOT NULL DEFAULT 'SWING_CLOSE';
ALTER TABLE auto_trade_settings ADD COLUMN IF NOT EXISTS close_check_time VARCHAR(10) NOT NULL DEFAULT '15:15';
ALTER TABLE auto_trade_settings ADD COLUMN IF NOT EXISTS stop_loss_atr_mult NUMERIC(5, 2) NOT NULL DEFAULT 1.50;
ALTER TABLE auto_trade_settings ADD COLUMN IF NOT EXISTS trail_atr_mult NUMERIC(5, 2) NOT NULL DEFAULT 3.00;
ALTER TABLE auto_trade_settings ADD COLUMN IF NOT EXISTS target_atr_mult NUMERIC(5, 2) NOT NULL DEFAULT 3.00;

-- Signal Dashboard: ADX (14) trend-strength indicator, added alongside RSI/EMA/MACD/VWAP.
-- market_indicators_* tables are created dynamically per-timeframe by sp_insert_market_indicator
-- (see stored_procedures.sql), so any of these may not exist yet on a given database - ALTER
-- TABLE IF EXISTS ... ADD COLUMN IF NOT EXISTS is a no-op for the ones that don't.
ALTER TABLE IF EXISTS market_indicators_1m ADD COLUMN IF NOT EXISTS adx NUMERIC(18, 6) NOT NULL DEFAULT 0;
ALTER TABLE IF EXISTS market_indicators_5m ADD COLUMN IF NOT EXISTS adx NUMERIC(18, 6) NOT NULL DEFAULT 0;
ALTER TABLE IF EXISTS market_indicators_15m ADD COLUMN IF NOT EXISTS adx NUMERIC(18, 6) NOT NULL DEFAULT 0;
ALTER TABLE IF EXISTS market_indicators_60m ADD COLUMN IF NOT EXISTS adx NUMERIC(18, 6) NOT NULL DEFAULT 0;
ALTER TABLE IF EXISTS market_indicators_1d ADD COLUMN IF NOT EXISTS adx NUMERIC(18, 6) NOT NULL DEFAULT 0;



-- Signal Dashboard: per-user favorite symbols, pinned to the top of the symbol dropdown.
-- Accessed via fn_get_user_favorite_symbols, sp_add_user_favorite_symbol and sp_remove_user_favorite_symbol.
CREATE TABLE IF NOT EXISTS user_favorite_symbols (
    user_id INT NOT NULL,
    symbol VARCHAR(50) NOT NULL,
    created_at TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT NOW(),
    PRIMARY KEY (user_id, symbol)
);

-- ----------------------------------------------------------------------------
-- Manual Paper Trading Tables
-- Settings and execution logs for Manual Paper Trading (ManualPaperTradeService), kept separate from
-- auto_trade_settings / auto_trade_execution_logs so Auto Paper Trading is unaffected. Orders,
-- positions and trade history still live in the shared paper_* tables (trade_type = 0 / Manual).
-- ----------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS manual_paper_trade_settings (
    id SERIAL PRIMARY KEY,
    user_id INT NOT NULL DEFAULT 1 UNIQUE,
    is_manual_trade_enabled BOOLEAN NOT NULL DEFAULT TRUE,
    available_capital NUMERIC(18, 4) NOT NULL DEFAULT 100000.00,
    profit_target_pct NUMERIC(5, 2) NOT NULL DEFAULT 5.00,
    stop_loss_pct NUMERIC(5, 2) NOT NULL DEFAULT 3.00,
    trailing_sl_pct NUMERIC(5, 2) NOT NULL DEFAULT 2.00,
    max_duration_days INT NOT NULL DEFAULT 20,
    max_trades_per_day INT NOT NULL DEFAULT 5,
    fixed_amount_per_trade NUMERIC(18, 4) NOT NULL DEFAULT 20000.00,
    trading_window_start VARCHAR(10) NOT NULL DEFAULT '09:15',
    trading_window_end VARCHAR(10) NOT NULL DEFAULT '15:30',
    entry_delay_minutes INT NOT NULL DEFAULT 15,
    max_daily_loss_limit NUMERIC(18, 4) NULL,
    exit_mode VARCHAR(20) NOT NULL DEFAULT 'SWING_CLOSE',
    close_check_time VARCHAR(10) NOT NULL DEFAULT '15:15',
    stop_loss_atr_mult NUMERIC(5, 2) NOT NULL DEFAULT 1.50,
    trail_atr_mult NUMERIC(5, 2) NOT NULL DEFAULT 3.00,
    target_atr_mult NUMERIC(5, 2) NOT NULL DEFAULT 3.00,
    updated_at TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS manual_paper_trade_execution_logs (
    id SERIAL PRIMARY KEY,
    user_id INT NOT NULL DEFAULT 1,
    symbol VARCHAR(50) NOT NULL,
    action_type VARCHAR(50) NOT NULL,
    price NUMERIC(18, 4),
    quantity INT,
    reason VARCHAR(255),
    executed_at TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT NOW()
);

-- Idempotent fix for databases where these tables were first created with user_id VARCHAR:
-- convert to INT (same as the real_* tables). 'default_user' / non-numeric values map to user 1.
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM information_schema.columns
               WHERE table_name = 'manual_paper_trade_settings' AND column_name = 'user_id' AND data_type = 'character varying') THEN
        ALTER TABLE manual_paper_trade_settings ALTER COLUMN user_id DROP DEFAULT;
        ALTER TABLE manual_paper_trade_settings ALTER COLUMN user_id TYPE INT
            USING (CASE WHEN user_id ~ '^[0-9]+$' THEN user_id::INT ELSE 1 END);
        ALTER TABLE manual_paper_trade_settings ALTER COLUMN user_id SET DEFAULT 1;
    END IF;

    IF EXISTS (SELECT 1 FROM information_schema.columns
               WHERE table_name = 'manual_paper_trade_execution_logs' AND column_name = 'user_id' AND data_type = 'character varying') THEN
        ALTER TABLE manual_paper_trade_execution_logs ALTER COLUMN user_id DROP DEFAULT;
        ALTER TABLE manual_paper_trade_execution_logs ALTER COLUMN user_id TYPE INT
            USING (CASE WHEN user_id ~ '^[0-9]+$' THEN user_id::INT ELSE 1 END);
        ALTER TABLE manual_paper_trade_execution_logs ALTER COLUMN user_id SET DEFAULT 1;
    END IF;
END;
$$;

CREATE INDEX IF NOT EXISTS ix_manual_paper_trade_logs_user ON manual_paper_trade_execution_logs(user_id, executed_at);

-- ----------------------------------------------------------------------------
-- Manual Paper Trading - own orders / positions / trade history tables.
-- Fully manual (Buy, Close, Edit levels from the Manual Trading page only). No Worker job, paper
-- matching engine or Auto Paper / Auto Real code reads or writes these tables.
-- ----------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS manual_paper_orders (
    id SERIAL PRIMARY KEY,
    user_id INT NOT NULL DEFAULT 1,
    position_id INT NULL,
    symbol VARCHAR(50) NOT NULL,
    side INT NOT NULL,                      -- 0 = BUY, 1 = SELL
    order_type INT NOT NULL DEFAULT 0,      -- 0 = Market
    quantity INT NOT NULL,
    price NUMERIC(18, 4) NOT NULL,
    stop_loss NUMERIC(18, 4),
    take_profit NUMERIC(18, 4),
    status INT NOT NULL DEFAULT 1,          -- 1 = Filled
    filled_price NUMERIC(18, 4),
    filled_at TIMESTAMP WITH TIME ZONE,
    created_at TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT NOW(),
    remarks VARCHAR(255)
);

CREATE TABLE IF NOT EXISTS manual_paper_positions (
    id SERIAL PRIMARY KEY,
    user_id INT NOT NULL DEFAULT 1,
    symbol VARCHAR(50) NOT NULL,
    side INT NOT NULL DEFAULT 0,            -- 0 = BUY (long), 1 = SELL (short)
    quantity INT NOT NULL,
    average_entry_price NUMERIC(18, 4) NOT NULL,
    stop_loss NUMERIC(18, 4),
    trailing_sl_pct NUMERIC(9, 4),
    take_profit NUMERIC(18, 4),
    status INT NOT NULL DEFAULT 0,          -- 0 = OPEN, 1 = CLOSED
    exit_price NUMERIC(18, 4),
    exit_reason VARCHAR(100),
    realized_pnl NUMERIC(18, 4) NOT NULL DEFAULT 0.00,
    opened_at TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT NOW(),
    closed_at TIMESTAMP WITH TIME ZONE,
    updated_at TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS manual_paper_trade_history (
    id SERIAL PRIMARY KEY,
    user_id INT NOT NULL DEFAULT 1,
    order_id INT NULL,
    position_id INT NULL,
    symbol VARCHAR(50) NOT NULL,
    side INT NOT NULL,
    quantity INT NOT NULL,
    entry_price NUMERIC(18, 4) NOT NULL DEFAULT 0.00,
    executed_price NUMERIC(18, 4) NOT NULL,
    realized_pnl NUMERIC(18, 4) NOT NULL DEFAULT 0.00,
    exit_reason VARCHAR(100),
    executed_at TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT NOW(),
    remarks VARCHAR(255)
);

CREATE INDEX IF NOT EXISTS ix_manual_paper_orders_user ON manual_paper_orders(user_id, created_at);
CREATE INDEX IF NOT EXISTS ix_manual_paper_positions_user ON manual_paper_positions(user_id, status);
CREATE INDEX IF NOT EXISTS ix_manual_paper_trade_history_user ON manual_paper_trade_history(user_id, executed_at);

-- One-time move of Manual Paper Trade rows created by the Manual Trading page while it still used the
-- shared paper_* tables (identified by trade_type = 0 plus the page's own trailing_sl_pct / remarks).
-- Rows are copied into the manual_* tables and removed from paper_* in the same block, so re-running
-- finds nothing left to move. Older manual paper rows (before the Manual Trading page) are not touched.
DO $$
BEGIN
    INSERT INTO manual_paper_positions (
        user_id, symbol, side, quantity, average_entry_price, stop_loss, trailing_sl_pct, take_profit,
        status, exit_price, exit_reason, realized_pnl, opened_at, closed_at, updated_at)
    SELECT 1, p.symbol, p.side, p.quantity, p.average_entry_price, p.stop_loss, p.trailing_sl_pct, p.take_profit,
           CASE WHEN p.status = 0 THEN 0 ELSE 1 END,
           CASE WHEN p.status = 0 THEN NULL ELSE p.current_price END,
           p.exit_reason, p.realized_pnl, p.opened_at, p.closed_at, NOW()
    FROM paper_positions p
    WHERE p.trade_type = 0 AND p.trailing_sl_pct IS NOT NULL;
    DELETE FROM paper_positions WHERE trade_type = 0 AND trailing_sl_pct IS NOT NULL;

    INSERT INTO manual_paper_orders (user_id, symbol, side, order_type, quantity, price, stop_loss, take_profit,
                                     status, filled_price, filled_at, created_at, remarks)
    SELECT 1, o.symbol, o.side, o.order_type, o.quantity, o.price, o.stop_loss, o.take_profit,
           o.status, o.filled_price, o.filled_at, o.created_at, o.remarks
    FROM paper_orders o
    WHERE o.trade_type = 0 AND o.remarks LIKE 'Manual Paper %';
    DELETE FROM paper_orders WHERE trade_type = 0 AND remarks LIKE 'Manual Paper %';

    INSERT INTO manual_paper_trade_history (user_id, symbol, side, quantity, entry_price, executed_price,
                                            realized_pnl, exit_reason, executed_at, remarks)
    SELECT 1, h.symbol, h.side, h.quantity, COALESCE(h.entry_price, 0.00), h.executed_price,
           h.realized_pnl, h.exit_reason, h.executed_at, h.remarks
    FROM paper_trade_history h
    WHERE h.trade_type = 0 AND h.remarks LIKE 'Manual Paper %';
    DELETE FROM paper_trade_history WHERE trade_type = 0 AND remarks LIKE 'Manual Paper %';
END;
$$;

-- Manual Short Selling. A position's side is 0 = BUY (long) or 1 = SELL (short), so a history row's side no
-- longer says whether it opened or closed a trade: is_exit does. Every row before shorts existed was a long,
-- where SELL = exit, so the backfill is side = 1 (re-runs touch nothing: only NULLs are filled).
ALTER TABLE manual_paper_trade_history ADD COLUMN IF NOT EXISTS is_exit BOOLEAN;
UPDATE manual_paper_trade_history SET is_exit = (side = 1) WHERE is_exit IS NULL;
ALTER TABLE manual_paper_trade_history ALTER COLUMN is_exit SET DEFAULT FALSE;
ALTER TABLE manual_paper_trade_history ALTER COLUMN is_exit SET NOT NULL;

-- Shorts are intraday only: no new short at/after short_entry_cutoff, and every open short is bought back
-- (auto square-off) at short_square_off_time IST.
ALTER TABLE manual_paper_trade_settings ADD COLUMN IF NOT EXISTS short_entry_cutoff VARCHAR(10) NOT NULL DEFAULT '15:00';
ALTER TABLE manual_paper_trade_settings ADD COLUMN IF NOT EXISTS short_square_off_time VARCHAR(10) NOT NULL DEFAULT '15:15';
CREATE INDEX IF NOT EXISTS ix_manual_paper_positions_open_side ON manual_paper_positions(status, side);

-- Auto Short Selling (Auto Paper + Auto Real). OFF by default for every user: an existing settings row gets
-- is_auto_short_enabled = FALSE, so nothing shorts until a user turns it on. Intraday only - no new short at/after
-- short_entry_cutoff, every open short is bought back (auto square-off) at short_square_off_time IST.
ALTER TABLE auto_trade_settings ADD COLUMN IF NOT EXISTS is_auto_short_enabled BOOLEAN NOT NULL DEFAULT FALSE;
ALTER TABLE auto_trade_settings ADD COLUMN IF NOT EXISTS short_entry_cutoff VARCHAR(10) NOT NULL DEFAULT '15:00';
ALTER TABLE auto_trade_settings ADD COLUMN IF NOT EXISTS short_square_off_time VARCHAR(10) NOT NULL DEFAULT '15:15';
ALTER TABLE real_trade_settings ADD COLUMN IF NOT EXISTS is_auto_short_enabled BOOLEAN NOT NULL DEFAULT FALSE;
ALTER TABLE real_trade_settings ADD COLUMN IF NOT EXISTS short_entry_cutoff VARCHAR(10) NOT NULL DEFAULT '15:00';
ALTER TABLE real_trade_settings ADD COLUMN IF NOT EXISTS short_square_off_time VARCHAR(10) NOT NULL DEFAULT '15:15';

-- real_orders.is_short marks both legs of a short (entry SELL, covering BUY): with shorts, the side alone no longer
-- says whether a fill opens or closes a position. Existing rows are all longs (FALSE).
ALTER TABLE real_orders ADD COLUMN IF NOT EXISTS is_short BOOLEAN NOT NULL DEFAULT FALSE;

-- History rows: is_exit says whether a row closed a trade. Left NULL (no default) for every writer that doesn't set
-- it, so readers keep the old rule for those rows: COALESCE(is_exit, side = 1 ...).
ALTER TABLE paper_trade_history ADD COLUMN IF NOT EXISTS is_exit BOOLEAN;
ALTER TABLE real_trade_history ADD COLUMN IF NOT EXISTS is_exit BOOLEAN;

-- ----------------------------------------------------------------------------
-- Sector Master
-- NSE sectoral indices used to group stocks by sector.
-- ----------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS sectors (
    id SERIAL PRIMARY KEY,
    name VARCHAR(100) UNIQUE NOT NULL,
    is_active BOOLEAN NOT NULL DEFAULT TRUE,
    created_at TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT NOW()
);

INSERT INTO sectors (name)
VALUES
    ('NIFTY AUTO'),
    ('NIFTY BANK'),
    ('NIFTY FINANCIAL SERVICES'),
    ('NIFTY FINANCIAL SERVICES 25/50'),
    ('NIFTY FMCG'),
    ('NIFTY IT'),
    ('NIFTY MEDIA'),
    ('NIFTY METAL'),
    ('NIFTY PHARMA'),
    ('NIFTY PSU BANK'),
    ('NIFTY REALTY'),
    ('NIFTY PRIVATE BANK'),
    ('NIFTY HEALTHCARE INDEX'),
    ('NIFTY CONSUMER DURABLES'),
    ('NIFTY OIL & GAS'),
    ('NIFTY MIDSMALL HEALTHCARE'),
    ('NIFTY CHEMICALS'),
    ('NIFTY500 HEALTHCARE'),
    ('NIFTY FINANCIAL SERVICES EX-BANK'),
    ('NIFTY MIDSMALL FINANCIAL SERVICES'),
    ('NIFTY MIDSMALL IT & TELECOM'),
    ('NIFTY CEMENT'),
    ('NIFTY REITS & REALTY')
ON CONFLICT (name) DO NOTHING;

-- Table: stock_sectors
-- Many-to-many link between stock_master and sectors (one symbol can belong to multiple sectors).
CREATE TABLE IF NOT EXISTS stock_sectors (
    stock_id INT NOT NULL REFERENCES stock_master(id) ON DELETE CASCADE,
    sector_id INT NOT NULL REFERENCES sectors(id) ON DELETE CASCADE,
    created_at TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT NOW(),
    PRIMARY KEY (stock_id, sector_id)
);

CREATE INDEX IF NOT EXISTS idx_stock_sectors_sector_id ON stock_sectors (sector_id);

-- ----------------------------------------------------------------------------
-- Table: live_quotes
-- Latest tick per symbol, written every ~2 s by the market-data feed (LiveQuoteRecorder) so the API - a
-- different process - shows the same live price. prev_close is Kite's ohlc.close from the quote tick (the
-- previous session's close), the reference NSE / TradingView use for the day change (DayChangeCalculator).
-- ----------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS live_quotes (
    symbol VARCHAR(50) PRIMARY KEY,
    ltp NUMERIC(18, 4) NOT NULL,
    prev_close NUMERIC(18, 4) NOT NULL DEFAULT 0,
    day_open NUMERIC(18, 4) NOT NULL DEFAULT 0,
    day_high NUMERIC(18, 4) NOT NULL DEFAULT 0,
    day_low NUMERIC(18, 4) NOT NULL DEFAULT 0,
    as_of TIMESTAMP WITH TIME ZONE NOT NULL,
    updated_at TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT NOW()
);

-- ----------------------------------------------------------------------------
-- real_orders: signal price + broker filled quantity (Plan L.1 P4/P5)
-- signal_price    = price the BUY/exit decision was made at; filled_price - signal_price = slippage.
-- filled_quantity = Zerodha's filled_quantity; below quantity when an order partly filled then cancelled.
-- Written best-effort by AutoRealTradeService; orders keep working if this hasn't been applied yet.
-- ----------------------------------------------------------------------------
ALTER TABLE real_orders ADD COLUMN IF NOT EXISTS signal_price NUMERIC(18, 4);
ALTER TABLE real_orders ADD COLUMN IF NOT EXISTS filled_quantity INT;

-- ----------------------------------------------------------------------------
-- Table: charge_rates (Plan L.1 P6 - Gross / Charges / Net P&L)
-- Dated brokerage + statutory rates per product. Percent columns are in percent (0.1 = 0.1%).
-- When Zerodha / NSE / SEBI revise a rate, INSERT a new row with the new effective_from - older trades keep the
-- rates that applied on their date. Seed values are Zerodha's published equity rates: verify at
-- https://zerodha.com/charges before relying on them. ChargesCalculator falls back to the same values in code.
-- ----------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS charge_rates (
    id SERIAL PRIMARY KEY,
    product VARCHAR(10) NOT NULL,                   -- CNC (delivery) / MIS (intraday; also same-day CNC round trips)
    effective_from DATE NOT NULL,
    brokerage_pct NUMERIC(9, 6) NOT NULL,           -- per executed order, % of order value
    brokerage_max_per_order NUMERIC(12, 2),         -- cap per executed order (NULL = no cap)
    stt_buy_pct NUMERIC(9, 6) NOT NULL,
    stt_sell_pct NUMERIC(9, 6) NOT NULL,
    exchange_txn_pct NUMERIC(9, 6) NOT NULL,        -- NSE transaction charge, both sides
    sebi_per_crore NUMERIC(12, 2) NOT NULL,         -- ₹ per ₹1 crore turnover, both sides
    stamp_buy_pct NUMERIC(9, 6) NOT NULL,           -- buy side only
    gst_pct NUMERIC(9, 4) NOT NULL,                 -- on brokerage + exchange + SEBI
    dp_per_sell NUMERIC(12, 2) NOT NULL,            -- ₹ per scrip per sell day incl. GST (delivery only)
    CONSTRAINT uq_charge_rates_product_date UNIQUE (product, effective_from)
);

INSERT INTO charge_rates (product, effective_from, brokerage_pct, brokerage_max_per_order, stt_buy_pct, stt_sell_pct,
                          exchange_txn_pct, sebi_per_crore, stamp_buy_pct, gst_pct, dp_per_sell)
VALUES ('CNC', DATE '2024-10-01', 0,    NULL, 0.1, 0.1,   0.00307, 10, 0.015, 18, 15.34),
       ('MIS', DATE '2024-10-01', 0.03, 20,   0,   0.025, 0.00307, 10, 0.003, 18, 0)
ON CONFLICT (product, effective_from) DO NOTHING;

-- ----------------------------------------------------------------------------
-- Table: broker_api_events
-- Every failed, rate-limited or skipped Zerodha call (REST, historical, WebSocket, session), from any process.
-- Feeds the header bell (category "zerodha"). Repeats of the same failure within 5 minutes are folded into one
-- row's repeat_count so an outage can't flood the table (BrokerApiEventRecorder).
-- ----------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS broker_api_events (
    id BIGSERIAL PRIMARY KEY,
    occurred_at TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT NOW(),
    source VARCHAR(20) NOT NULL,          -- REST / HISTORICAL / WEBSOCKET / SESSION / JOB
    operation VARCHAR(120) NOT NULL,      -- e.g. "GET /orders/{id}", "historical 15m", "connect"
    level VARCHAR(10) NOT NULL,           -- error / warning
    http_status INT,
    symbol VARCHAR(50),
    user_id INT,
    message VARCHAR(500),
    repeat_count INT NOT NULL DEFAULT 1,  -- this failure plus the repeats folded into it
    process_name VARCHAR(60)
);
CREATE INDEX IF NOT EXISTS ix_broker_api_events_time ON broker_api_events (occurred_at DESC);

-- ----------------------------------------------------------------------------
-- Table: real_order_charges (Plan L.1 P6 - actual charges for real trades)
-- Zerodha's own per-order charges from the Kite virtual contract note (POST /charges/orders), fetched once a day
-- after the close for every filled real order (RealOrderChargesWorker - one batched Kite call per user per day).
-- dp is not part of Kite's response: ₹ per scrip per sell day for delivery sells, added from charge_rates.
-- Reports use these instead of the ChargesCalculator estimate when both legs of a trade have a row.
-- ----------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS real_order_charges (
    order_id INT PRIMARY KEY REFERENCES real_orders(id) ON DELETE CASCADE,
    broker_order_id VARCHAR(100),
    brokerage NUMERIC(14, 4) NOT NULL DEFAULT 0,
    stt NUMERIC(14, 4) NOT NULL DEFAULT 0,
    exchange_txn NUMERIC(14, 4) NOT NULL DEFAULT 0,
    sebi NUMERIC(14, 4) NOT NULL DEFAULT 0,
    stamp NUMERIC(14, 4) NOT NULL DEFAULT 0,
    gst NUMERIC(14, 4) NOT NULL DEFAULT 0,
    dp NUMERIC(14, 4) NOT NULL DEFAULT 0,
    total NUMERIC(14, 4) NOT NULL DEFAULT 0,  -- Kite total + dp
    fetched_at TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT NOW()
);

-- ----------------------------------------------------------------------------
-- Table: nse_bhavcopy (Plan L.1 P9 - official NSE end-of-day prices)
-- NSE's CM bhavcopy (UDiFF: BhavCopy_NSE_CM_0_0_0_YYYYMMDD_F_0000.csv.zip from nsearchives.nseindia.com),
-- downloaded once a day after the close by NseBhavcopyWorker - no Zerodha call. close = NSE's official closing
-- price (the weighted average of the last 30 minutes), which is what TradingView / NSE show as the day's close.
-- After loading, the day's market_candles_1d rows for EQ-series stocks are overwritten with these official values.
-- ----------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS nse_bhavcopy (
    trade_date DATE NOT NULL,
    symbol VARCHAR(50) NOT NULL,
    series VARCHAR(10) NOT NULL,
    isin VARCHAR(20),
    open NUMERIC(18, 4) NOT NULL,
    high NUMERIC(18, 4) NOT NULL,
    low NUMERIC(18, 4) NOT NULL,
    close NUMERIC(18, 4) NOT NULL,      -- official closing price
    last NUMERIC(18, 4),                -- last traded price
    prev_close NUMERIC(18, 4),
    volume BIGINT NOT NULL DEFAULT 0,
    turnover NUMERIC(22, 2),
    loaded_at TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT NOW(),
    PRIMARY KEY (trade_date, symbol, series)
);

-- ----------------------------------------------------------------------------
-- Table: corporate_actions (Plan L.1 P10 - splits / bonuses)
-- Detected from NSE's own bhavcopy: on an ex-date NSE publishes an ADJUSTED previous close, so
-- factor = today's prev_close / the previous day's close (e.g. 0.5 for a 1:2 split or a 1:1 bonus).
-- A factor matching a simple ratio p/q (p, q <= 10) is APPLIED automatically: candles before the ex-date get
-- price x factor and volume / factor; open paper / manual / real positions get price x factor and quantity / factor.
-- Anything else (rights issues, unusual ratios) is left NEEDS_REVIEW and shown in the header bell.
-- ----------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS corporate_actions (
    id SERIAL PRIMARY KEY,
    symbol VARCHAR(50) NOT NULL,
    ex_date DATE NOT NULL,
    factor NUMERIC(18, 8) NOT NULL,       -- multiply old prices by this; divide old quantities / volumes by it
    ratio_text VARCHAR(30),               -- e.g. "price x 1/2"
    source VARCHAR(30) NOT NULL DEFAULT 'BHAVCOPY_PREVCLOSE',
    status VARCHAR(20) NOT NULL,          -- APPLIED / NEEDS_REVIEW
    details VARCHAR(500),
    detected_at TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT NOW(),
    applied_at TIMESTAMP WITH TIME ZONE,
    CONSTRAINT uq_corporate_actions_symbol_date UNIQUE (symbol, ex_date)
);

-- ----------------------------------------------------------------------------
-- Table: data_quality_issues (Plan L.7 - daily reconciliation)
-- Every mismatch found by the daily checks: our daily close vs NSE's official close (before the bhavcopy
-- overwrites it), and our open real positions vs Zerodha holdings/positions (quantity, average price).
-- Shown on the Reconciliation page; a summary goes to the header bell.
-- ----------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS data_quality_issues (
    id BIGSERIAL PRIMARY KEY,
    check_date DATE NOT NULL,
    check_type VARCHAR(40) NOT NULL,       -- DAILY_CLOSE_VS_BHAVCOPY / POSITION_QTY_VS_ZERODHA / POSITION_AVG_VS_ZERODHA / POSITION_MISSING_AT_ZERODHA
    symbol VARCHAR(50),
    user_id INT,
    ours NUMERIC(18, 4),
    external NUMERIC(18, 4),
    diff_pct NUMERIC(12, 4),
    details VARCHAR(500),
    created_at TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT NOW()
);
CREATE INDEX IF NOT EXISTS ix_data_quality_issues_date ON data_quality_issues (check_date DESC);

-- ----------------------------------------------------------------------------
-- Table: market_regime_daily (Plan Phase 1 / H - market regime)
-- One reading per trading day, computed after the close from stored data only (NIFTY + active-stock daily candles,
-- India VIX when stored) by MarketRegimeService - no Zerodha call. regime = the regime in force (changes only after two
-- days in a new band, STRONG_BEARISH at once); raw_regime = the band today's score falls in.
-- ----------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS market_regime_daily (
    trade_date DATE PRIMARY KEY,
    nifty_close NUMERIC(18, 4) NOT NULL,
    ema50 NUMERIC(18, 4),
    ema200 NUMERIC(18, 4),
    day_change_pct NUMERIC(9, 4),
    drawdown_pct NUMERIC(9, 4),
    vix NUMERIC(9, 4),
    vol_source VARCHAR(20),
    vol_percentile NUMERIC(6, 2),
    pct_above_ema50 NUMERIC(6, 2),
    pct_above_ema200 NUMERIC(6, 2),
    net_advances_10 INT,
    breadth_stocks INT,
    trend_pts INT NOT NULL,
    breadth_pts INT NOT NULL,
    vol_pts INT NOT NULL,
    drawdown_pts INT NOT NULL,
    score INT NOT NULL,
    raw_regime VARCHAR(20) NOT NULL,
    regime VARCHAR(20) NOT NULL,
    regime_streak INT NOT NULL DEFAULT 1,
    notes VARCHAR(500),
    computed_at TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT NOW()
);

-- ----------------------------------------------------------------------------
-- Table: regime_policy (Plan H) - how new entries are allowed in each regime. Edit rows to tune without a redeploy.
-- Used by the scan workers only when swing_strategy_settings.market_gate_mode = 'REGIME'.
--   min_stock_score          engine score needed for a BUY in this regime (replaces buy_score_threshold)
--   require_relative_strength the stock must be outperforming NIFTY (engine RELATIVE_STRENGTH rule)
--   max_positions            open positions allowed per user (0 = no new entries)
--   risk_pct                 % of capital risked per trade (Auto Paper sizing)
-- ----------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS regime_policy (
    regime VARCHAR(20) PRIMARY KEY,
    min_stock_score INT NOT NULL,
    require_relative_strength BOOLEAN NOT NULL,
    max_positions INT NOT NULL,
    risk_pct NUMERIC(6, 3) NOT NULL,
    max_exposure_pct NUMERIC(6, 2) NOT NULL,
    description VARCHAR(300)
);
INSERT INTO regime_policy (regime, min_stock_score, require_relative_strength, max_positions, risk_pct, max_exposure_pct, description) VALUES
    ('BULLISH',           70, FALSE, 10, 1.000, 100, 'Normal trading'),
    ('BULLISH_WEAKENING', 75, TRUE,   7, 0.750,  70, 'Fewer, stronger trades; only stocks beating NIFTY'),
    ('SIDEWAYS',          78, TRUE,   5, 0.750,  50, 'High-quality setups only'),
    ('BEARISH',           82, TRUE,   3, 0.500,  30, 'Only leaders that keep rising while NIFTY falls; half risk'),
    ('STRONG_BEARISH',    88, TRUE,   1, 0.250,  15, 'Exceptional strength only, quarter risk (set max_positions = 0 to stop new entries)')
ON CONFLICT (regime) DO NOTHING;

-- How the scan workers gate new entries on market conditions:
--   NIFTY_FILTER (default, previous behaviour) - require_nifty_market_filter decides (all-or-nothing)
--   REGIME - market_regime_daily + regime_policy decide (strong stocks may still be bought in a weak market)
ALTER TABLE swing_strategy_settings ADD COLUMN IF NOT EXISTS market_gate_mode VARCHAR(20) NOT NULL DEFAULT 'NIFTY_FILTER';

-- ----------------------------------------------------------------------------
-- Tables: backtest_runs / backtest_trades (Plan Phase 7 - backtesting)
-- A run is queued from the Backtest page (status QUEUED), picked up by BacktestWorker in the plain "marketdatafeed"
-- worker process, replayed on stored candles only (no Zerodha calls) and finished as DONE / FAILED / CANCELLED.
--   params   the fully resolved inputs (blank fields filled from the live settings at queue time)
--   summary  KPIs, equity curve, breakdowns, threshold sweep, factor edge, data coverage, assumptions
-- ----------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS backtest_runs (
    id SERIAL PRIMARY KEY,
    user_id INT NOT NULL DEFAULT 1,
    label VARCHAR(200),
    status VARCHAR(20) NOT NULL DEFAULT 'QUEUED',
    progress_pct INT NOT NULL DEFAULT 0,
    message TEXT,
    params JSONB NOT NULL,
    summary JSONB,
    error TEXT,
    created_at TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT NOW(),
    started_at TIMESTAMP WITH TIME ZONE,
    finished_at TIMESTAMP WITH TIME ZONE
);
CREATE INDEX IF NOT EXISTS idx_backtest_runs_status ON backtest_runs (status, id);

CREATE TABLE IF NOT EXISTS backtest_trades (
    id BIGSERIAL PRIMARY KEY,
    run_id INT NOT NULL REFERENCES backtest_runs(id) ON DELETE CASCADE,
    symbol VARCHAR(50) NOT NULL,
    sector VARCHAR(100),
    regime VARCHAR(30),
    signal_time TIMESTAMP WITH TIME ZONE NOT NULL,
    entry_time TIMESTAMP WITH TIME ZONE NOT NULL,
    entry_price NUMERIC(18, 4) NOT NULL,
    exit_time TIMESTAMP WITH TIME ZONE NOT NULL,
    exit_price NUMERIC(18, 4) NOT NULL,
    exit_reason VARCHAR(200),
    exit_category VARCHAR(30),
    quantity INT NOT NULL,
    stop_loss NUMERIC(18, 4),
    target NUMERIC(18, 4),
    score INT,
    met_count INT,
    sessions_held INT,
    gross_pnl NUMERIC(18, 2),
    charges NUMERIC(18, 2),
    net_pnl NUMERIC(18, 2),
    r_multiple NUMERIC(10, 3),
    mfe_pct NUMERIC(10, 2),
    mae_pct NUMERIC(10, 2),
    factors VARCHAR(400),
    end_of_data BOOLEAN NOT NULL DEFAULT FALSE
);
CREATE INDEX IF NOT EXISTS idx_backtest_trades_run ON backtest_trades (run_id, entry_time);
