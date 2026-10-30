-- Backtesting of Short Selling: each simulated trade records its side. Trades stored earlier were all BUY.
-- Safe to run more than once. The app also adds the column itself on first use (BacktestService.EnsureSideColumnAsync).

ALTER TABLE backtest_trades ADD COLUMN IF NOT EXISTS side VARCHAR(10) NOT NULL DEFAULT 'BUY';
