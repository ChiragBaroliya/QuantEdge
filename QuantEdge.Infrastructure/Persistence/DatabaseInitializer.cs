using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Dapper;
using QuantEdge.Infrastructure.Configurations;

namespace QuantEdge.Infrastructure.Persistence;

/// <summary>
/// Service responsible for provisioning the PostgreSQL database and running schema.sql at startup.
/// </summary>
public class DatabaseInitializer
{
    private readonly BrokerConfig _config;
    private readonly ILogger<DatabaseInitializer> _logger;

    public DatabaseInitializer(
        IOptions<BrokerConfig> config,
        ILogger<DatabaseInitializer> logger)
    {
        _config = config?.Value ?? throw new ArgumentNullException(nameof(config));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Checks and creates the target database if it doesn't exist, and initializes the tables and stored procedures.
    /// </summary>
    public async Task InitializeAsync()
    {
        _logger.LogInformation("Starting database initialization check...");

        if (string.IsNullOrWhiteSpace(_config.ConnectionString))
        {
            _logger.LogError("ConnectionString is not configured. Database initialization skipped.");
            return;
        }

        try
        {
            var builder = new NpgsqlConnectionStringBuilder(_config.ConnectionString);
            string targetDb = builder.Database ?? "quantedge";

            // 1. Check if database exists, create if not
            await EnsureDatabaseCreatedAsync(builder, targetDb);

            // 2. Connect to the target database and check if tables exist
            await EnsureSchemaInitializedAsync(targetDb);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "An error occurred during database initialization.");
            throw;
        }
    }

    private async Task EnsureDatabaseCreatedAsync(NpgsqlConnectionStringBuilder originalBuilder, string targetDb)
    {
        // Copy builder and switch database to default 'postgres'
        var systemBuilder = new NpgsqlConnectionStringBuilder(originalBuilder.ConnectionString)
        {
            Database = "postgres"
        };

        using var conn = new NpgsqlConnection(systemBuilder.ConnectionString);
        await conn.OpenAsync();

        bool dbExists = await conn.ExecuteScalarAsync<bool>(
            "SELECT EXISTS (SELECT 1 FROM pg_database WHERE datname = @dbName);",
            new { dbName = targetDb }
        );

        if (!dbExists)
        {
            _logger.LogWarning("Database '{Database}' does not exist. Creating database...", targetDb);
            
            // Note: CREATE DATABASE cannot run inside a transaction block or with parameterized DB name easily,
            // so we sanitize and string-interpolate the name directly since it is from a trusted configuration source.
            string escapedDbName = targetDb.Replace("\"", "\"\"");
            await conn.ExecuteAsync($"CREATE DATABASE \"{escapedDbName}\";");
            
            _logger.LogInformation("Database '{Database}' created successfully.", targetDb);
        }
        else
        {
            _logger.LogInformation("Database '{Database}' already exists.", targetDb);
        }
    }

