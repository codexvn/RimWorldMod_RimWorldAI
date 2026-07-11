# ACP Agent 重构实施记录

## 当前目标

```text
C# Agent Runtime
  └─ IPC DTO + NDJSON stdin/stdout
       └─ Node ACP Host
            └─ 官方 ACP TypeScript SDK
                 └─ claude-agent-acp / future ACP backend
```

第一阶段保留现有 UI message DTO 和 UI WebSocket `:19999`。

## 已实现

- 删除 C# direct ACP Client、`dotacp.client` 和 `dotacp.protocol` 引用。
- 新增 `RimWorldAgent/IPC/schema/ipc.schema.json`，定义 IPC v1 envelope、请求、响应、runtime event 和 runtime config。
- 新增 C# IPC DTO：`RimWorldAgent/IPC/Generated/IpcDtos.cs`。
- 新增 C# NDJSON Host：`NodeAgentHost`、`NodeAgentSession`。
- 新增 Node Host：`RimWorldAgent/Node/rimworld-acp-host`。
- Node Host 使用官方 `@agentclientprotocol/sdk` 连接 ACP backend。
- C# 负责组装固定 Prompt、`agentMcpUrl` 和所选 backend 启动配置，通过 `initialize` 发送给 Node。
- Mod 设置页可选择内置或自定义 Backend，并管理自定义 Backend 的 command/args/cwd/env；Node.js 22+ 路径支持自动检测和手动覆盖。
- Backend 的认证、API endpoint/key、模型和 Provider 配置由 Backend 自身或父进程环境管理，不由 Mod UI 注入。
- `ProjectPath` 继续作为内部运行数据目录和 ACP cwd，但不再显示在主设置 UI。
- Node 负责 ACP session 生命周期、backend 子进程和 ACP event → runtime event 转换。
- C# 使用 `NodeRuntimeEventProjector` 将 runtime event 投影为现有 `UiMessage`。
- CCB WebSocket runtime 已移除；UI WebSocket `:19999` 保留。
- 内置 Claude Backend 禁用全部默认工具，只注入受控 `agent` MCP，并对该工具集合自动授权；未知 Backend 仍默认拒绝 permission request。

## IPC v1

请求：

```text
initialize
new_session
resume_session
load_session
prompt
cancel
close
```

响应/事件：

```text
*_response
event
error
```

stdout 只能发送 NDJSON；stderr 发送 Node Host 和 backend 日志。每个请求使用 `requestId` 关联响应。

## 配置注入

```text
AgentModSettings
  → GameComponent_RimworkAgent
  → AgentEngineConfig
  → AgentRuntimeConfig DTO
  → Node Host initialize
  → ACP session/new|load|resume
```

MCP 服务：

- `agent` HTTP MCP：`http://localhost:{AgentMcpPort}/mcp`

Prompt 通过 IPC runtime config 传递。Skills 沿用现有逻辑：C# 生成 `{skillsTable}` 摘要，完整内容通过 `get_skills` / `active_skill` 内置 MCP 工具按需读取，不再复制到 IPC DTO 或 ACP `_meta`。

## 发布布局

```text
publish/RimWorldAgent/1.6/Assemblies/
├── RimWorldAgent.dll / RimWorldAgent_Standalone.exe
├── rimworld-acp-host/
│   ├── dist/
│   ├── package.json
│   ├── node_modules/
│   └── schema/ipc.schema.json
├── claude-agent-acp/
│   ├── dist/
│   ├── package.json
│   └── package-lock.json
├── Skills/
└── Skills.d/
```

ACP 依赖不进入 C# AppDomain；C# 不再发布或加载 ACP 的 MessagePack 依赖。

## 验证

已验证：

- C# `RimWorldAgent` 构建通过。
- Node Host TypeScript 构建通过。
- Node Host 能完成 ACP `initialize`。
- Node Host 能完成 `session/new`。
- 严格 IPC schema 能校验 initialize 和 session 请求。
- stdout 无日志污染，Node 日志在 stderr。

待补充：

- 使用真实游戏 MCP 的完整 Mod 回归。
- prompt 流式 event、cancel、load/resume 的游戏环境回归。
- 发布包不携带 `claude-agent-acp/node_modules`；用户在干净 Node.js 22 环境自行安装 backend 生产依赖后验证启动，Mod 不负责 backend 依赖管理。
- IPC C#↔Node 自动化测试和 schema 生成一致性检查。
- 单条 NDJSON 消息 4 MiB 限制、请求超时和 Node/backend 异常的游戏环境回归。
