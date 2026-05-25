# CC Companion

RimWorldMCP 的 Claude Code 伴随进程。WebSocket 服务器接收游戏事件，Claude Agent SDK 驱动 AI 响应。TypeScript，`tsx` 运行时，所有依赖和配置自包含在 Mod 目录内。

## 快速开始

```bash
npm install          # 安装依赖（含 Claude Agent SDK + tsx）
npm start            # tsx companion.ts
```

游戏设置中将桥接器类型切换为 "CC"，Companion 由 RimWorld 自动启动（`node --import tsx/esm companion.ts ...`）。

## 文件结构

```
cc-companion/
├── companion.ts          # 编排入口
├── config.ts             # 配置（CLI + 环境变量），CompanionConfig 接口
├── auth.ts               # API 认证
├── sdk-loader.ts         # SDK 加载
├── session.ts            # SDK 会话管理 + AsyncStream<T>
├── ws-server.ts          # WebSocket Server，类型化消息
├── rimworld/
│   └── context.ts        # RimWorld 游戏上下文（系统提示词 + GameEvent 类型）
├── tsconfig.json         # strict, ES2022, NodeNext
└── package.json
```

## 配置

| 环境变量 | CLI 参数 | 默认值 | 说明 |
|----------|----------|--------|------|
| `ANTHROPIC_API_KEY` | - | - | API Key 认证（最高优先级） |
| `ANTHROPIC_AUTH_TOKEN` | - | - | Bearer Token 认证 |
| `CC_HOST` | `--host` | `127.0.0.1` | WebSocket 监听地址 |
| `CC_PORT` | `--port` | `19999` | WebSocket 监听端口 |
| `CC_TOKEN` | `--token` | 无 | 客户端认证 token |
| `CC_MODEL` | `--model` | `sonnet` | 模型名称 |
| `MCP_URL` | `--mcp-url` | `http://localhost:9877/mcp` | MCP Server 地址（支持 http/sse） |
| `MCP_HEADERS` | `--mcp-headers` | 无 | MCP 请求附加头 JSON |
| `CC_CONNECT_TIMEOUT` | `--connect-timeout` | `300000` | 无客户端连接超时自动退出（ms） |
| - | `--no-connect-timeout` | - | 禁用连接超时 |
| `RIMWORLD_PROJECT_PATH` | `--project-path` | `process.cwd()` | 会话存储目录 |

## 认证

优先级链：

1. 环境变量 `ANTHROPIC_API_KEY` / `ANTHROPIC_AUTH_TOKEN`
2. Mod 本地 `<modRoot>/claude-settings.json`
3. `~/.claude/settings.json`（Claude CLI 配置）
4. `~/.codemoss/config.json`（CC GUI 配置）

Mod 本地配置示例 (`publish/claude-settings.json`)：

```json
{
  "ANTHROPIC_API_KEY": "sk-ant-..."
}
```

## 会话存储

Claude SDK 将会话持久化到 `~/.claude/projects/<sanitizedCwd>/<sessionId>.jsonl`。

`cwd` 默认为 companion 进程的工作目录，RimWorld 自动启动时设为 `<modRoot>/claude-sessions/`。

## 手动启动（调试用）

```bash
# 游戏设置中关闭 "自动启动本地 Companion"
node --import tsx/esm companion.ts --port 19999 --no-connect-timeout

# 远程 MCP Server + 自定义头
node --import tsx/esm companion.ts \
  --mcp-url https://remote:9877/mcp \
  --mcp-headers '{"Authorization":"Bearer xxx"}'

# 允许局域网连接
node --import tsx/esm companion.ts --host 0.0.0.0
```
