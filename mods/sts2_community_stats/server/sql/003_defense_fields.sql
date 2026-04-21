-- Migration: Replace block_gained with defense attribution fields

ALTER TABLE contributions
    ADD COLUMN IF NOT EXISTS effective_block INT DEFAULT 0,
    ADD COLUMN IF NOT EXISTS mitigated_by_debuff INT DEFAULT 0,
    ADD COLUMN IF NOT EXISTS mitigated_by_buff INT DEFAULT 0;

-- Migrate old block_gained data to effective_block
UPDATE contributions SET effective_block = block_gained WHERE block_gained > 0 AND effective_block = 0;

-- Drop old column (optional, can keep for backwards compatibility)
-- ALTER TABLE contributions DROP COLUMN IF EXISTS block_gained;
