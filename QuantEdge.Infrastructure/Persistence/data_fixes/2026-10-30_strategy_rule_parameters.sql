-- Rule numbers (Trade Conditions page): stores the numbers behind the 11 swing conditions as JSON.
-- NULL means the original values, so adding the column changes nothing until someone saves new numbers.
-- Safe to run more than once. The app also adds the column itself on first use (SwingStrategySettingsRepository.EnsureTableCreated).

ALTER TABLE swing_strategy_settings ADD COLUMN IF NOT EXISTS rule_parameters JSONB;
