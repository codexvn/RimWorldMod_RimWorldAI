# RimWorldMCP

MCP (Model Context Protocol) 服务器，将 RimWorld 游戏状态和操作暴露为 LLM 可调用的 Tool。作为 RimWorld mod DLL 内嵌运行。

- MCP Server: `../RimWorldMCP/`（游戏 Mod DLL，100+ Tool）
- Agent Runtime: `../RimWorldAgent/`（独立 EXE/MOD，通过 MCP 协议通信）
- MCP Shared: `../SimpleMspServer/`（JSON-RPC + Transport）

## 项目结构

```
RimWorldMCP/
├── CLAUDE.md
├── resource/                  ← MOD 元数据（构建时复制到根 publish）
│   ├── About/About.xml
│   ├── Languages/
│   └── Symbols.json           ← 词表文件 — defName→显示字符映射（由脚本从游戏XML生成）
├── scripts/                   ← 开发脚本
│   ├── generate_symbols.py    ← 从游戏XML生成Symbols.json
│   ├── check_symbols.py       ← 校验Symbols.json一对一映射
│   └── semantic_map.py        ← AI润色后的语义字符参照表
├── Tools/                     ← 100+ 游戏 Tool
├── MapRendering/              ← 网格渲染与符号映射
│   ├── CellCharProviders.cs   ← 单元格→字符映射（tile/terrain/fertility/temperature/pollution）
│   ├── SymbolDictionary.cs    ← 词表驱动符号字典 — 每次启动重建，无缓存
│   ├── MapChunker.cs          ← Chunk索引↔世界坐标转换（TryParseChunkId/GetChunkByIndex等）
│   ├── MapChunk.cs            ← Chunk数据模型
│   ├── GridRenderer.cs        ← 矩形范围字符网格渲染器
│   └── AiObservationOverlay.cs← AI操作区域半透明覆盖层
├── Mcp/                       ← MCP Server (JSON-RPC dispatch)
├── Harmony/                   ← 事件拦截 (NotificationBus)
├── Bridge/                    ← 空 stub (原 CC 桥接已迁至 Agent)
├── Transport/                 ← SseTransport (HTTP + SSE)
├── Compression/               ← Chunk压缩（RLE/RowRefRLE/未压缩）
├── Skills/                                # 领域知识 Skill 系统
│   ├── SkillInfo.cs                       # Skill 数据模型
│   ├── SkillRegistry.cs                   # 加载 .md 文件、解析 frontmatter
│   └── *.md                               # 6 个 Skill 文件
├── McpOssUploader.cs                      # 阿里云 OSS 截图自动上传
├── McpOssConfig.cs                        # OSS 配置数据
└── About/
    └── About.xml                          # Mod 元数据
```

## 架构

单进程 mod 内嵌——LLM 通过 SSE 或 Streamable HTTP 连接游戏内的 MCP 服务。

- **net472 Library**：与 RimWorld Unity 运行时一致，`OutputType=Library`
- **引用 Assembly-CSharp.dll**：Tool 直接调用游戏 API（`Find.*`、`DefDatabase<>`、`PawnsFinder` 等）
- **McpServiceManager 入口**：`[StaticConstructorOnStartup]` 时启动，Def 加载完毕后创建 Transport + McpServer 并在主菜单即监听端口，跨存档持续运行。`GameComponent_McpServer` 仅管理 Bridge 与会话生命周期：`StartedNewGame()` / `LoadedGame()` 时启动桥接器，`Game.Dispose()` 时停止桥接器；`ExposeData()` 持久化 sessionId 到存档
- **线程安全**：只读 Tool 在 HttpListener 线程直接执行；写操作 Tool 通过 `McpCommandQueue` 调度到主线程
- **NuGet**: 仅 `System.Text.Json` 8.0.5（JSON 序列化）
- **输出**: `publish/1.6/Assemblies/RimWorldMCP.dll`

### IntVec3 坐标系统

RimWorld 的 `IntVec3(x, y, z)` 字段含义：
- `x` = 水平网格轴（东西方向）
- `y` = **海拔高度层**（地面=0，多层建筑用）
- `z` = **垂直网格轴**（南北方向）

2D 地图的有效网格范围是 `(x: 0 ~ map.Size.x-1, z: 0 ~ map.Size.z-1)`。

**Tool 参数映射规则**：MCP 用户的 `pos_x`/`pos_y`（2D 网格坐标）必须映射为 `new IntVec3(posX, 0, posY)`。
- `pos_y`（用户 Y 坐标）→ `IntVec3.z`（网格垂直轴）
- 海拔（`IntVec3.y`）始终为 0

