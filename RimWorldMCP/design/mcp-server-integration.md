# MCP Server 集成

## 概述

基于官方 `ModelContextProtocol` v1.3.0 SDK 实现 MCP 服务端。SDK 提供 Streamable HTTP 传输和 JSON-RPC 协议实现，我们做 net472 适配层（HttpListener 代理 + per-session 管理 + 响应清洗）。

## 关键文件

| 文件 | 职责 |
|------|------|
| `../SimpleMspServer/McpServiceHost.cs` | HTTP 代理层：HttpListener 接收请求，路由到 SDK transport，回写响应 |
| `../SimpleMspServer/Mcp/McpMessage.cs` | 自有数据模型：`ToolDefinition`、`ToolCallResult`、`ContentItem` |
| `../SimpleMspServer/Mcp/IToolProvider.cs` | Tool 提供者接口（与 SDK `McpServerHandlers` 桥接） |
| `../RimWorldMCP/McpServiceManager.cs` | 游戏侧入口：创建 `McpServiceHost`，注册 `ToolRegistry` |
| `../RimWorldMCP/Tools/Tool_*.cs` | 99+ Tool 实现 |
| `../SimpleMspServer.Tests.Cli/Program.cs` | CLI 测试工具，可独立验证 MCP 服务 |

## SDK 接入方式

### 架构分层

```
MCP 客户端 (Claude Desktop / Agent)
       │
       ▼  HTTP (POST/GET/DELETE)
       │
┌── McpServiceHost ───────────────────────┐
│  HttpListener (AcceptLoop)              │  ← 自实现：HTTP 代理
│  ├─ POST /mcp ───→ HandleMcpPost        │
│  ├─ GET  /mcp ───→ HandleMcpSse         │
│  └─ DELETE /mcp ─→ HandleMcpDelete      │
│       │                                 │
│       ▼                                 │
│  StreamableHttpServerTransport          │  ← SDK：MCP 传输层
│  McpServer (per-session)                │  ← SDK：JSON-RPC 引擎
│       │                                 │
│       ▼                                 │
│  McpServerHandlers                      │  ← 自实现：tool 路由回调
│  ├─ ListToolsHandler → BuildToolList()  │
│  └─ CallToolHandler  → ExecuteAsync()   │
└─────────────────────────────────────────┘
```

### SDK 职责（不改动）

| 功能 | SDK API |
|------|---------|
| `initialize` 握手 | `HandlePostRequestAsync` 内部处理 |
| JSON-RPC 序列化/反序列化 | SDK `JsonRpcMessage` 类型体系 |
| SSE GET 流管理 | `HandleGetRequestAsync` |
| 服务端→客户端通知 | `SendMessageAsync(JsonRpcNotification)` |
| 工具执行路由 | `McpServerHandlers.CallToolHandler` |
| Session ID 管理 | `ITransport.SessionId`（读写） |

### 自实现部分（适配层）

| 适配 | 为什么必须自己写 |
|------|-----------------|
| HttpListener 代理 | net472 不支持 ASP.NET Core；SDK 的 `StreamableHttpServerTransport` 只读写 `Stream`，不管 HTTP 层 |
| per-session 创建/回收 | SDK transport 自身不管理 session 生命周期，需要外层 `_sessions` 字典 + UUID 生成 |
| `ExtractDataLine` | SDK POS 响应包裹 SSE 格式（`event: message\ndata: {...}`），客户端 Zod 校验需要纯 JSON |
| `Write` 写响应 | HttpListener 的 `OutputStream.Write` + `Close` 模式 |
| CORS | 自写 Origin 头检查 |
| 错误响应（-32700/-32002） | JSON-RPC 标准错误，SDK 不自动生成 |

## per-session 架构

### 为什么不能用共享 transport

SDK 的 `StreamableHttpServerTransport` 内部有一个 `Channel<JsonRpcMessage>` 接收消息、一个 SSE writer 发送通知。多个客户端共享同一个 transport 会：
- 通知推送到错误的客户端（无法区分目标）
- `HandleGetRequestAsync` 的 `Interlocked.Exchange` 单 GET 保护被绕过
- session 生命周期无法管理（不知道哪个客户端还在）

### 方案

每个 `initialize` 请求创建一套新的 transport + server：

