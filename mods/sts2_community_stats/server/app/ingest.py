"""Run upload ingestion — splits payload into normalized database rows."""

import logging
import asyncpg
from .models import RunUploadPayload

logger = logging.getLogger("sts2stats.ingest")


async def ingest_run(pool: asyncpg.Pool, payload: RunUploadPayload) -> int:
    """Insert a complete run and all sub-records. Returns the run_id.
    On duplicate run_hash, backfills player_win_rate + shop data on the
    existing row (older mod versions shipped empty / zero values for these)
    and returns the existing run_id.
    """
    async with pool.acquire() as conn:
        async with conn.transaction():
            # ── Main run record ──────────────────────────────
            existing_id: int | None = None
            if payload.run_hash:
                existing_id = await conn.fetchval(
                    "SELECT id FROM runs WHERE run_hash = $1", payload.run_hash)

            if existing_id is not None:
                # Backfill-on-reupload: older mod builds shipped
                # player_win_rate=0 and missed colored-card shop purchases /
                # had no shop offerings. Newer payloads carry correct data;
                # overwrite the duplicable fields so re-imports correct the
                # historical record instead of being silently dropped.
                #
                # Fields NOT backfilled:
                #   - card_choices / event_choices / card_upgrades /
                #     card_removals / final_deck / encounters / contributions
                #     are deterministic per run or already captured
                #     correctly; re-running them risks duplicates.
                await _backfill_existing_run(conn, existing_id, payload)
                logger.info(
                    "Backfilled run #%d (hash=%s): wr=%.3f purchases=%d offerings=%d",
                    existing_id, payload.run_hash, payload.player_win_rate,
                    len(payload.shop_purchases), len(payload.shop_card_offerings),
                )
                return existing_id

            run_id: int = await conn.fetchval(
                """INSERT INTO runs
                   (game_version, mod_version, character, ascension, win,
                    num_players, floor_reached, run_hash, player_win_rate, branch)
                   VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10) RETURNING id""",
                payload.game_version, payload.mod_version, payload.character,
                payload.ascension, payload.win, payload.num_players,
                payload.floor_reached, payload.run_hash, payload.player_win_rate,
                payload.branch,
            )

            # ── Card choices ─────────────────────────────────
            if payload.card_choices:
                await conn.executemany(
                    """INSERT INTO card_choices
                       (run_id, game_version, character, ascension, player_win_rate,
                        win, num_players, card_id, upgrade_level, was_picked, floor, branch)
                       VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12)""",
                    [
                        (run_id, payload.game_version, payload.character,
                         payload.ascension, payload.player_win_rate, payload.win,
                         payload.num_players, c.card_id, c.upgrade_level,
                         c.was_picked, c.floor, payload.branch)
                        for c in payload.card_choices
                    ],
                )

            # ── Event choices ────────────────────────────────
            if payload.event_choices:
                await conn.executemany(
                    """INSERT INTO event_choices
                       (run_id, game_version, character, ascension, player_win_rate,
                        win, event_id, option_index, total_options,
                        combo_key, chosen_option_id, branch)
                       VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12)""",
                    [
                        (run_id, payload.game_version, payload.character,
                         payload.ascension, payload.player_win_rate, payload.win,
                         e.event_id, e.option_index, e.total_options,
                         e.combo_key, e.chosen_option_id, payload.branch)
                        for e in payload.event_choices
                    ],
                )

            # ── Relic records ────────────────────────────────
            if payload.final_relics:
                await conn.executemany(
                    """INSERT INTO relic_records
                       (run_id, game_version, character, ascension, player_win_rate,
                        win, relic_id, branch)
                       VALUES ($1,$2,$3,$4,$5,$6,$7,$8)""",
                    [
                        (run_id, payload.game_version, payload.character,
                         payload.ascension, payload.player_win_rate, payload.win, r,
                         payload.branch)
                        for r in payload.final_relics
                    ],
                )

            # ── Shop purchases ───────────────────────────────
            if payload.shop_purchases:
                await conn.executemany(
                    """INSERT INTO shop_purchases
                       (run_id, game_version, character, ascension, player_win_rate,
                        win, item_id, item_type, cost, floor, branch)
                       VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11)""",
                    [
                        (run_id, payload.game_version, payload.character,
                         payload.ascension, payload.player_win_rate, payload.win,
                         s.item_id, s.item_type, s.cost, s.floor, payload.branch)
                        for s in payload.shop_purchases
                    ],
                )

            # ── Card removals ────────────────────────────────
            if payload.card_removals:
                await conn.executemany(
                    """INSERT INTO card_removals
                       (run_id, game_version, character, ascension, win,
                        card_id, source, floor, branch)
                       VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9)""",
                    [
                        (run_id, payload.game_version, payload.character,
                         payload.ascension, payload.win, r.card_id, r.source, r.floor,
                         payload.branch)
                        for r in payload.card_removals
                    ],
                )

            # ── Card upgrades ────────────────────────────────
            if payload.card_upgrades:
                await conn.executemany(
                    """INSERT INTO card_upgrades
                       (run_id, game_version, character, ascension, win,
                        card_id, source, branch)
                       VALUES ($1,$2,$3,$4,$5,$6,$7,$8)""",
                    [
                        (run_id, payload.game_version, payload.character,
                         payload.ascension, payload.win, u.card_id, u.source,
                         payload.branch)
                        for u in payload.card_upgrades
                    ],
                )

            # ── Final deck ──────────────────────────────────
            if payload.final_deck:
                await conn.executemany(
                    """INSERT INTO final_deck
                       (run_id, game_version, character, ascension, win,
                        card_id, upgrade_level, branch)
                       VALUES ($1,$2,$3,$4,$5,$6,$7,$8)""",
                    [
                        (run_id, payload.game_version, payload.character,
                         payload.ascension, payload.win, d.card_id, d.upgrade_level,
                         payload.branch)
                        for d in payload.final_deck
                    ],
                )

            # ── Shop card offerings ─────────────────────────
            if payload.shop_card_offerings:
                await conn.executemany(
                    """INSERT INTO shop_card_offerings
                       (run_id, game_version, character, ascension, win,
                        card_id, floor, branch)
                       VALUES ($1,$2,$3,$4,$5,$6,$7,$8)""",
                    [
                        (run_id, payload.game_version, payload.character,
                         payload.ascension, payload.win, o.card_id, o.floor,
                         payload.branch)
                        for o in payload.shop_card_offerings
                    ],
                )

            # ── Encounter records ────────────────────────────
            if payload.encounters:
                await conn.executemany(
                    """INSERT INTO encounter_records
                       (run_id, game_version, character, ascension,
                        encounter_id, encounter_type, damage_taken,
                        turns_taken, player_died, floor, branch)
                       VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11)""",
                    [
                        (run_id, payload.game_version, payload.character,
                         payload.ascension, e.encounter_id, e.encounter_type,
                         e.damage_taken, e.turns_taken, e.player_died, e.floor,
                         payload.branch)
                        for e in payload.encounters
                    ],
                )

            # ── Contributions ────────────────────────────────
            # Round 14 v5: added modifier_damage/modifier_block/self_damage/
            # upgrade_damage/upgrade_block/origin_source_id columns (migration 005).
            if payload.contributions:
                await conn.executemany(
                    """INSERT INTO contributions
                       (run_id, game_version, character, ascension,
                        source_id, source_type, encounter_id,
                        times_played, direct_damage, attributed_damage,
                        effective_block, mitigated_by_debuff, mitigated_by_buff,
                        cards_drawn, energy_gained, hp_healed,
                        stars_contribution, mitigated_by_str,
                        modifier_damage, modifier_block, self_damage,
                        upgrade_damage, upgrade_block, origin_source_id, branch)
                       VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15,$16,$17,$18,$19,$20,$21,$22,$23,$24,$25)""",
                    [
                        (run_id, payload.game_version, payload.character,
                         payload.ascension, c.source_id, c.source_type,
                         c.encounter_id, c.times_played, c.direct_damage,
                         c.attributed_damage, c.effective_block,
                         c.mitigated_by_debuff, c.mitigated_by_buff,
                         c.cards_drawn, c.energy_gained, c.hp_healed,
                         c.stars_contribution, c.mitigated_by_str,
                         c.modifier_damage, c.modifier_block, c.self_damage,
                         c.upgrade_damage, c.upgrade_block, c.origin_source_id,
                         payload.branch)
                        for c in payload.contributions
                    ],
                )

            # ── Register game version ────────────────────────
            await conn.execute(
                """INSERT INTO game_versions (version)
                   VALUES ($1)
                   ON CONFLICT (version) DO UPDATE SET last_seen = NOW()""",
                payload.game_version,
            )

    logger.info(
        "Ingested run #%d: %s asc%d %s floor=%d cards=%d events=%d encounters=%d",
        run_id, payload.character, payload.ascension,
        "WIN" if payload.win else "LOSS", payload.floor_reached,
        len(payload.card_choices), len(payload.event_choices),
        len(payload.encounters),
    )
    return run_id