**禁止**写成 `new IntVec3(posX, posY, 0)`——这会把用户 Y 坐标塞进海拔字段，所有建筑落到 z=0 行。

### 词表符号系统（SymbolDictionary）

网格地图中每个 Def（建筑/物品/植物/地形）用**单个 Unicode 字符**表示。字符映射由词表文件 `resource/Symbols.json` 驱动，格式 `{defName: {char, group}}`。

**工作流**：
1. `scripts/generate_symbols.py` — 从 RimWorld XML 自动生成词表，每个 Def 分配独立 Unicode 字符
2. AI 手工编辑 `resource/Symbols.json`，将核心 Def 的字符替换为语义匹配的 ASCII 符号（如 `Wall→#`, `Door→D`, `Steel→S`）
3. `scripts/check_symbols.py` — 校验：一对一映射、无固定网格冲突、无控制字符

**运行时**：`SymbolDictionary.Initialize()` 每次启动直接读词表+游戏 Def 重建，无缓存文件。词表中没有的 Def 走兜底动态池分配。`DictHash` 供网格工具输出，验证 LLM 侧符号表一致。

**固定网格**（不经过词表，字符硬编码）：
- `fertility_grid` / `temperature_grid` / `pollution_grid` 使用 `▓▒░·○◎●█P.?` 等字符
- `get_tile_grid` 的迷雾用 `█`

**图例**：`GetLegendString(usedSymbols)` 输出 `{char}={def.label}`，字符→Def 一一对应。

### 网格查询模式切换

5 个网格工具（`get_tile_grid`, `fertility_grid`, `terrain_grid`, `temperature_grid`, `pollution_grid`）支持两种查询模式，通过设置 `GridQueryMode` 切换：

| 模式 | InputSchema 参数 | Execute 行为 | 输出格式 |
|------|-----------------|-------------|---------|
| **Chunk**（默认） | `chunk_id: string`（格式 `"X_Z"`） | `MapChunker.TryParseChunkId` → `GetChunkByIndex` → 手动迭代 → 压缩 | RLE/RowRefRLE 压缩 |
| **坐标** | `pos_x, pos_y, end_x?, end_y?` | 直接坐标解析 → `GridRenderer.RenderGrid()` | 逐行 `z{WZ}: {chars}` |

Chunk 模式内部通过 `MapChunker` 完成坐标转换，不新增 Helper。

### 端口清理机制

`McpServiceManager` 全局单例管理唯一传输层实例。通过 `[StaticConstructorOnStartup]` 在 Def 加载后立即启动，跨存档持续运行。`McpServiceManager.Start()` 幂等（`IsRunning` 检查）。端口变更需重启 RimWorld 生效。

### 进程生命周期

Companion 进程由 Agent 侧 `CcbManager` 管理（spawn/stop/Job Object 绑定），详见 `../RimWorldAgent/CLAUDE.md`。MCP 侧 `BridgeLifecycle` 为 stub，仅保留接口兼容。

### Mod 设置

`RimWorldMCPMod`（继承 `Verse.Mod`）提供游戏内设置界面（Options → Mod 设置 → RimWorld MCP），设置项通过 `McpModSettings` 持久化：

| 配置项 | 默认值 | 说明 |
|--------|--------|------|
| 日志级别 | Info | Debug / Info / Warn / Error 过滤 |
| MCP 监听地址 | 0.0.0.0 | 可设为 localhost / 内网 IP |
| MCP 端口 | 9877 | HTTP 监听端口 |
| CCB 主机 | 127.0.0.1 | Companion 进程本地监听地址 |
| CCB 端口 | 19999 | Companion 进程本地监听端口 |
| CCB 远程主机 | 127.0.0.1 | C# WebSocket 远程连接地址 |
| CCB 远程端口 | 19999 | C# WebSocket 远程连接端口 |
| 自动启动 | 开启 | 游戏加载时自动 spawn Node.js 子进程 |
| Token | - | WS 握手认证，companion 层面 |
| 模型名称 | - | Companion 启动时传入的模型名 |
| 自动移动视角 | 开启 | AI 调用坐标工具时自动平移到目标位置 |
| AI观察覆盖层 | 开启 | AI 查询时在地图上短暂显示彩色标记 |
| 自动跟踪殖民者 | 开启 | 运行时自动平移到殖民者聚集位置 |
| OSS 上传 | 关闭 | 截图自动上传到阿里云 OSS |
| OSS Endpoint/Bucket/Key | - | 阿里云 OSS 访问配置 |
| 签名 URL | 开启 | 预签名 URL 有效期 24h |
| 分块宽度 | 32 | Chunk 网格查询的单元格宽度（16/24/32/48/64） |
| 分块高度 | 32 | 与宽度同步，保持一致 |
| 压缩方法 | RLE | Chunk 数据压缩 — 未压缩 / RLE / 行引用+RLE |
| 网格查询模式 | Chunk | Chunk: 按分块查询(压缩输出) / 坐标: 按坐标范围查询(逐行输出) |

