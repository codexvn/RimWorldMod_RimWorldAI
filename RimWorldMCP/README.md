# RimWorldMCP

MCP Server — 将 RimWorld 游戏状态和操作暴露为 LLM 可调用的 Tool。作为 RimWorld Mod DLL 内嵌运行。

## 架构

- **net472 Library**，引用 Assembly-CSharp.dll 直接调用游戏 API
- **MCP Server :9877**，HTTP + SSE 双通道
- **99 个 Tool**，覆盖查询/建造/制造/战斗/医疗/贸易等全游戏操作
- **事件系统**，6 个 Harmony Patch 拦截游戏事件，L0-L3 四级分级推送

## 项目结构

```
RimWorldMCP/
├── Tools/          99 个游戏 Tool
├── Mcp/            MCP Server (JSON-RPC dispatch)
├── Harmony/        事件拦截 (NotificationBus)
├── Bridge/         stub（CC 桥接已迁至 Agent）
├── Transport/      SseTransport (HTTP + SSE)
├── Skills/         领域知识 Skill 系统
└── resource/       MOD 元数据 + 语言文件
```

## 构建

```bash
cd F:\RiderProjects\RimWorldMCP
dotnet build RimWorldMCP/RimWorldMCP.csproj
```

输出到 `publish/RimWorldMCP/1.6/Assemblies/RimWorldMCP.dll`。

## 部署

将 `publish/RimWorldMCP/` 目录放入 RimWorld Mods 文件夹。游戏启动后 MCP 服务自动运行在 `http://localhost:9877`。

## 相关项目

- Agent Runtime: `../RimWorldAgent/`（多 Agent 自主管理，通过 MCP 协议通信）
- MCP 协议库: `../SimpleMspServer/`（JSON-RPC + Transport）
