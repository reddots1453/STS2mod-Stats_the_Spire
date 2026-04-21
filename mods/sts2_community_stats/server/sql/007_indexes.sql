-- ============================================================
--  Migration 007: 补建缺失的聚合索引
--  Beta 上线阻塞修复（B10-FIX P0 #1）
--
--  Why:
--   - shop_purchases / card_removals 表此前完全没有非主键索引；
--     aggregation._aggregate_cards/_aggregate_relics 每次 precompute 都会
--     全表扫描这两张表。Beta 数据量放大后 35 个 bundle × 每 bundle 2 次
--     全扫 → precompute 超时 → 缓存失效 → 在线计算雪崩。
--   - CONCURRENTLY 在事务外执行；本迁移不包 BEGIN/COMMIT，允许线上热加。
-- ============================================================

-- ── shop_purchases：按角色/版本/进阶/类型/物品聚合 ───────────
CREATE INDEX IF NOT EXISTS idx_sp_stats
    ON shop_purchases (character, game_version, ascension, item_type, item_id);

-- ── card_removals：按角色/版本/进阶/卡聚合 ──────────────────
CREATE INDEX IF NOT EXISTS idx_cr_stats
    ON card_removals (character, game_version, ascension, card_id);
