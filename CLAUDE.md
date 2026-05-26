# RimWorldMCP

MCP (Model Context Protocol) 服务器，将 RimWorld 游戏状态和操作暴露为 LLM 可调用的 Tool。作为 RimWorld mod DLL 内嵌运行。

**游戏源码**: `F:\RiderProjects\Assembly-CSharp\`（RimWorld 反编译 C# 源码，net472 Class Library）
**Openclaw源码**: `D:\WebstormProjects\openclaw`（已弃用，CC 替代）

## 项目结构

```
RimWorldMCP/
├── RimWorldMCPMod.cs                      # Mod 入口：Mod 子类，管理设置窗口与生命周期
├── McpModSettings.cs                      # Mod 设置数据模型（日志/监听/桥接器/OSS）
├── McpLog.cs                              # 统一日志（按级别过滤，输出到 Verse.Log）
├── GameComponent_McpServer.cs             # GameComponent 子类，管理 MCP 服务生命周期
├── McpCommandQueue.cs                     # 线程安全命令队列（ConcurrentQueue + TaskCompletionSource）
├── Transport/                             # 传输层
│   ├── ITransport.cs                      # 抽象接口
│   ├── SseTransport.cs                    # GET /sse + POST /message（HttpListener 后台线程）
│   ├── StreamableHttpTransport.cs         # POST /mcp（新版协议）
│   └── StdioTransport.cs                  # stdin/stdout（保留，不在游戏内使用）
├── Mcp/                                   # MCP 协议层
│   ├── McpServer.cs                       # JSON-RPC 调度：initialize/tools/list/tools/call/resources
│   └── McpMessage.cs                      # 数据类型：请求/响应/Tool定义/资源
├── Tools/                                 # 78 个 Tool（真实 RimWorld API 调用）
│   ├── ITool.cs                           # Tool 接口 + ToolResult
│   ├── ToolRegistry.cs                    # 注册表 + 执行调度 + 资源映射（反射自动注册）
│   ├── ResourceCheckHelper.cs             # 建造资源检查辅助工具
│   └── Tool_*.cs                          # 各 Tool 实现
├── Bridge/                                # Claude Code 桥接
│   ├── BridgeLifecycle.cs                 # CC 连接生命周期 + 事件转发 + 子进程管理
│   ├── CCClient.cs                        # WebSocket 客户端（心跳+重连）
│   ├── ChatDisplayState.cs                # 线程安全聊天显示状态
│   └── GameContextProvider.cs             # 游戏上下文文本构建
├── Skills/                                # 领域知识 Skill 系统
│   ├── SkillInfo.cs                       # Skill 数据模型
│   ├── SkillRegistry.cs                   # 加载 .md 文件、解析 frontmatter
│   └── *.md                               # 6 个 Skill 文件
├── McpOssUploader.cs                      # 阿里云 OSS 截图自动上传
├── McpOssConfig.cs                        # OSS 配置数据
├── cc-companion/                           # Claude Code 伴随进程（TypeScript, tsx 运行时）
│   ├── companion/                         # 入口 + 配置 + SDK 加载
│   │   ├── companion.ts                   # 编排入口
│   │   ├── config.ts                      # 配置解析（CLI 参数、环境变量）
│   │   └── sdk-loader.ts                  # SDK 加载
│   ├── bridge/                            # 桥接层（WS + SDK 会话）
│   │   ├── ws-server.ts                   # WebSocket Server
│   │   └── session.ts                     # SDK 会话管理（mcpServers、env、systemPrompt）
│   ├── chat/                              # 聊天 UI
│   │   ├── chat-http.ts                   # HTTP 路由
│   │   └── chat-page.ts                   # 聊天页面 HTML
│   ├── rimworld/context.ts                # 系统提示词加载
│   └── Prompt.md                          # AI 行为提示词
└── About/
    └── About.xml                          # Mod 元数据
