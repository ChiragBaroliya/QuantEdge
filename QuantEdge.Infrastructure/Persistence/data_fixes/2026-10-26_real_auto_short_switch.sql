-- Auto Real Trading - Auto Short Selling switch.
-- 1. Switches Real Auto Short OFF for every user: each user turns it back on themselves from the new switch on the
--    Auto Real Trading page (outside market hours, after accepting the risk warning). Open shorts are not touched -
--    they keep their SL / Target and are still bought back at the Short Square-off time.
-- 2. Adds short_risk_accepted_at and fn_toggle_real_auto_short (copies of schema.sql / functions.sql), so the switch
--    works without re-applying those whole files. Safe to run more than once. Auto Paper Trading is unchanged.

UPDATE real_trade_settings SET is_auto_short_enabled = FALSE, updated_at = NOW() WHERE is_auto_short_enabled;

ALTER TABLE real_trade_settings ADD COLUMN IF NOT EXISTS short_risk_accepted_at TIMESTAMP WITH TIME ZONE NULL;

DROP FUNCTION IF EXISTS fn_toggle_real_auto_short(INT, BOOLEAN);

CREATE OR REPLACE FUNCTION fn_toggle_real_auto_short(p_user_id INT, p_enabled BOOLEAN)
RETURNS VOID
LANGUAGE plpgsql
AS $$
BEGIN
    INSERT INTO real_trade_settings (user_id, is_auto_short_enabled, short_risk_accepted_at, updated_at)
    VALUES (p_user_id, p_enabled, CASE WHEN p_enabled THEN NOW() END, NOW())
    ON CONFLICT (user_id) DO UPDATE SET
        is_auto_short_enabled = p_enabled,
        short_risk_accepted_at = CASE WHEN p_enabled THEN NOW() ELSE real_trade_settings.short_risk_accepted_at END,
        updated_at = NOW();
END;
$$;
