# RimWorldAgent

AI Colony Runtime — AI 自主管理殖民地。通过 MCP 协议连接 RimWorldMCP，集成 Claude Agent SDK。

**相关项目**:
- MCP Server: `../RimWorldMCP/`（游戏 Mod DLL，99+ Tool）
- MCP 协议库: `../SimpleMspServer/`（JSON-RPC + SSE Transport）
- CC Companion: `./cc-companion/`（Node.js，CCB 桥接）

## 项目结构

```
RimWorldAgent/
├── CLAUDE.md
├── RimWorldAgent.csproj       ← 单一项目 (net472, OutputType=Exe)
├── README.md
├── resource/                  ← MOD 元数据（构建时复制到根 publish）
│   ├── About/About.xml
│   └── Skills/*.md (13个)
├── Core/                      ← 共享逻辑
│   ├── AgentRuntime/
│   │   ├── AgentLoop.cs / AgentOrchestrator.cs / ToolDispatcher.cs
│   │   ├── IGameStateProvider.cs  ★ 游戏状态抽象接口（tick/paused/早报/wake）
│   │   ├── SdkMessageParser.cs    ★ SDK → UiMessage 转换
│   │   ├── InternalToolRegistry.cs ★ 内部工具注册 + Skill 加载（IToolProvider）
│   │   └── Tools/                 7 个内部工具 + ProxyToolProvider
│   ├── models/                  ★ 类型定义 — SdkMessage / UiMessage / ChatChannel
│   ├── Data/                    ★ 数据抽象层 — IDbStore + JsonDbStore (EXE) / ScribeDbStore (MOD)
│   ├── Mcp/                     MCP 客户端 + Agent MCP Server (:9878)
│   ├── CcbManager/              CCB 子进程管理 + CcbWebSocket + TokenUsageTracker
│   └── UIMessageBus.cs          ★ UI 总线 — Fleck WS :19999
├── Exe/                         ← EXE Loader
│   └── Program.cs               入口：JsonDbStore + RemoteGameStateProvider → AgentEngine 构造注入
├── Mod/                         ← MOD Loader (RimWorld 加载)
│   ├── GameComponent_RimworkAgent.cs   GameComponent 生命周期 + Agent 初始化 + UIMessageBus 启动
│   ├── CompanionInstaller.cs           npm install 管理 + Node.js 查找
│   ├── ScribeDbStore.cs                Token 数据 Scribe 持久化
│   ├── DirectGameStateProvider.cs      MOD 模式 — 直接从 Find.TickManager 读取 tick/paused
│   └── AgentModSettings.cs / RimWorldAgentMod.cs   Mod 设置 UI
├── cc-companion/                ← CCB 桥接 (Node.js, npm start)
└── publish/                     ← 构建输出 (git ignored)
```

## 架构

### 零引用设计

RimWorldAgent 和 RimWorldMCP **互不引用**，仅通过 MCP 协议通信。

```
RimWorldAgent (EXE/MOD)             RimWorldMCP (Mod DLL)
┌──────────────────────┐           ┌──────────────────────┐
│ AgentEngine          │   HTTP    │ MCP Server :9877     │
│ McpClient ───────────── POST ───→ /mcp (tools/call)    │
│ McpClient ───────────── GET ────→ /sse (tick 推送)     │
│ AgentMcpServer :9878  ←───────── SDK tools/call         │
│ CcbManager ── spawn ──→ cc-companion (Node.js :19998)  │
└──────────────────────┘           └──────────────────────┘
```

### 两种启动模式

| 模式 | 进程 | IDbStore | IGameStateProvider |
|------|------|----------|-------------------|
| **EXE** | `RimWorldAgent.exe` | `JsonDbStore`（JSON 文件） | `RemoteGameStateProvider`（MCP 推送 + 查询） |
| **MOD** | RimWorld 加载 DLL | `ScribeDbStore`（Scribe_Values） | `DirectGameStateProvider`（TickManager 直读） |

### DB 存储抽象 — IDbStore

Token 数据存储接口（`Core/Data/IDbStore.cs`）。

```csharp
public interface IDbStore
{
    string CurrentModel { get; set; }
    void Record(string model, long input, long output, long cacheRead, long cacheCreate, long durationMs);
    void RecordToolResult(bool isError);
    // 累计属性
    long TotalInputTokens { get; } long TotalOutputTokens { get; }
    long TotalCacheReadTokens { get; } long TotalCacheCreateTokens { get; }
    long TotalAllTokens { get; } int TotalRequests { get; }
    int TotalToolSuccess { get; } int TotalToolFailure { get; }
    long TotalDurationMs { get; }
    Dictionary<string, TokenModelUsage> GetModelUsages();
    void Clear(); event Action? OnRecorded;
    // 持久化
    string GetCompactDisplay(long budgetLimit);
    string GetSummary();
}
```

