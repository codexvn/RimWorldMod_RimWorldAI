# RimWorldMCP

MCP (Model Context Protocol) 服务器，将 RimWorld 游戏状态和操作暴露为 LLM 可调用的 Tool。作为 RimWorld mod DLL 内嵌运行。

**游戏源码**: `F:\RiderProjects\Assembly-CSharp\`（RimWorld 反编译 C# 源码，net472 Class Library）
**Openclaw源码**: `D:\WebstormProjects\openclaw`

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
├── Tools/                                 # 40 个 Tool（真实 RimWorld API 调用）
│   ├── ITool.cs                           # Tool 接口 + ToolResult
│   ├── ToolRegistry.cs                    # 注册表 + 执行调度 + 资源映射
│   ├── ResourceCheckHelper.cs             # 建造资源检查辅助工具
│   └── Tool_*.cs                          # 各 Tool 实现
├── Bridge/                                # OpenClaw Gateway 桥接
│   ├── BridgeLifecycle.cs                 # 连接生命周期管理（独立于 MCP Server）
│   ├── GatewayClient.cs                   # WebSocket 客户端 + ED25519 签名握手
│   ├── GatewayEventMonitor.cs             # 事件监控（Letter/消息/空闲/早报/警报）
│   └── GatewayMessageQueue.cs             # 消息队列（分类/节流/去重）
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
- **GameComponent 入口**：反射自动发现，`StartedNewGame()` 时启动 HttpListener
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

- `GameComponent_McpServer.StopMcpService()` — 启动前调用，停止当前实例及静态残留的传输层
- `s_activeTransport` — 静态字段跨 Game 实例追踪活跃监听器
- `HttpListenerException` 错误码中文诊断（5=拒绝访问, 183=端口占用）
- `_transport` 在 `StartAsync()` 成功后才赋值，失败保持 null 允许下次重试

### Mod 设置

`RimWorldMCPMod`（继承 `Verse.Mod`）提供游戏内设置界面（Options → Mod 设置 → RimWorld MCP），设置项通过 `McpModSettings` 持久化：

| 配置项 | 默认值 | 说明 |
|--------|--------|------|
| 日志级别 | Info | Debug / Info / Warn / Error 过滤 |
| MCP 监听地址 | 0.0.0.0 | 可设为 localhost / 内网 IP |
| MCP 端口 | 9877 | HTTP 监听端口 |
| 桥接器类型 | 无 | 无 / OpenClaw |
| Gateway URL | - | WebSocket 连接地址（ws://127.0.0.1:18789） |
| Token / Password | - | 桥接认证凭据 |
| OSS 上传 | 关闭 | 截图自动上传到阿里云 OSS |
| OSS Endpoint/Bucket/Key | - | 阿里云 OSS 访问配置 |
| 签名 URL | 开启 | 预签名 URL 有效期 24h |

## OpenClaw Gateway 桥接

GatewayClient 作为 WebSocket 客户端连接 OpenClaw Gateway（默认 `ws://127.0.0.1:18789`）。

### 连接流程

```
1. WebSocket 连接  ws://127.0.0.1:18789
2. 启动 ReceiveLoop 后台接收
3. 等待 Gateway 发送 connect.challenge 事件
4. 收到 challenge 后发送 connect RPC 请求（含 ED25519 设备签名）
5. 等待 hello-ok 响应
6. 握手完成，进入 Ready
```

**Step 1: 收到 challenge**
```json
{"type":"event","event":"connect.challenge","payload":{"nonce":"xxx","ts":1737264000000}}
```

**Step 2: 发送 connect RPC**
```json
{
  "type": "req",
  "id": "uuid8",
  "method": "connect",
  "params": {
    "minProtocol": 3,
    "maxProtocol": 4,
    "client": { "id": "gateway-client", "displayName": "RimWorldMCP", "version": "1.0", "platform": "windows", "mode": "backend" },
    "role": "operator",
    "scopes": ["operator.read", "operator.write"],
    "caps": ["tool-events"],
    "locale": "zh-CN",
    "userAgent": "RimWorldMCP/1.0",
    "auth": { "token": "..." },
    "device": { "id": "abc123...", "publicKey": "base64url...", "signature": "base64url...", "signedAt": 1737264000000, "nonce": "xxx" }
  }
}
```

- ED25519 设备签名（BouncyCastle），V3 pipe 分隔载荷
- 签名载荷: `v3|<deviceId>|gateway-client|backend|operator|<scopes>|<ts>|<token>|<nonce>|<platform>|`
- protocol v3~v4 协商

**Step 3: 收到 hello-ok**
```json
{"type":"res","ok":true,"payload":{"type":"hello-ok","protocol":4,"server":{"version":"...","connId":"..."},"policy":{"tickIntervalMs":30000}}}
```

- `policy.tickIntervalMs` 心跳间隔，超时 `2×tickIntervalMs` 断开

