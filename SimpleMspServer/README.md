# SimpleMspServer

MCP 协议共享库 — 基于官方 `ModelContextProtocol` v1.3.0 SDK，为 net472 提供 Streamable HTTP MCP 服务端。被 `RimWorldMCP` 和 `RimWorldAgent` 共同引用。

## 架构

```
MCP 客户端
  │  HTTP POST/GET/DELETE
  ▼
McpServiceHost (HttpListener 代理)
  ├─ 会话管理 (per-session transport + server)
  ├─ 请求路由 (POST→JSON-RPC, GET→SSE, DELETE→释放)
  └─ SDK 透传 (响应不加修改)
  │
  ▼
StreamableHttpServerTransport (SDK)
McpServer (SDK)
  └─ McpServerHandlers → IToolProvider
```

所有 MCP 协议逻辑（initialize 握手、tools/list、tools/call、SSE 流、通知发送）由 SDK 处理，`McpServiceHost` 只做 HTTP 代理 + session 管理。

## 文件

| 文件 | 职责 |
|------|------|
| `McpServiceHost.cs` | HTTP 代理层：HttpListener 接收、session 管理、SDK 透传 |
| `Mcp/McpMessage.cs` | 自有数据模型：`ToolDefinition`、`ToolCallResult`、`ContentItem` |
| `Mcp/IToolProvider.cs` | Tool 提供者接口，与 SDK `McpServerHandlers` 桥接 |
| `IMspLog.cs` | 日志接口 |

## 构建

```bash
dotnet build SimpleMspServer/SimpleMspServer.csproj
```

## CLI 测试

```bash
# 启动测试服务
.\SimpleMspServer.Tests.Cli\bin\Debug\SimpleMspTest.exe [端口]

# 初始化
curl -s -D - -X POST http://localhost:29878/mcp \
  -H "Content-Type: application/json" \
  -d '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-03-26","capabilities":{},"clientInfo":{"name":"test","version":"1.0"}}}'

# 提取 Mcp-Session-Id → 后续请求带此 header
```

## 依赖

- `ModelContextProtocol` 1.3.0
- `System.ValueTuple` 4.6.2
- net472

## 相关项目

- MCP Server: `../RimWorldMCP/` — 游戏 Mod DLL，99+ Tool
- Agent Runtime: `../RimWorldAgent/` — 自主管理殖民地的多 Agent 系统
- 设计文档: `../RimWorldMCP/design/mcp-server-integration.md`