`TokenUsageTracker` 保留为静态门面，`AgentEngine.InitAsync` 中 `TokenUsageTracker.Db = _dbStore` 注入。

### 游戏状态抽象 — IGameStateProvider

```csharp
public interface IGameStateProvider
{
    int GameTick { get; }
    int GameDay { get; }
    int GameHour { get; }
    bool IsPaused { get; }
    bool ShouldMorningReport();       // GameHour>=6 && GameDay>_lastMorningDay
    void MarkMorningReportSent();
    bool ShouldWake(int intervalGameHours);
    Task SyncGameStatusAsync();       // Remote=调MCP, Direct=读TickManager
}
```

| 实现 | tick 来源 | paused 来源 |
|------|----------|------------|
| `RemoteGameStateProvider` | MCP `OnGameTick` 推送 | MCP `get_game_speed` 查询 |
| `DirectGameStateProvider` | `Find.TickManager.TicksGame` | `Find.TickManager.Paused` |

`AgentEngine` 构造注入两者，`TickAsync` 通过 `_gameState.SyncGameStatusAsync()` 统一刷新。无类型判断。

### Agent 调度循环 (TickAsync, ~2s)

```
SyncGameStatusAsync() → 刷新 tick + paused
├─ 弹框扫描 (2500 tick)
├─ 状态检测 (120 tick, 仅空闲)
│   ├─ 暂停提醒 (>30s / 每 60s)
│   └─ 早报防抖 (GameHour>=6, 每天只发一次)
├─ 优先 1: 中断处理 → 等待 AgentLoop 中 SendAbort
├─ 优先 2: 中断 + 空闲 → 立即启动新会话
├─ 优先 3: 每日 PLAN (ShouldMorningReport) → EnterPlanPhase + PauseForPlanning → RunAgent
└─ 优先 4: 定期 ACT (ShouldWake) → RunAgent
```

### 消息协议

#### CcbWebSocket ↔ Companion (WS :19998)

```
C# → companion:  {"type":"chat", "text":"...", "session":"bus", "thinking":{mode,effort,tokens}}
                 {"type":"abort"}
companion → C#:  {"type":"hello-ok"}
                 SDK 消息 (assistant / stream_event / result / system / user / aborted)
```

仅 4 种消息。不经过 `type=event` 包装，C# `SendChat`/`SendAbort` 直发顶层 type。

#### UIMessageBus (WS :19999)

```
Agent → UI:       UiMessage JSON (text_delta / text_block / tool_call / result / aborted / system_init / error / user / budget_status)
UI → Agent:       {"type":"chat", "text":"...", "thinking":{mode,effort,tokens}}
                  {"type":"abort"}
```

#### 工具调用通路

```
SDK → mcp__agent__* → Agent MCP (:9878) → ProxyToolProvider → 游戏 MCP (:9877) → 返回结果 → SDK
```
**工具调用不经过 companion**，SDK 直接调 MCP 端点。

### 数据流

```
                    CC Companion (Node.js :19998)
                         │
            chat/abort  │  SDK 消息 (assistant/stream_event/result/...)
                         │
                  CcbWebSocket (C#)
                    │           │
          SdkMessage.FromJson  SendChat/Abort
                    │           │
              ┌─────┴───────────┴─────┐
              │  AgentLoop.WireUIMessageBus  │
              │                         │
    SdkMessageParser              UIMessageBus.OnChat/Abort
    (SdkMessage → UiMessage)           │
              │               PushUiMessage(User)
              ▼                         │
       UIMessageBus.PushUiMessages ───────┘
              │
      UiMessage WS :19999 广播
         │                │
    ┌────┘                └────┐
    ▼                         ▼
 WebUI :19997           Dialog_AiChat
 (BridgeClient WS)      (BridgeClient WS)
```

| 层 | 文件 | 职责 |
|----|------|------|
| **CcbWebSocket** | `Core/CcbManager/CcbWebSocket.cs` | SDK WS 客户端，SendChat/Abort 直发顶层 type，Receive → SdkMessage.FromJson → OnSdkMessage |
| **SdkMessage** | `Core/models/SdkMessage.cs` | 类型化消息模型，与 `@anthropic-ai/claude-agent-sdk` coreSchemas.ts 对齐。抽象基类 + 8 子类型，FromJson 工厂 + ValidateFields 校验 |
| **SdkMessageParser** | `Core/AgentRuntime/SdkMessageParser.cs` | SdkMessage → UiMessage 转换（typed switch） |
| **AgentLoop** | `Core/AgentRuntime/AgentLoop.cs` | WireUIMessageBus — SDK↔UiMessage 双向中继 + 预算检查 + 用户消息回显。RunSessionAsync — 会话生命周期 |
| **UIMessageBus** | `Core/UIMessageBus.cs` | 纯 UiMessage WS 广播 + 客户端消息接收（不引用 SDK 类型）。单条 PushUiMessage / 批量 PushUiMessages |
| **ChatChannel** | `Core/models/ChatChannel.cs` | 聊天频道常量 Bus/System（C# 与 TS companion protocol.ts 对齐） |

