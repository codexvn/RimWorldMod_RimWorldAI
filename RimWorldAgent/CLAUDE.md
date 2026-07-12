# RimWorldAgent

AI Colony Runtime。通过 MCP 连接 `RimWorldMCP`，通过 Node ACP Host 连接 ACP backend。

## 架构边界

```text
RimWorldAgent (C# / net472)
  ├── AgentEngine / AgentLoop / AgentOrchestrator
  ├── MCP Client + Agent MCP Server :9878
  ├── IPC DTO + NDJSON stdin/stdout
  │       │
  │       ▼
  │   Node ACP Host (Node.js 22+)
  │       ├── IPC schema validation
  │       ├── ACP TypeScript SDK Client
  │       └── backend process lifecycle
  │               │ ACP stdio
  │               ▼
  │       claude-agent-acp
  └── existing UiMessage → UI WebSocket :19999
```

- C# 不引用 `dotacp.client` / `dotacp.protocol`，不解析 ACP 原始 DTO。
- ACP 只存在于 Node Host 与 backend 之间。
- C# 与 Node 之间使用自定义 `rimworld-agent-ipc` v1 DTO。
- `:19998` 和旧 CCB 不再参与运行时；UI WebSocket `:19999` 保留。
- stdin/stdout 只传 NDJSON；Node 日志和 backend 日志只能写 stderr。
- 不使用 Windows Named Pipe、localhost TCP 或 MessagePack 作为 IPC。

## IPC DTO

唯一协议来源：`RimWorldAgent/IPC/schema/ipc.schema.json`。

C# DTO 位于：

```text
RimWorldAgent/IPC/Generated/IpcDtos.cs
```

Node DTO/type 位于：

```text
RimWorldAgent/Node/rimworld-acp-host/src/protocol.ts
```

顶层 envelope：

```json
{
  "protocol": "rimworld-agent-ipc",
  "version": 1,
  "type": "prompt",
  "requestId": "req-1",
  "payload": {}
}
```

支持的请求：

```text
initialize
new_session
resume_session
load_session
prompt
cancel
close
```

支持的异步消息：

```text
event
initialize_response
new_session_response
resume_session_response
load_session_response
prompt_response
cancel_response
close_response
error
```

所有有效请求及其响应都必须有 `requestId`；无法解析请求时允许无关联 id 的 `error`。Node 使用 Ajv 校验 envelope 和 payload；C# 使用 `System.Text.Json` 编解码。单条 NDJSON 消息上限为 4 MiB。

## 配置注入

C# 是游戏侧运行时配置的唯一来源，`initialize` 时发送：

```text
AgentRuntimeConfig
├── backend
├── cwd / additionalDirectories
├── prompt
│   └── systemPrompt
└── agentMcpUrl
```

### Prompt

- `Prompt.md` 是稳定约束的默认来源，C# 启动时读取并替换 `{projectPath}` / `{skillsTable}`。
- Prompt 通过 IPC `AgentRuntimeConfig.prompt` 发送给 Node。
- 为兼容不支持 `_meta.systemPrompt` 扩展的 ACP 后端，每个 ACP session 的首次 `session/prompt` 会前置发送一次 Prompt；后续 prompt 只发送动态用户消息和世界状态。
- Prompt 缺失或为空时 fail closed，不启动 Agent session。
- 每轮 `prompt` 只发送动态用户消息和世界状态，不重复发送系统约束。

### Skills

- `InternalToolRegistry` 加载内置 `Skills/` 和用户 `Skills.d/`。
- C# 生成 `skills-desc.txt` 并替换 Prompt 中的 `{skillsTable}`。
- Skill 内容继续通过内置 `get_skills` / `active_skill` MCP 工具按需读取，不复制到 IPC DTO。

### MCP

C# 只向 ACP Session 注入受控的内置 `agent` MCP：`http://localhost:{AgentMcpPort}/mcp`。它包含 `InternalToolRegistry` 以及 `ProxyToolProvider` 的 `discover_tools` / `get_tool_schema` / `execute_tool`。Node 不向 Backend 注入自定义 MCP。

## Session 生命周期

```text
AgentEngine.InitAsync
  → start Node Host
  → initialize
  → load/resume persisted session 或 new_session
  → AgentLoop.WireUIMessageBus
```

- AgentEngine 依据 backend 在 initialize 中声明的能力优先选择 `session/load`，其次 `session/resume`，否则新建 session。
- `session/load` 用于加载后端持久化状态；`session/resume` 用于重新接管仍可恢复的 session。具体历史重建行为由 backend 定义。
- `session/new` 用于创建新会话。
- `cancel` 只取消当前 prompt，不清空 session。
- `close + new_session` 用于清空上下文。
- sessionId 由 C# 持久化到游戏/MCP 会话存储。

