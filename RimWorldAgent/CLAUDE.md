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
│   ├── AgentRuntime/          Scheduler / AgentOrchestrator / AgentConfig / ContextBuilder / InternalTools / ToolDispatcher
│   ├── Data/                  ★ 数据抽象层 — ITokenStore + InMemory/LocalFile 实现
│   ├── Mcp/                   MCP 客户端 + Agent MCP Server (:9878)
│   └── CcbManager/            CCB 子进程管理 + CcbWebSocket + TokenUsageTracker
├── Exe/                       ← EXE Loader
│   └── Program.cs             入口：find CCB → spawn → connect MCP → Agent Main Loop
├── Mod/                       ← MOD Loader (RimWorld 加载)
│   ├── GameComponent_RimworldAgent.cs
│   └── UI/
│       ├── Dialog_AiChat.cs       聊天窗 (Ctrl+Shift+C)
│       ├── MapComponent_McpUI.cs  右下角按钮
│       ├── ChatStateTypes.cs      本地类型
├── cc-companion/              ← CCB 桥接 (Node.js, npm start)
└── publish/                   ← 构建输出 (git ignored)
```

## 架构

### 零引用设计

RimWorldAgent 和 RimWorldMCP **互不引用**，仅通过 MCP 协议通信。

```
RimWorldAgent (EXE/MOD)             RimWorldMCP (Mod DLL)
┌──────────────────────┐           ┌──────────────────────┐
│ AgentRuntime         │   HTTP    │ MCP Server :9877     │
│ McpClient ───────────── POST ───→ /mcp (tools/call)    │
│ McpClient ───────────── GET ────→ /sse (事件 SSE 推送)  │
│ AgentMcpServer :9878  ←───────── CCB SDK tools/call     │
│ CcbManager ── spawn ──→ cc-companion (Node.js WS :19999) │
└──────────────────────┘           └──────────────────────┘
```

### 两种启动模式

| 模式 | 进程 | 说明 |
|------|------|------|
| **EXE** | `RimWorldAgent.exe` | 独立进程，开发/测试用 |
| **MOD** | RimWorld 加载 DLL | 游戏内运行, GameComponent 驱动 |

### Agent 架构总览

单 Agent (Commander) 全权负责殖民地所有事务：策略规划、生产建造、战斗指挥、医疗管理。

调度优先级：中断请求 → 每日 PLAN → 定期 ACT 检查 (每 4 游戏小时)

所有通知（MCP 事件、弹框检测）均触发立即中断，通过 `SendAbort()` 打断当前 CCB 会话。每轮工具结果末尾追加当前模式+速度状态。

### Plan/Act 阶段

AI 通过工具显式控制游戏暂停/恢复：
- `enter_plan()` — 暂停游戏，进入规划阶段
- `enter_act()` — 恢复游戏，进入执行阶段
- Agent 休眠时自动恢复游戏（`GamePaceController.EnsureResumed`）

### Tool Result Suffix 双工通知

Agent 通过 MCP 工具设置一次性 suffix，MCP Server 在下一次工具结果末尾自动追加并清空。AI 在工具调用结果中即时看到通知。

- `set_tool_result_suffix(suffix)` — 设置一次性后缀，追加后自动清空

**NotisAgent 统一入口**：`AgentOrchestrator.NotisAgent(notification)` 封装双路逻辑：
- Agent 运行中 + SessionMcp 可用 → `set_tool_result_suffix`（AI 下次工具调用时看到）
- Agent 休眠或 MCP 不可用 → CcbWs 直接发送到 Companion（AI 立即收到）

内部工具通过 `AgentOrchestrator.SessionMcp` 转发到 MCP Server。MCP 侧用 `volatile string ToolResultSuffix` 存储，`ExecuteAsync` 中追加后立即清空。

### Internal Tools（Agent 端，通过 AgentMCP :9878 暴露给 CCB）

这些工具不在 RimWorldMCP 模块中，由 Agent 内部实现：

| Tool | 说明 | 实现 |
|------|------|------|
| `get_skills` | 列出可用领域技能 | `InternalToolRegistry` → `SkillRegistry` |
| `active_skill` | 激活获取 Skill 内容 | `InternalToolRegistry` → `SkillRegistry` |
| `enter_plan` / `enter_act` | Plan/Act 阶段切换 | `InternalToolRegistry` → `GamePaceController` |
| `set_tool_result_suffix` | 设置一次性工具结果后缀 | `InternalToolRegistry` → MCP Server |

### 中断机制

所有 MCP 事件和弹框检测均触发立即中断：
- `AgentOrchestrator.RequestInterrupt(summary)` — 标记中断 + 存储摘要 + `SendAbort()` 打断当前 CCB 会话
- 中断后新会话 prompt 顶部注入通知内容 + "如有必要可以暂停游戏"
- `NotisAgent()` 作为保底双工通知（suffix 注入或 Direct WS）

## 开发

### 构建

```bash
cd F:\RiderProjects\RimWorldMCP
dotnet build RimWorldAI.sln           # 全量构建
dotnet build RimWorldAgent/RimWorldAgent.csproj  # 单独 Agent
```

### 关键约束

- 不引用 RimWorldMCP（零编译依赖）
- 引用 SimpleMspServer（MCP 协议共享库）
- 游戏数据通过 MCP HTTP 获取
- 游戏操作通过 MCP Tool 调用
- 数据持久化通过 Core.Data/ 抽象层（Token），InMemory 和 LocalFile 两种实现

### 异常处理规范

**任何时候捕获异常都不允许忽略（禁止空 catch）**。每个 catch 必须记录日志，包含异常类型和原始错误信息。涉及外部调用（HTTP、WebSocket、文件 I/O、进程管理）时必须展开 `InnerException` 链输出完整堆栈。

**格式**：`$"[模块标识] 操作描述: {ex.GetType().Name}: {ex.Message}"`

**UnwrapException 辅助方法**（Core 层共享）：
```csharp
internal static string UnwrapException(Exception ex)
{
    var sb = new StringBuilder();
    while (ex != null)
    {
        if (sb.Length > 0) sb.Append(" → ");
        sb.Append($"{ex.GetType().Name}: {ex.Message}");
        ex = ex.InnerException;
    }
    return sb.ToString();
}
```

**允许精简**：`OperationCanceledException` 无需日志，保留空 catch

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

### MessageBus 双总线机制

Companion 通过 `cc-companion/bridge/message-bus.ts` 集中管理所有 WebSocket 广播消息，分为两条独立总线：

```
                    Companion
                    ┌──────────────────────────────────────┐
                    │                                      │
  C# (RimWorld) ──→│  onEvent  ──→ Game Bus ──→ Web 页面   │
                    │    │           colony-stats          │
                    │    │           budget-status         │
                    │    │           user (回显)            │
                    │    │           error                 │
                    │    │                                 │
                    │    └──→ inputStream.enqueue()        │
                    │              │                       │
                    │              ▼                       │
                    │         SDK query()                  │
                    │              │                       │
                    │              ▼                       │
                    │  processResponses ──→ Agent Bus ──→  │
                    │    assistant, stream_event,         │
                    │    result, system/init                │
                    │                                      │
                    └──────────────────────────────────────┘
                              ↓
                     Web 页面 + C# 客户端
