# RimWorld AI

AI Colony Operating System — 多 Agent 自主管理 RimWorld 殖民地。

## 项目结构

```
RimWorldAI/
├── SimpleMspServer/          ← MCP 协议共享库 (net472)
│   ├── McpMessage.cs         JSON-RPC 2.0 类型
│   ├── ITransport.cs         传输接口
│   ├── SseTransport.cs       SSE + HTTP 传输
│   └── StreamableHttpTransport.cs
│
├── RimWorldMCP/              ← 游戏 Mod (net472)
│   ├── Tools/                99 个游戏 Tool
│   ├── MCP Server :9877
│   ├── Harmony/              事件拦截
│   └── Transport/            StdioTransport
│
└── RimWorldAgent/             ← Agent Runtime (net472)
    ├── Core/                 共享库
    │   ├── AgentRuntime/     4 Agent + Scheduler + TaskBoard
    │   ├── Mcp/              MCP 客户端 + Agent MCP Server
    │   ├── CcbManager/       CCB 子进程 + WebSocket
    │   └── Skills/           领域知识
    ├── Loader/Exe/          EXE 启动模式
    ├── Loader/Mod/          MOD 启动模式
    └── cc-companion/        Node.js CCB 桥接

三者互不引用：RimWorldMCP ↔ RimWorldAgent 仅通过 MCP 协议通信。
SimpleMspServer 被两者共同引用。
```

## 构建

```
cd F:\RiderProjects\RimWorldAI
dotnet build RimWorldAI.sln
```

5 个子项目，全部 net472，统一编译。