UI 模组 `RimWorldAgentUI` 通过 WebSocket 连接 UIMessageBus，不引用 Agent 项目。

### SdkMessage 类型层次

```
SdkMessage (abstract)
├── SdkAssistantMessage   { Content[], Usage, Model, StopReason }
├── SdkStreamEventMessage  { ParentToolUseId, Index, Event }
├── SdkResultMessage       { Subtype, IsError, NumTurns, DurationMs, Usage }
├── SdkSystemMessage       { Subtype, Model, SessionId, Tools[], Skills[], McpServers[] }
├── SdkUserMessage         { Content[], ParentToolUseId, IsSynthetic }
├── SdkAbortedMessage      { }
├── SdkHelloOkMessage      { }
└── SdkUnknownMessage      { Type, Root }  ← 未知类型不可识别时报错日志
```

所有子类字段与 TS 协议 `coreSchemas.ts` 一致，FromJson 构造时 `ValidateFields` 检测多余未知字段。

### Plan/Act 阶段

- `enter_plan()` — 暂停游戏，进入 PLAN
- `enter_act()` — 恢复游戏，进入 ACT
- `GamePaceController`：`toggle_pause` 幂等，直接调 MCP，不维护本地暂停缓存

### Tool Result Suffix — BuildModeSuffixAsync

每次工具调用结果末尾注入模式+速度+提醒：

```
---
当前模式: ACT

<system-reminder> PLAN 停留提醒 (20 次后触发) </system-reminder>
<system-reminder> ACT 暂停提醒 (5 次后触发) </system-reminder>
<system-reminder> 任务未完成提醒 (10 次后触发，最多显示 5 个) </system-reminder>
```

### Internal Tools（AgentMCP :9878）

| Tool | 说明 |
|------|------|
| `get_skills` / `active_skill` | 领域技能 |
| `enter_plan` / `enter_act` | Plan/Act 切换 |
| `set_tool_result_suffix` | 一次性后缀注入 |
| `read_memory` / `update_memory` | 记忆文件读写（替代 SDK Write/Edit） |

### Proxy 工具代理

`ProxyToolProvider`（`Core/AgentRuntime/ProxyToolProvider.cs`）实现 `SimpleMspServer.Mcp.IToolProvider`，将 100+ 游戏 MCP 工具代理到 Agent MCP Server (:9878)。

- `CcbManager` 写入的 `.mcp.json` 只含 `agent` 一个端点
- SDK 所有工具调用走 `mcp__agent__*`
- 每个工具结果末尾注入 `BuildModeSuffixAsync()` 后缀
- SDK `disallowedTools` 已加 `Write`/`Edit`，AI 用 `update_memory` 代替

### 中断机制

所有 MCP 事件触发 `RequestInterrupt(summary) → SendAbort()`，中断后新会话 prompt 顶部注入通知 + "如有必要可以暂停游戏"。

### 线程安全

CcbWebSocket.ReceiveLoop 后台线程 → `ChatDisplayState.EnqueueUiEvent` 入队 → `Dialog_AiChat.DoWindowContents` 首行 `DrainEvents()` UI 线程消费。

### 缓存命中率

```
cacheHitRate = TotalCacheReadTokens / (TotalInputTokens + TotalCacheReadTokens + TotalCacheCreateTokens) * 100
```

## 开发

```bash
cd F:\RiderProjects\RimWorldMCP
dotnet build RimWorldAI.sln
```

- 不引用 RimWorldMCP（零编译依赖）
- 引用 SimpleMspServer（MCP 协议共享库）
- 游戏数据通过 MCP HTTP 获取，操作通过 MCP Tool 调用
- 异常处理：禁止空 catch，`OperationCanceledException` 除外

### 发布布局

```
publish/RimWorldAgent/1.6/Assemblies/
├── RimWorldAgent.dll / RimWorldAgent_Standalone.exe
├── SimpleMspServer.dll
├── cc-companion/    ← 与 DLL 同级
├── Skills/
└── ... (NuGet deps)
```

## 运行

**EXE**: `dotnet run --project RimWorldAgent/RimWorldAgent.csproj`
**MOD**: 加载存档自动启动，Ctrl+Shift+C 聊天窗
**Web 面板**: `http://127.0.0.1:19997`