```

| Bus | 数据来源 | 消息类型 | 消费者 |
|-----|---------|---------|--------|
| **Game Bus** | C# 游戏事件 → Companion onEvent | `colony-stats`, `budget-status`, `user`(回显), `error`, `model-info` | Web 页面, 游戏内 UI |
| **Agent Bus** | SDK query() → processResponses | `assistant`, `user`, `stream_event`, `result`, `system/init`, `aborted` | Web 页面, C# CCClient |

**关键设计**：
- 两个 Bus 走同一条 WebSocket 连接，通过 `MessageBus` 类型约束保证消息格式一致
- Game Bus 消息不经 SDK（零延迟、不消耗 Token），直接在 Companion 侧广播
- Agent Bus 消息由 `createResponseProcessor` 遍历 SDK AsyncIterator，逐条经 `publishSdkMessage()` 广播
- Companion 是两股流的**多路复用器**——接收端鉴别 `msg.type` 路由到对应处理器

### 连接流程

`CcbManager.SpawnCompanion(sessionId)` 在 Agent 启动时执行：
1. `StopExisting()` — 停止旧进程
2. `KillStaleByPidFile()` — 清理 `.pid` 残留
3. `StartCompanionProcess()` — 创建 `claude-sessions/rimworld-<sessionId>/` 目录，spawn `node --import tsx/esm companion/companion.ts --idle-timeout 30000`；config 通过环境变量传递，SDK 配置由用户 `.claude/settings.json` 提供，Windows 额外通过 Job Object 绑定子进程生命周期
4. `CcbWebSocket.Connect()` — WebSocket 握手（hello/hello-ok）

### 事件消费（SSE）

Agent 通过 `McpClient.SubscribeEvents(agentId)` 订阅 `GET /sse?agent=overseer`，接收游戏事件推送。事件格式：

```json
{"type":"event","category":"Combat","severity":"Critical","message":"大型突袭来袭"}
```

事件路由规则（已简化）：
- 所有事件均触发中断，不再区分优先级
- 弹框检测由 AgentEngine.TickAsync() 每 2500 tick 扫描

MCP 侧的事件分级表和 Harmony 拦截逻辑详见 `../RimWorldMCP/CLAUDE.md`。

### 参数覆盖顺序

```
用户 .claude/settings.json   ← 低优先级（API Key、Base URL、MCP 等）
        ↓