```

## 架构

单进程 mod 内嵌——LLM 通过 SSE 或 Streamable HTTP 连接游戏内的 MCP 服务。

- **net472 Library**：与 RimWorld Unity 运行时一致，`OutputType=Library`
- **引用 Assembly-CSharp.dll**：Tool 直接调用游戏 API（`Find.*`、`DefDatabase<>`、`PawnsFinder` 等）
- **GameComponent 入口**：反射自动发现，`StartedNewGame()` / `LoadedGame()` 时启动 HttpListener；`ExposeData()` 持久化 sessionId 到存档，按存档隔离 CC session 数据
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

### 端口清理机制

RimWorld 返回主菜单时 `Game.Dispose()` 不通知 GameComponent，导致上一 Game 实例的 HttpListener（http.sys 内核级 URL 注册）残留。

- `GameComponent_McpServer.StopMcpService()` — 启动前调用，停止当前实例及静态残留的传输层，同时调用 `BridgeLifecycle.Stop()` 杀旧 companion 进程
- `s_activeTransport` — 静态字段跨 Game 实例追踪活跃监听器
- `HttpListenerException` 错误码中文诊断（5=拒绝访问, 183=端口占用）
- `_transport` 在 `StartAsync()` 成功后才赋值，失败保持 null 允许下次重试

### 进程生命周期

Companion 进程在 RimWorld 退出/崩溃/重进入时可靠清理，三层保障：

| 机制 | 平台 | 场景 |
|------|------|------|
| Harmony `Game.Dispose()` postfix → `BridgeLifecycle.Stop()` | 跨平台 | 正常返回主菜单 / 退出游戏 |
| `StopMcpService()` → `BridgeLifecycle.Stop()` | 跨平台 | 读档 / 新档（先杀旧进程再启动新进程） |
| Windows Job Object `KILL_ON_JOB_CLOSE` | Windows | RimWorld 强杀 → OS 立即杀 companion |
| `--idle-timeout 30000`（WS 断开计时） | 跨平台 | companion 侧兜底 — WS 断开 30s 无重连自动退出 |

### Mod 设置

`RimWorldMCPMod`（继承 `Verse.Mod`）提供游戏内设置界面（Options → Mod 设置 → RimWorld MCP），设置项通过 `McpModSettings` 持久化：

| 配置项 | 默认值 | 说明 |
|--------|--------|------|
| 日志级别 | Info | Debug / Info / Warn / Error 过滤 |
| MCP 监听地址 | 0.0.0.0 | 可设为 localhost / 内网 IP |
| MCP 端口 | 9877 | HTTP 监听端口 |
| CCB 主机 | 127.0.0.1 | Companion WebSocket 主机 |
| CCB 端口 | 19999 | Companion WebSocket 端口 |
| 自动启动 | 开启 | 游戏加载时自动 spawn Node.js 子进程 |
| Token | - | WS 握手认证，companion 层面 |
| 模型名称 | - | Companion 启动时传入的模型名 |
| 自动移动视角 | 开启 | AI 调用坐标工具时自动平移到目标位置 |
| OSS 上传 | 关闭 | 截图自动上传到阿里云 OSS |
| OSS Endpoint/Bucket/Key | - | 阿里云 OSS 访问配置 |
| 签名 URL | 开启 | 预签名 URL 有效期 24h |

## Claude Code 桥接

游戏事件通过 WebSocket 推送到本地 Node.js 进程（CC Companion），Companion 使用 Claude Agent SDK 与 Claude API 通信。Claude 的响应广播回游戏内聊天窗口。

### 数据流

```
RimWorld (C#)                  CC Companion (Node.js)       Claude API
    │                                │                         │
    │ 游戏事件(WS)                     │  SDK query()            │
    │──────────────────────────────▶ │────────────────────────▶│
    │                                │                         │
    │ 聊天窗 ◀─ WS broadcast ────────│  ◀── assistant/tool ────│
    │                                │                         │
    │  MCP Server :9877 ◀────────────│──── tools/call ─────────│
```

### 连接流程

`BridgeLifecycle.StartAsync(sessionId)` 在加载存档时执行：
1. `StopCompanionProcess()` — 停止旧进程
2. `KillStaleByPidFile()` — 清理 `.pid` 残留
3. `StartCompanionProcess()` — 创建 `claude-sessions/rimworld-<sessionId>/` 目录，spawn `node --import tsx/esm companion/companion.ts --idle-timeout 30000 --project-setting-sources "{...}"`；config 通过环境变量传递，SDK 配置由用户 `.claude/settings.json` 提供，Windows 额外通过 Job Object 绑定子进程生命周期
4. `CCClient.Connect()` — WebSocket 握手（hello/hello-ok）

sessionId 由 `GameComponent_McpServer` 生成并持久化：新游戏随机生成 12 位 hex，`ExposeData()` 通过 `Scribe_Values` 写入存档。读档时从存档恢复；兼容旧存档（无 sessionId 时自动补生成）。

CLI 示例（手动运行）：
```bash
# 从 RimWorldMCP/cc-companion/ 目录，--setting-sources 默认 user,project,local 可省略
npm start
# 等效：tsx companion/companion.ts
```

C# 自动 spawn 时实际执行的命令：
```bash
node --import tsx/esm companion/companion.ts
# 参数通过环境变量传递：
#   RIMWORLD_PROJECT_PATH → SDK cwd，确定 .claude/settings.json 位置
#   CCB_HOST, CCB_PORT      → Companion WebSocket 监听
#   CCB_AUTH_TOKEN          → WS 握手认证（可选）
```

### 事件推送

`BridgeLifecycle.CCEventTick()` 每帧检查 `NotificationBus`，高危通知即时推送，普通通知每 120 tick 批量。事件格式化在 C# 端完成（`FormatGameEvent()`），companion 透传文本。

### 中断通知

6 个 Harmony Patch 拦截游戏事件 → `NotificationBus` → `IsHighDanger` 过滤。

| 事件源 | 条件 |
|--------|------|
| Letter: 大威胁/小威胁 | `ThreatBig`/`ThreatSmall` |
| Letter: 死亡 | `LetterDefOf.Death` |
| Letter: 负面 | `LetterDefOf.NegativeEvent` |
| Message: 大威胁/小威胁/死亡/负面 | 实时消息 |
| AlertStart: 全部 | 饥饿、崩溃风险等 |

### CC Companion 自动管理

设置面板可安装/卸载/重装 Claude Code 依赖（`npm install`），状态和日志实时显示。安装状态通过 `BridgeLifecycle.InstallStatus` 暴露，设置窗口每帧刷新。

Companion 完整 CLI 参数见 `cc-companion/companion/config.ts` 的 `printHelp()`，或用 `tsx companion/companion.ts --help` 查看。

## 运行教程

### 模式一：自动模式（推荐）

1. 在 Mod 设置中确保：`自动启动` = 开启，`CCB 端口` = 19999
2. 在 `cc-companion/` 目录执行 `npm install`（或设置面板点"安装 Claude Code 依赖"）
3. 启动 RimWorld，加载存档
4. 游戏内自动 spawn companion 进程，控制台输出 `[js] [cc-companion] 就绪`
5. 打开聊天窗（Ctrl+Shift+C）即可与 AI 交互

### 模式二：手动模式（调试/开发）

```bash
# 终端 1：启动 companion
cd RimWorldMCP/cc-companion
npm start
# 输出：[cc-companion] 就绪，等待 RimWorldMCP 连接...
#       WebSocket: ws://127.0.0.1:19999
#       聊天页面: http://127.0.0.1:19999/

# 游戏设置：自动启动 = 关闭，CCB 主机 = 127.0.0.1，CCB 端口 = 19999
```

### 聊天页面

浏览器打开 `http://127.0.0.1:19999/` 可查看实时对话、SDK 版本、模型、MCP 服务状态。发消息通过 RimWorld 游戏内聊天窗——聊天页面是只读面板。

### 参数覆盖顺序

```
用户 .claude/settings.json   ← 低优先级（API Key、Base URL、MCP 等）
        ↓
SDK Options (session.ts)     ← 中优先级（model、settingSources、cwd）
        ↓
环境变量 (ProcessStartInfo)   ← 高优先级（RIMWORLD_PROJECT_PATH、CCB_HOST、CCB_PORT、CCB_AUTH_TOKEN）
```

SDK 从用户本地 `.claude/settings.json` 读取 API Key、Base URL、MCP 服务、权限等配置。C# 不再写入 settings.json，完全沿用用户本地配置。

### 日志查看

| 来源 | 怎么看 |
|------|--------|
| Companion 进程 | `BridgeLifecycle.cs` 注册 `OutputDataReceived`/`ErrorDataReceived`，输出到游戏控制台 `[js]` 前缀 |
| SDK 内部 | `session.ts` 中 `stderr: (data) => process.stderr.write(\`[sdk] ${text}\`)` |
| 游戏日志 | Verse.Log — `McpLog.Info/Warn/Error` 方法 |
| C# 桥接 | `McpLog.Info($"[cc] ...")` 形式，输出带 `[cc]` 前缀 |

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

中文名称参见 `publish/Languages/ChineseSimplified/Keyed/RimWorldMCP_Tools.xml`。以下为全部 78 个工具。

### 通用查询 (4)
| Tool | 说明 | 数据源 |
|------|------|--------|
| `get_game_context` | 游戏全局状态快照 | `Find.CurrentMap`, `Find.TickManager`, `Find.ResearchManager` |
| `get_resources` | 资源库存报告 | `map.resourceCounter.AllCountedAmounts` |
| `check_colony` | 殖民地提醒（空闲/崩溃/流血/食物/防御） | `PawnsFinder`, `map.wealthWatcher` |
| `toggle_pause` | 切换游戏暂停状态，恢复时设为最大速度 | `Find.TickManager.CurTimeSpeed` (入队) |

### 网格查询 (2)
| Tool | 说明 | 参数 |
|------|------|------|
| `get_tile_detail` | 指定坐标范围详情（建筑/物品/植物/生物） | pos_x, pos_y, end_x, end_y |
| `get_tile_grid` | 文本化字符网格地图（64 种符号） | pos_x, pos_y, end_x, end_y |

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

### 装备管理 (1)
| Tool | 说明 | 数据源/操作 |
|------|------|------------|
| `find_equipment` | 搜索地图可用武器/衣物，按类型品质分组 | `map.listerThings.AllThings` |

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

### 搜索 (4)
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

### 区域管理 (2)
| Tool | 说明 | 数据源/操作 |
|------|------|------------|
| `set_bed_owner_type` | 设置床位类型（医疗/囚犯/殖民者） | `Building_Bed.Medical`, `CompAssignableToPawn` (入队) |
| `set_temp_control` | 设置温度控制设备 | `CompTempControl` (入队) |

### TODO 系统 (3)
| Tool | 说明 | 数据源 |
|------|------|--------|
| `todo_add` | 添加待办任务 | `TodoManager` |
| `todo_delete` | 删除待办任务 | `TodoManager` |
| `todo_query` | 查询待办任务 | `TodoManager` |

### 基地模板 (2)
| Tool | 说明 | 数据源/操作 |
|------|------|------------|
| `list_base_templates` | 列出可用基地模板 | `BaseTemplateManager` |
| `apply_base_template` | 应用基地模板到地图 | `Designator_Build` 批量 (入队) |

### 反馈 (1)
| Tool | 说明 | 数据源 |
|------|------|--------|
| `submit_feedback` | 向开发者提交反馈 | 文本收集 |

### 腐坏追踪 (1)
| Tool | 说明 | 数据源 |
|------|------|--------|
| `get_deteriorating_items` | 腐坏/耐久降低物品清单 | `DeteriorationTracker` |

### 地图 (1)
| Tool | 说明 | 数据源/操作 |
|------|------|------------|
| `regenerate_map` | 重新生成当前地图（i_know_danger 确认） | `GetOrGenerateMapUtility` (入队) |

### Skill (2)
| Tool | 说明 | 数据源 |
|------|------|--------|
| `get_skills` | 列出可用领域知识 | `SkillRegistry.GetAll()` |
| `active_skill` | 激活获取 Skill 内容 | `SkillRegistry.Get(name)` |

### 可达性检测

以下工具默认检查殖民者是否可达目标区域/位置，不可达到返回错误。传 `ignore_unreachable=true` 可跳过：

`designate_build`, `designate_room`, `designate_plants_cut`, `designate_harvest`, `designate_deconstruct`, `designate_clear_plants`, `create_stockpile`, `create_growing_zone`, `uninstall_building`, `install_minified_thing`

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

- net472，仅 `System.Text.Json` NuGet 依赖
- 日志输出到 `Console.Error`（Transport 层）和 `Verse.Log`（GameComponent 层）
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

### 开发规范

**1. 新增 Tool 先查游戏源码**

开发任何新 Tool 时，第一步是到 `F:\RiderProjects\Assembly-CSharp\` 反编译源码中追踪完整链路：用户在游戏界面点击 → Designator/Command → JobGiver/JobDriver → 游戏执行。理解原版如何处理输入验证、资源检查、失败路径，然后尽量复用游戏原有逻辑（Designator、Job、Bill 等），不要凭空造轮子。

**2. 坐标参数统一左上→右下**

所有 MCP Tool 的区域坐标参数使用 `pos_x/pos_y`（左上角）→ `end_x/end_y`（右下角）模式，禁止使用中心点+半径/宽高向外扩展的 API 设计。参考 `designate_mine` 的实现。
- `pos_x`/`pos_y` — 必填，区域起始角
- `end_x`/`end_y` — 可选，区域结束角（不提供则只操作单格）

**3. 坐标类 Tool 实现 GetTargetPos**

所有接收坐标参数的 Tool 必须 override `GetTargetPos(JsonElement? args)` 返回目标坐标 `(int x, int y)?`。框架在开关开启时自动提取坐标并平滑移动视角，无需 Tool 自行处理。

- 从 `args` 提取 `pos_x`/`pos_y`，解析失败返回 `null`
- 非坐标类 Tool 保持默认实现（返回 `null`）
- `Tool_MoveCamera` 本身也不跳转（返回 `null`）

**4. 用 thingIDNumber 精确定位 Pawn/物品**

所有涉及殖民者（Pawn）或物品（Thing）的操作，参数统一使用 `thingIDNumber`（int）而非名称字符串匹配。名称有重名、翻译、截断风险，ID 唯一且稳定。
- 殖民者：`PawnsFinder.AllMaps_FreeColonistsSpawned.FirstOrDefault(c => c.thingIDNumber == id)`
- 目标/物品：`map.mapPawns.AllPawnsSpawned.FirstOrDefault(p => p.thingIDNumber == id)` 或 `map.listerThings.AllThings.FirstOrDefault(t => t.thingIDNumber == id)`
- 工具之间传递引用时优先输出 `thingIDNumber`，让 LLM 在后续调用中精确回传
- 参考实现：`Tool_ArrestPawn.cs`、`Tool_AttackPawn.cs`、`Tool_EquipPawn.cs`

**5. 关注 LLM 缓存命中率**

Tool 的返回内容直接影响 prompt caching 的存活时间。大段返回会挤掉上下文中缓存的 system prompt，导致下次请求 cache miss，重新计费。

- **List 工具必须分页**：数据量可能超过 20 条的工具，提供 `page`/`page_size` 参数，默认每页 10。AI 按需翻页，不是一次性灌入
- **精简输出格式**：用表格而非段落，省略无意义的装饰文本。只输出 AI 决策必需的信息
- **查询类工具设计为「按需获取」**：如 `get_colonists` 返回摘要列表（名称+ID+心情），详细信息（健康/需求）由单独工具按 ID 查询
- **避免在结果中重复描述 Tool 自身的用法**：AI 已从 InputSchema 知道参数含义
- **价格判断**：一次 cache miss 相当于几千 token 的额外开销。如果一个 Tool 返回 2000 token，每次调用都可能触发 cache eviction，比翻 10 页（每页 200 token）的总成本更高
