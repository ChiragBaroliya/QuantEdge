-- Trade Conditions page - which of the 11 BUY / SHORT conditions each user's Auto Paper / Auto Real bot applies.
-- Adds the trade_condition_profiles table and its functions / procedures (copies of schema.sql / functions.sql), so the
-- feature works without re-applying those whole files. Safe to run more than once. Until this is applied every user
-- keeps the default (all 11 conditions on) and the Trade Conditions page cannot save.

CREATE TABLE IF NOT EXISTS trade_condition_profiles (
    user_ref VARCHAR(50) NOT NULL,
    trade_mode VARCHAR(20) NOT NULL,
    side VARCHAR(10) NOT NULL,
    disabled_conditions TEXT[] NOT NULL DEFAULT '{}',
    min_conditions_match INT NULL,
    risk_accepted_at TIMESTAMP WITH TIME ZONE NULL,
    updated_at TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT NOW(),
    PRIMARY KEY (user_ref, trade_mode, side),
    CONSTRAINT chk_tcp_trade_mode CHECK (trade_mode IN ('AUTO_PAPER', 'AUTO_REAL')),
    CONSTRAINT chk_tcp_side CHECK (side IN ('BUY', 'SHORT'))
);

-- ----------------------------------------------------------------------------
-- Trade Conditions (trade_condition_profiles) - which of the 11 BUY / SHORT conditions each user's
-- Auto Paper / Auto Real bot applies. Used by TradeConditionProfileRepository.
-- ----------------------------------------------------------------------------

-- Function: fn_get_trade_condition_profiles - one user's stored profiles for a mode (0-2 rows: BUY / SHORT).
DROP FUNCTION IF EXISTS fn_get_trade_condition_profiles(VARCHAR, VARCHAR);

CREATE OR REPLACE FUNCTION fn_get_trade_condition_profiles(p_user_ref VARCHAR, p_trade_mode VARCHAR)
RETURNS TABLE (
    UserRef VARCHAR,
    TradeMode VARCHAR,
    Side VARCHAR,
    DisabledConditions TEXT[],
    MinConditionsMatch INT,
    RiskAcceptedAt TIMESTAMP WITH TIME ZONE,
    UpdatedAt TIMESTAMP WITH TIME ZONE
)
LANGUAGE plpgsql
AS $$
BEGIN
    RETURN QUERY
    SELECT t.user_ref AS UserRef,
           t.trade_mode AS TradeMode,
           t.side AS Side,
           t.disabled_conditions AS DisabledConditions,
           t.min_conditions_match AS MinConditionsMatch,
           t.risk_accepted_at AS RiskAcceptedAt,
           t.updated_at AS UpdatedAt
    FROM trade_condition_profiles t
    WHERE t.user_ref = p_user_ref AND t.trade_mode = p_trade_mode
    ORDER BY t.side;
END;
$$;

-- Function: fn_get_trade_condition_profiles_by_mode - every stored profile of a mode (the Auto Real scan reads all users).
DROP FUNCTION IF EXISTS fn_get_trade_condition_profiles_by_mode(VARCHAR);

CREATE OR REPLACE FUNCTION fn_get_trade_condition_profiles_by_mode(p_trade_mode VARCHAR)
RETURNS TABLE (
    UserRef VARCHAR,
    TradeMode VARCHAR,
    Side VARCHAR,
    DisabledConditions TEXT[],
    MinConditionsMatch INT,
    RiskAcceptedAt TIMESTAMP WITH TIME ZONE,
    UpdatedAt TIMESTAMP WITH TIME ZONE
)
LANGUAGE plpgsql
AS $$
BEGIN
    RETURN QUERY
    SELECT t.user_ref AS UserRef,
           t.trade_mode AS TradeMode,
           t.side AS Side,
           t.disabled_conditions AS DisabledConditions,
           t.min_conditions_match AS MinConditionsMatch,
           t.risk_accepted_at AS RiskAcceptedAt,
           t.updated_at AS UpdatedAt
    FROM trade_condition_profiles t
    WHERE t.trade_mode = p_trade_mode
    ORDER BY t.user_ref, t.side;
END;
$$;

-- Procedure: sp_upsert_trade_condition_profile - saves one user / mode / side.
DROP PROCEDURE IF EXISTS sp_upsert_trade_condition_profile(VARCHAR, VARCHAR, VARCHAR, TEXT[], INT, TIMESTAMP WITH TIME ZONE);

CREATE OR REPLACE PROCEDURE sp_upsert_trade_condition_profile(
    p_user_ref VARCHAR,
    p_trade_mode VARCHAR,
    p_side VARCHAR,
    p_disabled_conditions TEXT[],
    p_min_conditions_match INT,
    p_risk_accepted_at TIMESTAMP WITH TIME ZONE
)
LANGUAGE plpgsql
AS $$
BEGIN
    INSERT INTO trade_condition_profiles (user_ref, trade_mode, side, disabled_conditions, min_conditions_match, risk_accepted_at, updated_at)
    VALUES (p_user_ref, p_trade_mode, p_side, COALESCE(p_disabled_conditions, '{}'), p_min_conditions_match, p_risk_accepted_at, NOW())
    ON CONFLICT (user_ref, trade_mode, side) DO UPDATE SET
        disabled_conditions = EXCLUDED.disabled_conditions,
        min_conditions_match = EXCLUDED.min_conditions_match,
        risk_accepted_at = EXCLUDED.risk_accepted_at,
        updated_at = NOW();
END;
$$;

-- Procedure: sp_delete_trade_condition_profiles - back to the default (all 11 on) for one user / mode.
DROP PROCEDURE IF EXISTS sp_delete_trade_condition_profiles(VARCHAR, VARCHAR);

CREATE OR REPLACE PROCEDURE sp_delete_trade_condition_profiles(p_user_ref VARCHAR, p_trade_mode VARCHAR)
LANGUAGE plpgsql
AS $$
BEGIN
    DELETE FROM trade_condition_profiles
    WHERE user_ref = p_user_ref AND trade_mode = p_trade_mode;
END;
$$;
