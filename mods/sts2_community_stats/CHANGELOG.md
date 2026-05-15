# Changelog

## v0.16.2 (2026-05-10) — Bug 修复合集

### F9 版本选择框过滤

- **正式版仅保留 0.99.1 / 0.103.1**：其他版本一律归为 beta 分支
- **Beta 分支仅显示最近 3 个**：`PopulateVersionSlotsAsync` 中限制 `betaCount <= 3`，其余隐藏

### 自动更新系统

- 新增 `update.bat` 脚本（PowerShell），用户关游戏后双击即可下载并替换最新 DLL
- Mod 启动时检查版本，有新版弹窗提示（F9 风格深色面板 + 金色标题）
- `Updater.cs`：版本检查 + 自定义对话框（外观与 F9 一致）
- 服务端 `/v1/meta/update-info` + `/v1/updates/` 端点支持

### 数据加载修复

- **主菜单不自动拉取社区数据**：`Initialize()` 末尾新增 `OnFilterChangedAsync` 初始调用，不再依赖 F9 筛选变更才首次加载
- **近期对局筛选影响 Boss 战损**：`GetCached` 在 `recentCount > 0` 时跳过磁盘缓存，防止过时未过滤数据覆盖

### UI 修复

- **F9 菜单**：版本+分支合并为单一下拉框（版本槽），异步拉取版本列表
- **F9 角色下拉图标过大**：角色头像限制 24×24，超过等比缩放（防止美化 mod 替换高清大图导致布局错位）
- **F9 面板可拖动**：标题栏支持拖拽移动
- **回放贡献图表**：收起子条目时不再退化为空面板（`_replayRunData` 保存回放数据，`RefreshTabs` 优先使用）
- **回放贡献图表**：移除多余"本场战斗"tab，只显示"本局汇总"

### 跨版本兼容

- **门扉缔造者在 beta 中显示空条目**：`BetaRemovedBosses` 包含 `DOORMAKER_BOSS`，beta 分支隐藏（同时过滤历史数据中的遗留条目）
- **Neow 遗物缺失**：`KALEIDOSCOPE` / `FISHING_ROD` / `SILKEN_TRESS` 加入 positive_pool
- **BranchManager**：改用 `ReleaseInfo.Branch` 判断 beta（`release_info.json` 比 Steam API 更可靠），缓存结果
- **`.xyz` / `.org` / DuckDNS 域名 GFW 阻断**：最终迁移至阿里云 `statsthespire.org.cn`（境内 DNS 不投毒，HTTPS 全链路）

### 发布修复

- `manifest.json` 重复 `version` key（本地版）→ 删除旧值
- `config.json` 被引擎扫描为 mod manifest → 重命名为 `settings.cfg`（引擎不扫描 `.cfg`）

---

## v0.15.0 (2026-05-08) — v0.105.0 Beta 兼容 + 新功能

### v0.105.0 Beta 适配

- `CombatState` → `ICombatState` 接口提取：所有 `CombatHistory` / `Hook` / `CombatManager` 方法签名变更。使用 `#if STS2_GE_V105` 编译期宏 + `using` 类型别名，同一源码兼容 v0.103 和 v0.105+。构建方式：`dotnet build -p:DefineConstants=STS2_GE_V105`
- `Creature.ShowsInfiniteHp` 移除 → 替换为 `HpDisplay.IsInfinite()`
- `Hook.AfterCardGeneratedForCombat` 参数 `bool addedByPlayer` → `Player? creator`

### 新功能：个人生涯统计"最近对局"筛选

- SectionPanel 标题栏新增第三个筛选项：OptionButton（全部 / 最近N局）+ 条件可见 SpinBox（用户输入 N）
- `RunHistoryAnalyzer` 缓存 key 扩展为三元组 `(角色, 进阶, 最近局数)`
- `BuildSnapshot` 按 `StartTime` 降序后 `Take(N)` 筛选
- 中英文均已适配

### Bug 修复

- **Infused Core** +1 闪电球伤害未归因：`PendingOrbFocusContrib` 单例 → `PendingOrbValueContribs` 列表，支持 Focus + 遗物同时修饰 orb 值
- **荆棘/火焰屏障** 多源归因错误：`OnDamageDealt` 间接伤害分派新增 `DistributeByPowerSources` 回退逻辑，处理玩家增益的多源情况
- **Sword Sage** Replay 1 后 `FORGE:BASE` 丢失：`FlushForgeSubBars` 改用 `+=` 累加，base 写入移至 `_forgeLog.Count == 0` 守卫外
- **本地版** 功能开关不持久化：`Initialize()` 新增 `ModConfig.LoadOverrides()`；`SaveSettings` 写入 `%AppData%/mod_prefs.json`
- **HydrateRunTotals** 空字典防护：`totals.Count == 0` 时跳过 Clear

---

## v0.14.1 (2026-05-06) — Bug 修复

### 怪物意图状态机面板残留修复