## UI 兼容层

```text
ACP session/update
  → Node runtime event DTO
  → C# NodeRuntimeEventProjector
  → existing UiMessage
  → UIMessageBus :19999
```

第一阶段不修改现有 UI message schema。支持文本、思考、工具调用、工具结果、usage、状态、取消和错误事件。

## 权限策略

游戏 Agent 无人工接入。Node Host 不区分具体 ACP backend：

- 每次 `request_permission` 都检查请求中的工具名；仅 Agent MCP 白名单命名空间（`mcp__agent__`、`agent__`、`agent/`）选择 allow；
- 非白名单工具默认选择 reject，若 backend 未提供 reject 选项则返回 cancelled；
- 文件读取、文件写入和 terminal client capability 均为 false；
- backend 是否暴露内置工具由 backend 自己决定，但未进入白名单的工具不能通过本 Client 的权限请求。

所有 ACP request 必须返回，不允许永久挂起 backend。

## 发布与依赖

发布目录：

```text
publish/RimWorldAgent/1.6/Assemblies/
├── RimWorldAgent.dll / RimWorldAgent_Standalone.exe
├── rimworld-acp-host/
│   ├── dist/
│   ├── package.json
│   ├── node_modules/（生产依赖）
│   └── schema/ipc.schema.json
├── claude-agent-acp/
│   ├── dist/
│   ├── package.json
│   └── package-lock.json
│   ├── dist/
│   ├── package.json
│   └── package-lock.json
├── Skills/
└── Skills.d/
```

- Node.js 最低版本为 22。设置页会在修改路径后立即重新检测，并解析实际 Node 可执行文件。ACP backend 的安装、升级和依赖管理由用户自行负责，Mod 不检查 backend 内部依赖。
- ACP 依赖不进入 RimWorld AppDomain。

## ACP / IPC 诊断日志

设置页的“记录 ACP / IPC 调用日志”会在下次初始化 Agent Runtime 时启用：

- C# 记录 IPC 的方向、消息类型、requestId、UTF-8 消息大小和响应耗时；
- Node Host 将 ACP `initialize`、session 生命周期、prompt、cancel、close 以及 `session/update` 类型写到 stderr，C# 再转入游戏日志；
- 日志不记录 Prompt 原文或 backend 环境变量值，避免把游戏对话和配置内容塞入 RimWorld 日志。

该开关由 `AgentModSettings.LogAcpIpc` 通过 `AgentEngineConfig` 传递给 Node Host；修改设置后需要重新初始化 Agent Runtime 才会生效。

## 设置生效路径

```text
AgentModSettings
  → GameComponent_RimworkAgent
  → AgentEngineConfig
  → AgentRuntimeConfig IPC DTO
  → Node ACP Host
  → ACP session/backend
```

Prompt、内置 agent MCP 和所选 backend 启动配置从该链路进入运行时。Skills 由 C# 加载并通过 Prompt 摘要及 Agent MCP 工具提供。Mod 设置页负责选择 Backend、管理自定义 Backend 的 command/args/cwd/env，并允许自动检测或显式指定 Node.js 22+ 可执行文件。Backend 的认证、API endpoint/key、模型和 Provider 配置由 Backend 自身或父进程环境管理，不由 Mod UI 注入。`ProjectPath` 仍作为内部运行数据目录和 ACP cwd 使用，但不再暴露在主设置 UI。

## 构建与验证

```powershell
$env:NUGET_PACKAGES='C:\Users\codexvn\.nuget\packages'
dotnet build RimWorldAgent\RimWorldAgent.csproj --no-restore

cd RimWorldAgent\Node\rimworld-acp-host
npm install --ignore-scripts --no-audit --no-fund
npm run build
```

验证重点：

- C# 构建不引用 `dotacp`。
- Node Host initialize/new_session 可用。
- stdout 无日志污染，stderr 承载日志。
- Prompt 和内置 agent MCP 能进入 initialize 配置。
- UI `:19999` 继续接收现有 UiMessage。
- Node/backend 退出时 C# 能感知并显示错误。
- 不提交 git commit，除非获得明确授权。


### ACP Session Config Options

Mod 设置对每个 backend 支持：测试连通性 + 拉取 `configOptions` + 选择保存。Agent 仅在新建会话（`session/new`）后应用保存值。