## 事件系统

6 个 Harmony Patch 拦截游戏事件 → `NotificationBus` → `GetEventLevel()` 分级 → SSE 推送给 Agent。

### 事件分级

| 级别 | SSE 推送 | 工具结果注入 | 暂停 | Letter | Message | Alert |
|------|---------|-------------|------|--------|---------|-------|
| **L3 Critical** | ✅ | ✅ DangerSummary | ✅ | 大威胁、小威胁、死亡、负面、Boss | 大威胁、小威胁、角色死亡、健康事件、负面、游戏减速 | AlertStart (全部) |
| **L2 Warning** | ✅ | ✅ 计数 | ❌ | 仪式失败 | 警告 | - |
| **L1 Info** | ✅ | ✅ 计数 | ❌ | 正面、事件、来人、成长、任务、仪式成功 | 正面、事件、完成、状态解除 | - |
| **L0 Silent** | ✅ | ✅ | ❌ | 选择角色、游戏结束、捆绑 | 拒绝、SilentInput | - |

> 所有级别均通过 SSE 推送。Agent 侧：Critical/Warning 立即中断 + UI/DB，Info/Silent 仅 suffix 注入。**双工机制**：Agent 调用 `set_tool_result_suffix` 设置一次性后缀，MCP Server 在下次工具结果末尾自动追加并清空。详见 `design/tool-result-suffix.md`。

### 中断通知

| 事件源 | L3 Critical | L2 Warning | L1 Info | L0 Silent |
|--------|-------------|------------|---------|-----------|
| Letter | 大威胁、小威胁、死亡、负面、Boss | 仪式失败 | 正面、事件、来人、成长、任务、仪式成功 | 选择角色、游戏结束、捆绑 |
| Message | 大威胁、小威胁、角色死亡、健康事件、负面、游戏减速 | 警告 | 正面、事件、完成、状态解除 | 拒绝、SilentInput |
| Alert | 全部 | - | - | - |

### 任务队列

9 个日常操作工具支持 `queue` 参数（默认 `true`）——空闲时立即执行，忙碌时加入队列末尾（等价游戏内 Shift+右键）。

| 有 `queue` 参数 | 无 `queue`（永远打断） |
|---------------|---------------------|
| haul_item, pick_up_item, equip_pawn, move_pawn | attack_pawn, force_attack |
| force_dress, ingest_item, strip_pawn | arrest_pawn, capture_pawn |
| drop_equipment, drop_carried | rescue_pawn, tend_now |

`get_colonists` 的"当前活动"列会显示排队任务数，如 `建造 (排队:2)`。

## OSS 截图上传

`McpOssUploader` 在截图完成后自动上传到阿里云 OSS，支持预签名 URL（私有 Bucket）和公开 URL。

- 依赖：阿里云 OSS SDK（Aliyun.OSS.SDK.Net472）
- 配置：通过 Mod 设置界面或配置文件
- 触发：`take_screenshot` 工具调用后自动上传
- 返回：图片 URL（公开或预签名）

## 部署

```bash
# 构建
dotnet build

# 输出到 publish/1.6/Assemblies/
# 将整个 publish/ 目录放入 RimWorld Mods/RimWorldMCP/
# 或创建目录链接:
mklink /D F:\SteamLibrary\steamapps\common\RimWorld\Mods\RimWorldMCP F:\RiderProjects\RimWorldMCP\publish
```

游戏启动后，MCP 服务自动运行在 `http://localhost:9877`。

## Tool 清单（含 I18N 中文名 + 可达性检测）

中文名称参见 `publish/Languages/ChineseSimplified/Keyed/RimWorldMCP_Tools.xml`。以下为全部 100 个工具。

