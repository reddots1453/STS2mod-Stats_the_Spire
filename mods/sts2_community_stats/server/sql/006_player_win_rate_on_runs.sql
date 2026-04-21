-- Migration 006: Add player_win_rate to runs table
-- The client now uploads the uploader's local historical win rate with each
-- run. This column enables server-side filtering: "only aggregate runs from
-- players whose win rate >= X%".
ALTER TABLE runs ADD COLUMN IF NOT EXISTS player_win_rate REAL DEFAULT 0;
CREATE INDEX IF NOT EXISTS idx_runs_win_rate ON runs (player_win_rate);
