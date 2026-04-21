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
        _redis = aioredis.from_url(config.REDIS_URL, decode_responses=True)
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

def bulk_key(character: str, asc_range: str, version: str) -> str:
    return f"bulk:{character}:{asc_range}:{version}"


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
