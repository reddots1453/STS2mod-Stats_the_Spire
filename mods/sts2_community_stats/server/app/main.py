"""FastAPI application — the main API server entry point."""

import logging
from contextlib import asynccontextmanager

import orjson
from apscheduler.schedulers.asyncio import AsyncIOScheduler
from fastapi import FastAPI, Request, Response, HTTPException, Query
from fastapi.responses import ORJSONResponse
from slowapi import Limiter
from slowapi.util import get_remote_address
from slowapi.errors import RateLimitExceeded
from starlette.middleware.gzip import GZipMiddleware

from . import config
from .database import init_pool, close_pool, get_pool
from .cache import init_redis, close_redis, get_redis, bulk_key, map_asc_range
from .models import RunUploadPayload, BulkStatsBundle
from .ingest import ingest_run
from .aggregation import compute_bulk_stats
from .precompute import precompute_all
from .id_migration import load_migrations

# ── Logging ─────────────────────────────────────────────────
logging.basicConfig(
    level=getattr(logging, config.LOG_LEVEL.upper()),
    format="%(asctime)s [%(levelname)s] %(name)s: %(message)s",
)
logger = logging.getLogger("sts2stats.api")


# ── Lifespan ────────────────────────────────────────────────
@asynccontextmanager
async def lifespan(app: FastAPI):
    # Startup
    await init_pool()
    await init_redis()
    await load_migrations(get_pool())

    # Start precompute scheduler
    scheduler = AsyncIOScheduler()
    scheduler.add_job(
        precompute_all,
        "interval",
        minutes=config.PRECOMPUTE_INTERVAL_MINUTES,
        id="precompute",
        max_instances=1,
        misfire_grace_time=60,
    )
    scheduler.start()
    logger.info(
        "Precompute scheduler started (every %d min)", config.PRECOMPUTE_INTERVAL_MINUTES
    )

    # Run initial precompute
    try:
        await precompute_all()
    except Exception:
        logger.exception("Initial precompute failed (non-fatal)")

    yield

    # Shutdown
    scheduler.shutdown(wait=False)
    await close_redis()
    await close_pool()


# ── App ─────────────────────────────────────────────────────
app = FastAPI(
    title="STS2 Community Stats API",
    version="1.0.0",
    lifespan=lifespan,
    default_response_class=ORJSONResponse,
)

# Gzip for large bulk responses
app.add_middleware(GZipMiddleware, minimum_size=1000)

# Rate limiter
limiter = Limiter(key_func=get_remote_address)
app.state.limiter = limiter


@app.exception_handler(RateLimitExceeded)
async def rate_limit_handler(request: Request, exc: RateLimitExceeded):
    return ORJSONResponse(
        status_code=429,
        content={"error": "Rate limit exceeded. Please slow down."},
    )


# ── Middleware: mod version check ───────────────────────────
@app.middleware("http")
async def check_mod_version(request: Request, call_next):
    mod_ver = request.headers.get("X-Mod-Version", "")
    if mod_ver and mod_ver in config.BLOCKED_MOD_VERSIONS:
        return ORJSONResponse(
            status_code=426,
            content={"error": "Please update Community Stats mod to the latest version."},
        )
    return await call_next(request)


# ════════════════════════════════════════════════════════════
#  ENDPOINTS
# ════════════════════════════════════════════════════════════

# ── Health ──────────────────────────────────────────────────

@app.get("/health")
async def health():
    errors = {}
    try:
        pool = get_pool()
        async with pool.acquire() as conn:
            await conn.fetchval("SELECT 1")
    except Exception as e:
        errors["db"] = str(e)

    try:
        redis = get_redis()
        await redis.ping()
    except Exception as e:
        errors["redis"] = str(e)

    if errors:
        # Log full error details server-side for debugging, but do NOT
        # return them to the caller — exception strings may leak pg
        # version, connection hostnames, Redis internals, etc.
        logger.warning("Health check failed: %s", errors)
        return ORJSONResponse(
            status_code=503,
            content={"status": "unhealthy"},
        )
    return {"status": "healthy", "db": "ok", "redis": "ok"}


# ── Upload ──────────────────────────────────────────────────

@app.post("/v1/runs")
@limiter.limit(config.RATE_LIMIT_UPLOAD)
async def upload_run(request: Request, payload: RunUploadPayload):
    run_id = await ingest_run(get_pool(), payload)
    # ingest_run no longer returns -1 — duplicates now trigger a backfill
    # and return the existing run_id. Preserve the `dedup` flag in the
    # response so the client can tell fresh inserts from backfills.
    return {"status": "ok", "run_id": run_id}


# ── Bulk Stats ──────────────────────────────────────────────

