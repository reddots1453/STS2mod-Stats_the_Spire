-- ============================================================
--  002: Add run_hash for deduplication (PRD §3.19.3)
--  Allows history import from multiple clients without duplicates.
-- ============================================================

BEGIN;

ALTER TABLE runs ADD COLUMN IF NOT EXISTS run_hash VARCHAR(64);

-- Partial unique index: only enforce uniqueness when run_hash is not null.
-- Normal uploads (from live gameplay) don't set run_hash, so they're unconstrained.
CREATE UNIQUE INDEX IF NOT EXISTS idx_runs_hash
    ON runs (run_hash) WHERE run_hash IS NOT NULL;

COMMIT;