- 怪物死亡时若鼠标仍悬停，`OnUnfocus` 不触发 → 面板永久残留，只能重启清除。
- `IntentHoverPatch` 新增 `ForceHideAll()` 方法，战斗开始/结束时遍历 scene root 移除所有 intent 面板。
- `ShowPanel` 增加 `IsInsideTree()` + `IsDead` 有效性检查，拒绝为已死/已移除的怪物创建面板。
- `ForceHideAll` 改为按 metadata 标记（`PanelMeta`）查找面板，而非面板名称（`InfoModPanel.Create()` 固定设 `Name = "InfoModPanel"`，所有面板共享同名导致查找失败）。

### save&load 后 avg Damage per turn 数据失真修复

- `RunContributionAggregator._encounters` 从未被持久化到 `_live.json`。
- save&load 后 `_runTotals` 恢复（伤害数据完整）但 `_encounters` 为空 → `TotalRunTurns = 0` → DPS 显示 "—"。打完一场后回合数仅 1 场但伤害为全部历史累计 → 数值严重膨胀。
- `LiveContributionSnapshot` 新增 `Encounters` 字段，`BuildLiveSnapshot` 包含 encounter 记录，`HydrateFromLiveSnapshot` 恢复；`ContributionPersistence` 新增 `EncounterDto` 序列化支持。

---

## v0.14.0 (2026-05-04) — 网络安全加固 + 正式域名迁移

> 全面审查客户端-服务端通信安全，修复多个 P0/P1 级别漏洞；最终迁移到阿里云 `statsthespire.org.cn`（境内 DNS 不投毒，HTTPS 全链路可用）。

### 安全加固

- **P0**：API 端口绑定从 `0.0.0.0:5080` 改为 `127.0.0.1:5080`，禁止外部绕过 nginx TLS 直接访问
- **P0**：nginx 统一使用正式域名 + Let's Encrypt 证书
- **P1**：FastAPI 层新增请求体大小限制中间件（413 on >1MB），纵深防御
- **P1**：`/health` 端点添加限流（nginx `30r/m` + FastAPI `30r/m`）
- **P1**：Redis 密码认证支持（`REDIS_PASSWORD` 环境变量，空值向后兼容）
- **P1**：Pydantic 模型严格校验：`branch`（pattern）、`character`（白名单）、`run_hash`（hex32 格式）
- **P2**：nginx 新增安全头：`Referrer-Policy`、`Permissions-Policy`、`Content-Security-Policy`
- **P2**：HSTS 限制为当前域名（移除 `includeSubDomains`）

### 客户端 HTTPS 强制

- `ApiClient.cs`：无条件强制 HTTPS（`AllowHttp` 作为显式 opt-in 保留）
- 新增 `User-Agent: StatsTheSpire/x.x` 便于服务端日志追踪
- `ModConfig.cs` 默认 URL 更新为 `https://statsthespire.org.cn/v1`

### 域名历程

- DuckDNS (`duckdns.org`) → GFW DNS + TLS SNI 双重阻断（RST）
- Dynadot `.xyz` → GFW 权威 DNS 层阻断（SERVFAIL），DoH 亦无效
- Dynadot `.org` → 短暂可用后 GFW DNS 投毒
- **最终方案**：阿里云 `statsthespire.org.cn` — 国内注册商 DNS 不投毒，HTTPS 全链路验证通过

### 修复

- `BranchManager.cs`：修正 `PlatformUtil.GetPlatformBranch()` 返回类型（enum 非 string），用 `ToString() == "Public"` 判断分支

---

## v0.13.1 (2026-04-15) — Map/TopBar UI 回归修复

> v0.13.0 发布后用户报告两个 UI 功能丢失，以及相关的边界 case。本 patch 版本定位并修复。

### 地图节点危险度 — 用真实 encounter ID 查询

[MapPointPatch.cs](src/Patches/MapPointPatch.cs) 之前在 postfix 中用 `point.PointType.ToString().ToLowerInvariant()` 作为 bundle 查询 key（返回 `"monster"` / `"elite"` / `"boss"`），但 bundle 的 encounter 字段 key 是真实的 encounter ModelId（`SLIMES_NORMAL` / `BYRDONIS_ELITE` / `KAISER_CRAB_BOSS` / ...），两者**永远不可能匹配**，所以 traveled 节点从未显示过死亡率 / 平均伤害 pill。

**修复**：新增 `ResolveEncounterIdForPoint(point)` helper，走 `RunState.MapPointHistory[act][coord.row]` 拿到该 traveled 节点的真实 encounter ModelId，然后按 ID 查 bundle。同时校验 `entry.MapPointType == point.PointType` 防止 row index 错位。

### Elite / rare encounter "No Data" 兜底

[MapPointOverlay.cs](src/UI/MapPointOverlay.cs) 之前 `if (stats == null) return;` 会在 bundle 没有该 encounter 条目时（例如稀有 elite，社区样本量 = 0）静默跳过 overlay，看起来跟 "完全没修" 一样。

**修复**：新增 [StatsLabel.cs](src/UI/StatsLabel.cs) `ForEncounterNoData()` — 灰色 "无数据 / No data" label。Bundle 没数据时 fallback 到这个 pill，让玩家知道节点已被识别但社区无样本。

### Top bar 药水/卡牌掉落概率 — 地图入场立即显示

