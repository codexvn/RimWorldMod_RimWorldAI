# 会话恢复 — sessionId 持久化 + ACP load/resume

## 背景

`RimWorldAgent` 使用 ACP session 生命周期。游戏存档保存后端 session id；进程重启或 ACP transport 中断后，依据 initialize 返回的能力优先使用 `session/load`，其次 `session/resume`，两者都不可用时新建会话。完整 UI 历史仍由本地 conversation store 负责，ACP session id 只负责后端会话关联。

## 核心 API

ACP session 通过 `session/new`、`session/load`、`session/resume`、`session/prompt`、`session/cancel` 和 `session/close` 管理生命周期：

- `session/new`：没有可恢复的 session id，或用户明确清空上下文时创建新会话。
- `session/load`：按持久化 id 加载后端保存的 session 状态。
- `session/resume`：重新接管仍存在但当前 transport 已断开的 session。
- `session/prompt`：向当前 session 发送用户输入。
- `session/cancel`：中断当前 prompt，不删除 session。
- `session/close`：明确结束后端 session；普通 abort 和 clear 不应混用。

## 架构：游戏存档权威 + ACP session bridge

```
MCP (RimWorldMCP)                          RimWorldAgent
┌────────────────────────┐                ┌──────────────────────────┐
│ GameComponent_McpServer│                │ AgentEngine               │
│   _sessionId (Scribe)  │◄──HTTP──MCP──►│   NodeAgentSession          │
│   get_session_id()     │                │   load/resume/new          │
│   set_session_id()     │                │   prompt/cancel/clear      │
└────────────────────────┘                └────────────┬─────────────┘
                                                       │ Node ACP Host / ACP TypeScript SDK stdio
                                                       ▼
                                          ┌──────────────────────────┐
                                          │ NodeAgentHost             │
                                          │ node claude-agent-acp      │
                                          └────────────┬─────────────┘
                                                       │ ACP session/update
                                                       ▼
                                          NodeRuntimeEventProjector
                                                       │ existing UiMessage DTO
                                                       ▼
                                          UIMessageBus :19999 → existing UI
```

Agent 负责 ACP session 生命周期；MCP 侧负责游戏工具和存档中的 session id；UI 仍消费原有 `UiMessage` DTO，避免首轮协议切换同时改变 UI schema。

## 数据流

```
═══════════════════════════════════════════════
  冷启动
═══════════════════════════════════════════════

MCP: _sessionId = ""（Scribe 空）
Agent InitAsync: get_session_id → 空 → 调用 session/new
Node ACP Host → backend 返回 session id "A"
Agent: 保存 session id "A" → MCP Scribe 落盘

═══════════════════════════════════════════════
  读档
═══════════════════════════════════════════════

MCP LoadedGame: Scribe → _sessionId = "A"
Agent InitAsync: get_session_id → "A" → 若支持则调用 session/load
若不支持 load 但支持 resume，则调用 session/resume；两者都不支持则创建新 session
Node ACP Host → backend 恢复 session "A"

═══════════════════════════════════════════════
  正常 interrupt
═══════════════════════════════════════════════

当前 prompt → session/cancel
session id 保持不变；下一轮 prompt 继续使用当前 ACP session

═══════════════════════════════════════════════
  清空上下文
═══════════════════════════════════════════════

WebUI/Dialog → { type: "clear_context" }
Agent → cancel 当前 prompt（如有）→ session/new
Node ACP Host → backend 返回新 session id "B"
Agent → 保存新 session id "B" → MCP Scribe 落盘
旧 session 不再作为当前游戏上下文使用
```

## 当前实现文件

| 文件 | 作用 |
|------|------|
| `Core/AgentTransport/NodeAgentHost.cs` | 启动 Node ACP Host，并把 stdin/stdout 接入自定义 IPC DTO + NDJSON |
| `Node/rimworld-acp-host/src/backend-bridge.ts` | 使用官方 ACP TypeScript SDK 连接 backend，处理权限和 ACP client 回调 |
| `Core/AgentTransport/NodeAgentSession.cs` | 管理 `initialize/new/load/resume/prompt/cancel` 生命周期与 session id |
| `Core/AgentTransport/NodeRuntimeEventProjector.cs` | 将 ACP `session/update` 投影为现有 UI `UiMessage` DTO |
| `Core/Data/SessionStore.cs` | 保存/读取游戏存档关联的 session id |
| `Core/AgentEngine.cs` | MCP 连接建立后创建 ACP host/session，并按存档 session id 选择 load/resume/new |
| `Core/AgentRuntime/AgentLoop.cs` | 通过 `IAgentSession` 发送 prompt/cancel/clear，不直接依赖 transport |
| `Core/UIMessageBus.cs` | 保留既有 UI WebSocket `:19999` 和 UI 消息格式 |

## MCP 存档约束

- `_sessionId` 是存档关联的后端 session id，不是 UI conversation id。
- MCP 侧不自动生成 session id；只有 ACP session 成功创建/恢复后，Agent 才回写它。
- `clear_context` 必须创建新的 ACP session，不能只清空 UI 文本或复用旧 session。
- 普通 `abort` 只取消当前 prompt，不清空存档 session id。
- 如果 `load/resume` 都失败，Agent 应记录诊断并创建新的 session；不得把旧 id 静默当作新 session id。

## 依赖

- `@agentclientprotocol/sdk`：Node Host 内部的官方 ACP TypeScript SDK transport 与协议模型，C# 仅使用 IPC DTO
- `external/claude-agent-acp`：Node.js ACP backend，使用 ACP stdio 与 Node Host 通信
- Node.js 22+
- `Scribe_Values`（MCP 侧存档持久化）

## 已知边界

当前首轮切换只替换 transport/session/runtime。ACP 的 session/update 通过 compatibility projector 转换为原有 UI DTO；UI DTO 全量迁移和统一命名属于后续阶段。权限请求在无人值守的游戏 Agent 中统一拒绝或取消，避免 session 永久挂起等待人工处理。
