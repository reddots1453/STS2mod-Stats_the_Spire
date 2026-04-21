-- ============================================================
--  004: Final Deck + Shop Card Offerings tables
--  Supports: upgrade rate from final deck, shop buy rate from offerings
-- ============================================================

BEGIN;

-- ── Final Deck (per-run, per-card) ────────────────────────
-- Used to compute upgrade rate: upgraded instances / total instances

CREATE TABLE IF NOT EXISTS final_deck (
    id           BIGSERIAL PRIMARY KEY,
    run_id       BIGINT       NOT NULL REFERENCES runs(id) ON DELETE CASCADE,
    game_version VARCHAR(16)  NOT NULL,
    character    VARCHAR(32)  NOT NULL,
    ascension    SMALLINT     NOT NULL,
    win          BOOLEAN      NOT NULL,
    card_id      VARCHAR(64)  NOT NULL,
    upgrade_level SMALLINT    DEFAULT 0,
    created_at   TIMESTAMPTZ  DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_fd_stats
    ON final_deck (character, game_version, ascension, card_id);

-- ── Shop Card Offerings (every card shown in shop) ────────
-- Used to compute shop buy rate: purchases / times offered

CREATE TABLE IF NOT EXISTS shop_card_offerings (
    id           BIGSERIAL PRIMARY KEY,
    run_id       BIGINT       NOT NULL REFERENCES runs(id) ON DELETE CASCADE,
    game_version VARCHAR(16)  NOT NULL,
    character    VARCHAR(32)  NOT NULL,
    ascension    SMALLINT     NOT NULL,
    win          BOOLEAN      NOT NULL,
    card_id      VARCHAR(64)  NOT NULL,
    floor        SMALLINT     DEFAULT 0,
    created_at   TIMESTAMPTZ  DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_sco_stats
    ON shop_card_offerings (character, game_version, ascension, card_id);

COMMIT;
