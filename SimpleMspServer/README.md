# SimpleMspServer

MCP 协议共享库 — JSON-RPC 2.0 + SSE/HTTP Transport。被 RimWorldMCP 和 RimWorldAgent 共同引用。

## 文件

| 文件 | 职责 |
|------|------|
| `McpMessage.cs` | JSON-RPC 2.0 类型定义 |
| `ITransport.cs` | 传输层接口 |
| `SseTransport.cs` | SSE + HTTP 传输实现 |
| `StreamableHttpTransport.cs` | Streamable HTTP 传输 |
| `SimpleLog.cs` | 日志工具 |

## 构建

```bash
cd F:\RiderProjects\RimWorldMCP
dotnet build SimpleMspServer/SimpleMspServer.csproj
```

## 相关项目

- MCP Server: `../RimWorldMCP/`（使用本库作为传输层）
- Agent Runtime: `../RimWorldAgent/`（使用本库作为 MCP 客户端传输）
