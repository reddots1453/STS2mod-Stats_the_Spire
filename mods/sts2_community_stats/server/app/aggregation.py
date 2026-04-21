"""SQL aggregation queries — builds BulkStatsBundle from raw data."""

import logging
from datetime import datetime, timezone
import asyncpg
from .models import (
    BulkStatsBundle, CardStats, RelicStats,
    EventStats, EventOptionStats, ComboOptionStats, EncounterStats,
)

logger = logging.getLogger("sts2stats.agg")


async def compute_bulk_stats(
    pool: asyncpg.Pool,
    character: str,
    version: str,
    min_asc: int,
    max_asc: int,
) -> BulkStatsBundle:
    """Run all aggregation queries and assemble a complete bundle."""

    async with pool.acquire() as conn:
        total_runs = await _count_runs(conn, character, version, min_asc, max_asc)
        cards = await _aggregate_cards(conn, character, version, min_asc, max_asc)
        relics = await _aggregate_relics(conn, character, version, min_asc, max_asc)
        events = await _aggregate_events(conn, character, version, min_asc, max_asc)
        encounters = await _aggregate_encounters(conn, character, version, min_asc, max_asc)

    bundle = BulkStatsBundle(
        generated_at=datetime.now(timezone.utc).isoformat(),
        total_runs=total_runs,
        cards=cards,
        relics=relics,
        events=events,
        encounters=encounters,
    )

    logger.info(
        "Aggregated %s asc%d-%d ver=%s: %d runs, %d cards, %d relics, %d events, %d encounters",
        character, min_asc, max_asc, version,
        total_runs, len(cards), len(relics), len(events), len(encounters),
    )
    return bundle


# ── Internal query functions ────────────────────────────────


def _build_where(char: str, ver: str) -> tuple[str, list]:
    """Build a dynamic WHERE clause, skipping character/version when 'all'."""
    conditions = []
    params: list = []
    idx = 1

    if char.lower() != "all":
        conditions.append(f"character = ${idx}")
        params.append(char)
        idx += 1

    if ver.lower() != "all":
        conditions.append(f"game_version = ${idx}")
        params.append(ver)
        idx += 1

    conditions.append(f"ascension BETWEEN ${idx} AND ${idx + 1}")
    # params for lo, hi are appended by caller
    return (" WHERE " + " AND ".join(conditions) if conditions else "", params, idx)


def _where_with_asc(char: str, ver: str, lo: int, hi: int) -> tuple[str, list]:
    """Return (where_clause, full_params_list) ready for query."""
    clause, params, _ = _build_where(char, ver)
    params.extend([lo, hi])
    return clause, params


async def _count_runs(
    conn: asyncpg.Connection, char: str, ver: str, lo: int, hi: int,
) -> int:
    where, params = _where_with_asc(char, ver, lo, hi)
    row = await conn.fetchval(
        f"SELECT COUNT(*) FROM runs {where}", *params,
    )
    return row or 0