[CombatUiOverlayPatch.cs](src/Patches/CombatUiOverlayPatch.cs) 之前 attach 时机：
- `OnRunStarted`（`SetUpSavedSinglePlayer` postfix，TopBar 未必初始化完成）
- `AfterSetUpCombat` fallback（必须先进入一场战斗才会 attach）

结果：玩家读档继续一局 → 从地图界面开始 → indicators 缺失，直到进第一场战斗。

**修复**：新增 `[HarmonyPatch(typeof(NMapScreen), nameof(NMapScreen.Open))]` postfix，玩家每次进入 map screen 都立即调用 `EnsureAttachedToTopBar() + ShowAll() + RefreshValuesFromPlayer`，不依赖战斗发生。

### Osty hook 名字纠错

v0.13.0 的 `RelicHookContextPatcher.PatchAll` 新增 3 条 Osty 相关 patch 用了错误的方法名，客户端日志持续输出 3 条 `[RelicPatch] Method not found` warning：
- `BeatingRemnant.AfterOsty` → `AfterModifyingHpLostAfterOsty`
- `TungstenRod.AfterOsty` → `AfterModifyingHpLostAfterOsty`
- `TheBoot.BeforeOsty` → `AfterModifyingHpLostBeforeOsty`

基于 `_decompiled/sts2/MegaCrit.Sts2.Core.Models.Relics/` 实际方法签名核对后纠正。

---

## v0.13.0 (2026-04-15) — 遗物贡献全覆盖 + 自伤独立行 + HTTP API fallback

> 主要工作：按 `DesignDoc/relics.md` 清单把遗物贡献追踪从 ~77 个扩充到 ~150+ 个；把同时提供正防御和自伤的卡牌拆成两行显示；给服务端加一条 HTTP/80 的 `/v1/*` fallback 绕过 GFW SNI 封锁。

### 战斗贡献 — 遗物覆盖扩展

- **`RelicHookContextPatcher.PatchAll` 新增 ~55 条 async hook**，覆盖 relics.md 清单中所有尚未被 patch 的战斗内生效/能治疗的遗物。按类别分组：
  - **房间/战斗开始**: OddlySmoothStone / Planisphere / StoneCracker / Pantograph / SwordOfJade / PumpkinCandle / BigMushroom / LoomingFruit / SneckoEye / FakeSneckoEye / FakeAnchor
  - **回合事件**: FuneraryMask / SymbioticVirus / OrangeDough / BigHat / Sai / Crossbow / SealOfGold / TwistedFunnel / PollinousCore / GamblingChip / VexingPuzzlebox / ChoicesParadox / Bookmark
  - **打牌前后**: MummifiedHand / RazorTooth / HelicalDart / ChemicalX / Vambrace / UnsettlingLamp / MusicBox / DiamondDiadem / BrilliantScarf / PaelsLegion
  - **抽牌**: Toolbox / NinjaScroll / RadiantPearl / ToastyMittens / JeweledMask
  - **Osty / 受伤 / 格挡清空**: TungstenRod / TheBoot / BeatingRemnant / SelfFormingClay / SparklingRouge
  - **Power 加成/减免**: SneckoSkull / RuinedHelmet
  - **Orb / 卡堆变动 / 攻击后 / 洗牌 / 休息治疗**: GoldPlatedCables / BookOfFiveRings / DarkstonePeriapt / BoneFlute / TheAbacus / StoneHumidifier
  - **药水协同 / Exhaust 串联**: ReptileTrinket / BeltBuckle / BurningSticks
  - **Card 进战斗 / Doom 复活治疗**: Regalite / BookRepairKnife (AfterDiedToDoom)
  - **星星**: MiniRegent

- **`RelicHandDrawBonusPatch` 新增 4 条** — BoomingConch / Fiddle (`ModifyHandDrawLate`) / BigMushroom / PollinousCore 的 `ModifyHandDraw` 被动加成。

- **`EnemyDamageIntentPatch.AfterModifyDamage_Enemy` 新增 RelicModel 分支**。对玩家侧遗物调用 `ModifyDamageMultiplicative`，检测 `< 1m` 的减免系数并按 PRD §防御归因公式 `finalDmg * (1 - m) / m` 拆分被减免的伤害，走新增的 `CombatTracker.OnRelicIncomingDamageMitigation(relicId, prevented)` → `MitigatedByBuff`。自动覆盖 **UndyingSigil** 低血量 0.5× 减伤。

- **`BlockModifierPatch.AfterModifyBlock` 新增 RelicModel 分支**。对玩家侧遗物同时支持 `ModifyBlockAdditive`（直接记 `ModifierBlock`）和 `ModifyBlockMultiplicative`（按 PRD §格挡修正拆分公式 `finalBlock - finalBlock / multiplicative` 计算 delta）。自动覆盖 **VitruvianMinion** 2× 小怪卡格挡加成。