@app.get("/v1/stats/bulk")
@limiter.limit(config.RATE_LIMIT_QUERY)
async def get_bulk_stats(
    request: Request,
    char: str = Query(..., max_length=32),
    ver: str = Query(..., max_length=16),
    min_asc: int = Query(0, ge=0, le=20),
    max_asc: int = Query(20, ge=0, le=20),
):
    char = char.upper()
    redis = get_redis()
    asc_range = map_asc_range(min_asc, max_asc)
    key = bulk_key(char, asc_range, ver)

    # Try Redis cache
    cached = await redis.get(key)
    if cached:
        return Response(
            content=cached if isinstance(cached, bytes) else cached.encode(),
            media_type="application/json",
        )

    # Cache miss — compute on the fly
    bundle = await compute_bulk_stats(get_pool(), char, ver, min_asc, max_asc)
    json_bytes = orjson.dumps(bundle.model_dump())

    # Store in Redis
    await redis.setex(key, config.CACHE_TTL_SECONDS, json_bytes)

    return Response(content=json_bytes, media_type="application/json")


# ── Card Stats (on-demand) ──────────────────────────────────

@app.get("/v1/stats/cards")
@limiter.limit(config.RATE_LIMIT_QUERY)
async def get_card_stats(
    request: Request,
    cards: str = Query(..., max_length=2000),
    char: str = Query(..., max_length=32),
    ver: str = Query(..., max_length=16),
    min_asc: int = Query(0, ge=0, le=20),
    max_asc: int = Query(20, ge=0, le=20),
):
    char = char.upper()
    card_ids = [c.strip() for c in cards.split(",") if c.strip()]
    if not card_ids or len(card_ids) > 50:
        raise HTTPException(400, "Provide 1-50 card IDs")

    bundle = await compute_bulk_stats(get_pool(), char, ver, min_asc, max_asc)
    result = {cid: bundle.cards.get(cid) for cid in card_ids}
    return result


# ── Relic Stats (on-demand) ─────────────────────────────────

@app.get("/v1/stats/relics")
@limiter.limit(config.RATE_LIMIT_QUERY)
async def get_relic_stats(
    request: Request,
    relics: str = Query(..., max_length=2000),
    char: str = Query(..., max_length=32),
    ver: str = Query(..., max_length=16),
    min_asc: int = Query(0, ge=0, le=20),
    max_asc: int = Query(20, ge=0, le=20),
):
    char = char.upper()
    relic_ids = [r.strip() for r in relics.split(",") if r.strip()]
    if not relic_ids or len(relic_ids) > 50:
        raise HTTPException(400, "Provide 1-50 relic IDs")

    bundle = await compute_bulk_stats(get_pool(), char, ver, min_asc, max_asc)
    result = {rid: bundle.relics.get(rid) for rid in relic_ids}
    return result


# ── Event Stats (on-demand) ─────────────────────────────────

@app.get("/v1/stats/events/{event_id}")
@limiter.limit(config.RATE_LIMIT_QUERY)
async def get_event_stats(
    request: Request,
    event_id: str,
    char: str = Query(..., max_length=32),
    ver: str = Query(..., max_length=16),
    min_asc: int = Query(0, ge=0, le=20),
    max_asc: int = Query(20, ge=0, le=20),
):
    char = char.upper()
    bundle = await compute_bulk_stats(get_pool(), char, ver, min_asc, max_asc)
    stats = bundle.events.get(event_id)
    if stats is None:
        raise HTTPException(404, f"No data for event {event_id}")
    return stats


# ── Encounter Stats (on-demand) ─────────────────────────────

@app.get("/v1/stats/encounters")
@limiter.limit(config.RATE_LIMIT_QUERY)
async def get_encounter_stats(
    request: Request,
    ids: str = Query(..., max_length=2000),
    char: str = Query(..., max_length=32),
    ver: str = Query(..., max_length=16),
    min_asc: int = Query(0, ge=0, le=20),
    max_asc: int = Query(20, ge=0, le=20),
):
    char = char.upper()
    encounter_ids = [e.strip() for e in ids.split(",") if e.strip()]
    if not encounter_ids or len(encounter_ids) > 50:
        raise HTTPException(400, "Provide 1-50 encounter IDs")

    bundle = await compute_bulk_stats(get_pool(), char, ver, min_asc, max_asc)
    result = {eid: bundle.encounters.get(eid) for eid in encounter_ids}
    return result


# ── Version list ────────────────────────────────────────────

@app.get("/v1/meta/versions")
async def get_versions():
    pool = get_pool()
    async with pool.acquire() as conn:
        rows = await conn.fetch(
            "SELECT version FROM game_versions WHERE is_active = TRUE ORDER BY first_seen DESC"
        )
    return [r["version"] for r in rows]