async def _aggregate_cards(
    conn: asyncpg.Connection, char: str, ver: str, lo: int, hi: int,
) -> dict[str, CardStats]:
    where, params = _where_with_asc(char, ver, lo, hi)

    # ── Pick rate & win rate from card_choices ──
    rows = await conn.fetch(f"""
        SELECT
            card_id,
            AVG(was_picked::int)::real       AS pick_rate,
            AVG(CASE WHEN was_picked THEN win::int END)::real AS win_rate,
            COUNT(*)                          AS sample_size
        FROM card_choices
        {where}
        GROUP BY card_id
    """, *params)

    result: dict[str, CardStats] = {}
    for r in rows:
        result[r["card_id"]] = CardStats(
            pick=r["pick_rate"] or 0,
            win=r["win_rate"] or 0,
            n=r["sample_size"],
        )

    # ── Removal rates ──
    removal_rows = await conn.fetch(f"""
        SELECT card_id, COUNT(*) AS cnt
        FROM card_removals
        {where}
        GROUP BY card_id
    """, *params)
    total_runs = await _count_runs(conn, char, ver, lo, hi)
    if total_runs > 0:
        for r in removal_rows:
            cid = r["card_id"]
            if cid in result:
                result[cid].removal = r["cnt"] / total_runs

    # ── Upgrade rates (from final deck: upgraded / total instances) ──
    upgrade_rows = await conn.fetch(f"""
        SELECT card_id,
               SUM(CASE WHEN upgrade_level > 0 THEN 1 ELSE 0 END)::real
                   / COUNT(*)::real AS upgrade_rate
        FROM final_deck
        {where}
        GROUP BY card_id
    """, *params)
    for r in upgrade_rows:
        cid = r["card_id"]
        if cid in result:
            result[cid].upgrade = r["upgrade_rate"] or 0

    # ── Shop buy rates (purchases / times offered in shop) ──
    # Semantics: P(buy card | card was offered).
    # The numerator MUST be scoped to "a purchase that was paired with an
    # offering of the same card in the same run". Counting all purchases
    # regardless of offering data produces ratios >1 whenever the offering
    # wasn't captured — notably for pre-fix runs where MerchantRoom.Enter
    # was silently unbound (zero offerings recorded) but BoughtColorless
    # from MapHistory still contributed purchases. Observed in the wild:
    # colorless cards hitting 500%+ because denominator = 1 new offering
    # while numerator accumulated across several old imports.
    offering_rows = await conn.fetch(f"""
        SELECT card_id, COUNT(*) AS offered
        FROM shop_card_offerings
        {where}
        GROUP BY card_id
    """, *params)
    # `where` uses bare column names (character, game_version, ascension);
    # PostgreSQL resolves them to shop_purchases since shop_card_offerings
    # only appears as a correlated subquery. Alias `sp` is only needed for
    # the subquery's cross-reference `sco.run_id = sp.run_id`.
    purchase_rows = await conn.fetch(f"""
        SELECT sp.item_id, COUNT(*) AS bought
        FROM shop_purchases sp
        {where} AND sp.item_type = 'card'
          AND EXISTS (
            SELECT 1 FROM shop_card_offerings sco
            WHERE sco.run_id = sp.run_id AND sco.card_id = sp.item_id
          )
        GROUP BY sp.item_id
    """, *params)
    purchase_map = {r["item_id"]: r["bought"] for r in purchase_rows}
    for r in offering_rows:
        cid = r["card_id"]
        offered = r["offered"]
        bought = purchase_map.get(cid, 0)
        if cid in result and offered > 0:
            # Defensive min(): even with the EXISTS guard, repeat-offering
            # rows within a single shop could theoretically produce
            # bought > offered — clamp to prevent any >100% leak.
            result[cid].shop_buy = min(1.0, bought / offered)

    return result


async def _aggregate_relics(
    conn: asyncpg.Connection, char: str, ver: str, lo: int, hi: int,
) -> dict[str, RelicStats]:
    where, params = _where_with_asc(char, ver, lo, hi)

    rows = await conn.fetch(f"""
        SELECT
            relic_id,
            AVG(win::int)::real  AS win_rate,
            COUNT(*)             AS sample_size
        FROM relic_records
        {where}
        GROUP BY relic_id
    """, *params)

    total_runs = await _count_runs(conn, char, ver, lo, hi)

    result: dict[str, RelicStats] = {}
    for r in rows:
        pick = r["sample_size"] / total_runs if total_runs > 0 else 0
        result[r["relic_id"]] = RelicStats(
            win=r["win_rate"] or 0,
            pick=pick,
            n=r["sample_size"],
        )

    # ── Shop buy rates for relics ──
    shop_rows = await conn.fetch(f"""
        SELECT item_id, COUNT(*) AS cnt
        FROM shop_purchases
        {where} AND item_type = 'relic'
        GROUP BY item_id
    """, *params)
    if total_runs > 0:
        for r in shop_rows:
            rid = r["item_id"]
            if rid in result:
                result[rid].shop_buy = r["cnt"] / total_runs

    return result


