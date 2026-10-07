-- ----------------------------------------------------------------------------
-- One-time data fix (Plan L.1 P7/P8)
--
-- Before the fix, live-built candles (CandleBuilderService) were bucketed on raw UTC time:
--   * daily candles were stamped 00:00 UTC (05:30 IST) while Kite's synced daily candles are 00:00 IST
--     (18:30 UTC the day before) -> the same trading day could be stored twice in market_candles_1d;
--   * 60m candles started at 09:30, 10:30 ... IST instead of NSE's 09:15, 10:15 ...
-- New candles are correct after the fix; this removes the old misaligned rows.
--
-- Run each PREVIEW, check the counts, then run the matching DELETE inside the transaction and COMMIT.
-- Afterwards, re-sync 60m history for active stocks (History / "Reset" tools) so the deleted hours are refilled
-- from Kite with the correct 09:15-based timestamps.
-- ----------------------------------------------------------------------------

-- PREVIEW 1: daily rows NOT stamped at 00:00 IST that duplicate a Kite row for the same symbol and IST date
SELECT d.symbol, (d.candle_time AT TIME ZONE 'Asia/Kolkata')::date AS trade_date, d.candle_time AS misaligned_time, k.candle_time AS kite_time
FROM market_candles_1d d
JOIN market_candles_1d k
  ON k.symbol = d.symbol
 AND (k.candle_time AT TIME ZONE 'Asia/Kolkata')::date = (d.candle_time AT TIME ZONE 'Asia/Kolkata')::date
 AND (k.candle_time AT TIME ZONE 'Asia/Kolkata')::time = TIME '00:00'
WHERE (d.candle_time AT TIME ZONE 'Asia/Kolkata')::time <> TIME '00:00'
ORDER BY trade_date DESC, d.symbol;

-- PREVIEW 2: 60m rows not starting at :15 IST (live-built, misaligned)
SELECT TO_CHAR(candle_time AT TIME ZONE 'Asia/Kolkata', 'HH24:MI') AS start_ist, COUNT(*)
FROM market_candles_60m
WHERE EXTRACT(MINUTE FROM candle_time AT TIME ZONE 'Asia/Kolkata') <> 15
GROUP BY 1 ORDER BY 1;

BEGIN;

-- DELETE 1: duplicate daily rows (the Kite 00:00 IST row for that day is kept)
DELETE FROM market_candles_1d d
USING market_candles_1d k
WHERE k.symbol = d.symbol
  AND (k.candle_time AT TIME ZONE 'Asia/Kolkata')::date = (d.candle_time AT TIME ZONE 'Asia/Kolkata')::date
  AND (k.candle_time AT TIME ZONE 'Asia/Kolkata')::time = TIME '00:00'
  AND (d.candle_time AT TIME ZONE 'Asia/Kolkata')::time <> TIME '00:00';

DELETE FROM market_indicators_1d i
WHERE (i.candle_time AT TIME ZONE 'Asia/Kolkata')::time <> TIME '00:00'
  AND NOT EXISTS (SELECT 1 FROM market_candles_1d c WHERE c.symbol = i.symbol AND c.candle_time = i.candle_time);

-- DELETE 2: misaligned 60m rows and their indicators
DELETE FROM market_candles_60m WHERE EXTRACT(MINUTE FROM candle_time AT TIME ZONE 'Asia/Kolkata') <> 15;
DELETE FROM market_indicators_60m WHERE EXTRACT(MINUTE FROM candle_time AT TIME ZONE 'Asia/Kolkata') <> 15;

-- Check the row counts above match the previews, then:
-- COMMIT;   (or ROLLBACK;)