SDK Options (session.ts)     ← 中优先级（model、settingSources、cwd）
        ↓
环境变量 (ProcessStartInfo)   ← 高优先级（RIMWORLD_PROJECT_PATH、CCB_HOST、CCB_PORT、CCB_AUTH_TOKEN）
```

SDK 从用户本地 `.claude/settings.json` 读取 API Key、Base URL、MCP 服务、权限等配置。C# 不再写入 settings.json，完全沿用用户本地配置。

## 运行教程

### 模式一：EXE 自动模式（开发/测试）

```bash
# 终端 1：启动 companion（由 Agent 自动 spawn，或手动）
cd RimWorldAgent/cc-companion
npm install   # 首次
npm start

# 终端 2：启动 Agent
dotnet run --project RimWorldAgent/RimWorldAgent.csproj
```

### 模式二：MOD 游戏内模式

1. 启动 RimWorld，加载 RimWorldMCP + RimWorldAgent 两个 Mod
2. Agent 自动 spawn companion 进程，连接 MCP Server :9877
3. 打开聊天窗（Ctrl+Shift+C）即可与 AI 交互

### 聊天页面

浏览器打开 `http://127.0.0.1:19999/` 可查看实时对话、SDK 版本、模型、MCP 服务状态。发消息通过 RimWorld 游戏内聊天窗——聊天页面是只读面板。

### 日志查看

| 来源 | 怎么看 |
|------|--------|
| Companion 进程 | Agent `CcbManager` 注册 `OutputDataReceived`/`ErrorDataReceived`，输出到控制台 `[js]` 前缀 |
| SDK 内部 | `session.ts` 中 `stderr: (data) => process.stderr.write(\`[sdk] ${text}\`)` |
| Agent 日志 | `Console.Error` — `[Agent]` 前缀 |

### Token 预算

Token 预算执行在 MCP 侧，详见 `../RimWorldMCP/CLAUDE.md` 的 Token 预算系统章节。
