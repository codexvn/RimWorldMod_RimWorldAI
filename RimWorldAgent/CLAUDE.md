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
│   ├── Data/                    ★ 数据抽象层 — IDbStore + IConversationStore (ConversationEntry / MemoryConvStore / SqliteConvStore / UiHistoryFormatter)
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

| 模式 | 进程 | IDbStore | IConversationStore | IGameStateProvider |
|------|------|----------|-------------------|-------------------|
| **EXE** | `RimWorldAgent.exe` | `JsonDbStore`（JSON 文件） | `SqliteConversationStore`（SQLite WAL） | `RemoteGameStateProvider`（MCP 推送 + 查询） |
| **MOD** | RimWorld 加载 DLL | `ScribeDbStore`（Scribe_Values） | `MemoryConversationStore`（List+lock） | `DirectGameStateProvider`（TickManager 直读） |

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
├─ 优先 0: 冷启动检测 — HasEverSent=false 时 get_game_speed 检查游戏就绪 → RunAgent
├─ 弹框扫描 (2500 tick)
├─ 状态检测 (120 tick, 仅空闲)
│   ├─ 暂停提醒 (>30s / 每 60s)
│   └─ 早报防抖 (GameHour>=6, 每天只发一次)
├─ 优先 1: 中断处理 → 等待 AgentLoop 中 SendAbort
├─ 优先 2: 中断 + 空闲 → 立即启动新会话
├─ 优先 3: 每日 PLAN (ShouldMorningReport) → EnterPlanPhase + PauseForPlanning → RunAgent
└─ 优先 4: 定期 ACT (ShouldWake) → RunAgent
```

### 协议 (Protocol)

项目有 4 个消息层，每层有独立的消息格式。

---

#### 层 1：CcbWebSocket ↔ Companion (WS :19998)

仅 4 种消息，C# `SendChat`/`SendAbort` 直发顶层 type，不经过包装。

| 方向 | type | 字段 | 说明 |
|------|------|------|------|
| C# → | `chat` | `text` (string), `session` ("bus"\|"system"), `thinking` ({mode,effort,tokens?}) | 用户消息或系统 prompt |
| C# → | `abort` | (无) | 中断当前 SDK 会话 |
| ← C# | `hello-ok` | (无) | 握手确认 |
| ← C# | `assistant` | `message.content[]` (text/tool_use/thinking), `usage`, `model`, `stop_reason` | 完整 AI 回复 |
| ← C# | `stream_event` | `event` (content_block_start/delta), `index` | 流式增量（text_delta/thinking_delta） |
| ← C# | `result` | `subtype` (success/error_*), `stop_reason`, `duration_ms`, `usage`, `num_turns` | 会话结束 |
| ← C# | `system` | `subtype` (init/compact_boundary/status/...), 各子类型字段 | 系统消息 |
| ← C# | `user` | `message.content[]`, `parent_tool_use_id`, `isSynthetic` | SDK 回显的用户消息 |
| ← C# | `aborted` | (无) | 中断确认 |

C# 侧：`CcbWebSocket.ReceiveLoop` → `SdkMessage.FromJson` → `OnSdkMessage` 事件 → `SdkMessageParser` → `UiMessage`。

---

#### 层 2：UIMessageBus (WS :19999)

##### Agent → UI（推送）

所有消息继承 `UiMessage` 基类，`ToJson()` 序列化后 WS 广播。

| type | C# 类 | 字段 | 说明 |
|------|-------|------|------|
| `text_delta` | `UiTextDelta` | `text` | 流式文本增量（空串=新 block 开始） |
| `thinking_delta` | `UiThinkingDelta` | `thinking` | 流式思考增量（空串=新 block 开始） |
| `text_block` | `UiTextBlock` | `text` | 完整文本块 |
| `tool_call` | `UiToolCall` | `id`, `name`, `input` | 工具调用请求（stream_event block_start{tool_use} 推送名称，assistant 推送完整 input） |
| `tool_result` | `UiToolResult` | `id`, `isError`, `durationMs`, `content?` | 工具执行结果（从 SDK user 消息提取） |
| `result` | `UiResult` | `subtype`, `stop_reason` | 会话结束 |
| `aborted` | `UiAborted` | (无) | 中断确认 |
| `system_init` | `UiSystemInit` | `model`, `session_id` | SDK 初始化信息 |
| `error` | `UiError` | `error` | 错误消息 |
| `user` | `UiUser` | `text` | 用户消息回显 |
| `system` | `UiSystem` | `text` | 系统消息（暂停提醒等） |
| `budget_status` | `UiBudgetStatus` | `used`, `limit`, `action`, `cacheRead`, `totalInput`, `cacheCreate` | Token 预算状态 |
| `agent-status` | `UiAgentStatus` | `role` | Agent 阶段状态（PLAN / ACT / 休眠） |

##### UI → Agent（客户端消息）

| type | 字段 | 说明 |
|------|------|------|
| `chat` | `text`, `thinking?` ({mode,effort,tokens?}) | 用户发送消息 |
| `abort` | (无) | 中断当前会话 |
| `history` | `n` | 请求最近 N 条历史消息（初始加载） |
| `history_before` | `before_id`, `n` | 请求指定 ID 之前的更早消息（向上滚动） |

C# 侧：`UIMessageBus.OnMessage` → `OnChat`/`OnAbort`/`OnHistory`/`OnHistoryBefore` 事件 → `AgentLoop.WireUIMessageBus`。
`IConversationStore` 由 EXE/MOD 在 `WireUIMessageBus` 前注入 `AgentLoop.ConversationStore`。
录制点（**C# 侧统一，SDK 不回传 user echo**）：
- `OnChat` → `RecordUserMessage` + `PushUiMessage(User)`（用户消息：落盘+推送）
- `SdkMessageParser.ParseAssistant` → `OnAssistantContent` → `RecordAssistantMessage`（AI 回复 text+thinking）
- `SdkMessageParser` tool_use block → `OnToolCallRecorded` → `RecordToolCall`
- `AgentLoop.OnToolUse` (Stopwatch 计时) + `SdkMessageParser` tool_result block → `OnToolResultRecorded` → `RecordToolResult(isError, durationMs, content)`
- `AgentLoop` 静态构造 `OnDisplayMessage` → 过滤 `system`/`error` → `RecordSystemMessage`
- `RunSessionAsync` 发送 System Prompt 后 → `RecordSystemMessage` + `PushUiMessage(System)`
- `WireEvents.OnGameEvent` 游戏事件 → `PushUiMessage(User)` + `RecordUserMessage`（与 SDK 文本一致）

新客户端连接：`OnClientConnected` → 推送 `agent-status` + `budget_status` 初始状态。
阶段变化：`AgentOrchestrator.OnStatusChanged` → `UiAgentStatus` WS 广播。

---

#### 层 3：SdkMessage（C# 内部类型）

`SdkMessage` 是 C# 内部类型化消息模型，对齐 `@anthropic-ai/claude-agent-sdk` `coreSchemas.ts`。

`FromJson(rawJson)` 工厂：Parse JSON → type dispatch → 子类构造 → `ValidateFields` 检测多余字段。

```
SdkMessage (abstract)
├── SdkAssistantMessage  { Content[], Usage, Model, StopReason, Error }
├── SdkStreamEventMessage { ParentToolUseId, Index, Event }
├── SdkResultMessage      { Subtype, IsError, NumTurns, DurationMs, Usage, Result, TotalCostUsd }
├── SdkSystemMessage      { Subtype, Model, SessionId, ClaudeCodeVersion, Tools[], Skills[], McpServers[] }
├── SdkUserMessage        { Content[], ParentToolUseId, IsSynthetic, Priority }
├── SdkAbortedMessage     { }
├── SdkHelloOkMessage     { }
└── SdkUnknownMessage     { Type, Root }  ← 未知类型日志告警
```

---

#### 层 4：MCP（Agent ↔ 游戏）

| 方向 | 协议 | 说明 |
|------|------|------|
| Agent → 游戏 | HTTP POST `/mcp` | JSON-RPC `tools/call` 调用游戏工具 |
| 游戏 → Agent | SSE `GET /sse` | `game/tick` 推送 + `game/notification` 事件 |
| SDK → Agent | HTTP POST `:9878/mcp` | SDK `tools/call` → Agent MCP → ProxyToolProvider → 游戏 MCP |

SDK 工具调用不经过 companion。

---

#### 完整流转示例（用户发消息 → AI 回复）

```
1. UI WS → UIMessageBus  {"type":"chat","text":"你好","thinking":{"mode":"default","effort":"medium"}}
2. UIMessageBus → AgentLoop.OnChat → IConversationStore.RecordUserMessage("你好")
3. AgentLoop → CcbWS.SendAbort()  →  companion  {"type":"abort"}
4. AgentLoop → CcbWS.SendChat()   →  companion  {"type":"chat","text":"你好","session":"bus","thinking":{...}}
5. companion → SDK: inputStream.enqueue({type:'user',message:{role:'user',content:'你好'}})
6. SDK → companion: {type:'stream_event',event:{type:'content_block_start',content_block:{type:'thinking'}}}
7. companion → C#: busBroadcast(JSON) → CcbWS.ReceiveLoop → ProcessMessage
8. ProcessMessage → SdkMessage.FromJson → OnSdkMessage → SdkMessageParser → UiThinkingDelta("")
   → UIMessageBus.PushUiMessages → WS 广播 → UI 创建 thinking 面板
