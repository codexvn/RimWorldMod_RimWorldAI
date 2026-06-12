# 会话恢复 — sessionId 持久化 + SDK resume

## 背景

RimWorldAgent 使用 `@anthropic-ai/claude-agent-sdk` 的 `query({ prompt: AsyncStream })` 模式。进程重启或 SDK 会话中断后，通过 `session-id.txt` + SDK `options.resume` 恢复对话历史。

## 核心 API

SDK 的 `sdk.query()` 接受 `options.resume: "<uuid>"`。SDK 自动从 `~/.claude/projects/<cwd-hash>/<uuid>.jsonl` 加载完整对话历史。

## 架构：MCP 权威 + session-id.txt 桥梁

```
MCP (RimWorldMCP)                          Companion (Node.js)
┌────────────────────────┐                ┌──────────────────────────┐
│ GameComponent_McpServer│                │ session.ts: 读 sid 文件   │
│   _sessionId (Scribe)  │                │   → options.resume       │
│   get_session_id()     │──HTTP──Agent──→│ companion.ts:             │
│   set_session_id()     │                │   onSdkMessage → 写文件   │
└────────────────────────┘                │   clear_context → 删文件  │
                                          └──────────────────────────┘

Agent (RimWorldAgent)：只做桥梁
  InitAsync: get_session_id → 写 session-id.txt → 启动 companion
  system.init: OnSessionIdChanged → set_session_id → Scribe 落盘
```

## 数据流

```
═══════════════════════════════════════════════
  冷启动
═══════════════════════════════════════════════

MCP: _sessionId = ""（Scribe 空）
Agent InitAsync: get_session_id → 空 → 不写 session-id.txt
companion: session.ts 读文件 → 不存在 → sdk.query({})
SDK → system.init { session_id: "A" }
companion: onSdkMessage → 写 session-id.txt → "A"
Agent: OnSessionIdChanged → set_session_id("A") → MCP Scribe 落盘

═══════════════════════════════════════════════
  读档
═══════════════════════════════════════════════

MCP LoadedGame: Scribe → _sessionId = "A"
Agent InitAsync: get_session_id → "A" → 写 session-id.txt → "A" → 启动 companion
companion: session.ts 读 "A" → sdk.query({ resume: "A" }) ✅

═══════════════════════════════════════════════
  正常 interrupt（abort）
═══════════════════════════════════════════════

abort → startNewSession → session.ts 读 session-id.txt → resume ✅

═══════════════════════════════════════════════
  清空上下文（clear_context）
═══════════════════════════════════════════════

WebUI/Dialog → { type: "clear_context" }
Agent: OnClearContext → CcbSessionId = null → SendAbort(clear=true)
companion: currentSessionId = undefined → 删 session-id.txt → sdk.query({})
SDK → init { session_id: "B" }
companion: 写 session-id.txt → "B"
Agent: set_session_id("B") → Scribe 写 "B"
```

## 修改文件

### Companion

| 文件 | 作用 |
|------|------|
| `bridge/session.ts` | `createSession` 读 `session-id.txt` → `options.resume` |
| `companion/companion.ts` | session-id.txt 读写+删；`currentSessionId` 捕获；`startNewSession` 无参；clear 时删文件 |
| `companion/config.ts` | 无 `resumeSessionId` 相关字段 |

### Agent

| 文件 | 作用 |
|------|------|
| `AgentEngine.cs` | `InitAsync`：MCP 连后 `get_session_id` → 写 `session-id.txt` → 启动 CCB；订阅 `OnSessionIdChanged` |
| `AgentLoop.cs` | `CcbSessionId` 内存值；`OnClearContext` 发 clear abort；`OnAbort` 只中断不清空 |
| `SdkMessageParser.cs` | `system.init` → `CcbSessionId` + `RaiseSessionIdChanged` |
| `GameComponent_RimworkAgent.cs` | 无 sessionId 逻辑（MCP 管理 Scribe） |
| `UIMessageBus.cs` | 新增 `OnClearContext` 事件 + 解析 `clear_context` |

### MCP

| 文件 | 作用 |
|------|------|
| `GameComponent_McpServer.cs` | `_sessionId` 静态字段 + Scribe；`SetSessionId()`；不自动生成 |
| `Tools/Tool_SetSessionId.cs` | Agent 调用的设置工具 |
| `Tools/Tool_GetSessionId.cs` | Agent 调用的读取工具 |

### UI

| 文件 | 作用 |
|------|------|
| `RimWorldAgentUI/UI/BridgeClient.cs` | `SendClearContext()` |
| `RimWorldAgentUI/UI/Dialog_AiChat.cs` | "清空"按钮发 `SendClearContext()` |
| `index.html` / `index-v2.html` | "清空"按钮发 `{"type":"clear_context"}` |

## 依赖

- `@anthropic-ai/claude-agent-sdk` ≥ 0.3.173（`options.resume` 支持）
- `Scribe_Values`（MCP 侧存档持久化）
