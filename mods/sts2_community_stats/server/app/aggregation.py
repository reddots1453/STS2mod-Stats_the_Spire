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
    min_wr: float = 0.0,
    branch: str = "all",
) -> BulkStatsBundle:
    """Run all aggregation queries and assemble a complete bundle.

    ``min_wr`` (0.0–1.0) filters contributing runs by the uploader's
    cumulative local win rate (``runs.player_win_rate``, migration 006).
    Detail tables are filtered through a correlated ``run_id IN (...)``
    subquery so the same filter applies uniformly regardless of whether
    the detail table carries a denormalized ``player_win_rate`` column.
    """

    async with pool.acquire() as conn:
        total_runs = await _count_runs(conn, character, version, min_asc, max_asc, min_wr, branch)
        cards = await _aggregate_cards(conn, character, version, min_asc, max_asc, min_wr, branch)
        relics = await _aggregate_relics(conn, character, version, min_asc, max_asc, min_wr, branch)
        events = await _aggregate_events(conn, character, version, min_asc, max_asc, min_wr, branch)
        encounters = await _aggregate_encounters(conn, character, version, min_asc, max_asc, min_wr, branch)

    bundle = BulkStatsBundle(
        generated_at=datetime.now(timezone.utc).isoformat(),
        total_runs=total_runs,
        cards=cards,
        relics=relics,
        events=events,
        encounters=encounters,
    )

    logger.info(
        "Aggregated %s asc%d-%d ver=%s br=%s wr>=%.2f: %d runs, %d cards, %d relics, %d events, %d encounters",
        character, min_asc, max_asc, version, branch, min_wr,
        total_runs, len(cards), len(relics), len(events), len(encounters),
    )
    return bundle


# ── Internal query functions ────────────────────────────────


def _build_where(char: str, ver: str, branch: str = "all") -> tuple[list, list, int]:
    """Shared char/version/branch conditions. Returns (conditions, params, next_idx).

    The caller is responsible for appending the ascension BETWEEN clause
    plus any win-rate filter — see ``_where_with_asc`` and ``_where_runs_only``.
    """
    conditions: list = []
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

    if branch != "all":
        conditions.append(f"branch = ${idx}")
        params.append(branch)
        idx += 1

    return conditions, params, idx


def _where_with_asc(
    char: str, ver: str, lo: int, hi: int, min_wr: float = 0.0,
    branch: str = "all",
) -> tuple[str, list]:
    """WHERE for a detail table (card_choices / event_choices / encounter_records /
    final_deck / shop_card_offerings / shop_purchases / relic_records / …).

    Win-rate filter goes through ``run_id IN (SELECT id FROM runs WHERE …)`` so
    it works on every detail table regardless of whether it carries a local
    ``player_win_rate`` column. Postgres compiles this to a semi-join on
    ``runs.id`` (PRIMARY KEY) and, when ``min_wr > 0``, ``idx_runs_win_rate``
    keeps the inner scan bounded.
    """
    conditions, params, idx = _build_where(char, ver, branch)
    conditions.append(f"ascension BETWEEN ${idx} AND ${idx + 1}")
    params.extend([lo, hi])
    idx += 2
    if min_wr > 0:
        conditions.append(
            f"run_id IN (SELECT id FROM runs WHERE player_win_rate >= ${idx})"
        )
        params.append(min_wr)
    clause = " WHERE " + " AND ".join(conditions) if conditions else ""
    return clause, params


def _where_runs_only(
    char: str, ver: str, lo: int, hi: int, min_wr: float = 0.0,
    branch: str = "all",
) -> tuple[str, list]:
    """WHERE for the ``runs`` table itself. ``runs`` doesn't have a ``run_id``
    column, so we can't use the subquery form — hit the local column directly.
    """
    conditions, params, idx = _build_where(char, ver, branch)
    conditions.append(f"ascension BETWEEN ${idx} AND ${idx + 1}")
    params.extend([lo, hi])
    idx += 2
    if min_wr > 0:
        conditions.append(f"player_win_rate >= ${idx}")
        params.append(min_wr)
    clause = " WHERE " + " AND ".join(conditions) if conditions else ""
    return clause, params


async def _count_runs(
    conn: asyncpg.Connection, char: str, ver: str, lo: int, hi: int,
    min_wr: float = 0.0,
    branch: str = "all",
) -> int:
    where, params = _where_runs_only(char, ver, lo, hi, min_wr, branch)
    row = await conn.fetchval(
        f"SELECT COUNT(*) FROM runs {where}", *params,
    )
    return row or 0


async def _aggregate_cards(
    conn: asyncpg.Connection, char: str, ver: str, lo: int, hi: int,
    min_wr: float = 0.0,
    branch: str = "all",
) -> dict[str, CardStats]:
    where, params = _where_with_asc(char, ver, lo, hi, min_wr, branch)

    # ── Pick rate & win rate from card_choices ──
    # Round 16: sample_size is COUNT(DISTINCT run_id) — the number of runs
    # that offered this card at least once — not COUNT(*) (the number of
    # rewards rows containing this card). The latter inflates by avg
    # rewards-per-run (≈ 3-5 for any common card) and was confusing UX:
    # F9 says "数据范围: 1000 局" but a Strike's "样本: 4500" appeared
    # 4× larger. pick_rate / win_rate stay row-based (out of every offer,
    # how often was it picked / did the picker win) — those semantics
    # need the raw row count, not distinct runs.
    rows = await conn.fetch(f"""
        SELECT
            card_id,
            AVG(was_picked::int)::real       AS pick_rate,
            AVG(CASE WHEN was_picked THEN win::int END)::real AS win_rate,
            COUNT(DISTINCT run_id)            AS sample_size
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
    total_runs = await _count_runs(conn, char, ver, lo, hi, min_wr, branch)
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
    min_wr: float = 0.0,
    branch: str = "all",
) -> dict[str, RelicStats]:
    where, params = _where_with_asc(char, ver, lo, hi, min_wr, branch)

    # Round 16: relic_records already has at most one row per (run, relic),
    # so COUNT(DISTINCT run_id) ≡ COUNT(*) in practice — kept here for
    # symmetry with cards and to stay safe if the schema ever changes.
    rows = await conn.fetch(f"""
        SELECT
            relic_id,
            AVG(win::int)::real           AS win_rate,
            COUNT(DISTINCT run_id)        AS sample_size
        FROM relic_records
        {where}
        GROUP BY relic_id
    """, *params)

    total_runs = await _count_runs(conn, char, ver, lo, hi, min_wr, branch)

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
    min_wr: float = 0.0,
    branch: str = "all",
) -> dict[str, EventStats]:
    where, params = _where_with_asc(char, ver, lo, hi, min_wr, branch)

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
    min_wr: float = 0.0,
    branch: str = "all",
) -> dict[str, EncounterStats]:
    where, params = _where_with_asc(char, ver, lo, hi, min_wr, branch)

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