9. SDK → companion: {type:'stream_event',event:{type:'content_block_delta',delta:{type:'thinking_delta',thinking:'考虑...'}}}
   → SdkMessageParser → UiThinkingDelta("考虑...") → UI 追加思考文本
10. SDK → companion: {type:'stream_event',event:{type:'content_block_start',content_block:{type:'text'}}}
    → UiTextDelta("") → UI 关闭 thinking、创建 agent 面板
11. SDK → companion: {type:'stream_event',event:{type:'content_block_start',content_block:{type:'tool_use'},id:'...',name:'get_game_speed'}}
    → SdkMessageParser → UiToolCall(id,name,"") → UI 渲染工具卡片
12. SDK → companion: {type:'assistant',message:{content:[{type:'tool_use',id:'...',name:'get_game_speed',input:{...}}]}}
    → SdkMessageParser → UiToolCall(id,name,input) → UI 更新工具卡片 input + OnToolCallRecorded → RecordToolCall
13. SDK → companion: {type:'assistant',message:{content:[{type:'text',text:'...'}]}}
    → SdkMessageParser → OnAssistantContent(text,thinking,runId,agentType) → RecordAssistantMessage
14. SDK MCP → Agent MCP :9878 → ProxyToolProvider → 游戏 MCP :9877 → 工具执行
15. SDK → companion: {type:'user',message:{content:[{type:'tool_result',tool_use_id:'...',is_error:false,content:'...'}]}}
    → SdkMessageParser → UiToolResult + OnToolResultRecorded → RecordToolResult → UI 显示工具结果
