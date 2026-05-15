"""Precompute worker — periodically builds BulkStatsBundle for all dimension combos
and stores them in Redis. Can run standalone or as a background task in the API process."""

import logging
import time
import orjson

from . import config
from .database import get_pool
from .cache import get_redis, bulk_key
from .aggregation import compute_bulk_stats

logger = logging.getLogger("sts2stats.precompute")


async def precompute_all() -> int:
    """Run one full precompute pass. Returns number of bundles written."""
    pool = get_pool()
    redis = get_redis()
    t0 = time.monotonic()

    # Get active versions
    async with pool.acquire() as conn:
        version_rows = await conn.fetch(
            "SELECT version FROM game_versions WHERE is_active = TRUE"
        )

    versions = [r["version"] for r in version_rows]
    if not versions:
        logger.warning("No active game versions found — skipping precompute")
        return 0

    count = 0
    for branch in config.BRANCHES:
        for ver in versions:
            for char in config.CHARACTERS:
                for lo, hi in config.ASC_RANGES:
                    try:
                        bundle = await compute_bulk_stats(pool, char, ver, lo, hi, branch=branch)
                        key = bulk_key(char, f"{lo}-{hi}", ver, branch=branch)
                        json_bytes = orjson.dumps(bundle.model_dump())
                        await redis.setex(key, config.CACHE_TTL_SECONDS, json_bytes)
                        count += 1
                    except Exception:
                        logger.exception(
                            "Failed to precompute %s asc%d-%d ver=%s br=%s",
                            char, lo, hi, ver, branch,
                        )

    elapsed = time.monotonic() - t0
    logger.info(
        "Precompute complete: %d bundles in %.1fs (%d branches × %d versions × %d chars × %d ranges)",
        count, elapsed, len(config.BRANCHES), len(versions), len(config.CHARACTERS), len(config.ASC_RANGES),
    )
    return count