async def _backfill_existing_run(
    conn: asyncpg.Connection, run_id: int, payload: RunUploadPayload,
) -> None:
    """Update an already-ingested run's backfillable fields from a newer
    payload. Idempotent: safe to call repeatedly with the same payload."""

    # runs.player_win_rate — client previously uploaded 0 when the local
    # history snapshot wasn't cached; a later reupload may have the real
    # value. Take the max so once a good value is recorded it sticks even
    # if a later import transiently sends 0.
    await conn.execute(
        """UPDATE runs
           SET player_win_rate = GREATEST(player_win_rate, $2)
           WHERE id = $1""",
        run_id, payload.player_win_rate,
    )

    # Propagate to denormalized detail tables (these copy runs.player_win_rate
    # at insert time for query performance; keep them in sync when runs is
    # updated so aggregation filters see consistent values).
    for tbl in ("card_choices", "event_choices",
                "shop_purchases", "relic_records"):
        await conn.execute(
            f"""UPDATE {tbl}
                SET player_win_rate = GREATEST(player_win_rate, $2)
                WHERE run_id = $1""",
            run_id, payload.player_win_rate,
        )

    # branch backfill — when a history import (branch="unknown") is later
    # re-uploaded by a live client (branch="release"/"beta"), upgrade the
    # unknown tag to the known one. Also covers old pre-feature rows.
    if payload.branch not in ("", "unknown"):
        await conn.execute(
            """UPDATE runs SET branch = $2
               WHERE id = $1 AND branch = 'unknown'""",
            run_id, payload.branch,
        )
        for tbl in ("card_choices", "event_choices", "relic_records",
                    "shop_purchases", "card_removals", "card_upgrades",
                    "final_deck", "shop_card_offerings", "encounter_records",
                    "contributions"):
            await conn.execute(
                f"""UPDATE {tbl} SET branch = $2
                    WHERE run_id = $1 AND branch = 'unknown'""",
                run_id, payload.branch,
            )

    # shop_purchases — older ShopPatch builds missed colored-card buys and
    # recorded cost=0 via the MapHistory fallback. A newer payload contains
    # the authoritative list from ShopPurchasePersistence. Reset + reinsert
    # when the payload actually carries data; skip when empty so we don't
    # erase an existing good record if the reupload source was lossy.
    if payload.shop_purchases:
        await conn.execute(
            "DELETE FROM shop_purchases WHERE run_id = $1", run_id)
        await conn.executemany(
            """INSERT INTO shop_purchases
               (run_id, game_version, character, ascension, player_win_rate,
                win, item_id, item_type, cost, floor, branch)
               VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11)""",
            [
                (run_id, payload.game_version, payload.character,
                 payload.ascension, payload.player_win_rate, payload.win,
                 s.item_id, s.item_type, s.cost, s.floor, payload.branch)
                for s in payload.shop_purchases
            ],
        )

    # shop_card_offerings — MerchantRoom.Enter patch was silently unbound
    # in pre-v0.103.2 DLLs, so offerings tables were empty for those runs.
    # Reset + reinsert under the same skip-if-empty rule as purchases.
    if payload.shop_card_offerings:
        await conn.execute(
            "DELETE FROM shop_card_offerings WHERE run_id = $1", run_id)
        await conn.executemany(
            """INSERT INTO shop_card_offerings
               (run_id, game_version, character, ascension, win,
                card_id, floor, branch)
               VALUES ($1,$2,$3,$4,$5,$6,$7,$8)""",
            [
                (run_id, payload.game_version, payload.character,
                 payload.ascension, payload.win, o.card_id, o.floor,
                 payload.branch)
                for o in payload.shop_card_offerings
            ],
        )