16. SDK → companion: {type:'stream_event',...text_delta...} → UiTextDelta → UI 流式渲染正文
17. SDK → companion: {type:'result',subtype:'success'} → UiResult → UI 结束标记
```

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
| **SdkMessageParser** | `Core/AgentRuntime/SdkMessageParser.cs` | SdkMessage → UiMessage 转换（typed switch）。stream_event tool_use → UiToolCall，user tool_result → UiToolResult，assistant text 不重复推送（stream 已推） |
| **AgentLoop** | `Core/AgentRuntime/AgentLoop.cs` | WireUIMessageBus — SDK↔UiMessage 双向中继 + 预算检查 + 用户消息回显 + 历史查询/录制 + 初始状态推送 + OnClientConnected/OnHistory/OnHistoryBefore/OnAssistantContent/OnToolCallRecorded/OnToolResultRecorded 订阅。RunSessionAsync — 会话生命周期 + System Prompt 录制 |
| **UIMessageBus** | `Core/UIMessageBus.cs` | 纯 UiMessage WS 广播 + 客户端消息接收（不引用 SDK 类型）。单条 PushUiMessage / 批量 PushUiMessages，OnChat/OnAbort/OnHistory/OnHistoryBefore/OnClientConnected 事件 |
| **IConversationStore** | `Core/Data/IConversationStore.cs` | 多轮对话持久化抽象 — RecordUserMessage / RecordAssistantMessage / RecordSystemMessage / RecordToolCall / RecordToolResult / GetRecent / GetBefore / GetAt |
| **ConversationEntry** | `Core/Data/ConversationEntry.cs` | 会话条目数据模型 — User/Assistant/System/ToolCall/ToolResult 五种角色 + tool 扩展字段 |
| **SqliteConversationStore** | `Core/Data/SqliteConversationStore.cs` | SQLite WAL 持久化（EXE），自动建表 + ALTER TABLE 迁移，参数化查询 |
| **MemoryConversationStore** | `Core/Data/MemoryConversationStore.cs` | 纯内存存储（MOD），List+lock |
| **UiHistoryFormatter** | `Core/Data/UiHistoryFormatter.cs` | ConversationEntry → 前端 history_response / history_before_response JSON |

UI 模组 `RimWorldAgentUI` 通过 WebSocket 连接 UIMessageBus，不引用 Agent 项目。

### SdkMessage 类型层次

协议来源：`@anthropic-ai/claude-agent-sdk` `coreSchemas.ts`（Zod 模式）。

`FromJson(rawJson)` 工厂：Parse JSON → type dispatch → 子类构造 → `ValidateFields` 检测多余字段。
未知字段记录 `[WARN]` 但**不拒绝**消息；已知字段静默通过。

```
SdkMessage (abstract)
├── SdkAssistantMessage   { ParentToolUseId, Error, Content[], Usage?, Model, StopReason, StopSequence }
├── SdkStreamEventMessage  { ParentToolUseId, Index, Event }
├── SdkResultMessage       { Subtype, StopReason, IsError, NumTurns, DurationMs, DurationApiMs, Result, TotalCostUsd, Usage? }
├── SdkSystemMessage       { Subtype, Model?, SessionId?, ClaudeCodeVersion?, PermissionMode?, Cwd?, ApiKeySource?, Tools[], Skills[], McpServers[] }
├── SdkUserMessage         { ParentToolUseId, IsSynthetic, Priority, Content[] }
├── SdkAbortedMessage      { }
├── SdkHelloOkMessage      { }
└── SdkUnknownMessage      { Type, Root }  ← 未知类型不可识别时记录日志
```

#### 各类型字段详情

**SdkAssistantMessage** — AI 完整回复。
| 字段 | 类型 | 说明 |
|------|------|------|
| ParentToolUseId | string? | 父工具调用链 ID，顶层为 null |
| Error | string? | API 错误类型（authentication_failed / rate_limit / server_error …) |
| Content | List\<SdkContentBlock\> | text / tool_use / thinking 块 |
| Usage | SdkUsage? | Token 统计（已消费→TokenUsageTracker.Record） |
| Model | string? | 模型标识符 |
| StopReason | string? | end_turn / max_tokens / stop_sequence / tool_use |
| StopSequence | string? | 触发停止的序列文本 |
| [known] | context_management | SDK 发出，当前未解析使用 |

**SdkStreamEventMessage** — 流式增量事件。
| 字段 | 类型 | 说明 |
|------|------|------|
| ParentToolUseId | string? | 父工具调用链 ID |
| Index | int? | 内容块索引 |
| Event | SdkStreamEvent? | content_block_start / delta / stop |
| [known] | ttft_ms | ★ 首 Token 延迟 (ms)，SDK 发出，**当前未解析使用** |

**SdkResultMessage** — 会话结束。
| 字段 | 类型 | 说明 |
|------|------|------|
| Subtype | string | success / error_during_execution / error_max_turns / error_max_budget_usd / error_max_structured_output_retries |
| StopReason | string? | 停止原因 |
| IsError | bool | 是否错误结束 |
| NumTurns | int? | ★ 会话总轮次，**当前未消费** |
| DurationMs | long? | ★ 会话总耗时 (ms)，**已解析但 Record() 硬编码传 0** |
| DurationApiMs | long? | ★ API 纯耗时 (ms)，**当前未消费** |
| Result | string? | AI 最终回复文本 |
| TotalCostUsd | double? | ★ 总费用 (USD)，**已解析但无存储路径** |
| Usage | SdkUsage? | 会话聚合 Token 统计（**当前未消费**；per-assistant Usage 已消费） |
| [known] | modelUsage, permission_denials, errors, structured_output, fast_mode_state | SDK 发出，当前未解析 |

**SdkSystemMessage** — 系统生命周期事件。
| Subtype | 携带字段 | 消费情况 |
|---------|---------|---------|
| `init` | Model, SessionId, ClaudeCodeVersion, PermissionMode, Cwd, ApiKeySource, Tools[], Skills[], McpServers[], slash_commands, output_style, agents, plugins, betas, fast_mode_state, analytics_disabled, product_feedback_disabled, memory_paths | Model→TokenUsageTracker.CurrentModel, Model+SessionId→UiSystemInit UI 推送 |
| `status` | status ("compacting"\|null), permissionMode | **状态变更，当前未消费** |
| `compact_boundary` | compact_metadata {trigger, pre_tokens, preserved_segment} | ★ 上下文压缩分界，**当前未消费** |
| 其他 (14+) | task_notification, task_started, task_progress, session_state_changed, api_retry, hook_started/progress/response, files_persisted, elicitation_complete, local_command_output, post_turn_summary | **全部未消费** |

**SdkUserMessage** — SDK 回显的用户消息。
| 字段 | 类型 | 说明 |
|------|------|------|
| ParentToolUseId | string? | 父工具调用链 ID |
| IsSynthetic | bool | 是否 SDK 内部合成 |
| Priority | string? | now / next / later |
| Content | List\<SdkContentBlock\> | text / tool_result 块 |
| [known] | timestamp, isReplay, tool_use_result | SDK 发出，当前未解析 |

**辅助类型：**
| 类型 | 字段 | 说明 |
|------|------|------|
| SdkContentBlock (abstract) | BlockType | text / tool_use / thinking / tool_result |
| SdkTextBlock | Text | 文本内容 |
| SdkToolUseBlock | Id, Name, Input | 工具调用（Id 关联 tool_result） |
| SdkThinkingBlock | Thinking, Signature? | 思考过程 |
| SdkToolResultBlock | ToolUseId?, IsError, Content | 工具执行结果 |
| SdkStreamEvent | EventType, Index, BlockType?, Text?, Thinking?, PartialJson?, ToolUseId?, ToolName? | 流式增量（工厂方法 TextDelta/ThinkingDelta/InputJsonDelta/TextBlockStart/ThinkingBlockStart/ToolUseBlockStart） |
| SdkUsage | InputTokens, OutputTokens, CacheReadInputTokens?, CacheCreationInputTokens? | Token 统计 |
| SdkMcpServerInfo | Name, Status | MCP 服务器连接状态（connected/failed/needs-auth/pending/disabled） |

#### 已知但未解析的性能指标

| ★ 高价值 | 所在消息 | 字段 | 当前状态 |
|----------|---------|------|---------|
| 首 Token 延迟 | stream_event | ttft_ms | 仅 known，不解析 |
| 会话总耗时 | result | DurationMs | 已解析，但 Record() 传 0 |
| 会话 API 耗时 | result | DurationApiMs | 已解析，未消费 |
| 总费用 | result | TotalCostUsd | 已解析，无存储路径 |
| 会话轮次 | result | NumTurns | 已解析，未消费 |
| 压缩通知 | system(status) | status="compacting" | 仅 known，不处理 |


### Plan/Act 阶段

- `enter_plan()` — 暂停游戏，进入 PLAN
- `enter_act()` — 恢复游戏，进入 ACT
- `GamePaceController`：`toggle_pause` 幂等，直接调 MCP，不维护本地暂停缓存

### 工具耗时

`AgentLoop.OnToolUse` Stopwatch 计时 → `_toolDurations[toolId] = elapsedMs` 暂存。
SDK echo tool_result → `OnToolResultRecorded` → `TryRemove(toolId)` 读耗时 → `RecordToolResult(isError, dur, content)` 合并落盘。

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

立即中断 + 通知三路统一（SDK/UI/DB 文本一致）：

```
游戏事件 → StripRichTags → summary = "[Severity/Category] text"
→ RequestInterrupt(summary):
    InterruptSummary += summary                         // 累积
    SendAbort()                                         // 杀旧 session
    SendChat(ChatChannel.Bus, notifyText)               // 立即通知 SDK