### 事件监控

`GatewayEventMonitor` 在 `BridgeLifecycle.Tick()` 中每 120 tick 轮询，自动检测并推送以下事件到 Gateway：

| 监控类型 | 说明 | 触发方式 |
|----------|------|----------|
| Letter 通知 | 游戏事件（袭击/死亡/完成等） | 检测 `Find.LetterStack` 新 Letter |
| 右侧消息 | 游戏内飘字消息 | 检测 `Find.Archive` 新消息，推送后清除游戏内显示 |
| 空闲检测 | 殖民者空闲提醒 | 对比上次空闲人数变化 |
| 综合警报 | 崩溃风险/流血/食物/防御/床位 | 同 `check_colony` 逻辑 |
| 每早汇报 | 游戏时间每日 6 点汇总 | 含天气/殖民者/资源/电力/研究/财富 |

消息分类：
- `MessageCategory.RaidStart` — 中断通知（chat.abort），高优先级立即发送
- `MessageCategory.Alert` — 一般警报队列发送
- `MessageCategory.DailyMorning` — 每日早报
- `MessageCategory.SessionInit` — 连接后首次会话 Prompt

### 中断通知（chat.abort）系统

6 个 Harmony Patch 拦截游戏事件 → `NotificationBus` → `IsHighDanger` 过滤 → `PushEmergency`（每帧检查 `HighDangerPending`）→ `GatewayMessageQueue.SendNow(RaidStart)`。

#### 触发中断的事件（Agent 应放弃当前任务优先处理）

| 事件源 | 条件 | 实例 |
|--------|------|------|
| Letter: 大威胁 | `LetterDefOf.ThreatBig` | 袭击、虫害、猎杀人类包、异常实体群 |
| Letter: 小威胁 | `LetterDefOf.ThreatSmall` | 单只发狂动物、越狱 |
| Letter: 死亡 | `LetterDefOf.Death` | 殖民者/牵绊动物死亡 |
| Letter: 负面 | `LetterDefOf.NegativeEvent` | 枯萎病、瘟疫、太阳耀斑、热浪、寒流 |
| Message: 大威胁/小威胁 | `ThreatBig`/`ThreatSmall` | 袭击实时消息 |
| Message: 角色死亡 | `PawnDeath` | 地圖上角色死亡 |
| Message: 健康事件 | `NegativeHealthEvent` | 心脏病発作、伤口感染 |
| Message: 游戏减速 | `TimeSlower.SignalForceNormalSpeed*` Patch | 战斗爆发、异常活动（限流 600 tick/10秒） |
| AlertStart: 全部 | 饥饿、崩溃风险、需要治疗、裸奔、无武器等 | 所有原生警报变活跃 |

#### 不触发中断的事件（走 120 tick 批量 Alert）

| 事件 | 原因 |
|------|------|
| Letter: 正面/事件/通知 | 好事/中性/未分类，不紧急 |
| Message: 正面/事件/完成/状态解除/拒绝/警告/消息 | 非紧急或高频噪声 |
| AlertEnd: 全部 | 正向变化 |
| `TickManager.Pause` | 对话框暂停不是危险信号（Letter 暂停已由 Letter Patch 覆盖） |

### 已验证

Gateway 2026.5.22 + ED25519 V3 签名握手已通过。

### 设置

游戏内 Options → Mod 设置 → RimWorld MCP → 桥接器按钮
或游戏右下角工具栏 "MCP 桥接" 按钮。

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

## Tool 清单（40 个，真实 API）

### 通用查询 (4)
| Tool | 说明 | 数据源 |
|------|------|--------|
| `get_game_context` | 游戏全局状态快照 | `Find.CurrentMap`, `Find.TickManager`, `Find.ResearchManager` |
| `get_resources` | 资源库存报告 | `map.resourceCounter.AllCountedAmounts` |
| `check_colony` | 殖民地提醒（空闲/崩溃/流血/食物/防御） | `PawnsFinder`, `map.wealthWatcher` |
| `toggle_pause` | 切换游戏暂停状态 | `Find.TickManager.TogglePaused()` (入队) |

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

### 建造 (2)
| Tool | 说明 | 数据源/操作 |
|------|------|------------|
| `designate_build` | 放置建造蓝图（参数: pos_x=水平, pos_y=垂直网格） | `GenConstruct.PlaceBlueprintForBuild()` (入队) |
| `designate_room` | 快速建造矩形房间（参数: pos_x/pos_y=左上, end_x/end_y=右下） | 批量 `PlaceBlueprintForBuild()` (入队) |