async def _aggregate_events(
    conn: asyncpg.Connection, char: str, ver: str, lo: int, hi: int,
) -> dict[str, EventStats]:
    where, params = _where_with_asc(char, ver, lo, hi)

    # ── Static events (option_index >= 0, no combo_key) ──
    rows = await conn.fetch(f"""
        SELECT
            event_id,
            option_index,
            COUNT(*)::real / SUM(COUNT(*)) OVER (PARTITION BY event_id) AS selection_rate,
            AVG(win::int)::real AS win_rate,
            COUNT(*)            AS sample_size
        FROM event_choices
        {where} AND option_index >= 0 AND combo_key IS NULL
        GROUP BY event_id, option_index
        ORDER BY event_id, option_index
    """, *params)

    result: dict[str, EventStats] = {}
    for r in rows:
        eid = r["event_id"]
        if eid not in result:
            result[eid] = EventStats()
        result[eid].options.append(EventOptionStats(
            idx=r["option_index"],
            sel=r["selection_rate"] or 0,
            win=r["win_rate"] or 0,
            n=r["sample_size"],
        ))

    # ── Combo events (ancient events with combo_key) ──
    combo_rows = await conn.fetch(f"""
        SELECT
            event_id,
            combo_key,
            chosen_option_id,
            COUNT(*)::real / SUM(COUNT(*)) OVER (
                PARTITION BY event_id, combo_key
            ) AS selection_rate,
            AVG(win::int)::real AS win_rate,
            COUNT(*)            AS sample_size
        FROM event_choices
        {where} AND combo_key IS NOT NULL
        GROUP BY event_id, combo_key, chosen_option_id
        ORDER BY event_id, combo_key, chosen_option_id
    """, *params)

    for r in combo_rows:
        eid = r["event_id"]
        if eid not in result:
            result[eid] = EventStats()
        es = result[eid]
        if es.combos is None:
            es.combos = {}
        ck = r["combo_key"]
        if ck not in es.combos:
            es.combos[ck] = []
        es.combos[ck].append(ComboOptionStats(
            id=r["chosen_option_id"],
            sel=r["selection_rate"] or 0,
            win=r["win_rate"] or 0,
            n=r["sample_size"],
        ))

    # ── Flat dynamic events (COLORFUL_PHILOSOPHERS etc.) ──
    flat_rows = await conn.fetch(f"""
        SELECT
            event_id,
            chosen_option_id,
            COUNT(*)::real / SUM(COUNT(*)) OVER (
                PARTITION BY event_id
            ) AS selection_rate,
            AVG(win::int)::real AS win_rate,
            COUNT(*)            AS sample_size
        FROM event_choices
        {where} AND combo_key IS NULL AND chosen_option_id IS NOT NULL
        GROUP BY event_id, chosen_option_id
        ORDER BY event_id, chosen_option_id
    """, *params)

    for r in flat_rows:
        eid = r["event_id"]
        if eid not in result:
            result[eid] = EventStats()
        es = result[eid]
        if es.flat_options is None:
            es.flat_options = []
        es.flat_options.append(ComboOptionStats(
            id=r["chosen_option_id"],
            sel=r["selection_rate"] or 0,
            win=r["win_rate"] or 0,
            n=r["sample_size"],
        ))

    return result


async def _aggregate_encounters(
    conn: asyncpg.Connection, char: str, ver: str, lo: int, hi: int,
) -> dict[str, EncounterStats]:
    where, params = _where_with_asc(char, ver, lo, hi)

    rows = await conn.fetch(f"""
        SELECT
            encounter_id,
            encounter_type,
            AVG(damage_taken)::real      AS avg_dmg,
            AVG(turns_taken)::real       AS avg_turns,
            AVG(player_died::int)::real  AS death_rate,
            COUNT(*)                     AS sample_size
        FROM encounter_records
        {where}
        GROUP BY encounter_id, encounter_type
    """, *params)

    result: dict[str, EncounterStats] = {}
    for r in rows:
        result[r["encounter_id"]] = EncounterStats(
            type=r["encounter_type"],
            avg_dmg=r["avg_dmg"] or 0,
            death=r["death_rate"] or 0,
            avg_turns=r["avg_turns"] or 0,
            n=r["sample_size"],
        )

    return result
