-- ----------------------------------------------------------------------------
-- One-time data fix (Plan/swing_strategy_review_and_redesign.md, L.1 P1)
--
-- Before the fix, closing a paper position with a market SELL/BUY order wrote the closing
-- paper_trade_history row with realized_pnl = 0 (the P&L only went to paper_positions). Reports read
-- paper_trade_history, so those trades showed ₹0 profit.
--
-- This script copies the position's realized P&L onto that closing history row. Matching rule: same
-- account + symbol, history row is an opposite-side "Market Order Executed" row with realized_pnl = 0,
-- written within 2 minutes of the position's closed_at. Positions closed by SL/TP or the auto bot already
-- have correct history rows and are not touched.
--
-- Run STEP 1, check the rows, then run STEP 2 inside the transaction and COMMIT only if the count matches.
-- ----------------------------------------------------------------------------

-- STEP 1: preview
SELECT h.id AS history_id, h.symbol, h.executed_at, h.quantity AS history_qty,
       p.id AS position_id, p.quantity AS position_qty, p.average_entry_price, p.current_price AS exit_price, p.realized_pnl
FROM paper_trade_history h
JOIN paper_positions p
  ON p.account_id = h.account_id
 AND UPPER(p.symbol) = UPPER(h.symbol)
 AND p.status = 1
 AND p.realized_pnl <> 0
 AND p.side <> h.side
 AND ABS(EXTRACT(EPOCH FROM (h.executed_at - p.closed_at))) <= 120
WHERE h.realized_pnl = 0
  AND h.remarks = 'Market Order Executed'
ORDER BY h.executed_at DESC;

-- STEP 2: apply
BEGIN;

UPDATE paper_trade_history h
SET realized_pnl = p.realized_pnl,
    entry_price = p.average_entry_price,
    exit_reason = COALESCE(h.exit_reason, 'Manual Close'),
    remarks = 'Market Order Executed - position closed (P&L backfilled 2026-10-06)'
FROM paper_positions p
WHERE p.account_id = h.account_id
  AND UPPER(p.symbol) = UPPER(h.symbol)
  AND p.status = 1
  AND p.realized_pnl <> 0
  AND p.side <> h.side
  AND ABS(EXTRACT(EPOCH FROM (h.executed_at - p.closed_at))) <= 120
  AND h.realized_pnl = 0
  AND h.remarks = 'Market Order Executed';

-- Check the reported row count equals STEP 1's row count, then:
-- COMMIT;   (or ROLLBACK;)