- **`AfterEnergyResetAttribution` 扩展遗物最大能量归因**。遵循 Demesne/Friendship/Pyre 的 `Hook.AfterEnergyReset` postfix 模式，在迭代玩家 Powers 之后追加 `foreach (var relic in player.Relics)` 块：对每个遗物调用 `relic.ModifyMaxEnergy(player, 0m)`，结果 > 0 即为该遗物提供的每回合能量加成，通过 `AddEnergyBonusDirect(relicId, "relic", N)` 记到 `EnergyGained`。自动覆盖 **SOZU / ECTOPLASM / PRISMATIC_GEM / SPIKED_GAUNTLETS / WHISPERING_EARRING / BLOOD_SOAKED_ROSE / PUMPKIN_CANDLE**（PumpkinCandle 的 ActiveAct 守卫会自己返回 0 在错误 Act 上）。

### 贡献面板 UI

- **自伤独立行显示**。绯红披风 / 血墙 / 祭献等同时提供正防御和自伤的卡牌不再混在一行。[ContributionChart.cs](src/UI/ContributionChart.cs) 新增 `RowMode { Normal, PositiveOnly, NegativeOnly }` 枚举 + `PositiveDefense(accum)` helper。`BuildSection` 检测 Defense 类别下 `SelfDamage > 0 && PositiveDefense > 0` 时拆为两行：
  - 第一行 `PositiveOnly`：卡名 + 播放次数 + 纯正防御 bar（不含 SelfDamage）+ 正百分比
  - 第二行 `NegativeOnly`：缩进 `└ 自伤` 标签 + 纯红色自伤 bar + `-N` 值，无播放次数、无百分比

### 服务端部署

- **HTTP/80 fallback**：nginx 配置增加 `/health` 和 `/v1/` 的 HTTP 直通 location（其他路径仍 301→HTTPS）。解决 GFW 按 SNI 阻断 `duckdns.org` 的 TLS 握手问题（客户端 `api_base_url` 从 `https://statsthespire.duckdns.org/v1` 切到 `http://64.176.85.164/v1`）。
- **rate limit 上调**：upload 限制从 `10/minute` 提升到 `300/minute`，避免历史记录批量上传触发 429。
- **DB 密码同步修复**：镜像 rebuild 后 pg 用户密码与 .env 分歧，通过 `ALTER USER sts2stats WITH PASSWORD` 重置。

### 客户端 UX

- **F9 `resolvedChar=null` UX 漏洞**：主菜单打开 F9 时 `CharacterFilterMode="auto"` 且无当前 run → `ResolveCharacter()` 返回 null → 原代码直接落回 test bundle 显示 42,850 局。现在 `OnFilterChangedAsync` 在 character null 时会向 API 传 `"all"` 拉取聚合数据。
- **历史导入与实时上传 UI 重叠**：`HistoryImporter` 暴露 `IsRunning` 标志，`UploadNotice.Show()` 在导入期间静默。
- **上传 URL 重复 `char=` 参数**：`GetBulkStatsAsync` strip 掉 query string 里的 char/ver 再拼接，避免 `char=X&ver=Y&char=X&...`。

### 数据完整性修复

- **Live run save+quit 数据丢失**：`RunDataCollector.BuildPayload` 之前依赖 6 个 in-memory list（`_cardChoices / _eventChoices / _shopPurchases / _shopCardOfferings / _cardRemovals / _cardUpgrades`）由 Record\*() patches 在打牌/选卡/进商店时填充。save+quit+reload 后这些 list 清空，造成上传 payload 中 6 张表全为空。
  **修复**：抽出 `HistoryImporter.PopulateFromMapHistory(mapHistory, ...)` 共用 walker，`BuildPayload` 改为从 `SerializableRun.MapPointHistory`（游戏自带的权威持久化，save+quit 不丢）直接 walk 填充 card/event/shop/encounter 等字段。历史导入也走同一个 helper。
- **Contributions 磁盘兜底**：`OnMetricsUpload` 上传前先调用新增的 `ContributionPersistence.AssembleAndHydrateRunTotals(seed)`，从所有 `{seed}_{floor}.json` per-combat 磁盘文件重建 `RunContributionAggregator._runTotals`，兜底 save+quit+Reset 清空内存 + live.json hydration 失败的场景。
- **ShopCardOfferings 持久化**：新增 [ShopOfferingPersistence.cs](src/Util/ShopOfferingPersistence.cs)，`RecordShopCardOffering` 时写 `{seed}_shop_offers.json`，`OnRunStart` 时 load 回 in-memory list，`OnMetricsUpload` 时删除。因为 `MapPointHistory` 只记录 `BoughtRelics/BoughtPotions/BoughtColorless`（购买），不记录 shop 的卡牌池展示内容，而后者是 `ShopPatch.AfterMerchantRoomEnter` 从活动 shop 实例读的瞬时数据。
- **AssembleFromCombats glob 过滤**：之前 `{seed}_*.json` 匹配会扫到 `{seed}_live.json` 和新增的 `{seed}_shop_offers.json`，尝试按 `ContributionDoc` schema 反序列化失败并 warning spam。现在跳过以 `_live` / `_shop_offers` 结尾的文件。

### 新增文件

- [ShopOfferingPersistence.cs](src/Util/ShopOfferingPersistence.cs) — Shop card 池展示的 per-seed JSON 持久化

### 兼容性

- 数据库 schema 无变化
- API payload schema 无变化
- 历史存档兼容（live.json / _shop_offers.json 向后兼容，旧版客户端读不到 shop_offers 无影响）

---

