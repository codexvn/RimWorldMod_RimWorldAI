# RimWorld AI — AI Colony Operating System

多 Agent 自主管理 RimWorld 殖民地。

## 项目

| 项目 | 说明 | 输出 |
|------|------|------|
| **SimpleMspServer** | MCP 协议共享库 (JSON-RPC + Transport) | SimpleMspServer.dll |
| **RimWorldMCP** | 游戏 MOD — MCP Server + 99 Tool | RimWorldMCP.dll (MOD) |
| **RimWorldAgent** | Agent Runtime — 4 Agent + CCB | RimWorldAgent.exe + MOD |

## 架构

```
RimWorldAgent ←── MCP (HTTP+SSE) ──→ RimWorldMCP ←── 游戏 API
       ↕ MCP
    CCB SDK → Claude API
```

Agent 和 MCP 互不引用，仅通过 MCP 协议通信。SimpleMspServer 是共享协议库。

## 构建

```bash
dotnet build RimWorldAI.sln
```

发布到 `publish/RimWorldMCP/` 和 `publish/RimWorldAgent/`。

## 链接到游戏 Mod 目录

以管理员权限运行：

```cmd
mklink /D F:\SteamLibrary\steamapps\common\RimWorld\Mods\RimWorldMCP F:\RiderProjects\RimWorldMCP\publish\RimWorldMCP
mklink /D F:\SteamLibrary\steamapps\common\RimWorld\Mods\RimWorldAgent F:\RiderProjects\RimWorldMCP\publish\RimWorldAgent
```
