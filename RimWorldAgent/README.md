# RimWorldAgent

Agent Runtime — 多 Agent 自主管理 RimWorld 殖民地。通过 MCP 协议连接 RimWorldMCP，集成 Claude Agent SDK。

## 架构

```
Overseer (策略, 每天/12h) → 全局摘要分析 → 发布 TaskBoard 目标
Economy (生产+军械, 每 2-6h) → 建造/制造/装备分配
Combat (战斗, L3 事件驱动) → 暂停→分析→部署→接敌→收尾→退出
Medic (医疗, 每天+战斗后) → 治疗/手术/仿生体
```

- **零引用**：与 RimWorldMCP 互不引用，仅通过 MCP 协议通信
- **CCB 桥接**：spawn Node.js cc-companion，连接 Claude Agent SDK
- **两种启动模式**：EXE（独立进程开发测试）/ MOD（游戏内运行）

## 项目结构

```
RimWorldAgent/
├── Core/
│   ├── AgentRuntime/   Scheduler / TaskBoard / Memory / AgentOrchestrator
│   ├── Mcp/            MCP 客户端 + Agent MCP Server (:9878)
│   └── CcbManager/     CCB 子进程管理 + WebSocket
├── Exe/                EXE Loader (Program.cs)
├── Mod/                MOD Loader (GameComponent + UI)
├── cc-companion/       Node.js CCB 桥接
└── resource/           MOD 元数据 + Skill 知识文件
```

## 构建

```bash
cd F:\RiderProjects\RimWorldMCP
dotnet build RimWorldAgent/RimWorldAgent.csproj
```

输出到 `publish/RimWorldAgent/1.6/Assemblies/`。

## 运行

```bash
# EXE 模式（开发/测试）
dotnet run --project RimWorldAgent/RimWorldAgent.csproj

# MOD 模式：将 publish/RimWorldAgent/ 放入 RimWorld Mods，启动游戏即可
```

## 相关项目

- MCP Server: `../RimWorldMCP/`（游戏 Mod DLL，99+ Tool）
- MCP 协议库: `../SimpleMspServer/`（JSON-RPC + Transport）
