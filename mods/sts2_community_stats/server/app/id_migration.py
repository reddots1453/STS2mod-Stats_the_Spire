"""Cross-version ID migration — maps old card/relic/encounter IDs to current ones."""

import logging
import asyncpg

logger = logging.getLogger("sts2stats.migration")

# In-memory cache: (old_id, entity_type) → new_id
_migration_cache: dict[tuple[str, str], str] = {}


async def load_migrations(pool: asyncpg.Pool) -> None:
    """Load all ID migrations into memory. Call at startup and after updates."""
    global _migration_cache
    async with pool.acquire() as conn:
        rows = await conn.fetch(
            "SELECT old_id, new_id, entity_type FROM id_migrations ORDER BY since_version"
        )
    _migration_cache = {(r["old_id"], r["entity_type"]): r["new_id"] for r in rows}
    logger.info("Loaded %d ID migrations", len(_migration_cache))


def resolve_id(entity_id: str, entity_type: str) -> str:
    """Resolve an old ID to its current equivalent. Returns unchanged if no migration exists."""
    resolved = entity_id
    # Follow migration chain (A→B→C)
    seen: set[str] = set()
    while (resolved, entity_type) in _migration_cache and resolved not in seen:
        seen.add(resolved)
        resolved = _migration_cache[(resolved, entity_type)]
    return resolved
