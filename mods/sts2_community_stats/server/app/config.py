"""Application configuration — loaded from environment variables."""

import os
from dotenv import load_dotenv

load_dotenv()

# ── Database ────────────────────────────────────────────────
_db_url = os.getenv("DATABASE_URL", "")
if not _db_url:
    raise RuntimeError(
        "DATABASE_URL environment variable must be set. "
        "Example: postgresql://sts2stats:<password>@db:5432/sts2stats"
    )
DATABASE_URL: str = _db_url

# asyncpg wants a DSN without the +asyncpg suffix that SQLAlchemy uses
DATABASE_DSN: str = DATABASE_URL.replace("postgresql+asyncpg://", "postgresql://")

DB_MIN_POOL: int = int(os.getenv("DB_MIN_POOL", "2"))
DB_MAX_POOL: int = int(os.getenv("DB_MAX_POOL", "10"))

# ── Redis ───────────────────────────────────────────────────
REDIS_URL: str = os.getenv("REDIS_URL", "redis://localhost:6379/0")
CACHE_TTL_SECONDS: int = int(os.getenv("CACHE_TTL_SECONDS", "900"))  # 15 minutes

# ── Precompute ──────────────────────────────────────────────
PRECOMPUTE_INTERVAL_MINUTES: int = int(os.getenv("PRECOMPUTE_INTERVAL_MINUTES", "10"))

CHARACTERS: list[str] = [
    "IRONCLAD", "SILENT", "DEFECT", "NECROBINDER", "REGENT",
]
# Ranges that precompute pre-warms. The cache key now reflects the exact
# range, so these are just "common queries to keep hot". The client's F9
# panel defaults to min=0, max=10, so (0, 10) must be present. (0, 20)
# covers "all ascensions" on the same page. The 5 narrow buckets are kept
# as legacy (dashboard / analytics may query specific asc tiers).
ASC_RANGES: list[tuple[int, int]] = [
    (0, 10), (0, 20),
    (0, 4), (5, 9), (10, 14), (15, 19), (20, 20),
]

# ── Security ────────────────────────────────────────────────
BLOCKED_MOD_VERSIONS: set[str] = set(
    os.getenv("BLOCKED_MOD_VERSIONS", "").split(",")
) - {""}

RATE_LIMIT_UPLOAD: str = os.getenv("RATE_LIMIT_UPLOAD", "10/minute")
RATE_LIMIT_QUERY: str = os.getenv("RATE_LIMIT_QUERY", "60/minute")

# ── Server ──────────────────────────────────────────────────
HOST: str = os.getenv("HOST", "0.0.0.0")
PORT: int = int(os.getenv("PORT", "8080"))
WORKERS: int = int(os.getenv("WORKERS", "1"))
LOG_LEVEL: str = os.getenv("LOG_LEVEL", "info")
