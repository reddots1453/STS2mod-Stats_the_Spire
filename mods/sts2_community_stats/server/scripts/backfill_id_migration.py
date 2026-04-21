#!/usr/bin/env python3
"""一次性回填脚本：把历史明细行里的老实体 id 翻译为当前 id。

Why:
  B10-FIX P0 #4 把 resolve_id 接入 ingest_run 的写入路径后，**新** 上传会
  自动归一，但数据库里的历史行仍是老 id，聚合时依然被拆成两组。本脚本在
  迁移 007 执行后跑一次，扫描所有 id_migrations 条目并在对应表上批量 UPDATE。

设计要点:
  - 按实体类型分发到对应表和列（见 _TABLE_COLUMN_MAP）。
  - 单次循环链式 rename (A→B→C)：优先用客户端的 resolve_id 解出最终 id，
    避免 UPDATE 后再 UPDATE 的多次回表。
  - UPDATE 用 WHERE col = old_id AND col != new_id 防止幂等重放时刷零行。
  - 单事务整批提交，失败可安全回滚；生产建议先 --dry-run 一次。
  - 不改 runs 表自身（runs 没有 entity id 列）。

使用:
    python backfill_id_migration.py --dsn postgresql://user:pw@host/db [--dry-run]
"""

import argparse
import asyncio
import logging
import os
import sys
from pathlib import Path

import asyncpg

# 把 server 根目录放进 sys.path 以便复用 app.id_migration.resolve_id，
# 保证 ingest 与回填使用完全相同的链式解析逻辑。
_HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(_HERE.parent))

from app.id_migration import resolve_id, _migration_cache  # noqa: E402

logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s [%(levelname)s] %(name)s: %(message)s",
)
logger = logging.getLogger("backfill_id_migration")


# entity_type → [(table, column), ...]
# 与 app/ingest.py::_normalize_payload_ids 的映射保持严格一致：
# 任何新增的明细表若带可迁移 id，两边必须同步。
_TABLE_COLUMN_MAP: dict[str, list[tuple[str, str]]] = {
    "card": [
        ("card_choices", "card_id"),
        ("card_removals", "card_id"),
        ("card_upgrades", "card_id"),
        ("final_deck", "card_id"),
        ("shop_card_offerings", "card_id"),
        # shop_purchases 按 item_type 过滤后也含 card 行
        ("shop_purchases", "item_id"),
        # contributions.source_id 按 source_type 过滤
        ("contributions", "source_id"),
        ("contributions", "origin_source_id"),
    ],
    "relic": [
        ("relic_records", "relic_id"),
        ("shop_purchases", "item_id"),
        ("contributions", "source_id"),
        ("contributions", "origin_source_id"),
    ],
    "potion": [
        ("shop_purchases", "item_id"),
        ("contributions", "source_id"),
        ("contributions", "origin_source_id"),
    ],
    "encounter": [
        ("encounter_records", "encounter_id"),
        ("contributions", "encounter_id"),
    ],
    "event": [
        ("event_choices", "event_id"),
    ],
}

# shop_purchases / contributions 需要 item_type / source_type 过滤，否则会把
# 同名的不同实体串起来（例如 card 和 relic 共用一个 id 字符串时）。
_TYPE_FILTER: dict[tuple[str, str], tuple[str, str]] = {
    ("shop_purchases", "item_id"): ("item_type", "{entity}"),
    ("contributions", "source_id"): ("source_type", "{entity}"),
    ("contributions", "origin_source_id"): ("source_type", "{entity}"),
}


async def _load_migrations(conn: asyncpg.Connection) -> None:
    """把 id_migrations 表加载到内存缓存（复用 app.id_migration 的全局）。"""
    rows = await conn.fetch(
        "SELECT old_id, new_id, entity_type FROM id_migrations ORDER BY since_version"
    )
    _migration_cache.clear()
    for r in rows:
        _migration_cache[(r["old_id"], r["entity_type"])] = r["new_id"]
    logger.info("Loaded %d id_migrations entries", len(_migration_cache))