### 通用查询 (5)
| Tool | 说明 | 数据源 |
|------|------|--------|
| `get_game_context` | 游戏全局状态快照 | `Find.CurrentMap`, `Find.TickManager`, `Find.ResearchManager` |
| `get_resources` | 资源库存报告 | `map.resourceCounter.AllCountedAmounts` |
| `check_colony` | 殖民地提醒（空闲/崩溃/流血/食物/防御） | `PawnsFinder`, `map.wealthWatcher` |
| `toggle_pause` | 切换游戏暂停状态，恢复时设为最大速度 | `Find.TickManager.CurTimeSpeed` (入队) |
| `advance_tick` | 让游戏运行指定 tick 数后暂停返回状态，用于观察结果避免过度思考 | `Find.TickManager` (入队) |
| `get_mcp_latency` | 探查 Agent 与游戏之间的 MCP 延迟 | `McpCommandQueue.DispatchAsync` 排队计时 |
| `check_map_loaded` | 检查游戏和地图加载状态 | `Current.Game`, `Find.CurrentMap` |

### 网格查询 (6)
| Tool | 说明 | 参数 |
|------|------|------|
| `get_tile_detail` | 指定坐标范围详情（建筑/物品/植物/生物） | pos_x, pos_y, end_x, end_y（也支持 chunk_id） |
| `get_tile_grid` | 文本化字符网格地图 | chunk_id 或 pos_x/pos_y/end_x/end_y（视设置切换） |
| `fertility_grid` | 地面肥沃度视图 | chunk_id 或 pos_x/pos_y/end_x/end_y（视设置切换） |
| `terrain_grid` | 地形类型视图 | chunk_id 或 pos_x/pos_y/end_x/end_y（视设置切换） |
| `temperature_grid` | 温度分布视图 | chunk_id 或 pos_x/pos_y/end_x/end_y（视设置切换） |
| `pollution_grid` | 污染程度视图 | chunk_id 或 pos_x/pos_y/end_x/end_y（视设置切换） |

### 制造 (4)
| Tool | 说明 | 数据源/操作 |
|------|------|------------|
| `list_recipes` | 列出可用配方 | `DefDatabase<RecipeDef>.AllDefs` |
| `create_production_bill` | 创建制造单据 | `BillStack.AddBill()` (入队) |
| `get_bills` | 查看工作单状态 | `map.listerBuildings.AllBuildingsColonistOfClass<Building_WorkTable>()` |
| `manage_bill` | 管理单据（暂停/恢复/删除/优先级） | `bill.suspended`, `billStack.Delete()`, `billStack.Reorder()` (入队) |

### 建造 (4)
| Tool | 说明 | 数据源/操作 |
|------|------|------------|
| `designate_build` | 放置建造蓝图（单格） | `Designator_Build.DesignateSingleCell()` (入队) |
| `designate_room` | 快速建造矩形房间（墙+门+地板） | 批量 `Designator_Build` (入队) |
| `uninstall_building` | 拆卸建筑为微缩物品 | `Designator_Uninstall.DesignateThing()` |
| `install_minified_thing` | 安装微缩物品到指定坐标 | `GenConstruct.PlaceBlueprintForInstall()` |

### 标记 (5)
| Tool | 说明 | 数据源/操作 |
|------|------|------------|
| `designate_mine` | 标记采矿（支持矩形范围） | `Designator_Mine` (入队) |
| `designate_plants_cut` | 标记植物砍伐（支持矩形范围，可过滤树种） | `Designator_PlantsCut` (入队) |
| `designate_harvest` | 标记作物收割（仅成熟作物） | `Designator_PlantsHarvest` (入队) |
| `designate_deconstruct` | 标记建筑拆除（矩形范围） | `Designator_Deconstruct` (入队) |
| `designate_clear_plants` | 标记清除非树木植物（草/灌木等） | `Designator_PlantsCut` (入队) |

### 存储/种植 (6)
| Tool | 说明 | 数据源/操作 |
|------|------|------------|
| `create_stockpile` | 创建物品储藏区（预设+优先级+筛选） | `Zone_Stockpile` (入队) |
| `create_growing_zone` | 创建种植区并设置植物类型 | `Zone_Growing` (入队) |
| `set_grower_plant` | 设置种植区作物类型 | 区域相关 API (入队) |
| `manage_stockpile_filter` | 管理储藏区物品筛选 | `StorageSettings` (入队) |
| `delete_zone` | 删除区域（储藏区/种植区） | `Zone.Deregister()` (入队) |
| `expand_zone` | 扩展已有区域的范围 | `Zone.AddCell()` (入队) |

### 装备管理 (3)
| Tool | 说明 | 数据源/操作 |
|------|------|------------|
| `find_equipment` | 搜索地图可用武器/衣物，按类型品质分组 | `map.listerThings.AllThings` |
| `get_recommended_apparel` | 按游戏内置评分推荐衣物（复用 ApparelScoreGain） | `JobGiver_OptimizeApparel` |
| `get_recommended_weapon` | 按科技等级推荐武器（支持远程/近战过滤） | `map.listerThings.AllThings` |

