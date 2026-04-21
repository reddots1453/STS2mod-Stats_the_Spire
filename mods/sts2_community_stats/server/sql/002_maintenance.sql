-- ============================================================
--  Maintenance queries — run periodically via cron
-- ============================================================

-- ── 1. Mark inactive versions (no uploads in 30 days) ──────
UPDATE game_versions
SET is_active = FALSE
WHERE is_active = TRUE
  AND last_seen < NOW() - INTERVAL '30 days';

-- ── 2. Purge old data for inactive versions (>6 months) ────
-- Run with care — these are destructive!

DELETE FROM card_choices
WHERE game_version IN (SELECT version FROM game_versions WHERE is_active = FALSE)
  AND created_at < NOW() - INTERVAL '6 months';

DELETE FROM event_choices
WHERE game_version IN (SELECT version FROM game_versions WHERE is_active = FALSE)
  AND created_at < NOW() - INTERVAL '6 months';

DELETE FROM relic_records
WHERE game_version IN (SELECT version FROM game_versions WHERE is_active = FALSE)
  AND created_at < NOW() - INTERVAL '6 months';

DELETE FROM encounter_records
WHERE game_version IN (SELECT version FROM game_versions WHERE is_active = FALSE)
  AND created_at < NOW() - INTERVAL '6 months';

DELETE FROM contributions
WHERE game_version IN (SELECT version FROM game_versions WHERE is_active = FALSE)
  AND created_at < NOW() - INTERVAL '6 months';

DELETE FROM shop_purchases
WHERE game_version IN (SELECT version FROM game_versions WHERE is_active = FALSE)
  AND created_at < NOW() - INTERVAL '6 months';

DELETE FROM card_removals
WHERE game_version IN (SELECT version FROM game_versions WHERE is_active = FALSE)
  AND created_at < NOW() - INTERVAL '6 months';

DELETE FROM card_upgrades
WHERE game_version IN (SELECT version FROM game_versions WHERE is_active = FALSE)
  AND created_at < NOW() - INTERVAL '6 months';

-- Cascade deletes runs whose children are all gone
DELETE FROM runs r
WHERE NOT EXISTS (SELECT 1 FROM card_choices WHERE run_id = r.id)
  AND NOT EXISTS (SELECT 1 FROM encounter_records WHERE run_id = r.id)
  AND created_at < NOW() - INTERVAL '6 months';

-- ── 3. Analyze tables after bulk deletes ───────────────────
ANALYZE card_choices;
ANALYZE event_choices;
ANALYZE relic_records;
ANALYZE encounter_records;
ANALYZE contributions;
ANALYZE runs;
