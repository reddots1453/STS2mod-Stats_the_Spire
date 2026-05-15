# Changelog

## v1.1.0 (2026-05-08) — v0.105.0 Beta 兼容 + 新功能

### v0.105.0 Beta 适配

- `CombatState` → `ICombatState`：`#if STS2_GE_V105` 编译期宏，`dotnet build -p:DefineConstants=STS2_GE_V105`
- `ShowsInfiniteHp` → `HpDisplay.IsInfinite()`；`AfterCardGeneratedForCombat` bool→Player?

### 新功能：个人生涯统计"最近对局"筛选

- OptionButton（全部/最近N局）+ SpinBox，按 `StartTime` 降序 `Take(N)`

### Bug 修复

- Infused Core orb 值修饰器追踪（列表替代单例）
- 荆棘/火焰屏障多源归因
- Sword Sage Replay 1 后 FORGE:BASE 修复
- F7 功能开关持久化

---

## v1.0.1 (2026-05-06) — Bug 修复

### 怪物意图状态机面板残留修复

- 怪物死亡时若鼠标仍悬停，`OnUnfocus` 不触发 → 面板永久残留。
- `IntentHoverPatch` 新增 `ForceHideAll()`，战斗开始/结束时移除所有 intent 面板。
- `ShowPanel` 增加 `IsInsideTree()` + `IsDead` 检查。
- `ForceHideAll` 改为按 metadata 标记查找面板（修复 `InfoModPanel.Create()` 固定名称导致查找失败）。

### save&load 后 avg Damage per turn 数据失真修复

- `RunContributionAggregator._encounters` 未持久化 → save&load 后 `TotalRunTurns = 0`。
- `LiveContributionSnapshot` 新增 `Encounters` 字段，完整保存/恢复 encounter 记录。

---

## v1.0.0 (2026-05-05) — Local Edition Initial Release

Forked from `sts2_community_stats` v0.14.0. All server-dependent features removed; only local functionality retained.

### Removed (vs. community edition)
- All server communication (API client, stats provider, cache, offline queue, history import)
- Community stat labels on card rewards, shop items, relic tooltips, event options
- Map encounter danger overlay
- Data upload / download
- Filter panel community data controls (ascension range, version, branch, win rate, character filter)
- "My data" vs "Community" dual-column comparison

### Changed
- **F9 → F7**: Settings panel hotkey
- **Settings panel**: simplified to language selector + feature toggles only
- **Compendium card library / relic collection**: show personal data only (single column, from local RunHistory)
- Mod ID: `sts2_community_stats` → `sts2_statsthespire_local`

### Retained (all local features)
- Combat contribution tracking and panel (F8)
- Real-time contribution updates during combat
- Mid-run save/quit contribution persistence
- Personal career statistics (Compendium → Character Data)
- Run history single-run stats and contribution replay
- Shop price panel
- Card drop odds indicator
- Potion drop odds indicator
- Unknown room encounter probability
- Monster intent state machine
- Feature toggles
- Language switching (中文 / English)
