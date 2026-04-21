-- ============================================================
--  Migration 005: Contribution v5 fields + source_type widening
--  Round 14 v5: align server schema with client ContributionUpload
--  which now carries modifier/upgrade/self-damage/origin fields.
-- ============================================================

-- Widen source_type from VARCHAR(8) to VARCHAR(16) to accept
-- "power", "untracked", "orb", "event", "rest", "merchant", "floor_regen"
-- in addition to the original "card"|"relic"|"potion".
ALTER TABLE contributions
    ALTER COLUMN source_type TYPE VARCHAR(16);

-- Add 6 new contribution fields (Round 14 v5 additions in
-- ContributionAccum / ApiModels.cs).
ALTER TABLE contributions
    ADD COLUMN IF NOT EXISTS modifier_damage   INT DEFAULT 0,
    ADD COLUMN IF NOT EXISTS modifier_block    INT DEFAULT 0,
    ADD COLUMN IF NOT EXISTS self_damage       INT DEFAULT 0,
    ADD COLUMN IF NOT EXISTS upgrade_damage    INT DEFAULT 0,
    ADD COLUMN IF NOT EXISTS upgrade_block     INT DEFAULT 0,
    ADD COLUMN IF NOT EXISTS origin_source_id  VARCHAR(64);
