# RimWorld Agent

AI 殖民地操作系统 — 连接 Claude API，让 AI 成为你的 RimWorld 指挥官。

## 这是什么？

RimWorld Agent 是一个 AI 运行时，通过 **MCP 协议**与游戏无缝通信，集成 **Claude Agent SDK**，实现 AI 自主管理殖民地。AI 会规划策略、执行操作、应对突发事件——你可以通过对话框观察或随时介入。

**核心流程**：游戏状态 → MCP 服务器 → Agent 调度循环 → ACP session → AI 决策 → MCP 工具调用 → 游戏执行。

## 功能特性

### Plan / Act 双阶段

AI 自主在两个阶段间切换：

- **Plan（规划）**：暂停游戏，分析殖民地全局状态，制定策略计划。冷启动和每日早报时自动进入。
- **Act（执行）**：恢复游戏速度，执行计划中的操作。AI 通过 `enter_plan` / `enter_act` 自主切换。

每次工具调用结果末尾会注入阶段提醒——Plan 停留过久提示 `enter_act()`，Act 暂停过久提示恢复速度。

### 事件中断系统

游戏事件按严重程度分四级，Critical/Warning 立即中断 AI 当前任务，Info/Silent 通过工具结果后缀静默注入，确保 AI 下次工具调用时不遗漏：

| 级别 | 中断 | 后缀注入 | 典型事件 |
|------|------|---------|---------|
| **L3 Critical** | ✅ 暂停游戏，紧急处理 | ✅ | 突袭、Boss 战、角色死亡 |
| **L2 Warning** | ✅ 立即中断 | ✅ | 仪式失败、角色被困 |
| **L1 Info** | ❌ | ✅ | 商队来访、任务完成 |
| **L0 Silent** | ❌ | ✅ | 技能升级、操作被拒 |

### ACP 运行时桥接

通过 Node.js 子进程（`rimworld-acp-host`）承载官方 ACP TypeScript SDK，并管理 `claude-agent-acp` backend。C# 侧只通过自定义 IPC DTO + NDJSON stdin/stdout 调用 Node Host；ACP `session/update` 先转换为 runtime event，再投影为现有 UI DTO，保持 UI 兼容。

Mod 设置页可以选择内置或自定义 ACP Backend，并直接配置自定义 Backend 的启动命令、参数、工作目录和非敏感环境变量。Node.js 22+ 路径默认自动检测，也可以手动覆盖。Backend 的认证、API 地址、模型和 Provider 配置由 Backend 自身或父进程环境管理；Mod UI 不保存这些凭据。Agent 运行数据目录由程序内部管理，不在主设置页暴露。

### 用户约束 Prompt

Prompt.md 保存殖民地 Agent 的稳定行为约束。C# 在 initialize 阶段读取，并通过 IPC `AgentRuntimeConfig.prompt` 发送给 Node Host；为兼容不支持 `_meta.systemPrompt` 扩展的 ACP 后端，NodeAgentSession 只在每个 ACP session 的首次 `session/prompt` 前置发送一次。启动前还会从 SkillRegistry 生成项目目录下的 skills-desc.txt，替换 {skillsTable}。动态世界状态和用户消息仍通过 IPC prompt → ACP session/prompt 发送。Prompt 文件缺失或为空会阻止 Agent session 启动，避免 Agent 在无约束状态下运行。

### 100+ 游戏工具代理