### 截图 (1)
| Tool | 说明 | 数据源/操作 |
|------|------|------------|
| `take_screenshot` | 截取地图指定 X/Z 范围画面 | `ScreenshotTaker.TakeNonSteamShot()` (入队), 自动 OSS 上传 |

### 研究 (5)
| Tool | 说明 | 数据源/操作 |
|------|------|------------|
| `list_research_projects` | 列出研究项目（分页） | `DefDatabase<ResearchProjectDef>.AllDefsListForReading` |
| `get_research_progress` | 获取研究进度 | `Find.ResearchManager.GetProgress()` |
| `set_research_project` | 设置研究项目 | `Find.ResearchManager.SetCurrentProject()` (入队) |
| `stop_research` | 停止当前研究 | `Find.ResearchManager.StopProject()` (入队) |
| `get_research_speed` | 研究速度详情 | `Find.ResearchManager.GetResearchSpeed()` |

### 殖民者需求 (4)
| Tool | 说明 | 数据源/操作 |
|------|------|------------|
| `get_colonists` | 殖民者信息 | `PawnsFinder.AllMaps_FreeColonistsSpawned` |
| `get_colonist_needs` | 详细需求状态 | `pawn.needs.AllNeeds` |
| `get_work_priorities` | 所有殖民者完整工作优先级表 | `pawn.workSettings.GetPriority()` |
| `set_work_priority` | 设置工作优先级 | `pawn.workSettings.SetPriority()` (入队) |

### 医疗 (5)
| Tool | 说明 | 数据源/操作 |
|------|------|------------|
| `get_colonist_health` | 健康报告 | `pawn.health.hediffSet.hediffs` |
| `schedule_operation` | 安排手术 | `billStack.AddBill(Bill_Medical)` (入队) |
| `tend_now` | 立即治疗指定殖民者 | `JobDefOf.TendPatient` (入队) |
| `force_surgery` | 强制执行指定手术 | `Bill_Medical` (入队) |
| `get_available_surgeries` | 列出可用手术（分页） | `RecipeDefOf` |

### 战斗 (6)
| Tool | 说明 | 数据源/操作 |
|------|------|------------|
| `equip_pawn` | 强制殖民者拾取并装备（Job 系统，自然走过去） | `JobDefOf.Equip` / `JobDefOf.Wear` (入队) |
| `draft_pawn` | 征召/解除征召 | `pawn.drafter.Drafted` (入队) |
| `get_defense_status` | 防御状态报告 | `pawn.equipment.Primary`, `map.listerBuildings` |
| `attack_pawn` | 攻击指定目标 | `JobDefOf.AttackMelee` / `JobDefOf.AttackStatic` (入队) |
| `force_attack` | 强制攻击（无视掩体/过墙） | `JobDefOf.AttackStatic` (入队) |
| `find_enemies` | 搜索地图上的敌人 | `map.mapPawns.AllPawnsSpawned` |

### 右键菜单操作 (10)
| Tool | 说明 | 操作 |
|------|------|------|
| `pick_up_item` | 拾取物品 | `JobDefOf.TakeInventory` (入队) |
| `drop_equipment` | 丢弃装备 | `pawn.equipment.Remove()` (入队) |
| `strip_pawn` | 剥除目标衣物/装备 | `JobDefOf.Strip` (入队) |
| `arrest_pawn` | 逮捕目标 | `JobDefOf.Arrest` (入队) |
| `rescue_pawn` | 救援倒地友方 | `JobDefOf.Rescue` (入队) |
| `capture_pawn` | 俘虏倒地敌人 | `JobDefOf.Capture` (入队) |
| `ingest_item` | 服食物品 | `JobDefOf.Ingest` (入队) |
| `force_dress` | 强制穿戴衣物 | `JobDefOf.Wear` (入队) |
| `haul_item` | 搬运物品到目标位置 | `JobDefOf.HaulToCell` (入队) |
| `drop_carried` | 放下手中物品 | `JobDefOf.DropEquipment` (入队) |

### 全局操作 (1)
| Tool | 说明 | 数据源/操作 |
|------|------|------------|
| `allow_all_items` | 允许地图上所有被禁止的物品 | `CompForbiddable.Forbidden = false` (入队) |

