# 会话历史恢复 — sessionId 持久化 + SDK resume

## 背景

RimWorldAgent 使用 `@anthropic-ai/claude-agent-sdk` 的 `query({ prompt: AsyncStream })` 模式——一个长连接 Stream 接受多条用户消息。但进程重启或 SDK 会话中断后，旧会话历史丢失，AI 不记得之前对话。

## 核心 API

SDK 的 `sdk.query()` 接受 `options.resume: "<uuid>"`。SDK 自动从 `~/.claude/projects/<cwd-hash>/<uuid>.jsonl` 加载完整对话历史（包括 tool_call/tool_result），无需手动导入。

## 两种 ID

| ID | 来源 | 用途 | 生命周期 |
|----|------|------|----------|
| **Save ID** | `get_session_id` MCP 工具 | SQLite `save_id` 列隔离不同存档的对话记录 | 同存档不变 |
| **SDK Session ID** | SDK `system.init` → `session_id` | `options.resume` 恢复对话上下文 | 每次 `sdk.query()` 生成新 UUID，跨存读档复用 |

两个 ID 用途不同，不要混淆。

## 数据流

```
═══════════════════════════════════════════════
  冷启动（无存档 sessionId）
═══════════════════════════════════════════════

CcbManager.StartCore()
  → node ... --resume-session-id ""

companion 启动
  → CONFIG.resumeSessionId = "" → 跳过
  → sdk.query({ options: {} })

SDK → system.init { session_id: "uuid-1" }
  → companion: onSdkMessage → currentSessionId = "uuid-1"
  → C# SdkMessageParser → AgentLoop.CcbSessionId = "uuid-1"
  → GameComponent.ExposeData → Scribe_Values.Look 写入存档

═══════════════════════════════════════════════
  存盘 → 读档（有存档 sessionId）
═══════════════════════════════════════════════

RimWorld LoadedGame → ExposeData → Scribe 读取 _ccSessionId = "uuid-1"
InitAgentRuntime → AgentEngineConfig.ResumeSessionId = "uuid-1"
CcbManager.StartCore()
  → node ... --resume-session-id "uuid-1"

companion 启动
  → CONFIG.resumeSessionId = "uuid-1"
  → sdk.query({ options: { resume: "uuid-1" } })
  → SDK 从 ~/.claude/projects/<hash>/uuid-1.jsonl 加载全部历史 ✅

SDK → system.init { session_id: "uuid-1" }  ← 同一 UUID
AI 拥有读档前完整上下文 ✅

═══════════════════════════════════════════════
  用户发消息 / 运行中打断
═══════════════════════════════════════════════

AgentLoop.OnChat:
  → SendAbort()                           ← 保持现有逻辑
  → companion: abort → startNewSession(currentSessionId)
  → CONFIG.resumeSessionId = "uuid-1"
  → sdk.query({ options: { resume: "uuid-1" } })
  → SendChat → SDK 拥有完整上下文 ✅

═══════════════════════════════════════════════
  用户清空上下文（UI 清空按钮 → abort）
═══════════════════════════════════════════════

AgentLoop.OnAbort:
  → CcbSessionId = null                   ← 清空

companion:
  → abort → startNewSession()             ← 不传 sessionId
  → CONFIG.resumeSessionId = ""
  → currentSessionId = undefined
  → sdk.query({ options: {} })            ← 全新会话
  → SDK → system.init { session_id: "uuid-2" }
  → 下次存档写新 UUID
```

## 修改文件

### Companion (TypeScript)

| 文件 | 改动 |
|------|------|
| `companion/config.ts` | `CONFIG` 加 `resumeSessionId: string`；`parseArgs` 解析 `--resume-session-id` |
| `bridge/session.ts` | `createSession` 读 `CONFIG.resumeSessionId` → `options.resume` |
| `companion/companion.ts` | `startNewSession(resumeSessionId?)` 参数化；`onSdkMessage` 从 `system.init` 捕获 `currentSessionId`；abort 后清空 `currentSessionId` |

### C# Side

| 文件 | 改动 |
|------|------|
| `CcbManager.cs` | 构造函数加 `resumeSessionId` 参数；`StartCore` 加 `--resume-session-id` CLI 参数 |
| `AgentEngine.cs` | `AgentEngineConfig` 加 `ResumeSessionId` 属性；构造 `CcbManager` 时传入 |
| `AgentLoop.cs` | 加 `CcbSessionId` 静态属性；`OnAbort` 清除 sessionId |
| `SdkMessageParser.cs` | `system.init` 时写 `AgentLoop.CcbSessionId` |
| `GameComponent_RimworkAgent.cs` | `_ccSessionId` 字段；`ExposeData` 用 `Scribe_Values.Look` 持久化；读档时传入 `AgentEngineConfig` |

## 不涉及

- ❌ `ConversationStore` / SQLite — 与我们无关，SDK 自己管理 JSONL
- ❌ `CcbWebSocket` / WS 协议 — 不走 WS 传 sessionId，走 CLI 参数
- ❌ 自定义历史文本注入 — SDK `resume` 原生恢复全部结构化历史

## 依赖

- `@anthropic-ai/claude-agent-sdk` ≥ 0.3.173（`options.resume` 支持）
- `Scribe_Values`（RimWorld 存档持久化 API）
