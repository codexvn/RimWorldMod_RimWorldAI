# Claude Code — RimWorldMCP 游戏 AI 助手

TypeScript 项目，WebSocket 接收游戏事件，Claude Agent SDK 驱动 AI 操控 RimWorld。`tsx` 运行时，所有依赖自包含。

## 快速开始

### 前置条件

- [Node.js](https://nodejs.org/) >= 18.19.0

### 安装依赖

```bash
npm install
```

依赖包括 Claude Agent SDK、ws（WebSocket）、tsx（TypeScript 运行时）。

### 启动

**方式一：自动启动（推荐）**

游戏内 → 选项 → Mod 设置 → RimWorld MCP → 开启"自动启动本地 Companion"。加载存档时 C# 侧自动 spawn companion 进程，RimWorld 退出/崩溃时自动清理。

C# spawn 的实际命令等效：

```bash
node --import tsx/esm companion/companion.ts --idle-timeout 30000
```

**方式二：手动启动**

```bash
npm start            # tsx companion/companion.ts
```

终端输出 `[cc-companion] 就绪，等待 RimWorldMCP 连接...` 即启动成功。

**开发模式**（文件改动自动重启）：

```bash
npm run dev          # tsx --watch companion/companion.ts
```

### 启动后

| 服务 | 地址 |
|------|------|
| WebSocket | `ws://127.0.0.1:19999` |
| 聊天页面（只读） | `http://127.0.0.1:19999/` |

## 文件结构

```
cc-companion/
├── companion/             # 入口 + 配置 + SDK 加载
│   ├── companion.ts       # 编排入口
│   ├── config.ts          # 配置（CLI + 环境变量）
│   └── sdk-loader.ts      # SDK 加载
├── bridge/                # 桥接层（WS + SDK 会话）
│   ├── ws-server.ts       # WebSocket Server
│   └── session.ts         # SDK 会话 + AsyncStream + 响应广播
├── chat/                  # 聊天 UI
│   ├── chat-http.ts       # HTTP 路由
│   └── chat-page.ts       # 聊天页面 HTML
├── rimworld/
│   └── context.ts         # 系统提示词加载（Prompt.md）
├── Prompt.md              # AI 行为提示词
├── package.json
├── tsconfig.json
└── README.md
```

## 数据流

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

## 参数来源

API Key、Base URL、权限模式等 SDK 配置由用户本地 `.claude/settings.json` 管理，SDK 自动读取。C# 通过 `--project-setting-sources` 传入 MCP 服务地址和权限模板。非敏感参数可通过 CLI/env var 覆盖：

| 环境变量 | CLI 参数 | 默认值 | 说明 |
|----------|----------|--------|------|
| `CCB_HOST` | `--host` | `127.0.0.1` | WebSocket 监听地址 |
| `CCB_PORT` | `--port` | `19999` | WebSocket 监听端口 |
| `CCB_AUTH_TOKEN` | `--token` | 无 | WS 握手认证 |
| `CCB_MODEL_NAME` | `--model-name` | 空 | 模型名称 |
| `CCB_IDLE_TIMEOUT` | `--idle-timeout` | `0`（永不退出），C# spawn 时传 `30000` | 无 client 连接/断开后超时自动退出（ms） |
| `CCB_SETTING_SOURCES` | `--setting-sources` | `user,project,local` | settings 加载源 |
| `RIMWORLD_PROJECT_PATH` | `--project-path` | `process.cwd()` | SDK 项目目录 |
| - | `--project-setting-sources` | 无 | 写入 `{projectPath}/.claude/settings.json` 的 JSON |
| - | `--local-setting-sources` | 无 | 写入 `{projectPath}/.claude/settings.local.json` 的 JSON |

## 进程生命周期

C# 侧自动 spawn 时传入 `--idle-timeout 30000`，companion 在 WS 断开 30s 无重连后自动退出。同时 C# 侧在返回主菜单、退出游戏、读档/新档时主动杀旧进程。Windows 额外通过 Job Object 绑定，RimWorld 强杀时 OS 自动清理 companion。

## 会话存储

SDK 会话持久化到 `~/.claude/projects/<sanitizedCwd>/<sessionId>.jsonl`。不同存档通过不同的 `RIMWORLD_PROJECT_PATH` 实现隔离。
