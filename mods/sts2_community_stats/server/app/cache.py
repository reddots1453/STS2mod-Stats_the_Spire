"""Redis cache layer."""

import logging
import redis.asyncio as aioredis
from . import config

logger = logging.getLogger("sts2stats.cache")

_redis: aioredis.Redis | None = None


async def init_redis() -> aioredis.Redis:
    global _redis
    if _redis is None:
        logger.info("Connecting to Redis: %s", config.REDIS_URL)
        kwargs: dict = {"decode_responses": True}
        if config.REDIS_PASSWORD:
            kwargs["password"] = config.REDIS_PASSWORD
        _redis = aioredis.from_url(config.REDIS_URL, **kwargs)
        await _redis.ping()
        logger.info("Redis connected")
    return _redis


async def close_redis() -> None:
    global _redis
    if _redis:
        await _redis.aclose()
        _redis = None


def get_redis() -> aioredis.Redis:
    assert _redis is not None, "Redis not initialized — call init_redis() first"
    return _redis


# ── Cache key helpers ───────────────────────────────────────

def bulk_key(
    character: str, asc_range: str, version: str, min_wr: float = 0.0,
    branch: str = "all",
) -> str:
    # min_wr == 0 keeps the original key layout so precomputed bundles and
    # pre-existing cache entries continue to hit. Any non-zero filter writes
    # under a distinct key — otherwise 30% and 50% requests would alias onto
    # the unfiltered bundle and the F9 slider would appear to do nothing.
    base = f"bulk:{character}:{asc_range}:{version}"
    if branch != "all":
        base += f":br{branch}"
    if min_wr > 0:
        base += f":wr{min_wr:.2f}"
    return base


def map_asc_range(min_asc: int, max_asc: int) -> str:
    """Return the EXACT ascension range as a cache key.

    Previously this mapped arbitrary ranges to the "nearest precomputed
    bucket" — but that was semantically broken. Client queries for 0-10
    were mapped to bucket "5-9" and got empty bundles back because the
    actual aggregation ran against an unrelated slice of the data. Now
    the cache key reflects the exact query range; precompute pre-warms
    the common client defaults (see config.ASC_RANGES). Any range not
    in ASC_RANGES still works — it just misses cache and recomputes on
    the fly (then caches under its own key for subsequent hits).
    """
    return f"{min_asc}-{max_asc}"