→ WireEvents:
    PushUiMessage(User, notifyText)                     // UI (user 模式)
    RecordUserMessage(notifyText)                       // DB

notifyText = InterruptPromptPrefix + "\n" + summary + "\n" + InterruptPromptSuffix
```

`InterruptPromptPrefix = "## 事件通知"`, `InterruptPromptSuffix = "以上是游戏内发生的新事件..."` 为 `AgentOrchestrator` 常量。
SDK/UI/DB 三路使用完全相同的 `notifyText`，`ChatChannel.Bus` (user) 模式。
Companion abort 采用**消息缓冲**：abort 触发 `buffering=true`，`startNewSession` 重建 session 后回放缓冲消息。

### 工具耗时

`AgentLoop.OnToolUse` Stopwatch 计时 → `_toolDurations[toolId] = elapsedMs` 暂存。
SDK echo tool_result → `OnToolResultRecorded` → `TryRemove(toolId)` 读耗时 → `RecordToolResult(isError, dur, content)` 合并落盘。

### 冷启动

新游戏/新连接：`HasEverSent=false` → `get_game_speed` 检测就绪 → `EnterPlanPhase` + `PauseForPlanning` 强制暂停 → `RunAgent(isPlan: true)`。
AI 先分析殖民地状态、制定计划，再自行调用 `enter_act()`。

### 提醒（Reminder class，BuildModeSuffixAsync 注入）

所有提醒统一为 `Reminder` 类：`Name` / `Threshold` / `Template` / `Condition` / `OnFire`。
`Tick()` 自动管理计数、触发、重置。`Reminders` 列表对外只读，供 UI 消费。

| 提醒 | 阈值 | 触发条件 | 文案 |
|------|------|---------|------|
| ACT 暂停 | 5 | ACT + 游戏暂停 | enter_act(speed) 恢复 |
| ACT 执行过久 | 10 | ACT 阶段 | enter_plan() 审视进度 |
| PLAN 停留过久 | 10 | PLAN 阶段 | 制定计划后 enter_act() |
| 通知堆积 | 5 | _notifReceivedCount > 5 | get_notifications 查看 |
| 任务未完成 | 10 | _tasks.Count > 0 | 列出未完成任务 |
| 未使用 task 工具 | 15 | 无条件（调 TaskCreate/Update 归零） | 提示使用 TaskCreate/TaskUpdate |
| 工具输出 | 20 | 无条件（调非只读工具归零） | 总结观察和下一步计划 |
| 世界摘要刷新 | 30 | 无条件（调 get_world_summary 归零） | 重新获取全局状态 |

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

## 设计文档

| 文档 | 内容 |
|------|------|
| `design/agent-runtime.md` | Agent Runtime 架构 |
| `design/conversation-history.md` | 会话历史持久化 — SQLite 表结构 + WS 协议 + 线程安全 + 向上滚动分页 |

## 运行

**EXE**: `dotnet run --project RimWorldAgent/RimWorldAgent.csproj`
**MOD**: 加载存档自动启动，Ctrl+Shift+C 聊天窗
**Web 面板**: `http://127.0.0.1:19997`