### 贸易 (2)
| Tool | 说明 | 数据源/操作 |
|------|------|------------|
| `list_faction_traders` | 列出可通讯的派系和商船 | `Find.FactionManager.AllFactionsVisible` |
| `trade_execute` | 执行交易（商船或定居点） | `TradeUtility` (入队) |

### 搜索 (6)
| Tool | 说明 | 数据源 |
|------|------|--------|
| `search_map` | 按类型搜索地图事物（分页） | `map.listerThings.AllThings` |
| `find_pawn` | 搜索指定角色/生物（分页） | `map.mapPawns.AllPawnsSpawned` |
| `get_thing_def` | 查询物品定义详情 | `ThingDef` |
| `search_thing_def` | 按关键词搜索 ThingDef（分页） | `DefDatabase<ThingDef>.AllDefs` |

### 建筑布局 (2)
| Tool | 说明 | 数据源 |
|------|------|--------|
| `get_structure_layout` | 查看建筑物内部结构布局 | `Building` / `CellRect` |
| `get_construction_status` | 查看建造项目进度 | `map.listerThings.ThingsOfDef` |

### 移动 (2)
| Tool | 说明 | 数据源/操作 |
|------|------|------------|
| `move_pawn` | 移动角色到指定坐标 | `JobDefOf.Goto` (入队) |
| `move_camera` | 移动视角（本身不返回 GetTargetPos） | `Find.CameraDriver` |

### 弹框 (2)
| Tool | 说明 | 数据源 |
|------|------|--------|
| `get_open_dialogs` | 列出当前打开的弹框 | `Find.WindowStack` |
| `select_dialog_option` | 选择弹框中的选项 | `WindowStack.TryRemove()` (入队) |

### 右键菜单 (2)
| Tool | 说明 | 数据源/操作 |
|------|------|------------|
| `get_right_click_menu` | 生成指定坐标+殖民者的右键菜单 | `FloatMenuMakerMap.GetOptions()` |
| `select_right_click` | 执行右键菜单选项 | `FloatMenuOption.Chosen()` (入队) |

### 命令工具 (8)
| Tool | 说明 | 数据源/操作 |
|------|------|------------|
| `cancel_task` | 取消殖民者当前/排队任务 | `pawn.jobs.StopAll()` (入队) |
| `cancel_build` | 取消蓝图/框架/标记（矩形范围） | `t.Destroy(Cancel)` + `RemoveDesignation` (入队) |
| `designate_hunt` | 标记动物狩猎 | `AddDesignation(Hunt)` (入队) |
| `designate_slaughter` | 标记已驯服动物宰杀 | `AddDesignation(Slaughter)` (入队) |
| `designate_tame` | 标记野生动物驯服 | `AddDesignation(Tame)` (入队) |
| `forbid_item` | 禁止区域内物品 | `t.SetForbidden(true)` (入队) |
| `allow_item` | 允许区域内物品（精确范围版） | `t.SetForbidden(false)` (入队) |
| `claim_item` | 占有区域内物品/建筑为玩家派系 | `t.SetFaction(Faction.OfPlayer)` (入队) |

### 区域管理 (2)
| Tool | 说明 | 数据源/操作 |
|------|------|------------|
| `set_bed_owner_type` | 设置床位类型（医疗/囚犯/殖民者） | `Building_Bed.Medical`, `CompAssignableToPawn` (入队) |
| `set_temp_control` | 设置温度控制设备 | `CompTempControl` (入队) |

### 基地模板 (2)
| Tool | 说明 | 数据源/操作 |
|------|------|------------|
| `list_base_templates` | 列出可用基地模板 | `BaseTemplateManager` |
| `apply_base_template` | 应用基地模板到地图 | `Designator_Build` 批量 (入队) |

### 反馈 (1)
| Tool | 说明 | 数据源 |
|------|------|--------|
| `submit_feedback` | 向开发者提交反馈 | 文本收集 |

### 腐坏追踪 (2)
| Tool | 说明 | 数据源 |
|------|------|--------|
| `check_deterioration` | 扫描地图检查物品腐坏/露天耐久降低，跨阈值时返回警告（可周期调用） | `DeteriorationTracker` |
| `get_deteriorating_items` | 腐坏/耐久降低物品清单 | `DeteriorationTracker` |

### 地图 (1)
| Tool | 说明 | 数据源/操作 |
|------|------|------------|
| `regenerate_map` | 重新生成当前地图（i_know_danger 确认） | `GetOrGenerateMapUtility` (入队) |

### Tool Result Suffix (1)
| Tool | 说明 | 数据源 |
|------|------|--------|
| `set_tool_result_suffix` | 设置工具结果后缀（一次性），下一次工具调用结果末尾追加后自动清空 | `ToolRegistry.ToolResultSuffix` |

