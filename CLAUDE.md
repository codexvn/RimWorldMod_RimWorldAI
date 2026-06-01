# RimWorld AI

AI Colony Operating System — Claude Agent SDK 自主管理 RimWorld 殖民地。

## 项目结构

```
RimWorldAI/
├── SimpleMspServer/          ← MCP 协议共享库 (net472)
│   ├── McpMessage.cs         JSON-RPC 2.0
│   ├── ITransport.cs         传输抽象
│   └── SseTransport.cs       SSE + HTTP

├── RimWorldMCP/              ← 游戏 Mod (net472)
│   ├── Tools/                112 个游戏 Tool
│   ├── MCP Server :9877      SSE + Streamable HTTP
│   ├── Harmony/              事件拦截 → NotificationBus
│   └── Transport/

├── RimWorldAgent/             ← Agent Runtime (net472)
│   ├── Core/
│   │   ├── AgentRuntime/     AgentLoop + AgentOrchestrator + ContextBuilder + ToolDispatcher
│   │   ├── CcbManager/       CCB 子进程 + CcbWebSocket
│   │   ├── BridgeBus.cs      ★ UI 总线 — Fleck WS :19998，SDK消息广播 + 客户端消息 → CCB
│   │   ├── Mcp/              MCP 客户端 + Agent MCP Server :9878
│   │   └── Data/             ★ IDbStore 抽象 — JsonDbStore (EXE) / ScribeDbStore (MOD)
│   ├── Mod/                  GameComponent + UI + Harmony Hooks
│   ├── Exe/                  独立 EXE 入口
│   ├── resource/WebUI/       Web 前端静态文件
│   └── cc-companion/         Node.js SDK 桥接 (纯 WS, ~358行)
│
└── RimWorldAgent.Tests/      C# 测试

四者关系：RimWorldMCP ↔ RimWorldAgent 通过 MCP 协议通信（互不引用）。
SimpleMspServer 被两者共同引用。Agent → companion 通过 WS :19999。
```

## 架构

```
                    CC Companion (Node.js :19998)
                         │
            chat/abort  │  SDK 流式消息 (type=event)
                         │
                  CcbWebSocket (C#)
                    │           │
          SdkMessage.FromJson  SendChat/Abort
                    │           │
              AgentLoop.WireBridgeBus
              │                       │
    SdkMessageParser            BridgeBus.OnChat/Abort
    (SdkMessage → UiMessage)         │
              │               PushGameEvent(User)
              ▼                       │
       BridgeBus.PushUiMessages ─────┘
              │
      UiMessage WS :19999 广播
         │                │
    ┌────┘                └────┐
    ▼                         ▼
 WebUI :19997           Dialog_AiChat
 (BridgeClient WS)      (BridgeClient WS)
```

| 端口 | 服务 | 协议 | 所属 |
|------|------|------|------|
| `:9877` | MCP Server（游戏 Tool） | SSE / HTTP | RimWorldMCP |
| `:9878` | Agent MCP Server（内部 + 代理全部游戏 Tool） | HTTP | RimWorldAgent |
| `:19998` | CC Companion（SDK 桥接） | WebSocket | RimWorldAgent |
| `:19999` | BridgeBus（UI 总线） | WebSocket | RimWorldAgent (Mod) |
| `:19997` | WebUI HTTP | HTTP | RimWorldAgentUI |

**关键设计**：
- **CC Companion** 是纯 SDK 桥接——收 chat/abort，吐 SDK 流式消息
- **SdkMessage**：类型化消息模型，与 `@anthropic-ai/claude-agent-sdk` 协议对齐（Assistant/StreamEvent/Result/System/User/Aborted 子类），`FromJson` 工厂完成 type=event 解包 + 未知字段校验
- **SdkMessageParser**：SdkMessage → UiMessage 转换，不碰 JSON
- **ProxyToolProvider**：游戏 MCP 工具全部代理到 Agent MCP，SDK 只连 `agent` 端点
- **BridgeBus**：只负责 UiMessage WS 广播 + 客户端消息接收，不感知 SDK 格式
- **RimWorldAgentUI**：独立模组，通过 WS 连接 BridgeBus，自带 HTTP 服务提供 WebUI
- **IDbStore + IGameStateProvider**：EXE/MOD 双模抽象，构造注入解耦

## 构建

```bash
cd F:\RiderProjects\RimWorldMCP
dotnet build RimWorldAI.sln
```

5 个项目，全部 net472。

## 开发规范

**子项目专属内容见**：[RimWorldMCP](RimWorldMCP/CLAUDE.md) | [RimWorldAgent](RimWorldAgent/CLAUDE.md)

### 异常处理

**禁止空 catch**。每个 catch 必须记录异常类型和 `ex.Message`。`OperationCanceledException` 允许空 catch。

### 日志

- **不含敏感信息**：token、密码、密钥不写入日志
- **调试**：`[CCGUI_DEBUG]` 前缀，解决后 grep 清理

### 设计文档

见 `design/` 目录。

| 文档 | 内容 |
|------|------|
| `design/camera-system.md` | 摄像头自动移动 |
| `design/bridge-lifecycle.md` | CCB 生命周期 |
| `design/tool-system.md` | Tool 系统 |
| `design/event-system.md` | 事件系统 |
| `design/token-budget-system.md` | Token 预算 |
| `design/mcp-server-integration.md` | MCP Server 集成 |
| `design/agent-runtime.md` | Agent Runtime |
| `design/tool-result-suffix.md` | Tool Result Suffix |

### 提交

- commit 信息简体中文，Conventional Commits 格式
- 提交前检查 diff：敏感信息 + `[CCGUI_DEBUG]` 残留
- 未经允许不提交