    private async Task EnsureSchemaInitializedAsync(string targetDb)
    {
        using var conn = new NpgsqlConnection(_config.ConnectionString);
        await conn.OpenAsync();

        // Check if market_candles_1m table exists as a proxy for schema existence
        bool schemaExists = await conn.ExecuteScalarAsync<bool>(@"
            SELECT EXISTS (
                SELECT FROM information_schema.tables 
                WHERE table_schema = 'public' 
                AND table_name = 'market_candles_1m'
            );"
        );

        if (!schemaExists)
        {
            _logger.LogWarning("Schema tables not found in database '{Database}'. Provisioning tables...", targetDb);

            string schemaFilePath = Path.Combine(AppContext.BaseDirectory, "Persistence", "schema.sql");
            if (!File.Exists(schemaFilePath))
            {
                // Fallback for development if not in output directory yet
                schemaFilePath = Path.Combine(Directory.GetCurrentDirectory(), "Persistence", "schema.sql");
            }

            if (!File.Exists(schemaFilePath))
            {
                throw new FileNotFoundException($"Database schema file not found at '{schemaFilePath}'. Unable to initialize database.");
            }

            _logger.LogInformation("Reading schema file from: {Path}", schemaFilePath);
            string schemaSql = await File.ReadAllTextAsync(schemaFilePath);

            // Execute the schema scripts
            await conn.ExecuteAsync(schemaSql);

            _logger.LogInformation("Database tables and stored procedures provisioned successfully in '{Database}'.", targetDb);
        }
        else
        {
            _logger.LogInformation("Database schema tables are already present in '{Database}'. Skipping schema script execution.", targetDb);
        }

        // Check and provision stock_master table (supports upgrading existing databases)
        bool stockMasterExists = await conn.ExecuteScalarAsync<bool>(@"
            SELECT EXISTS (
                SELECT FROM information_schema.tables 
                WHERE table_schema = 'public' 
                AND table_name = 'stock_master'
            );"
        );

        if (!stockMasterExists)
        {
            _logger.LogWarning("Table 'stock_master' not found in database '{Database}'. Provisioning and seeding table...", targetDb);
            await conn.ExecuteAsync(@"
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
                
                CREATE INDEX IF NOT EXISTS ix_stock_master_instrument_token ON stock_master (instrument_token);
                
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
            ");
            _logger.LogInformation("Table 'stock_master' created and seeded successfully.");
        }
        else
        {
            _logger.LogInformation("Ensuring missing fields are added to 'stock_master' table in database '{Database}'...", targetDb);
            await conn.ExecuteAsync(@"
                ALTER TABLE stock_master ADD COLUMN IF NOT EXISTS exchange_token VARCHAR(50);
                ALTER TABLE stock_master ADD COLUMN IF NOT EXISTS name VARCHAR(100);
                ALTER TABLE stock_master ADD COLUMN IF NOT EXISTS last_price NUMERIC(18, 4);
                ALTER TABLE stock_master ADD COLUMN IF NOT EXISTS expiry TIMESTAMP WITH TIME ZONE;
                ALTER TABLE stock_master ADD COLUMN IF NOT EXISTS strike NUMERIC(18, 4);
                ALTER TABLE stock_master ADD COLUMN IF NOT EXISTS tick_size NUMERIC(18, 4);
                ALTER TABLE stock_master ADD COLUMN IF NOT EXISTS lot_size INT;
                ALTER TABLE stock_master ADD COLUMN IF NOT EXISTS instrument_type VARCHAR(20);
                ALTER TABLE stock_master ADD COLUMN IF NOT EXISTS segment VARCHAR(20);
                ALTER TABLE stock_master ADD COLUMN IF NOT EXISTS exchange VARCHAR(20);
                ALTER TABLE stock_master ADD COLUMN IF NOT EXISTS is_histry_stored INT DEFAULT NULL;
            ");
        }

        // Check and provision trading_signals table (supports upgrading existing databases)
        bool tradingSignalsExists = await conn.ExecuteScalarAsync<bool>(@"
            SELECT EXISTS (
                SELECT FROM information_schema.tables 
                WHERE table_schema = 'public' 
                AND table_name = 'trading_signals'
            );"
        );

        if (!tradingSignalsExists)
        {
            _logger.LogWarning("Table 'trading_signals' not found in database '{Database}'. Provisioning table...", targetDb);
            await conn.ExecuteAsync(@"
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

                CREATE INDEX IF NOT EXISTS ix_trading_signals_symbol_candle_time ON trading_signals (symbol, candle_time DESC);
            ");
            _logger.LogInformation("Table 'trading_signals' and its index created successfully.");
        }

        // Always ensure stored procedures/functions for trading_signals are provisioned/updated
        _logger.LogInformation("Ensuring PostgreSQL procedures/functions for 'trading_signals' are provisioned...");
        await conn.ExecuteAsync(@"
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
        ");
        _logger.LogInformation("PostgreSQL procedures/functions for 'trading_signals' configured successfully.");

        // Always ensure stored functions for stock_master are provisioned/updated
        _logger.LogInformation("Ensuring PostgreSQL functions for 'stock_master' are provisioned...");
        await conn.ExecuteAsync(@"
            DROP FUNCTION IF EXISTS sp_get_active_stocks();
            DROP FUNCTION IF EXISTS sp_get_stock_by_symbol(VARCHAR);
            DROP FUNCTION IF EXISTS sp_upsert_instruments(JSONB);
        ");

        await conn.ExecuteAsync(@"
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
                is_histry_stored INT,
                created_at TIMESTAMP WITH TIME ZONE
            )
            LANGUAGE plpgsql
            AS $$
            BEGIN
                RETURN QUERY
                SELECT s.id, s.symbol, s.instrument_token, s.is_active,
                       s.exchange_token, s.name, s.last_price, s.expiry,
                       s.strike, s.tick_size, s.lot_size, s.instrument_type,
                       s.segment, s.exchange, s.is_histry_stored, s.created_at
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
                is_histry_stored INT,
                created_at TIMESTAMP WITH TIME ZONE
            )
            LANGUAGE plpgsql
            AS $$
            BEGIN
                RETURN QUERY
                SELECT s.id, s.symbol, s.instrument_token, s.is_active,
                       s.exchange_token, s.name, s.last_price, s.expiry,
                       s.strike, s.tick_size, s.lot_size, s.instrument_type,
                       s.segment, s.exchange, s.is_histry_stored, s.created_at
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
                    exchange = EXCLUDED.exchange,
                    is_active = EXCLUDED.is_active;
            END;
            $$;
        ");
        _logger.LogInformation("PostgreSQL functions for 'stock_master' configured successfully.");

        // Check and provision zerodha_sessions table
        bool zerodhaSessionsExists = await conn.ExecuteScalarAsync<bool>(@"
            SELECT EXISTS (
                SELECT FROM information_schema.tables 
                WHERE table_schema = 'public' 
                AND table_name = 'zerodha_sessions'
            );"
        );

        if (!zerodhaSessionsExists)
        {
            _logger.LogWarning("Table 'zerodha_sessions' not found in database '{Database}'. Provisioning table...", targetDb);
            await conn.ExecuteAsync(@"
                CREATE TABLE IF NOT EXISTS zerodha_sessions (
                    api_key      VARCHAR(50)  PRIMARY KEY,
                    access_token VARCHAR(255) NOT NULL,
                    is_active    BOOLEAN      NOT NULL DEFAULT FALSE,
                    created_at   TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT NOW()
                );
            ");
            _logger.LogInformation("Table 'zerodha_sessions' created successfully.");
        }
        else
        {
            // Ensure is_active column exists
            bool isActiveExists = await conn.ExecuteScalarAsync<bool>(@"
                SELECT EXISTS (
                    SELECT 1 FROM information_schema.columns
                    WHERE table_name = 'zerodha_sessions' AND column_name = 'is_active'
                );"
            );
            if (!isActiveExists)
            {
                _logger.LogWarning("Column 'is_active' not found in table 'zerodha_sessions'. Provisioning column...");
                await conn.ExecuteAsync("ALTER TABLE zerodha_sessions ADD COLUMN is_active BOOLEAN NOT NULL DEFAULT FALSE;");
                _logger.LogInformation("Column 'is_active' added successfully.");
            }
        }

        // Always ensure stored procedures/functions for zerodha_sessions are provisioned/updated
        _logger.LogInformation("Ensuring PostgreSQL procedures/functions for 'zerodha_sessions' are provisioned...");
        await conn.ExecuteAsync(@"
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
                -- 6:00 AM IST = 00:30 UTC. Calculate today's 6 AM IST boundary in UTC.
                v_cutoff_time := (DATE_TRUNC('day', NOW() AT TIME ZONE 'Asia/Kolkata')
                                 + INTERVAL '6 hours')
                                 AT TIME ZONE 'Asia/Kolkata';

                -- Find the latest token for this api_key
                SELECT access_token, created_at
                INTO v_access_token, v_token_created
                FROM zerodha_sessions
                WHERE api_key = p_api_key
                LIMIT 1;

                IF v_access_token IS NULL THEN
                    RAISE NOTICE 'sp_activate_zerodha_token: No session found for api_key %', p_api_key;
                    RETURN NULL;
                END IF;

                -- Only activate if token was created AFTER today's 6 AM IST
                IF v_token_created >= v_cutoff_time THEN
                    -- Activate this token, deactivate all others (in case of multi-key scenario)
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
        ");
        _logger.LogInformation("PostgreSQL procedures/functions for 'zerodha_sessions' configured successfully.");

        // Check and provision indian_holidays table (supports upgrading existing databases)
        bool indianHolidaysExists = await conn.ExecuteScalarAsync<bool>(@"
            SELECT EXISTS (
                SELECT FROM information_schema.tables 
                WHERE table_schema = 'public' 
                AND table_name = 'indian_holidays'
            );"
        );

        if (!indianHolidaysExists)
        {
            _logger.LogWarning("Table 'indian_holidays' not found in database '{Database}'. Provisioning table...", targetDb);
            await conn.ExecuteAsync(@"
                CREATE TABLE IF NOT EXISTS indian_holidays (
                    id SERIAL PRIMARY KEY,
                    holiday_date DATE UNIQUE NOT NULL,
                    description VARCHAR(255) NOT NULL,
                    created_at TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT NOW()
                );
                
                CREATE INDEX IF NOT EXISTS ix_indian_holidays_date ON indian_holidays (holiday_date);
            ");
            _logger.LogInformation("Table 'indian_holidays' and index created successfully.");
        }

        // Always ensure stored functions/procedures for indian_holidays are provisioned/updated
        _logger.LogInformation("Ensuring PostgreSQL procedures/functions for 'indian_holidays' are provisioned...");
        await conn.ExecuteAsync(@"
            -- Drop old function first to change return type from DATE to TIMESTAMP
            DROP FUNCTION IF EXISTS sp_get_indian_holidays();
        ");

        await conn.ExecuteAsync(@"
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
        ");
        _logger.LogInformation("PostgreSQL procedures/functions for 'indian_holidays' configured successfully.");

        // Check and provision market_candles_60m table
        bool marketCandles60mExists = await conn.ExecuteScalarAsync<bool>(@"
            SELECT EXISTS (
                SELECT FROM information_schema.tables 
                WHERE table_schema = 'public' 
                AND table_name = 'market_candles_60m'
            );"
        );
        if (!marketCandles60mExists)
        {
            _logger.LogWarning("Table 'market_candles_60m' not found in database '{Database}'. Provisioning table...", targetDb);
            await conn.ExecuteAsync(@"
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
                CREATE INDEX IF NOT EXISTS ix_market_candles_60m_symbol_candle_time ON market_candles_60m (symbol, candle_time DESC);
            ");
            _logger.LogInformation("Table 'market_candles_60m' and index created successfully.");
        }

        // Check and provision market_candles_1d table
        bool marketCandles1dExists = await conn.ExecuteScalarAsync<bool>(@"
            SELECT EXISTS (
                SELECT FROM information_schema.tables 
                WHERE table_schema = 'public' 
                AND table_name = 'market_candles_1d'
            );"
        );
        if (!marketCandles1dExists)
        {
            _logger.LogWarning("Table 'market_candles_1d' not found in database '{Database}'. Provisioning table...", targetDb);
            await conn.ExecuteAsync(@"
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
                CREATE INDEX IF NOT EXISTS ix_market_candles_1d_symbol_candle_time ON market_candles_1d (symbol, candle_time DESC);
            ");
            _logger.LogInformation("Table 'market_candles_1d' and index created successfully.");
        }

        // Check and provision market_indicators_60m table
        bool marketIndicators60mExists = await conn.ExecuteScalarAsync<bool>(@"
            SELECT EXISTS (
                SELECT FROM information_schema.tables 
                WHERE table_schema = 'public' 
                AND table_name = 'market_indicators_60m'
            );"
        );
        if (!marketIndicators60mExists)
        {
            _logger.LogWarning("Table 'market_indicators_60m' not found in database '{Database}'. Provisioning table...", targetDb);
            await conn.ExecuteAsync(@"
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
                CREATE INDEX IF NOT EXISTS ix_market_indicators_60m_symbol_candle_time ON market_indicators_60m (symbol, candle_time DESC);
            ");
            _logger.LogInformation("Table 'market_indicators_60m' and index created successfully.");
        }

        // Check and provision market_indicators_1d table
        bool marketIndicators1dExists = await conn.ExecuteScalarAsync<bool>(@"
            SELECT EXISTS (
                SELECT FROM information_schema.tables 
                WHERE table_schema = 'public' 
                AND table_name = 'market_indicators_1d'
            );"
        );
        if (!marketIndicators1dExists)
        {
            _logger.LogWarning("Table 'market_indicators_1d' not found in database '{Database}'. Provisioning table...", targetDb);
            await conn.ExecuteAsync(@"
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
                CREATE INDEX IF NOT EXISTS ix_market_indicators_1d_symbol_candle_time ON market_indicators_1d (symbol, candle_time DESC);
            ");
            _logger.LogInformation("Table 'market_indicators_1d' and index created successfully.");
        }

        // Always ensure created_at column with TIMESTAMP WITH TIME ZONE exists on all timeframe tables
        _logger.LogInformation("Ensuring 'created_at' columns on all candlestick and indicator tables are typed as TIMESTAMP WITH TIME ZONE...");
        await conn.ExecuteAsync(@"
            -- market_candles_1m
            ALTER TABLE market_candles_1m ADD COLUMN IF NOT EXISTS created_at TIMESTAMP WITH TIME ZONE DEFAULT NOW();
            ALTER TABLE market_candles_1m ALTER COLUMN created_at SET DATA TYPE TIMESTAMP WITH TIME ZONE;
            ALTER TABLE market_candles_1m ALTER COLUMN created_at SET DEFAULT NOW();

            -- market_candles_5m
            ALTER TABLE market_candles_5m ADD COLUMN IF NOT EXISTS created_at TIMESTAMP WITH TIME ZONE DEFAULT NOW();
            ALTER TABLE market_candles_5m ALTER COLUMN created_at SET DATA TYPE TIMESTAMP WITH TIME ZONE;
            ALTER TABLE market_candles_5m ALTER COLUMN created_at SET DEFAULT NOW();

            -- market_candles_15m
            ALTER TABLE market_candles_15m ADD COLUMN IF NOT EXISTS created_at TIMESTAMP WITH TIME ZONE DEFAULT NOW();
            ALTER TABLE market_candles_15m ALTER COLUMN created_at SET DATA TYPE TIMESTAMP WITH TIME ZONE;
            ALTER TABLE market_candles_15m ALTER COLUMN created_at SET DEFAULT NOW();

            -- market_candles_60m
            ALTER TABLE market_candles_60m ADD COLUMN IF NOT EXISTS created_at TIMESTAMP WITH TIME ZONE DEFAULT NOW();
            ALTER TABLE market_candles_60m ALTER COLUMN created_at SET DATA TYPE TIMESTAMP WITH TIME ZONE;
            ALTER TABLE market_candles_60m ALTER COLUMN created_at SET DEFAULT NOW();

            -- market_candles_1d
            ALTER TABLE market_candles_1d ADD COLUMN IF NOT EXISTS created_at TIMESTAMP WITH TIME ZONE DEFAULT NOW();
            ALTER TABLE market_candles_1d ALTER COLUMN created_at SET DATA TYPE TIMESTAMP WITH TIME ZONE;
            ALTER TABLE market_candles_1d ALTER COLUMN created_at SET DEFAULT NOW();

            -- market_indicators_1m
            ALTER TABLE market_indicators_1m ADD COLUMN IF NOT EXISTS created_at TIMESTAMP WITH TIME ZONE DEFAULT NOW();
            ALTER TABLE market_indicators_1m ALTER COLUMN created_at SET DATA TYPE TIMESTAMP WITH TIME ZONE;
            ALTER TABLE market_indicators_1m ALTER COLUMN created_at SET DEFAULT NOW();

            -- market_indicators_5m
            ALTER TABLE market_indicators_5m ADD COLUMN IF NOT EXISTS created_at TIMESTAMP WITH TIME ZONE DEFAULT NOW();
            ALTER TABLE market_indicators_5m ALTER COLUMN created_at SET DATA TYPE TIMESTAMP WITH TIME ZONE;
            ALTER TABLE market_indicators_5m ALTER COLUMN created_at SET DEFAULT NOW();

            -- market_indicators_15m
            ALTER TABLE market_indicators_15m ADD COLUMN IF NOT EXISTS created_at TIMESTAMP WITH TIME ZONE DEFAULT NOW();
            ALTER TABLE market_indicators_15m ALTER COLUMN created_at SET DATA TYPE TIMESTAMP WITH TIME ZONE;
            ALTER TABLE market_indicators_15m ALTER COLUMN created_at SET DEFAULT NOW();

            -- market_indicators_60m
            ALTER TABLE market_indicators_60m ADD COLUMN IF NOT EXISTS created_at TIMESTAMP WITH TIME ZONE DEFAULT NOW();
            ALTER TABLE market_indicators_60m ALTER COLUMN created_at SET DATA TYPE TIMESTAMP WITH TIME ZONE;
            ALTER TABLE market_indicators_60m ALTER COLUMN created_at SET DEFAULT NOW();

            -- market_indicators_1d
            ALTER TABLE market_indicators_1d ADD COLUMN IF NOT EXISTS created_at TIMESTAMP WITH TIME ZONE DEFAULT NOW();
            ALTER TABLE market_indicators_1d ALTER COLUMN created_at SET DATA TYPE TIMESTAMP WITH TIME ZONE;
            ALTER TABLE market_indicators_1d ALTER COLUMN created_at SET DEFAULT NOW();
        ");
        _logger.LogInformation("Timeframe tables schema alignment completed successfully.");

        // Check and provision daily_stock_analysis table
        bool dailyStockAnalysisExists = await conn.ExecuteScalarAsync<bool>(@"
            SELECT EXISTS (
                SELECT FROM information_schema.tables 
                WHERE table_schema = 'public' 
                AND table_name = 'daily_stock_analysis'
            );"
        );
        if (!dailyStockAnalysisExists)
        {
            _logger.LogWarning("Table 'daily_stock_analysis' not found in database '{Database}'. Provisioning table...", targetDb);
            await conn.ExecuteAsync(@"
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

                CREATE INDEX IF NOT EXISTS ix_daily_stock_analysis_date ON daily_stock_analysis (trade_date DESC);
                CREATE INDEX IF NOT EXISTS ix_daily_stock_analysis_stock_date ON daily_stock_analysis (stock_id, trade_date DESC);
            ");
            _logger.LogInformation("Table 'daily_stock_analysis' and indexes created successfully.");
        }

        // Check and provision swing_positions table
        bool swingPositionsExists = await conn.ExecuteScalarAsync<bool>(@"
            SELECT EXISTS (
                SELECT FROM information_schema.tables 
                WHERE table_schema = 'public' 
                AND table_name = 'swing_positions'
            );"
        );
        if (!swingPositionsExists)
        {
            _logger.LogWarning("Table 'swing_positions' not found in database '{Database}'. Provisioning table...", targetDb);
            await conn.ExecuteAsync(@"
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

                CREATE INDEX IF NOT EXISTS ix_swing_positions_symbol_closed ON swing_positions (symbol, is_closed);
            ");
            _logger.LogInformation("Table 'swing_positions' and indexes created successfully.");
        }
    }
}
