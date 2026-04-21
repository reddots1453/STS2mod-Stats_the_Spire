-- Migration 003: Add combo_key and chosen_option_id for dynamic/ancient events

ALTER TABLE event_choices
    ADD COLUMN IF NOT EXISTS combo_key         VARCHAR(256),
    ADD COLUMN IF NOT EXISTS chosen_option_id  VARCHAR(64);

-- Index for combo-based aggregation (ancient events)
CREATE INDEX IF NOT EXISTS idx_ec_combo
    ON event_choices (character, game_version, event_id, combo_key)
    WHERE combo_key IS NOT NULL;

-- Index for flat aggregation (COLORFUL_PHILOSOPHERS)
CREATE INDEX IF NOT EXISTS idx_ec_flat
    ON event_choices (character, game_version, event_id, chosen_option_id)
    WHERE combo_key IS NULL AND chosen_option_id IS NOT NULL;
