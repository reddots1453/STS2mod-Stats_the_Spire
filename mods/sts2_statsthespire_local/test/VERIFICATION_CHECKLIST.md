# Community Stats Mod — 测试验证清单

## 测试环境搭建

### 1. 启动 Mock API 服务器
```bash
cd mods/sts2_community_stats/test
python mock_server.py
```
控制台应显示:
```
Community Stats Mock API Server
Listening on http://localhost:8080/v1/
Cards:      30
Relics:     16
Events:     7
Encounters: 24
```

### 2. 确认 config.json 存在
文件 `mods/sts2_community_stats/config.json` 内容:
```json
{ "api_base_url": "http://localhost:8080/v1" }
```

### 3. 启动游戏
启动 STS2，确认右下角提示"已加载1个模组"（无错误）。

---

## 验证项目

### ✅ A. Mod 加载 (基础)

| # | 检查项 | 预期 | 通过? |
|---|--------|------|-------|
| A1 | 游戏启动无崩溃 | 正常进入主菜单 | ☐ |
| A2 | 右下角提示 | "已加载1个模组" (无错误字样) | ☐ |
| A3 | Mock 服务器无请求 | 主菜单阶段不应有 API 请求 | ☐ |
| A4 | Godot 日志 | `[CommunityStats] Community Stats v1.0.0 initializing...` | ☐ |
| A5 | Godot 日志 | `[CommunityStats] API endpoint: http://localhost:8080/v1` | ☐ |
| A6 | Godot 日志 | `[CommunityStats] Community Stats initialized successfully` | ☐ |

### ✅ B. Run 开始 → Bulk 数据预加载

| # | 检查项 | 预期 | 通过? |
|---|--------|------|-------|
| B1 | 开始新游戏 (任意角色) | Mock 服务器收到 `GET /v1/stats/bulk?char=<角色名>&ver=...` | ☐ |
| B2 | 服务器日志 | `returning 30 cards, 16 relics, 7 events, 24 encounters` | ☐ |
| B3 | Godot 日志 | `Preloaded bulk stats for <角色> (30 cards, 16 relics)` | ☐ |

### ✅ C. 卡牌奖励界面 — 统计显示

战斗后获得卡牌奖励时验证。测试数据中预置了以下值（以 Silent 角色为例）:

| # | 卡牌 ID | 预期显示文本 | 颜色 | 通过? |
|---|---------|-------------|------|-------|
| C1 | Backstab | `Pick 72% \| Win 65%` | 🟢 绿色 (win≥60%) | ☐ |
| C2 | Acrobatics | `Pick 85% \| Win 58%` | 🟡 黄色 (40%≤win<60%) | ☐ |
| C3 | BladeDance | `Pick 90% \| Win 55%` | 🟡 黄色 | ☐ |
| C4 | Dash | `Pick 40% \| Win 70%` | 🟢 绿色 | ☐ |
| C5 | WraithForm | `Pick 60% \| Win 75%` | 🟢 绿色 | ☐ |
| C6 | Footwork | `Pick 78% \| Win 66%` | 🟢 绿色 | ☐ |
| C7 | Neutralize | `Pick 10% \| Win 48%` | 🟡 黄色 | ☐ |
| C8 | 未在测试数据中的卡 | 无标签 或 "No data" | 灰色 | ☐ |

**颜色规则**: win≥60% → 绿(0.3,0.9,0.3) | 40%≤win<60% → 黄(1,0.85,0.2) | win<40% → 红(0.9,0.3,0.3)

如果用 Ironclad 角色:
| # | 卡牌 ID | 预期显示文本 | 颜色 |
|---|---------|-------------|------|
| C9 | Bash | `Pick 15% \| Win 52%` | 🟡 黄色 |
| C10 | Bludgeon | `Pick 55% \| Win 62%` | 🟢 绿色 |
| C11 | BattleTrance | `Pick 80% \| Win 60%` | 🟢 绿色 |
| C12 | Barricade | `Pick 35% \| Win 70%` | 🟢 绿色 |

### ✅ D. 事件选项 — 社区选择率

遇到事件时验证按钮上的标注。

| # | 事件 ID | 选项 | 预期显示 | 颜色 | 通过? |
|---|---------|------|----------|------|-------|
| D1 | Neow | 选项 0 | `Chosen 35% \| Win 60%` | 🟢 绿色 | ☐ |
| D2 | Neow | 选项 1 | `Chosen 25% \| Win 55%` | 🟡 黄色 | ☐ |
| D3 | Neow | 选项 2 | `Chosen 30% \| Win 58%` | 🟡 黄色 | ☐ |
| D4 | Neow | 选项 3 | `Chosen 10% \| Win 70%` | 🟢 绿色 | ☐ |
| D5 | AbyssalBaths | 选项 0 | `Chosen 50% \| Win 52%` | 🟡 黄色 | ☐ |
| D6 | AbyssalBaths | 选项 1 | `Chosen 35% \| Win 58%` | 🟡 黄色 | ☐ |
| D7 | AbyssalBaths | 选项 2 | `Chosen 15% \| Win 45%` | 🟡 黄色 | ☐ |
| D8 | TeaMaster | 选项 0 | `Chosen 55% \| Win 50%` | 🟡 黄色 | ☐ |
| D9 | TeaMaster | 选项 1 | `Chosen 45% \| Win 57%` | 🟡 黄色 | ☐ |
| D10 | 未在数据中的事件 | 无标签 | — | ☐ |

### ✅ E. 地图节点 — 危险度叠加

地图上战斗节点应显示汇总统计。MapPointPatch 按 `point.PointType` 的小写名称查询，对应数据:

| # | 节点类型 | 查询 key | 预期显示 | 颜色 | 通过? |
|---|----------|----------|----------|------|-------|
| E1 | 普通怪 (Monster) | `"monster"` | `Death 3.2% \| Avg DMG 10` | 🟢 绿色 (death<5%) | ☐ |
| E2 | 精英 (Elite) | `"elite"` | `Death 12.5% \| Avg DMG 23` | 🔴 红色 (death≥15%) | ☐ |
| E3 | Boss | `"boss"` | `Death 19.5% \| Avg DMG 36` | 🔴 红色 | ☐ |

**颜色规则**: death≥15% → 红 | 5%≤death<15% → 黄 | death<5% → 绿

> 注: E2 的 death=12.5% 按规则在 5%~15% 区间，应为黄色。修正:
> E2 预期颜色: 🟡 黄色 (5%≤12.5%<15%)

### ✅ F. 贡献面板 (F8 快捷键)

| # | 检查项 | 预期 | 通过? |
|---|--------|------|-------|
| F1 | 按 F8 | 面板出现/隐藏切换 | ☐ |
| F2 | 按 F8 两次 | 面板显示→隐藏 | ☐ |
| F3 | 战斗中按 F8 | 面板不崩溃 (可能无数据) | ☐ |
| F4 | 战斗结束后 | 面板自动显示战斗贡献数据 | ☐ |

### ✅ G. 筛选面板 (F9 快捷键)

| # | 检查项 | 预期 | 通过? |
|---|--------|------|-------|
| G1 | 按 F9 | 筛选面板出现/隐藏 | ☐ |
| G2 | 更改筛选条件并 Apply | Mock 服务器收到新的 bulk 请求 | ☐ |
| G3 | Apply 后卡牌奖励 | 显示的数据与新筛选一致 | ☐ |

### ✅ H. 数据上传 (Run 结束)

完成一局游戏后验证上传。

| # | 检查项 | 预期 | 通过? |
|---|--------|------|-------|
| H1 | Run 结束 (胜/败) | Mock 服务器收到 `POST /v1/runs` | ☐ |
| H2 | 服务器日志 | `★ RUN UPLOADED: <角色> A<等级> → floor <层数> (WIN/LOSS)` | ☐ |
| H3 | uploaded_runs.json | 文件已创建，包含完整 payload | ☐ |
| H4 | payload.character | 与当前角色一致 | ☐ |
| H5 | payload.win | true=胜利, false=失败 | ☐ |
| H6 | payload.floor_reached | 与实际到达层数一致 | ☐ |
| H7 | payload.card_choices | 包含所有卡牌选择记录 (每张offered卡一条) | ☐ |
| H8 | payload.event_choices | 包含遇到的事件选择 | ☐ |
| H9 | payload.encounters | 包含所有战斗记录 (伤害/回合/死亡) | ☐ |
| H10 | payload.final_deck | 与游戏结束时牌组一致 | ☐ |
| H11 | payload.final_relics | 与游戏结束时遗物一致 | ☐ |
| H12 | payload.shop_purchases | 记录了商店购买 (如有) | ☐ |
| H13 | payload.card_removals | 记录了卡牌移除 (如有) | ☐ |
| H14 | payload.card_upgrades | 记录了卡牌升级 (如有) | ☐ |
| H15 | payload.contributions | 包含贡献追踪数据 | ☐ |

### ✅ I. 上传数据精确验证

打开 `test/uploaded_runs.json`，逐项比对:

| # | 字段路径 | 验证方法 | 通过? |
|---|----------|----------|-------|
| I1 | `mod_version` | 应为 `"1.0.0"` | ☐ |
| I2 | `game_version` | 应为当前游戏版本 (如 `"v0.99.1"`) | ☐ |
| I3 | `ascension` | 与开始局对应 | ☐ |
| I4 | `num_players` | 单人=1, 多人=对应数 | ☐ |
| I5 | `card_choices[].card_id` | 每张offered卡都有记录, ID 格式正确 | ☐ |
| I6 | `card_choices[].was_picked` | picked 的那张为 true, 其余 false | ☐ |
| I7 | `card_choices[].floor` | 层数递增且合理 | ☐ |
| I8 | `encounters[].encounter_id` | 非空, 与遇到的怪物匹配 | ☐ |
| I9 | `encounters[].damage_taken` | ≥0, 与实际受伤一致 | ☐ |
| I10 | `encounters[].turns_taken` | ≥1, 合理 | ☐ |
| I11 | `encounters[].player_died` | 最后一场如果死亡应为 true | ☐ |
| I12 | `final_deck[].card_id` | 与结束时牌组完全一致 | ☐ |
| I13 | `final_deck[].upgrade_level` | 升过级的卡 ≥1 | ☐ |

---

## 快速测试流程 (最小路径)

1. **启动 mock server** → `python test/mock_server.py`
2. **启动游戏** → 确认无崩溃、无错误提示 (A1-A2)
3. **选 Silent 开始新游戏** → 观察 mock server 收到 bulk 请求 (B1-B2)
4. **第一场战斗** → 观察 mock server 日志、战斗后按 F8 查看面板 (F1, F4)
5. **获得卡牌奖励** → 检查卡牌下方统计标签 (C1-C8)
6. **遇到事件** → 检查选项按钮上的统计 (D1-D10)
7. **查看地图** → 检查战斗节点的危险度标注 (E1-E3)
8. **死亡/通关** → 检查 mock server 收到 POST /runs (H1-H3)
9. **打开 uploaded_runs.json** → 逐项验证字段 (I1-I13)

---

## 测试完成后

1. 停止 mock server (Ctrl+C)
2. 删除或重命名 `mods/sts2_community_stats/config.json` 恢复正式 API
3. 可保留 `test/uploaded_runs.json` 作为测试记录
