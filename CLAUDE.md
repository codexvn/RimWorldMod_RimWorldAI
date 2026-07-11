# RimWorld AI

AI Colony Operating System — 通过 ACP 自主管理 RimWorld 殖民地。

## 项目结构

```text
RimWorldAI/
├── SimpleMspServer/          ← MCP 协议共享库 (net472)
├── RimWorldMCP/              ← 游戏 Mod；MCP Server :9877
├── RimWorldAgent/            ← Agent Runtime (net472)
│   ├── Core/AgentTransport/  ACP 进程、Client、session 与 UI 兼容投影
│   ├── Core/AgentRuntime/    AgentLoop、Orchestrator、工具调度与调度状态
│   ├── Core/UIMessageBus.cs  UI 总线；WebSocket :19999
│   ├── Core/Mcp/             Agent MCP Server :9878 与 MCP 客户端
│   ├── Mod/                  GameComponent、设置 UI 与 Harmony Hooks
│   ├── Exe/                  独立 EXE 入口
│   └── resource/             Skills 与 MOD 元数据
├── RimWorldAgentUI/          ← Web UI Mod；HTTP :19997
└── RimWorldAgent.Tests/      ← C# 测试

RimWorldMCP ↔ RimWorldAgent 通过 MCP 协议通信；C# Agent ↔ Node ACP Host 通过 IPC DTO + NDJSON stdin/stdout；Node Host ↔ ACP backend 通过官方 ACP stdio；Agent ↔ UI 通过 UIMessageBus :19999。
```

## 架构

```text
AgentEngine
  ├── NodeAgentHost / NodeAgentSession
  │     └── IPC DTO + NDJSON stdin/stdout
  │           └── rimworld-acp-host (Node.js 22+)
  │                 ├── official ACP TypeScript SDK Client
  │                 └── claude-agent-acp backend (ACP stdio)
  ├── MCP Client / Agent MCP Server :9878
  └── AgentLoop / AgentOrchestrator
          └── NodeRuntimeEventProjector
                  └── UiMessage → UIMessageBus :19999
                          ├── WebUI :19997
                          └── Dialog_AiChat
```

当前里程碑只切换 Agent transport/session/runtime，保留现有 `UiMessage` DTO 和 UI WebSocket schema。`:19998` 不再承载 CCB；ACP 使用 stdio。`claude-agent-acp` 要求 Node.js 22+，发布目录只需要 adapter 的生产运行时文件。

| 端口 | 服务 | 协议 | 所属 |
|------|------|------|------|
| `:9877` | MCP Server（游戏 Tool） | SSE / HTTP | RimWorldMCP |
| `:9878` | Agent MCP Server | HTTP | RimWorldAgent |
| `:19999` | UIMessageBus | WebSocket | RimWorldAgent |
| `:19997` | WebUI HTTP | HTTP | RimWorldAgent |

**运行时边界**：ACP 的 Agent→Client request 由 Node Host 响应。内置 `claude-agent-acp` 禁用自身工具，仅注入游戏 `agent` MCP，并自动允许其请求；其他 backend 仍默认拒绝文件读写、terminal、权限和未知 extension request，不等待人工输入。C# 只处理 IPC DTO 和 runtime event。UI 仍只接收兼容投影后的现有 DTO；后续若切换 UI DTO，再单独定义里程碑。

**Agent 启动配置**：C# 将用户选择的 backend command、args、env、workingDirectory、固定 Prompt 与内置 `agent` MCP URL 组装为 `AgentRuntimeConfig`，通过 `rimworld-agent-ipc` 的 `initialize` 发送给 Node Host。未配置 backend 时不启动 Agent，不自动回退到 Claude。


**RimWorld 依赖隔离**：ACP 依赖全部在 Node Host 进程中，不进入 RimWorld AppDomain；C# 已不再发布或加载 ACP 的 MessagePack 依赖。

**ACP / IPC 日志**：设置中的“记录 ACP / IPC 调用日志”只记录 C#↔Node IPC 的类型、requestId、大小和耗时，以及 Node 对 ACP 方法/事件类型的 stderr 追踪；不记录 Prompt 原文或环境变量值。修改开关后需重新初始化 Agent Runtime。

## 构建

```bash
cd F:\RiderProjects\RimWorldMCP
dotnet build RimWorldAI.sln
cd RimWorldAgent\Node\rimworld-acp-host
npm install --ignore-scripts --no-audit --no-fund
npm run build
```

5 个项目，全部 net472。

## 开发规范

**子项目专属内容见**：[RimWorldMCP](RimWorldMCP/CLAUDE.md) | [RimWorldAgent](RimWorldAgent/CLAUDE.md)

### 异常处理

**禁止空 catch**。每个 catch 必须记录异常类型、Message 和完整 InnerException 链。`OperationCanceledException` 允许空 catch。

### 日志

- **不含敏感信息**：token、密码、密钥不写入日志
- **调试**：禁止未经标识的临时日志；正式日志不得输出 token、密钥或 prompt 原文

### 设计文档

见 `design/` 目录。

| 文档 | 内容 |
|------|------|
| `design/camera-system.md` | 摄像头自动移动 |
| `RimWorldMCP/design/device-ui-gizmo-tools.md` | 设备 UI/Gizmo 信息查询与通用操作工具 |
| `docs/plans/acp-agent-refactor.md` | ACP 迁移与 Agent 架构重构 |
| `design/tool-system.md` | Tool 系统 |
| `design/event-system.md` | 事件系统 |
| `design/token-budget-system.md` | Token 预算 |
| `design/mcp-server-integration.md` | MCP Server 集成 |
| `RimWorldAgent/design/agent-runtime.md` | Agent Runtime |
| `design/conversation-history.md` | 会话历史持久化（SQLite + WS 协议） |
| `RimWorldAgent/design/session-resume.md` | ACP sessionId 持久化与 load/resume 说明 |

### 提交

- commit 信息简体中文，Conventional Commits 格式
- 提交前检查 diff：敏感信息 + `[CCGUI_DEBUG]` 残留
- 未经允许不提交