def _collect_old_ids() -> dict[str, set[str]]:
    """按 entity_type 收集所有需要翻译的 old_id。"""
    by_type: dict[str, set[str]] = {}
    for (old_id, entity_type), _ in _migration_cache.items():
        by_type.setdefault(entity_type, set()).add(old_id)
    return by_type


async def _update_one(
    conn: asyncpg.Connection, table: str, column: str, entity_type: str,
    old_id: str, new_id: str, dry_run: bool,
) -> int:
    """把 table.column 中 = old_id 的行改为 new_id。返回受影响行数。"""
    # 对需要类型过滤的表拼出额外 WHERE 条件
    type_filter = _TYPE_FILTER.get((table, column))
    extra = ""
    params: list = [new_id, old_id]
    if type_filter:
        type_col, _ = type_filter
        extra = f" AND {type_col} = $3"
        params.append(entity_type)

    if dry_run:
        row = await conn.fetchval(
            f"SELECT COUNT(*) FROM {table} "
            f"WHERE {column} = $2 AND {column} <> $1{extra}",
            *params,
        )
        return int(row or 0)

    result = await conn.execute(
        f"UPDATE {table} SET {column} = $1 "
        f"WHERE {column} = $2 AND {column} <> $1{extra}",
        *params,
    )
    # asyncpg 的 execute 返回形如 "UPDATE 42" 的字符串
    try:
        return int(result.split()[-1])
    except (ValueError, IndexError):
        return 0


async def run(dsn: str, dry_run: bool) -> int:
    conn = await asyncpg.connect(dsn)
    try:
        await _load_migrations(conn)
        if not _migration_cache:
            logger.info("id_migrations 表为空，无需回填")
            return 0

        total_updated = 0
        by_type = _collect_old_ids()

        # 整批包在一个事务里；dry-run 也包进去以便最后 rollback 保持零副作用。
        tx = conn.transaction()
        await tx.start()
        try:
            for entity_type, old_ids in by_type.items():
                tables = _TABLE_COLUMN_MAP.get(entity_type)
                if not tables:
                    logger.warning("未知 entity_type=%s，跳过（共 %d 个 id）",
                                   entity_type, len(old_ids))
                    continue

                for old_id in old_ids:
                    new_id = resolve_id(old_id, entity_type)
                    if new_id == old_id:
                        continue  # 链解析后未变化（孤立环节点）
                    for table, column in tables:
                        n = await _update_one(
                            conn, table, column, entity_type,
                            old_id, new_id, dry_run,
                        )
                        if n > 0:
                            logger.info(
                                "%s[%s] %s → %s : %d rows%s",
                                table, column, old_id, new_id, n,
                                " (dry-run)" if dry_run else "",
                            )
                            total_updated += n

            if dry_run:
                await tx.rollback()
                logger.info("DRY-RUN 完成：预计共 %d 行会被更新", total_updated)
            else:
                await tx.commit()
                logger.info("回填完成：共更新 %d 行", total_updated)
        except Exception:
            await tx.rollback()
            raise
        return total_updated
    finally:
        await conn.close()


def _parse_args() -> argparse.Namespace:
    p = argparse.ArgumentParser(description="回填历史数据的 id 跨版本映射")
    p.add_argument(
        "--dsn",
        default=os.getenv("DATABASE_URL", ""),
        help="PostgreSQL DSN（默认读 DATABASE_URL 环境变量）",
    )
    p.add_argument(
        "--dry-run",
        action="store_true",
        help="只统计受影响行数，不实际修改",
    )
    return p.parse_args()


def main() -> None:
    args = _parse_args()
    if not args.dsn:
        print("错误：必须指定 --dsn 或设置 DATABASE_URL 环境变量", file=sys.stderr)
        sys.exit(2)
    asyncio.run(run(args.dsn, args.dry_run))


if __name__ == "__main__":
    main()