```csharp
ConcurrentDictionary<string, McpSession> _sessions;

// 新会话
var session = CreateSession();   // new transport + new server + UUID
_sessions[session.SessionId] = session;
res.Headers.Add("Mcp-Session-Id", session.SessionId);

// 后续请求
_sessions.TryGetValue(sid, out var session);
await session.Transport.HandlePostRequestAsync(msg, ms, ct);

// 释放
_sessions.TryRemove(sid, out var session);
_ = DisposeSessionAsync(session, sid);  // fire-and-forget
```

对标 SDK ASP.NET Core 的 `StreamableHttpSession` + `StatefulSessionManager`，但更轻量（无空闲超时、无迁移）。

### McpSession 生命周期

```
CreateSession()
  ├─ new StreamableHttpServerTransport { SessionId = UUID }
  ├─ new McpServerOptions { Handlers = ... }
  ├─ McpServer.Create(transport, options)
  ├─ Task.Run(() => server.RunAsync(cts.Token))
  └─ return new McpSession(transport, server, runTask, cts)

DisposeAsync()
  ├─ _cts.Cancel()
  ├─ await _runTask     // 等待 RunAsync 结束
  ├─ server.Dispose()
  └─ _cts.Dispose()
```

## 响应清洗

### 问题

SDK `HandlePostRequestAsync` 输出 SSE 格式：
```
event: message
data: {"result":{...},"id":1,"jsonrpc":"2.0"}
```

MCP 客户端（Claude Desktop、Agent SDK）Zod 校验只认纯 JSON。

### 方案

`ExtractDataLine` 提取 `data:` 行 JSON，再用 `JsonNode.ToJsonString(_cleanJsonOpts)` 剔除 SDK 序列化时多余的 null 字段：

```csharp
private static string ExtractDataLine(string sse)
{
    string json = sse;
    foreach (var line in sse.Split('\n'))
    { var t = line.Trim(); if (t.StartsWith("data:")) { json = t.Substring(5).Trim(); break; } }
    return System.Text.Json.Nodes.JsonNode.Parse(json)?.ToJsonString(_cleanJsonOpts) ?? json;
}
```

Content-Type 响应头设为 `application/json`。

## 通知通道

### 游戏事件 → MCP 通知

```
NotificationBus.Enqueue(notification)
  └─ SendEvent(jsonData)
       └─ 遍历 _sessions
            └─ session.SendNotificationAsync("game/event", jsonData)
                 └─ transport.SendMessageAsync(JsonRpcNotification)
                      └─ 写入 SSE GET 流
```

所有已连接客户端通过 GET /mcp SSE 实时接收游戏事件。

### 触发源

| 事件 | 方法 | 频率 |
|------|------|------|
| 游戏 tick | `GameComponentUpdate` → `PushTickEvent` | 每 60 tick (~1s) |
| 战斗/威胁/死亡等 | `NotificationBus.Enqueue` → `SendEvent` | 事件驱动 |
| 物品腐坏跨阈值 | `DeteriorationTracker.CheckAndNotify` → `SendEvent` | 每 6000 tick 扫描 |

## net472 兼容要点

| 约束 | 影响 | 处理 |
|------|------|------|
| 无 ASP.NET Core | 不能用 `ModelContextProtocol.AspNetCore` | 自写 HttpListener 代理 |
| Mono/Broken BCL | `System.Text.Json.Nodes` 需要 NuGet | 项目已引用 |
| 无 `ConfigureAwait` 上下文 | 线程池线程无 SyncContext | 不会死锁，但需全链路 async |
| `HttpListener` 前缀权限 | `0.0.0.0` 需要 `netsh urlacl` | 部署文档说明 |

## CLI 测试

`SimpleMspServer.Tests.Cli` 提供独立可运行的 MCP 测试服务：

```bash
dotnet run --project SimpleMspServer.Tests.Cli
# 或
.\SimpleMspServer.Tests.Cli\bin\Debug\SimpleMspTest.exe [端口]
```

内置 `DemoProvider`（hello + echo），每 5 秒自动发测试事件。验证流程：

```bash
# 终端 1：启动
.\SimpleMspTest.exe

# 终端 2：初始化
curl -s -D - -X POST http://localhost:29878/mcp \
  -H "Content-Type: application/json" \
  -d '{"jsonrpc":"2.0","id":1,"method":"initialize",...}'
# 提取 Mcp-Session-Id 响应头

# 终端 3：监听 SSE 事件
curl -N -H "Mcp-Session-Id: <sid>" http://localhost:29878/mcp
```
