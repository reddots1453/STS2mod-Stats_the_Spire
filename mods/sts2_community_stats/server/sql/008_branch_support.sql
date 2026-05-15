-- 008_branch_support: Add branch dimension for release/beta data separation.
-- All columns default to 'unknown' so pre-feature rows are handled correctly.

BEGIN;

-- Main runs table
ALTER TABLE runs ADD COLUMN IF NOT EXISTS branch VARCHAR(8) NOT NULL DEFAULT 'unknown';

-- Detail tables (denormalized for filter performance without JOINs)
ALTER TABLE card_choices       ADD COLUMN IF NOT EXISTS branch VARCHAR(8) NOT NULL DEFAULT 'unknown';
ALTER TABLE event_choices      ADD COLUMN IF NOT EXISTS branch VARCHAR(8) NOT NULL DEFAULT 'unknown';
ALTER TABLE relic_records      ADD COLUMN IF NOT EXISTS branch VARCHAR(8) NOT NULL DEFAULT 'unknown';
ALTER TABLE shop_purchases     ADD COLUMN IF NOT EXISTS branch VARCHAR(8) NOT NULL DEFAULT 'unknown';
ALTER TABLE card_removals      ADD COLUMN IF NOT EXISTS branch VARCHAR(8) NOT NULL DEFAULT 'unknown';
ALTER TABLE card_upgrades      ADD COLUMN IF NOT EXISTS branch VARCHAR(8) NOT NULL DEFAULT 'unknown';
ALTER TABLE final_deck         ADD COLUMN IF NOT EXISTS branch VARCHAR(8) NOT NULL DEFAULT 'unknown';
ALTER TABLE shop_card_offerings ADD COLUMN IF NOT EXISTS branch VARCHAR(8) NOT NULL DEFAULT 'unknown';
ALTER TABLE encounter_records  ADD COLUMN IF NOT EXISTS branch VARCHAR(8) NOT NULL DEFAULT 'unknown';
ALTER TABLE contributions      ADD COLUMN IF NOT EXISTS branch VARCHAR(8) NOT NULL DEFAULT 'unknown';

-- Composite indexes including branch for filtered aggregation queries.
-- The planner can use these for (char, ver, branch, asc) prefix queries;
-- old single-dimensional indexes remain as fallbacks.

CREATE INDEX IF NOT EXISTS idx_runs_char_ver_br
    ON runs (character, game_version, branch, ascension);
CREATE INDEX IF NOT EXISTS idx_cc_stats_br
    ON card_choices (character, game_version, branch, ascension, card_id);
CREATE INDEX IF NOT EXISTS idx_ec_stats_br
    ON event_choices (character, game_version, branch, event_id);
CREATE INDEX IF NOT EXISTS idx_rr_stats_br
    ON relic_records (character, game_version, branch, relic_id);
CREATE INDEX IF NOT EXISTS idx_er_stats_br
    ON encounter_records (character, game_version, branch, encounter_id);
CREATE INDEX IF NOT EXISTS idx_sp_stats_br
    ON shop_purchases (character, game_version, branch, ascension, item_type, item_id);
CREATE INDEX IF NOT EXISTS idx_cr_stats_br
    ON card_removals (character, game_version, branch, ascension, card_id);
CREATE INDEX IF NOT EXISTS idx_fd_stats_br
    ON final_deck (character, game_version, branch, ascension, card_id);
CREATE INDEX IF NOT EXISTS idx_sco_stats_br
    ON shop_card_offerings (character, game_version, branch, ascension, card_id);

COMMIT;
