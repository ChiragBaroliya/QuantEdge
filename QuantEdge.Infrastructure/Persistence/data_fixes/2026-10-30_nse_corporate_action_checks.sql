-- NSE bhavcopy: records which days finished their split / bonus check, so a failed check is retried.
-- Safe to run more than once. The app also creates the table itself on first use (NseBhavcopyService).

CREATE TABLE IF NOT EXISTS nse_corporate_action_checks (
    trade_date DATE PRIMARY KEY,
    checked_at TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT NOW()
);
