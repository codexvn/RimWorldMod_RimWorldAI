# RimWorld AI — AI Colony Operating System

多 Agent 自主管理 RimWorld 殖民地。

## ⚠️ 免责声明

本项目为个人学习与技术探索项目，不保证稳定性、鲁棒性和用户体验。使用产生的一切后果自行承担。

---

## 项目

| 项目 | 说明 | 输出 |
|------|------|------|
| **SimpleMspServer** | MCP 协议共享库 (JSON-RPC + Transport) | SimpleMspServer.dll |
| **RimWorldMCP** | 游戏 MOD — MCP Server + 99 Tool | RimWorldMCP.dll (MOD) |
| **RimWorldAgent** | Agent Runtime — ACP session + MCP tools | RimWorldAgent.exe + MOD |

## 架构

```
RimWorldAgent ←── MCP (HTTP+SSE) ──→ RimWorldMCP ←── 游戏 API
       │
       └── ACP stdio → claude-agent-acp → Claude 后端
```

Agent 和 MCP 互不引用，仅通过 MCP 协议通信。RimWorldAgent 内部通过 ACP session 管理后端会话，并将 ACP 更新投影为现有 UI DTO。SimpleMspServer 是共享协议库。

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
