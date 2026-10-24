-- Data Coverage Manager - Map Sector popup.
-- Creates only the three sector routines (copies of the ones in functions.sql / stored_procedures.sql), so the
-- popup works without re-applying those whole files. Safe to run more than once; no table or data changes.
--   fn_get_sector_list   - dropdown: all active sectors (sectors table) with linked stock counts
--   fn_upsert_sector     - "Add new sector": returns the existing sector (any case) or creates it
--   sp_set_stock_sectors - Save Mapping: replaces a stock's sector links

DROP FUNCTION IF EXISTS fn_get_sector_list();

CREATE OR REPLACE FUNCTION fn_get_sector_list()
RETURNS TABLE (
    "Id" INT,
    "Name" VARCHAR,
    "StockCount" INT
)
LANGUAGE plpgsql
AS $$
BEGIN
    RETURN QUERY
    SELECT sec.id, sec.name, COUNT(ss.stock_id)::INT
    FROM sectors sec
    LEFT JOIN stock_sectors ss ON ss.sector_id = sec.id
    WHERE sec.is_active = TRUE
    GROUP BY sec.id, sec.name
    ORDER BY sec.name;
END;
$$;


DROP FUNCTION IF EXISTS fn_upsert_sector(VARCHAR) CASCADE;

CREATE OR REPLACE FUNCTION fn_upsert_sector(
    p_name VARCHAR
)
RETURNS TABLE (
    "Id" INT,
    "Name" VARCHAR
)
LANGUAGE plpgsql
AS $$
DECLARE
    v_name VARCHAR := UPPER(REGEXP_REPLACE(TRIM(COALESCE(p_name, '')), '\s+', ' ', 'g'));
BEGIN
    IF v_name = '' THEN
        RAISE EXCEPTION 'fn_upsert_sector: sector name is required';
    END IF;

    RETURN QUERY
    WITH existing AS (
        UPDATE sectors sec SET is_active = TRUE
        WHERE UPPER(sec.name) = v_name
        RETURNING sec.id, sec.name
    ),
    inserted AS (
        INSERT INTO sectors (name)
        SELECT v_name
        WHERE NOT EXISTS (SELECT 1 FROM existing)
        ON CONFLICT (name) DO UPDATE SET is_active = TRUE
        RETURNING sectors.id, sectors.name
    )
    SELECT e.id, e.name FROM existing e
    UNION ALL
    SELECT i.id, i.name FROM inserted i;
END;
$$;


DROP PROCEDURE IF EXISTS sp_set_stock_sectors(INT, INT[]) CASCADE;

CREATE OR REPLACE PROCEDURE sp_set_stock_sectors(
    p_stock_id INT,
    p_sector_ids INT[]
)
LANGUAGE plpgsql
AS $$
BEGIN
    DELETE FROM stock_sectors ss
    USING sectors sec
    WHERE ss.stock_id = p_stock_id
      AND sec.id = ss.sector_id
      AND sec.is_active = TRUE
      AND NOT (ss.sector_id = ANY(COALESCE(p_sector_ids, ARRAY[]::INT[])));

    INSERT INTO stock_sectors (stock_id, sector_id)
    SELECT p_stock_id, sec.id
    FROM sectors sec
    WHERE sec.id = ANY(COALESCE(p_sector_ids, ARRAY[]::INT[]))
      AND sec.is_active = TRUE
    ON CONFLICT (stock_id, sector_id) DO NOTHING;
END;
$$;