### 可达性检测

以下工具默认检查殖民者是否可达目标区域/位置，不可达到返回错误。传 `ignore_unreachable=true` 可跳过：

`designate_build`, `designate_room`, `designate_plants_cut`, `designate_harvest`, `designate_deconstruct`, `designate_clear_plants`, `create_stockpile`, `create_growing_zone`, `uninstall_building`, `install_minified_thing`

### MCP 延迟探查

`get_mcp_latency` 工具测量 Agent ↔ 游戏双向延迟，三个指标：

| 指标 | 计时方式 | 意义 |
|------|---------|------|
| 主线程调度延迟 | DispatchAsync 入队 → ProcessPending 执行 (Stopwatch) | 游戏帧率、主线程繁忙度 |
| 命令队列积压 | ConcurrentQueue.Count 执行前后 | 任务消化速度 |
| HTTP → Tool 总耗时 | 方法入口到出口 (DateTime.UtcNow) | 含网络 RTT 的全链路 |

Agent 跨网协商：`网络 RTT ≈ 总耗时 - 主线程调度延迟`。超过 50ms 说明游戏侧繁忙，应降频请求。

## Skill 系统

Skill 是领域知识文件（Markdown + YAML frontmatter），存放在 `Skills/` 目录。

| Skill | 内容 |
|-------|------|
| `equipment-crafting` | 装备制造策略、品质控制、材料选择 |
| `colony-management` | 殖民地管理、工作分配、资源规划 |
| `combat-preparation` | 战斗准备、阵地部署、武器射程 |
| `base-building` | 基地布局设计、13x13 标准间、材料选择 |
| `research-management` | 科技树优先级、研究资源配置 |
| `medical-care` | 手术风险分析、植入体策略、药物使用 |

## Claude Desktop 配置

```json
{
  "mcpServers": {
    "rimworld": {
      "type": "sse",
      "url": "http://localhost:9877/sse"
    }
  }
}
```

或使用 Streamable HTTP：

```json
{
  "mcpServers": {
    "rimworld": {
      "type": "http",
      "url": "http://localhost:9877/mcp"
    }
  }
}
```

## 开发

> **通用规范**（异常处理、日志安全、设计文档、提交规范）见 [../CLAUDE.md](../CLAUDE.md)。

- net472，仅 `System.Text.Json` NuGet 依赖
- 后台线程日志必须通过 `McpLog`（线程安全 `ConcurrentQueue` → 主线程 `Flush`）
- Tool 返回值：`ToolResult` → `McpServer` 包装为 `{"content":[{"type":"text","text":"..."}]}`
- 写操作必须通过 `McpCommandQueue` 调度到主线程
- `dotnet build` → `publish/1.6/Assemblies/RimWorldMCP.dll`
- **坐标陷阱**：`IntVec3(x,y,z)` 中 `y` 是海拔，`z` 是网格垂直轴。MCP 用户的 `pos_y` 必须映射到 `IntVec3.z`，写 `new IntVec3(x, posY, 0)` 是 bug
- **HttpListener 陷阱**：`StartAsync` 中 `HttpListener.Start()` 可能抛 `HttpListenerException`（端口占用/权限不足），需提供中文诊断；`_transport` 要在 `StartAsync` 成功后才赋值；RimWorld 返回主菜单会导致 Game 对象被 Dispose 但 GameComponent 不通知，需静态字段跨实例清理
- **Session 隔离**：`cwd` 传入 SDK 后会被 sanitize 为目录名，checkpoint 落到 `~/.claude/projects/<sanitized-cwd>/`。不同存档的 `cwd` 不同 → sanitize 结果不同 → 隔离生效。不可改 SDK 内部的 `projects/` base path。

### I18N / 简体中文翻译

工具名称的简体中文翻译位于 `publish/Languages/ChineseSimplified/Keyed/RimWorldMCP_Tools.xml`，遵循 RimWorld Keyed 翻译格式：
- Key 格式：`RimWorldMCP_Tool_<tool_name>` → 中文名称
- 新增工具必须同步添加翻译条目
- 分页工具在中文名称后标注（分页）
- 工具名称应语义自包含：LLM 看到名称即知工具用途

### Tool 开发规范

**1. 新增 Tool 先查游戏源码**