Agent MCP Server（`:9878`）代理了 [RimWorld MCP](https://steamcommunity.com/sharedfiles/filedetails/?id=3732261759) 的全部 100+ 工具，覆盖建造、制造、战斗、医疗、贸易、种植、研究等全游戏操作。AI 像玩家一样操作游戏。

### 领域 Skill 系统

13 个 Markdown 知识文件为 AI 提供领域专长——基地布局、装备制造、战斗部署、医疗策略等。AI 可自行激活相应 Skill 来获取特定领域的专业指导。

### 对话交互

通过 [RimWorld Agent UI](https://steamcommunity.com/sharedfiles/filedetails/?id=3732261754)（游戏内 `Ctrl+Shift+C` 对话框），或浏览器访问 `http://127.0.0.1:19997` 使用 Web 面板。你可以随时查看 AI 的思考过程、工具调用状态，或直接与 AI 对话。

### 双工通知机制

Agent 与游戏之间通过 **Tool Result Suffix** 实现双向通知：Agent 调用 `set_tool_result_suffix` 设置一次性后缀，MCP Server 在下一次工具结果末尾自动追加并清空。游戏事件（Info/Silent 级）和阶段提醒均通过此后缀注入，AI 每次工具调用都能感知。

### Plan/Act 提醒

每次工具结果末尾自动注入提醒，避免 AI 停留在错误状态：

| 提醒 | 触发条件 | 提示 |
|------|---------|------|
| ACT 暂停 | ACT 模式 + 游戏暂停 ≥5 次 | `enter_act(speed)` 恢复游戏 |
| ACT 执行过久 | ACT 模式 ≥10 次 | `enter_plan()` 审视进度 |
| PLAN 停留过久 | PLAN 模式 ≥10 次 | 制定计划后 `enter_act()` |
| 通知堆积 | 未读通知 ≥5 条 | 调用 `get_notifications` 查看 |

样例 — AI 在工具结果末尾看到的内容：

```
---
当前模式: ACT

<system-reminder>
游戏仍处于暂停状态！你在 ACT 阶段，如需推进工作进度请调用 enter_act(speed="暂停/正常/高速/极速") 恢复游戏。
</system-reminder>

<system-reminder>
你已在 ACT 阶段连续执行 10+ 轮工具调用。建议调用 enter_plan() 暂停游戏、审视进度、更新计划，然后再继续执行。
</system-reminder>
```

### Task 任务机制

AI 通过内部工具 `task_create` / `task_update` / `task_list` / `task_get` 替代 SDK 原生 Task 工具管理任务列表。提醒机制会在 AI 未使用 task 工具 ≥15 次时提示创建任务，任务未完成 ≥10 次时列出待办项。任务状态实时显示在 UI 右侧面板。

样例 — UI 右侧面板中 AI 的任务视图：

```
── 任务 ──
[>] 扩大种植区到 10x10            ← 进行中
[ ] 为殖民者打造防弹背心            ← 待完成
[ ] 研究微电子基础                 ← 待完成
[✓] 建造医疗室                    ← 已完成
```

样例 — AI 长时间未用 task 工具时的提醒：

```
<system-reminder>
你已连续 15+ 轮工具调用未使用 task_create / task_update。建议调用 task_create 制定任务计划、跟踪执行进度。完成后用 task_update(status="completed") 标记。
</system-reminder>
```

## 两种运行模式

| 模式 | 进程 | 存储 | 说明 |
|------|------|------|------|
| **MOD** | RimWorld 加载 DLL | 存档内 Scribe 持久化 | 推荐，游戏内直接运行 |
| **EXE** | 独立进程 | JSON + SQLite | 开发测试用，远程连接游戏 |

## 技术架构

```mermaid
RimWorldAgent                         RimWorldMCP
┌──────────────────────┐              ┌──────────────────────┐
│ AgentEngine          │  HTTP POST   │ MCP Server :9877     │
│ McpClient ────────────→ /mcp ──────→ 100+ 游戏工具        │
│ McpClient ←──────────── /sse ←────── tick 推送 + 事件     │
│                      │              │                      │
│ AgentMcpServer :9878 ←─ tools/call ──────────────────────── SDK 工具调用
│                      │              │                      │
│ NodeAgentHost ── IPC NDJSON ──→ rimworld-acp-host       │
│                      │   ACP stdio → claude-agent-acp    │
│ UIMessageBus :19999 ──→ UI 广播     │                      │
└──────────────────────┘              └──────────────────────┘
```

**零引用设计**：RimWorldAgent 与 RimWorldMCP 互不引用，仅通过 MCP 协议通信。

**端口一览**：

| 端口 | 服务 | 说明 |
|------|------|------|
| `:9877` | 游戏 MCP Server | 游戏内工具 + 事件推送 |
| `:9878` | Agent MCP Server | 代理游戏工具 + 内部工具 |
| stdio | claude-agent-acp | ACP transport（无监听端口） |
| `:19999` | UIMessageBus | UI 消息广播 |

## 工具清单

Agent 内部工具（AgentMCP `:9878`），指导 AI 行为：

| Tool | 说明 |
|------|------|
| `get_skills` | 获取可用领域技能列表 |
| `active_skill` | 激活指定领域技能 |
| `enter_plan` | 暂停游戏，进入规划阶段（可选 `reason` 参数记录原因） |
| `enter_act` | 恢复游戏速度，进入执行阶段（可选 `reason` 参数记录原因） |
| `read_memory` | 读取持久记忆文件 |
| `update_memory` | 写入/更新持久记忆文件 |
| `set_tool_result_suffix` | 设置下次工具结果的一次性后缀 |

代理游戏工具：全部 100+ [RimWorld MCP](https://steamcommunity.com/sharedfiles/filedetails/?id=3732261759) 工具，通过 `mcp__agent__*` 调用。

## 相关 Mod

- [RimWorld MCP](https://steamcommunity.com/sharedfiles/filedetails/?id=3732261759) — 游戏工具接口（必需）
- [RimWorld Agent UI](https://steamcommunity.com/sharedfiles/filedetails/?id=3732261754) — 游戏内对话窗口（推荐）
