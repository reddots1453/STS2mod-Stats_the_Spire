-- ============================================================
--  STS2 Community Stats — Database Schema
--  PostgreSQL 16+
-- ============================================================

BEGIN;

-- ── Runs (master table) ────────────────────────────────────

CREATE TABLE IF NOT EXISTS runs (
    id          BIGSERIAL PRIMARY KEY,
    game_version VARCHAR(16)  NOT NULL,
    mod_version  VARCHAR(16)  NOT NULL,
    character    VARCHAR(32)  NOT NULL,
    ascension    SMALLINT     NOT NULL CHECK (ascension BETWEEN 0 AND 20),
    win          BOOLEAN      NOT NULL,
    num_players  SMALLINT     DEFAULT 1,
    floor_reached SMALLINT    DEFAULT 0,
    created_at   TIMESTAMPTZ  DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_runs_char_ver
    ON runs (character, game_version, ascension);

-- ── Card Choices ───────────────────────────────────────────

CREATE TABLE IF NOT EXISTS card_choices (
    id              BIGSERIAL PRIMARY KEY,
    run_id          BIGINT       NOT NULL REFERENCES runs(id) ON DELETE CASCADE,
    game_version    VARCHAR(16)  NOT NULL,
    character       VARCHAR(32)  NOT NULL,
    ascension       SMALLINT     NOT NULL,
    player_win_rate REAL,
    win             BOOLEAN      NOT NULL,
    num_players     SMALLINT     DEFAULT 1,
    card_id         VARCHAR(64)  NOT NULL,
    upgrade_level   SMALLINT     DEFAULT 0,
    was_picked      BOOLEAN      NOT NULL,
    floor           SMALLINT     DEFAULT 0,
    created_at      TIMESTAMPTZ  DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_cc_stats
    ON card_choices (character, game_version, ascension, card_id);

-- ── Event Choices ──────────────────────────────────────────

CREATE TABLE IF NOT EXISTS event_choices (
    id              BIGSERIAL PRIMARY KEY,
    run_id          BIGINT       NOT NULL REFERENCES runs(id) ON DELETE CASCADE,
    game_version    VARCHAR(16)  NOT NULL,
    character       VARCHAR(32)  NOT NULL,
    ascension       SMALLINT     NOT NULL,
    player_win_rate REAL,
    win             BOOLEAN      NOT NULL,
    event_id        VARCHAR(64)  NOT NULL,
    option_index    SMALLINT     NOT NULL,
    total_options   SMALLINT     NOT NULL,
    created_at      TIMESTAMPTZ  DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_ec_stats
    ON event_choices (character, game_version, event_id);

-- ── Relic Records ──────────────────────────────────────────

CREATE TABLE IF NOT EXISTS relic_records (
    id              BIGSERIAL PRIMARY KEY,
    run_id          BIGINT       NOT NULL REFERENCES runs(id) ON DELETE CASCADE,
    game_version    VARCHAR(16)  NOT NULL,
    character       VARCHAR(32)  NOT NULL,
    ascension       SMALLINT     NOT NULL,
    player_win_rate REAL,
    win             BOOLEAN      NOT NULL,
    relic_id        VARCHAR(64)  NOT NULL,
    created_at      TIMESTAMPTZ  DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_rr_stats
    ON relic_records (character, game_version, relic_id);

-- ── Shop Purchases ─────────────────────────────────────────

CREATE TABLE IF NOT EXISTS shop_purchases (
    id              BIGSERIAL PRIMARY KEY,
    run_id          BIGINT       NOT NULL REFERENCES runs(id) ON DELETE CASCADE,
    game_version    VARCHAR(16)  NOT NULL,
    character       VARCHAR(32)  NOT NULL,
    ascension       SMALLINT     NOT NULL,
    player_win_rate REAL,
    win             BOOLEAN      NOT NULL,
    item_id         VARCHAR(64)  NOT NULL,
    item_type       VARCHAR(16)  NOT NULL,
    cost            INT,
    floor           SMALLINT     DEFAULT 0,
    created_at      TIMESTAMPTZ  DEFAULT NOW()
);

-- ── Card Removals ──────────────────────────────────────────

CREATE TABLE IF NOT EXISTS card_removals (
    id           BIGSERIAL PRIMARY KEY,
    run_id       BIGINT       NOT NULL REFERENCES runs(id) ON DELETE CASCADE,
    game_version VARCHAR(16)  NOT NULL,
    character    VARCHAR(32)  NOT NULL,
    ascension    SMALLINT     NOT NULL,
    win          BOOLEAN      NOT NULL,
    card_id      VARCHAR(64)  NOT NULL,
    source       VARCHAR(16)  NOT NULL,
    floor        SMALLINT     DEFAULT 0,
    created_at   TIMESTAMPTZ  DEFAULT NOW()
);

-- ── Card Upgrades ──────────────────────────────────────────

CREATE TABLE IF NOT EXISTS card_upgrades (
    id           BIGSERIAL PRIMARY KEY,
    run_id       BIGINT       NOT NULL REFERENCES runs(id) ON DELETE CASCADE,
    game_version VARCHAR(16)  NOT NULL,
    character    VARCHAR(32)  NOT NULL,
    ascension    SMALLINT     NOT NULL,
    win          BOOLEAN      NOT NULL,
    card_id      VARCHAR(64)  NOT NULL,
    source       VARCHAR(16)  NOT NULL,
    created_at   TIMESTAMPTZ  DEFAULT NOW()
);

-- ── Encounter Records ──────────────────────────────────────

CREATE TABLE IF NOT EXISTS encounter_records (
    id              BIGSERIAL PRIMARY KEY,
    run_id          BIGINT       NOT NULL REFERENCES runs(id) ON DELETE CASCADE,
    game_version    VARCHAR(16)  NOT NULL,
    character       VARCHAR(32)  NOT NULL,
    ascension       SMALLINT     NOT NULL,
    encounter_id    VARCHAR(64)  NOT NULL,
    encounter_type  VARCHAR(16)  NOT NULL,
    damage_taken    INT          NOT NULL,
    turns_taken     INT          NOT NULL,
    player_died     BOOLEAN      NOT NULL,
    floor           SMALLINT     DEFAULT 0,
    created_at      TIMESTAMPTZ  DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_er_stats
    ON encounter_records (character, game_version, encounter_id);

-- ── Contributions (card+relic battle attribution) ──────────

CREATE TABLE IF NOT EXISTS contributions (
    id                BIGSERIAL PRIMARY KEY,
    run_id            BIGINT       NOT NULL REFERENCES runs(id) ON DELETE CASCADE,
    game_version      VARCHAR(16)  NOT NULL,
    character         VARCHAR(32)  NOT NULL,
    ascension         SMALLINT     NOT NULL,
    source_id         VARCHAR(64)  NOT NULL,
    source_type       VARCHAR(8)   NOT NULL,
    encounter_id      VARCHAR(64),
    times_played      INT          DEFAULT 0,
    direct_damage     INT          DEFAULT 0,
    attributed_damage INT          DEFAULT 0,
    effective_block   INT          DEFAULT 0,
    mitigated_by_debuff INT        DEFAULT 0,
    mitigated_by_buff INT          DEFAULT 0,
    cards_drawn       INT          DEFAULT 0,
    energy_gained     INT          DEFAULT 0,
    hp_healed         INT          DEFAULT 0,
    stars_contribution INT         DEFAULT 0,
    mitigated_by_str  INT          DEFAULT 0,
    created_at        TIMESTAMPTZ  DEFAULT NOW()
);

-- ── Game Versions ──────────────────────────────────────────

CREATE TABLE IF NOT EXISTS game_versions (
    version    VARCHAR(16) PRIMARY KEY,
    first_seen TIMESTAMPTZ DEFAULT NOW(),
    last_seen  TIMESTAMPTZ DEFAULT NOW(),
    is_active  BOOLEAN     DEFAULT TRUE
);

-- ── ID Migrations (cross-version renames) ──────────────────

CREATE TABLE IF NOT EXISTS id_migrations (
    old_id        VARCHAR(64) NOT NULL,
    new_id        VARCHAR(64) NOT NULL,
    since_version VARCHAR(16) NOT NULL,
    entity_type   VARCHAR(16) NOT NULL,
    PRIMARY KEY (old_id, since_version)
);

COMMIT;
