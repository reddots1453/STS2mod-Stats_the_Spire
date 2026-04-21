"""asyncpg connection pool management."""

import logging
import asyncpg
from . import config

logger = logging.getLogger("sts2stats.db")

_pool: asyncpg.Pool | None = None


async def init_pool() -> asyncpg.Pool:
    global _pool
    if _pool is None:
        logger.info("Creating database pool: min=%d max=%d", config.DB_MIN_POOL, config.DB_MAX_POOL)
        _pool = await asyncpg.create_pool(
            dsn=config.DATABASE_DSN,
            min_size=config.DB_MIN_POOL,
            max_size=config.DB_MAX_POOL,
        )
    return _pool


async def close_pool() -> None:
    global _pool
    if _pool:
        await _pool.close()
        _pool = None


def get_pool() -> asyncpg.Pool:
    assert _pool is not None, "Database pool not initialized — call init_pool() first"
    return _pool