## v0.12.1 (2026-04-15) — 贡献归因四连修（Round 14 v6）

> 在 v0.12.0 master 基础上对 contribution test 套件暴露的 4 类归因 bug 做定点修复。
> 设计文档：[DesignDoc/FIX_INTENT_upgrade_origin.md](DesignDoc/FIX_INTENT_upgrade_origin.md)

### Bug 修复

- **Fix A — 脆弱（Frail）产生伪正防御贡献**  
  `BlockModifierPatch.AfterModifyBlock` 之前读 `Math.Abs(power.Amount)` 作为 modifier 贡献值，而 Frail 的 `Amount` 其实是回合计数器（例：2 turns），被错误地当成 +2 block 计入 debuff 的 `ModifierBlock` 桶。现改为完整复刻伤害侧 `AfterModifyDamage` 的三段式：先查 `ModifyBlockAdditive`（正值直接取）→ 否则查 `ModifyBlockMultiplicative`（`< 1m` 直接 skip，这条过滤所有减少玩家格挡的负贡献，例如 Frail 0.75×、NoBlock、Shadowmeld、Unmovable）→ `> 1m` 按 `finalBlock - finalBlock/multiplicative` 折算。文件：[CombatHistoryPatch.cs:1034-1094](src/Patches/CombatHistoryPatch.cs#L1034-L1094)。

- **Fix C — 同名卡 origin 污染（composite bucket key）**  
  `CombatTracker._currentCombat` 之前按扁平 `cardId` 聚合，`TagCardOrigin` 在 bucket 创建**之后**回填 `OriginSourceId`，导致 last-writer-wins：牌库原生 Battle Trance 先打、技能药水生成的 Battle Trance 后打，所有 `BATTLE_TRANCE` 条目被共同的桶吞掉并覆写成 `SKILL_POTION` origin。双生成器 Shiv 场景同理。现新增 `_activeCardOriginAL` AsyncLocal 在 `OnCardPlayStarted` 从 `GetCardOrigin(cardHash)` 一次捕获，`GetOrCreate(sourceId, type, originOverride=null)` 通过 `MakeBucketKey(id, \u0001, origin)` 合成复合 key —— `(BATTLE_TRANCE, null)` 与 `(BATTLE_TRANCE, SKILL_POTION)` 成为两个独立 accum，OriginSourceId 在 bucket 创建时锁死，事后不再回填。`TagCardOrigin` 降为 no-op shim（保留签名以兼容任何外部调用）。文件：[CombatTracker.cs](src/Collection/CombatTracker.cs)。

- **Fix D — Upgrade 增量归因 origin 丢失**  
  `UpgradeDelta` record 之前只记 `SourceId/SourceType`（master Fix 6 已把升级增量归到 upgrader 桶而非被升级卡）。但这步忽略了 upgrader 自己的 origin —— 技能药水生成的 Armaments 升级 Strike 与牌库 Armaments 升级 Strike 会坍缩到同一个 `ARMAMENTS` 桶。现 `UpgradeDelta` 增 `UpgraderOrigin` 字段；`CardUpgradeTrackerPatch.AfterUpgrade` 通过 `tracker.ActiveCardOrigin` 捕获；伤害/块升级分流路径通过 `GetOrCreate(..., originOverride: _pendingUpgradeSourceOrigin)` 路由到 upgrader 的 origin 桶。文件：[ContributionMap.cs:702-707](src/Collection/ContributionMap.cs#L702-L707)、[CombatHistoryPatch.cs:1195-1208](src/Patches/CombatHistoryPatch.cs#L1195-L1208)、[CombatTracker.cs](src/Collection/CombatTracker.cs)。

- **Fix B — Doom 归因错位（per-enemy FIFO 栈 + self-doom 过滤）**  
  `OnDoomKillsCompleted` 之前把**所有**被 Doom 杀掉的敌人 HP 加总，整坨挂到 `GetPowerSource("DOOM_POWER")` 单一源（加 5 级 active-context fallback）。三类问题：(a) 多源 Doom 在同一敌人身上坍缩到一个 source；(b) BorrowedTime / Neurosurge 的 self-doom 经 `_powerSources["DOOM_POWER"]` 覆写，污染后续 enemy-Doom 的 source lookup；(c) 多目标场景下归因完全扁平。现新增 `ContributionMap.DoomLayer(SourceId, SourceType, OriginId, Amount)` record + `_doomStacks : Dictionary<int, List<DoomLayer>>`（per-enemy FIFO 栈）；`OnPowerApplied` 对 `DOOM_POWER` 分支：self-doom `return` 完全忽略，enemy-doom 走 `RecordDoomLayer` 并携带 upgrader origin；`OnDoomKillsCompleted` 按敌人循环调用 `ConsumeDoomLayers(hash, hp)` FIFO-consume 每层 `Math.Min(layer.Amount, remainingHp)` 分配 AttributedDamage 到各自 source 的 origin 桶。层无记录时回退到原 active-context 5 级 fallback。文件：[ContributionMap.cs](src/Collection/ContributionMap.cs)、[CombatTracker.cs](src/Collection/CombatTracker.cs)。

### 数据结构变更

- `ContributionMap.UpgradeDelta` record 加 `UpgraderOrigin` 字段（nullable string，旧存档反序列化到 null 保持兼容）
- `ContributionMap` 新增 `DoomLayer` record + `_doomStacks` dict + `RecordDoomLayer` / `ConsumeDoomLayers` / `ClearDoomStacks` API
- `CombatTracker` 新增 `_activeCardOriginAL` AsyncLocal 字段 + `ActiveCardOrigin` 公共访问器 + `_pendingUpgradeSourceOriginAL` + `MakeBucketKey` / `ResolveOriginFor` helpers
- `CombatTracker.GetOrCreate` 新增 `originOverride` 可选参数
- `TagCardOrigin` 降为 no-op shim

### 兼容性

- 旧存档 plain-key 快照仍可加载：`MakeBucketKey(id, null) == id`，key 形态一致
- `UpgradeDelta` 新字段 nullable，旧序列化数据自动映射到 null origin
- UI `ContributionChart` 遍历 `data.Values` 用 `accum.OriginSourceId` 分类子条，无 API 变更
- 遗物归因路径完全中性：relic sourceId 永不带 origin（`ResolveOriginFor("VAJRA") == null`），桶 key 形态不变

## v0.12.0 (2026-04-12) — 第二轮迭代发布版

### 新功能 — 个人生涯统计

- **百科大全 → 角色数据 新增 tab**：通过 `StatsScreenTabPatch` 在原生统计界面注入"个人生涯统计"标签，包含：
  - **胜率汇总卡片**：4 张 NStatEntry 风格卡片（数据范围 / 进阶 W-L / 进阶胜率 / 最高连胜）+ 4 个滚动窗口（最近 10 / 50 / 100 / 全部）+ 最低进阶 SpinBox 选择器
  - **死因排行**：Top 10 死因（带图标 — 战斗用怪物图标，事件用问号图标，先古之民用对应头像，中途放弃用箭头）
  - **卡组构筑** + **路线统计**：每 Act 平均值表格，含图标行（获取/购买/删除/升级 + 小怪/精英/?房间/商店/火堆）
  - **先古遗物选取率**：按 elder 分组的 dropdown，每个 elder 显示其 3 个池子的遗物选取率/选取次数/胜率/胜率浮动（vs 该角色+进阶下的总胜率）
  - **Boss 战损**：dropdown 列出全部 4 幕的所有 Boss（包括 0 遭遇次数的占位行），含 Underdocks 暗港作为第 1 幕替代图（密林/暗港）
  - 字号、SectionPanel 视觉、行图标全部对齐游戏原生统计界面风格

### 新功能 — 历史记录本局统计弹窗

- **历史记录详情新增"📊 本局统计"按钮**：右上角注入按钮，点击弹出居中模态弹窗
- 弹窗内容（与生涯统计同款 SectionPanel 风格）：
  - 头部：角色中文名 · 进阶 · 楼层 · 胜利/失败
  - **卡组构筑** / **路线统计**（含火堆数）每 Act 表格 + 行图标
  - **先古遗物选取**：行内显示 elder 头像 + 中文名 + 遗物图标 + 遗物名
  - **Boss 战损**：每行 Boss 头像 + 中文名 + HP 损失
  - **查看贡献图表**按钮：从历史记录回看本局战斗贡献（自动关闭弹窗避免 z-order 冲突）
- 弹窗 parent 到 `GetTree().Root`，自适应分辨率（70% × 85% 视口，最小 520×400）
- 关闭路径：✕ 按钮 / 点暗背景 / Esc 键 / SelectPlayer 触发自动清理

### 新功能 — 卡牌图书馆 + 遗物收藏统计注入

- **`CardLibraryPatch` / `RelicLibraryPatch`**：在 Compendium 卡牌/遗物详情页注入个人样本数 + 选取率 + 胜率 + 升级率 + 删除率 + 购买率 + 社区对比
- 数据来源：`RunHistoryAnalyzer` 离线聚合本地 RunHistory 文件（异步加载，磁盘缓存）

### 改进 — 战斗贡献面板

- **宽度 460 → 760**：解决长卡名 + 长进度条横向溢出，柱状图最大宽度 260 → 540
- **`WrapInScroll` 禁用水平滚动**：与 `NewTabScroll` 行为对齐，run 历史回看不再出现底部滚动条
- **打开历史记录的贡献回看时**：先关闭本局统计弹窗，避免 z-order 重叠

### 改进 — 配置 / 筛选

- **F9 面板进阶 SpinBox 上限 10**：游戏最高进阶就是 10，超过会触发后端数据读取异常
- **F9 面板 min/max ascension 联动**：min > max 时自动 clamp 另一边
- **生涯统计 SpinBox 上限同步 10**

### Bug 修复

- **卡组样本数显示 0** — 启动时 `LoadAllAsync` 在 `SaveManager.InitProfileId` 之前触发会拿到空快照，原代码把空快照写进缓存，导致整个 session 卡死。新增 `_lastBuildFailed` 标志，失败的快照不再污染 in-memory 缓存或磁盘
- **`GetCached` 不再回写内存缓存**：之前从磁盘读出的 `CareerStatsData` 会被写进 `_cache`，导致后续 `LoadAllAsync` 命中缓存直接返回，bundles 永不重建
- **CareerStatsCache schema 版本检测**：DTO 新增 `MaxWinStreak` 字段，旧缓存（Wins>0 但 MaxWinStreak==0）会被识别为 stale 并丢弃强制重建
- **OnCareerStatsLoaded 屏幕卡 loading**：`CallDeferred(nameof(Rebuild))` 通过 Godot 反射找不到 private C# 方法，改用 `Callable.From(Rebuild).CallDeferred()`
- **死因图标全部相同** — 原 .tres 路径加载失败，全部 fallback 到 ⚔ Unicode glyph，改为 `ui/run_history/{type}.png` 的已知好路径
- **NEOW 显示怪物图标** — NEOW 实际通过 KilledByEncounter 归类为 Combat，新增 `AncientElderIds` 检测优先用 `ui/run_history/{elder}.png` 的 elder 头像
- **遗物胜率浮动算错** — 原本对比同池平均，应该对比角色+进阶过滤下的总体胜率
- **历史记录注入按钮变成全屏 dim 层** — `_screenContents` 是 MarginContainer，把按钮拉伸到全屏；改为 parent 到 `screen` (NRunHistory 本身)
- **奥洛巴斯池 3 显示英文 ID** — `DISCOVERY_TOTEM_FIRE/WATER/EARTH` 在 relics.json 不存在；按 [Orobas.cs](_decompiled/sts2/MegaCrit.Sts2.Core.Models.Events/Orobas.cs) 实际游戏数据修正为 `TOUCH_OF_OROBAS` + `ARCHAIC_TOOTH`，把 `SEA_GLASS` 合入池 1
- **boss 列表缺 0 遭遇 boss** — 新增 `AllKnownBossEncounterIds()` 枚举 ModelDb 全部 4 幕的 boss，与已有 stats 求并集
- **暗港 boss 错误标为第 4 幕** — Underdocks 在 ActModel 数组里是 index 3 但实际是 Act 1 的替代地图，新增 `BossActFor` 把 Overgrowth/Underdocks 都映射为第 1 幕（密林/暗港 标签区分）
- **路径统计 ? 房间图标不显示** — 路径错为 `unknown_event.png`，正确路径是 `event.png`（详见 ImageHelper.GetRoomIconSuffix）

## v0.11.0 (2026-04-04)

### Improvements

- **Panel moved to left side** of screen to avoid overlapping with game UI
- **Auto-close panel** when player clicks the Proceed button after combat
- **Killing blow damage** now correctly counted — fixed `IsEnding` guard skipping `DamageReceived` for the final hit. Uses `KillingBlowPatcher` (manual Harmony patch on `Hook.AfterDamageGiven`) with identity-based dedup
- **Self-damage cards** (Bloodletting, Offering, etc.) show negative defense contribution in red
- **Poison damage attribution** — `PoisonPower.AfterSideTurnStart` added to PowerHookContextPatcher
- **+27 relic hook patches** — CharonsAshes, ForgottenSoul, FestivePopper, MercuryHourglass, MrStruggles, Metronome, Tingsha, ToughBandages, IntimidatingHelmet, HornCleat, CaptainsWheel, GalacticDust, Candelabra, Chandelier, Lantern, HappyFlower, FakeHappyFlower, GremlinHorn, Pendulum, BlessedAntler, Akabeko, BagOfMarbles, Brimstone, RedMask, SlingOfCourage

### Bug Fixes

- Fixed Harmony parameter name mismatch: compiled DLL uses `results`/`target` not `damageResult`/`originalTarget`

## v0.10.0 (2026-04-04)

### New Features — Contribution Attribution Full Coverage

- **Healing stats (run-level)**: New healing category in run summary showing rest site, relic (BurningBlood etc.), event, max HP gain, and per-floor recovery contributions with source attribution
- **~60 Power hook context patches**: PowerHookContextPatcher manually patches all power hook methods with `SetActivePowerSource`/`ClearActivePowerSource` for complete indirect effect attribution
- **~35 Relic hook context patches**: RelicHookContextPatcher wraps relic hook methods with `SetActiveRelic`/`ClearActiveRelic` for relic-triggered effects
- **Generated card sub-bars**: Shivs, GiantRock, and other generated/transformed cards now display as indented sub-bars under their origin card

### Bug Fixes

- **Potion contribution tracking**: Fixed async bug where `OnUseWrapper` Postfix cleared `_activePotionId` before effects executed. Strength potions, draw potions, block potions etc. now correctly attributed
- **Card removal screen label overlap**: CardRemovalPatch now uses shared `DeckViewPatch.StatsLabelMeta` and `RemoveExistingLabel()`, matching the upgrade screen pattern
- **DynamicVars KeyNotFoundException**: Changed `.Damage`/`.Block` property access to `TryGetValue()` for cards without those vars
- **Event healing distinction**: Each event's healing tracked separately with `[事件]`/`[E]` prefix instead of generic "EVENT"
- **Initial HP filtered**: Skip 0→full HP heal at run start (character creation)
- **Post-combat healing timing**: BurningBlood and similar AfterCombatVictory healing now writes to RunContributionAggregator (fires after OnCombatEnded)
- **Unpatchable methods removed**: ArsenalPower.AfterCardPlayed and StampedePower.BeforeTurnEnd gracefully skipped (abstract/interface methods)

### Architecture

- `HealingPatch`: Prefix/Postfix on `CreatureCmd.Heal` with room-type fallback (RestSiteRoom, EventRoom, MerchantRoom, floor regen)
- `PotionUsedPatch`: ClearActivePotion moved here (sync, fires after OnUse completes)
- `RunContributionAggregator.AddHealing()`: Out-of-combat healing direct write
- `ContributionChart`: Healing bars (green), `isRunLevel` parameter, event prefix display
- `Localization.cs`: 6 new healing/source string keys (EN + CN)

## v1.2.0 (2026-04-03)

### New Features — Contribution Attribution Overhaul

- **Module B: Power indirect effect attribution** — 7 powers (Rage, FlameBarrier, FeelNoPain, Plating, Inferno, Juggernaut, Grapple) now correctly attribute their indirect damage/block back to the card that applied the power, via `PowerHookContextPatch` Prefix/Postfix on each power's hook method
- **Module A+F: Damage/Block modifier attribution** — Strength, Dexterity, Vulnerable, Weak, Colossus, DoubleDamage, PenNib and all other additive/multiplicative modifiers now have their bonuses split out and attributed to their power source. Patches `Hook.ModifyDamage` and `Hook.ModifyBlock` to capture per-modifier contributions
- **Module C: Energy gain tracking** — `PlayerCmd.GainEnergy` is now patched; energy gained from cards like Bloodletting is properly attributed in the Energy Gained section
- **Module E: Sub-bar UI for generated/transformed cards** — Cards created by PrimalForce (GiantRock), Juggling, etc. display as indented sub-bars under their origin card in the contribution chart
- **Module D: Upgrade source tracking** — `CardCmd.Upgrade` Prefix/Postfix captures damage/block deltas; upgrade bonuses shown as orange segments in the damage bar

### Architecture

- `ContributionAccum`: Added `ModifierDamage`, `ModifierBlock`, `UpgradeDamage`, `UpgradeBlock`, `OriginSourceId` fields
- `ContributionMap`: Added `LastDamageModifiers`/`LastBlockModifiers` lists, `_cardOriginMap`, `_upgradeDeltaMap`
- `CombatTracker`: Added `_activePowerSourceId`/`_activePowerSourceType` context with `SetActivePowerSource()`/`ClearActivePowerSource()`, unified `ResolveSource()` fallback chain: cardSource → activeCard → activePotion → activeRelic → activePowerSource
- `ContributionChart`: New multi-segment damage bars (direct/attributed/modifier/upgrade), sub-bar rendering for child cards, purple color for modifier segments, orange for upgrade segments
- `PowerHookContextPatch`: Generic Prefix/Postfix pattern for 7 power types
- `DamageModifierPatch` / `BlockModifierPatch`: Patches Hook.ModifyDamage/ModifyBlock
- `EnergyGainPatch`: Patches PlayerCmd.GainEnergy
- `CardOriginPatch`: Patches CardCmd.Transform
- `CardUpgradeTrackerPatch`: Patches CardCmd.Upgrade Prefix/Postfix

## v1.1.0 (2026-04-03)

### New Features
- **Chinese/English localization**: All UI text supports language switching via settings panel
- **Settings panel (F9)**: Centralized mod settings with hotkey
  - Upload toggle: opt-out of data upload
  - Language switch: Chinese / English
  - Filter settings: ascension, win rate, game version
- **Potion contribution tracking**: Fire Potion, Poison Potion etc. damage/block now attributed in combat chart
- **Card draw section**: Per-source card draw contribution displayed as bar chart (e.g., Battle Trance x1 → 3 cards)
- **Energy gain section**: Per-source energy gain contribution displayed as bar chart (e.g., Bloodletting x1 → 2 energy)

### Bug Fixes
- **Neow event options no longer show "Loading"**: Bundled test data loaded synchronously at mod init, available before any async preload
- **Vulnerable damage attribution fixed**: PowerModel.ApplyInternal Postfix replaces CombatHistory.PowerReceived for debuff source tracking — fixes timing issue where power.Owner was null
- **Relic hover stats now work on in-game relic bar**: Patches both NRelicInventoryHolder (game panel) and NRelicBasicHolder (reward screens)
- **DLL reference paths fixed**: csproj HintPaths corrected for project directory depth

### Architecture
- `Localization.cs`: Simple key-value i18n system with `L.Get("key")` pattern
- `PotionContextPatch`: Wraps PotionModel.OnUseWrapper with Prefix/Postfix to set active potion context
- `PowerApplyPatch`: Patches PowerModel.ApplyInternal (post-Owner-set) for reliable debuff/buff source tracking
- Source types expanded: "card" | "relic" | "potion"
- ContributionChart now has 4 sections: Damage, Defense, Card Draw, Energy Gained

## v1.0.0 (2026-04-01)

- Initial release: card pick rates, event stats, encounter danger overlay, combat contributions, filter panel, shop tracking, card removal/upgrade tracking