### 标记 (4)
| Tool | 说明 | 数据源/操作 |
|------|------|------------|
| `designate_mine` | 标记采矿（支持矩形范围） | `DesignationManager.AddDesignation(Mine)` (入队) |
| `designate_plants_cut` | 标记植物砍伐（支持矩形范围） | `DesignationManager.AddDesignation(CutPlant)` (入队) |
| `designate_harvest` | 标记作物收割（仅成熟 Standard 作物） | `DesignationManager.AddDesignation(HarvestPlant)` (入队) |
| `designate_deconstruct` | 标记建筑拆除（逐格查找最上层建筑） | `DesignationManager.AddDesignation(Deconstruct)` (入队) |

### 截图 (1)
| Tool | 说明 | 数据源/操作 |
|------|------|------------|
| `take_screenshot` | 截取地图指定 X/Z 范围画面 | `ScreenshotTaker.TakeNonSteamShot()` (入队), 自动 OSS 上传 |

### 研究 (3)
| Tool | 说明 | 数据源/操作 |
|------|------|------------|
| `list_research_projects` | 列出研究项目 | `DefDatabase<ResearchProjectDef>.AllDefsListForReading` |
| `get_research_progress` | 获取研究进度 | `Find.ResearchManager.GetProgress()` |
| `set_research_project` | 设置研究项目 | `Find.ResearchManager.SetCurrentProject()` (入队) |

### 殖民者需求 (4)
| Tool | 说明 | 数据源/操作 |
|------|------|------------|
| `get_colonists` | 殖民者信息 | `PawnsFinder.AllMaps_FreeColonistsSpawned` |
| `get_colonist_needs` | 详细需求状态 | `pawn.needs.AllNeeds` |
| `get_work_priorities` | 所有殖民者完整工作优先级表 | `pawn.workSettings.GetPriority()` |
| `set_work_priority` | 设置工作优先级 | `pawn.workSettings.SetPriority()` (入队) |

### 医疗 (2)
| Tool | 说明 | 数据源/操作 |
|------|------|------------|
| `get_colonist_health` | 健康报告 | `pawn.health.hediffSet.hediffs` |
| `schedule_operation` | 安排手术 | `billStack.AddBill(Bill_Medical)` (入队) |

### 战斗 (3)
| Tool | 说明 | 数据源/操作 |
|------|------|------------|
| `equip_pawn` | 强制殖民者拾取并装备（Job 系统，自然走过去） | `JobDefOf.Equip` / `JobDefOf.Wear` (入队) |
| `draft_pawn` | 征召/解除征召 | `pawn.drafter.Drafted` (入队) |
| `get_defense_status` | 防御状态报告 | `pawn.equipment.Primary`, `map.listerBuildings` |

### 右键菜单操作 (8)
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

### 全局操作 (1)
| Tool | 说明 | 数据源/操作 |
|------|------|------------|
| `allow_all_items` | 允许地图上所有被禁止的物品 | `CompForbiddable.Forbidden = false` (入队) |

### Skill (2)
| Tool | 说明 | 数据源 |
|------|------|--------|
| `get_skills` | 列出可用领域知识 | `SkillRegistry.GetAll()` |
| `active_skill` | 激活获取 Skill 内容 | `SkillRegistry.Get(name)` |

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

### 开发规范

**1. 新增 Tool 先查游戏源码**

开发任何新 Tool 时，第一步是到 `F:\RiderProjects\Assembly-CSharp\` 反编译源码中追踪完整链路：用户在游戏界面点击 → Designator/Command → JobGiver/JobDriver → 游戏执行。理解原版如何处理输入验证、资源检查、失败路径，然后尽量复用游戏原有逻辑（Designator、Job、Bill 等），不要凭空造轮子。

**2. 坐标参数统一左上→右下**

所有 MCP Tool 的区域坐标参数使用 `pos_x/pos_y`（左上角）→ `end_x/end_y`（右下角）模式，禁止使用中心点+半径/宽高向外扩展的 API 设计。参考 `designate_mine` 的实现。
- `pos_x`/`pos_y` — 必填，区域起始角
- `end_x`/`end_y` — 可选，区域结束角（不提供则只操作单格）

**3. 用 thingIDNumber 精确定位 Pawn/物品**

所有涉及殖民者（Pawn）或物品（Thing）的操作，参数统一使用 `thingIDNumber`（int）而非名称字符串匹配。名称有重名、翻译、截断风险，ID 唯一且稳定。
- 殖民者：`PawnsFinder.AllMaps_FreeColonistsSpawned.FirstOrDefault(c => c.thingIDNumber == id)`
- 目标/物品：`map.mapPawns.AllPawnsSpawned.FirstOrDefault(p => p.thingIDNumber == id)` 或 `map.listerThings.AllThings.FirstOrDefault(t => t.thingIDNumber == id)`
- 工具之间传递引用时优先输出 `thingIDNumber`，让 LLM 在后续调用中精确回传
- 参考实现：`Tool_ArrestPawn.cs`、`Tool_AttackPawn.cs`、`Tool_EquipPawn.cs`
