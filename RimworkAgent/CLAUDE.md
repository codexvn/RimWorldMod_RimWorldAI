# RimworkAgent

AI Colony Runtime — 多 Agent 自主管理殖民地。通过 MCP 协议连接 RimWorldMCP，集成 Claude Agent SDK。

**相关项目**:
- MCP Server: `F:\RiderProjects\RimWorldMCP\`（RimWorld Mod DLL，提供 99+ Tool）
- CC Companion: `F:\RiderProjects\RimWorldMCP\cc-companion\`（Node.js，CCB 桥接）

## 项目结构

```
RimworkAgent/
├── CLAUDE.md                               ← 本文件
├── design/                                 ← 设计文档
├── RimworkAgent.sln
├── RimworkAgent.Core/                      ← net472 共享库（零游戏依赖）
│   ├── AgentRuntime/ (8 files)
│   ├── Mcp/ (2 files)
│   └── CcbManager/ (1 file)
├── Loader/
│   ├── Exe/                                ← EXE Loader
│   │   ├── RimworkAgent.csproj
│   │   └── Program.cs                      ← 入口：CCB→MCP→Agent Loop
│   └── Mod/                                ← MOD Loader
│       ├── RimworkAgent.Mod.csproj
│       └── GameComponent_RimworkAgent.cs    ← GameComponent 驱动 Agent Loop
└── publish/                               ← MOD 输出
    ├── About/About.xml
    └── 1.6/Assemblies/
```

## 架构

### 零引用设计

RimworkAgent 和 RimWorldMCP **互不引用**，仅通过 MCP 协议通信。

```
RimworkAgent (EXE)               RimWorldMCP (Mod DLL)
┌──────────────────────┐        ┌──────────────────────┐
│ AgentRuntime         │  HTTP  │ MCP Server :9877     │
│ McpClient ───────────── POST ─→ /mcp (tools/call)    │
│ McpClient ───────────── GET ──→ /sse (事件推送)      │
│ CcbManager ── spawn ──→ cc-companion (Node.js)       │
└──────────────────────┘        └──────────────────────┘
```

### 两种启动模式

| 模式 | 进程 | 说明 |
|------|------|------|
| **EXE** | `RimworkAgent.exe` | 独立进程，开发/测试用 |
| **MOD** | RimWorld 加载 DLL | 游戏内运行 |

### Agent 分类

| Agent | 触发 | 职责 |
|-------|------|------|
| Overseer | 每天/12h | 策略规划、研究路线 |
| Economy | 每 2-6h | 生产建造 + 军械后勤 |
| Combat | L3 事件驱动 | 战斗指挥 |
| Medic | 每天 + 战斗后 | 医疗健康 + 仿生体 |

### 数据流

```
Scheduler (Load Score 0-100)
    → Wake Agent
    → ContextBuilder → MCP get_world_summary → Memory → TaskBoard
    → Claude API (via CCB WebSocket)
    → Process response (tool calls)
    → Update TaskBoard / Memory
    → Sleep
```

## 开发

- net472，仅 `System.Text.Json` NuGet 依赖
- `dotnet build` → `publish/1.6/Assemblies/`
- EXE 模式：`dotnet run` 或直接运行 `RimworkAgent.exe`
- Core 库零游戏依赖，可独立编译测试

### 关键约束

- 不引用 RimWorldMCP，不引用 Assembly-CSharp
- 所有游戏数据通过 MCP 获取
- 所有游戏操作通过 MCP Tool 调用
- TaskBoard/Memory 持久化为 JSON 文件