开发任何新 Tool 时，第一步是到 `F:\RiderProjects\Assembly-CSharp\` 反编译源码中追踪完整链路：用户在游戏界面点击 → Designator/Command → JobGiver/JobDriver → 游戏执行。理解原版如何处理输入验证、资源检查、失败路径，然后尽量复用游戏原有逻辑（Designator、Job、Bill 等），不要凭空造轮子。

**2. 坐标参数统一左上→右下**

所有 MCP Tool 的区域坐标参数使用 `pos_x/pos_y`（左上角）→ `end_x/end_y`（右下角）模式，禁止使用中心点+半径/宽高向外扩展的 API 设计。参考 `designate_mine` 的实现。
- `pos_x`/`pos_y` — 必填，区域起始角
- `end_x`/`end_y` — 可选，区域结束角（不提供则只操作单格）

**3. 所有 Tool 必须实现 GetTargetRange**

所有 Tool 必须 override `GetTargetRange(JsonElement? args)` 返回摄像头目标矩形，使 AI 调用工具时画面自动移动到操作目标。
详见 `design/camera-system.md`。

**4. 用 thingIDNumber 精确定位 Pawn/物品**

所有涉及殖民者（Pawn）或物品（Thing）的操作，参数统一使用 `thingIDNumber`（int）而非名称字符串匹配。

**5. 关注 LLM 缓存命中率**

- **List 工具必须分页**：数据量可能超过 20 条的工具，提供 `page`/`page_size` 参数，默认每页 10
- **精简输出格式**：用表格而非段落，只输出 AI 决策必需的信息
- **查询类工具设计为「按需获取」**：摘要列表 + 按 ID 查询详情

## Token 预算系统

### 概述

按存档限制 LLM token 消耗，超出后阻止（暂停游戏+拒绝消息）或警告（Webhook 通知+继续）。

### 架构

```
McpModSettings（全局设置）
├── TokenBudgetLimit          — token 预算上限（0=无限制）
├── TokenBudgetExceedAction   — Block / Warn
├── TokenBudgetWebhookUrl     — Warn 模式回调 URL
└── GlobalModelUsageStore     — 全局模型用量汇总（独立 JSON 文件）

TokenUsageTracker（per-save，持久化到存档）
├── PerModelUsages            — 按模型分列
├── CheckBudget(limit)        — 80%/95%/100% 三档
└── Record(model, ...)        — 同步写 GlobalModelUsageStore

BridgeLifecycle.SendCCMessage()
├── Block + 超预算 → Find.TickManager.Pause() + return
└── Warn  + 超预算 → Webhook POST + 继续
```

### 关键文件

| 文件 | 职责 |
|------|------|
| `McpModSettings.cs` | 预算上限、行为模式、Webhook URL |
| `Bridge/GlobalModelUsageStore.cs` | 全局模型用量汇总，JSON 持久化 |
| `Bridge/TokenUsageTracker.cs` | 按模型追踪、预算检查、`GetCompactDisplay()` |
| `RimWorldMCPMod.cs` | 设置窗口：预算配置 + 全局用量汇总表 + 清空按钮 |

### 数据流

```
TokenUsageTracker.Record(model, ...)
    ├── 更新 PerModelUsages (per-save)
    └── GlobalModelUsageStore.Contribute(model, ...) → Save() (全局 JSON)
```

Agent 侧通过 CCClient → TokenUsageTracker.Record() 触发记录，详见 `../RimWorldAgent/CLAUDE.md`。

### UI 展示

- **底栏**（始终显示）：`Token: 1.2M/2M (60%) ████████░░░░ | 缓存 800K(40%)`
  - 颜色：<80% 绿 → 80-95% 黄 → ≥95% 红
- **顶部横幅**（Warning/Critical/Exceeded 时显示）：醒目半透明色条，显示预算状态文案
- **设置窗口**：预算配置 + 全局模型用量汇总表 + 清空按钮（需确认）

### 设置项

| 设置 | 默认值 | 说明 |
|------|--------|------|
| `TokenBudgetLimit` | `0` | Token 预算上限，0=无限制 |
| `TokenBudgetExceedAction` | `Block` | `Block` 暂停游戏+阻止消息 / `Warn` Webhook+继续 |
| `TokenBudgetWebhookUrl` | `""` | Warn 模式回调 URL，空则不回调 |

### Webhook 格式

超出预算时 POST JSON：
```json
{
  "event": "budget_exceeded",
  "save_name": "殖民地名",
  "session_id": "a1b2c3d4e5f6",
  "model": "claude-sonnet-4-6",
  "current_tokens": 2100000,
  "budget_limit": 2000000,
  "usage_percent": 105.0,
  "timestamp": "2026-05-27T12:00:00Z"
}
```
