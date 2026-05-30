# RimWorldAgent

AI Colony Runtime — 多 Agent 自主管理殖民地。通过 MCP 协议连接 RimWorldMCP，集成 Claude Agent SDK。

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
│   ├── AgentRuntime/          Scheduler / TaskBoard / Memory / AgentOrchestrator / AgentConfig / ContextBuilder / InternalTools / ToolDispatcher
│   ├── Mcp/                   MCP 客户端 + Agent MCP Server (:9878)
│   └── CcbManager/            CCB 子进程管理 + CcbWebSocket
├── Exe/                       ← EXE Loader
│   └── Program.cs             入口：find CCB → spawn → connect MCP → Agent Main Loop
├── Mod/                       ← MOD Loader (RimWorld 加载)
│   ├── GameComponent_RimworldAgent.cs
│   └── UI/
│       ├── Dialog_AiChat.cs       聊天窗 (Ctrl+Shift+C)
│       ├── MapComponent_McpUI.cs  右下角按钮
│       ├── ChatStateTypes.cs      本地类型
│       └── TodoManager.cs         本地 TODO 管理
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

```
Overseer (策略, 每天/12h) → 全局摘要分析 → 发布 TaskBoard 目标
Economy (生产+军械, 每 2-6h) → 建造/制造/装备分配
Combat (战斗, L3 事件驱动) → 暂停→分析→部署→接敌→收尾→退出
Medic (医疗, 每天+战斗后) → 治疗/手术/仿生体
```

### Plan/Act 阶段

AI 通过工具显式控制游戏暂停/恢复：
- `enter_plan()` — 暂停游戏，进入规划阶段
- `enter_act()` — 恢复游戏，进入执行阶段
- Agent 休眠时自动恢复游戏（`GamePaceController.EnsureResumed`）

### Agent 切换与建议

- `switch_agent(role)` — 切换当前活跃 Agent，当前会话结束，目标 Agent 唤醒
- `advise_agent(role, advice)` — 给其他 Agent 提供建议，切换时自动附加在 Prompt 中
- 事件路由在 Agent 侧（`AgentOrchestrator.RouteEvent()`），MCP 只推送 Category+Severity

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
- TaskBoard/Memory 持久化为 JSON

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
                    │    │           todo-state            │
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
| **Game Bus** | C# 游戏事件 → Companion onEvent | `colony-stats`, `todo-state`, `budget-status`, `user`(回显), `error`, `model-info` | Web 页面, 游戏内 UI |
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

事件路由规则：
- **L3 Critical** → Combat Agent 立即唤醒
- **L2/L1** → 加入对应 Agent 队列，下次唤醒时处理
- **L0** → 丢弃

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
