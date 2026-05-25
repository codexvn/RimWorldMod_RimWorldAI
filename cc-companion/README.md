# Claude Code — RimWorldMCP 游戏 AI 助手

TypeScript 项目，WebSocket 接收游戏事件，Claude Agent SDK 驱动 AI 操控 RimWorld。`tsx` 运行时，所有依赖自包含。

## 快速开始

```bash
npm install          # 安装依赖（Claude Agent SDK + tsx + ws）
npm start            # tsx companion.ts
```

游戏设置中开启"自动启动本地 Companion"，RimWorld 加载存档时自动 spawn（`node --import tsx/esm companion.ts ...`）。也可在设置面板点"安装 Claude Code 依赖"。

## 文件结构

```
cc-companion/
├── companion.ts          # 编排入口
├── config.ts             # 配置（CLI + 环境变量）
├── auth.ts               # API 认证
├── sdk-loader.ts         # SDK 加载
├── session.ts            # SDK 会话 + AsyncStream + 响应广播
├── ws-server.ts          # WebSocket Server
├── rimworld/
│   └── context.ts        # 系统提示词加载（Prompt.md）
├── Prompt.md             # AI 行为提示词
├── package.json
├── tsconfig.json
└── README.md
```

## 数据流

```
RimWorld CCClient --WS--> companion.ts --SDK--> Claude API
                                        │
                                   broadcast 响应
                                        │
                              RimWorld CCClient
                                        │
                              ChatDisplayState
                                        │
                              Dialog_AiChat（游戏内聊天窗）
```

## 配置

| 环境变量 | CLI 参数 | 默认值 | 说明 |
|----------|----------|--------|------|
| `ANTHROPIC_API_KEY` | - | - | API Key 认证 |
| `CC_HOST` | `--host` | `127.0.0.1` | WebSocket 监听地址 |
| `CC_PORT` | `--port` | `19999` | WebSocket 监听端口 |
| `CC_TOKEN` | `--token` | 无 | 客户端认证 token |
| `CC_MODEL` | `--model` | `sonnet` | 模型名称 |
| `MCP_URL` | `--mcp-url` | `http://localhost:9877/mcp` | MCP Server 地址 |
| `MCP_HEADERS` | `--mcp-headers` | 无 | MCP 请求附加头 JSON |
| `CC_CONNECT_TIMEOUT` | `--connect-timeout` | `300000` | 无连接超时退出（ms） |
| - | `--no-connect-timeout` | - | 禁用连接超时 |
| `RIMWORLD_PROJECT_PATH` | `--project-path` | `process.cwd()` | 会话存储目录 |

## 认证

优先级：env var → Mod 本地 `claude-settings.json` → `~/.claude/settings.json` → `~/.codemoss/config.json`

## 会话存储

SDK 会话持久化到 `~/.claude/projects/<sanitizedCwd>/<sessionId>.jsonl`。
