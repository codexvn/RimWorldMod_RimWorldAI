# Agent Runtime — AI Colony Operating System

## 技术栈

| 模块 | 技术 |
|------|------|
| Game Mod | C# RimWorld Mod |
| AI 对话 | Claude Agent SDK (Node.js/TS) |
| AI 自主决策 | Anthropic API |
| IPC | MCP Streamable HTTP + WebSocket MessageBus |
| 上下文缓存 | Prompt Caching (Anthropic 原生) |
| 持久化 | JSON 文件（TaskBoard、Metrics、Daily Report） |
| Chunk 编码 | RLE + RowRefRLE（已有） |
| 空间索引 | 不做 |

## 架构决策

1. 对话用 SDK，自主决策用 API
2. C# + CCB + MessageBus 统一对接多前端 + MCP
3. Combat/Emergency 走 LLM — 游戏暂停后 AI 决策
4. 现有 Chunk 系统直接用 — 32×32, RLE/RowRefRLE
5. 不加空间索引
6. 现有 Agent 代码全删重写

## Scheduler / Colony Load Score

Colony Load Score 0~100，动态控制 AI 轮询频率。

| Load | Mode | AI 轮询频率 |
|------|------|------------|
| 0~20 | Peace | 12h 游戏时间 |
| 20~40 | Normal | 6h |
| 40~60 | Busy | 2h |
| 60~80 | HighPressure | 30min |
| 80~100 | Crisis | 实时/持续 |

Scheduler 控制 Runtime 轮询频率，不影响 TickManager。

## Multi-Agent

4 个 Agent，分层决策：

```
                    Overseer (策略)
                    每天/12h
                        │
        ┌───────────────┼───────────────┐
        ▼               ▼               ▼
    Economy           Combat          Medic
    (生产建造+军械)   (战斗指挥)       (医疗健康)
    每 2-6h           事件驱动         每天 + 战斗后
                       即时唤醒         事件驱动
        │               │               │
        └───────────────┼───────────────┘
                        ▼
                    TaskBoard
              (跨 Agent 任务协作)
```

| Agent | 触发频率 | 优先级 | 可见 Tool |
|-------|---------|--------|----------|
| Overseer | 每天/12h | 最低 | ~20 |
| Economy | 每 2-6h | 正常 | ~60 |
| Medic | 每天 + 战斗后 + L3 医疗 | 正常/即时 | ~20 |
| Combat | L3 战斗事件 | 最高 | ~30 |

## 游戏速度与暂停策略

AI 不干涉玩家设的游戏速度，只在危机时暂停。

| Load | 调度频率 | 1x 下间隔 | 3x 下间隔 | 暂停 |
|------|---------|----------|----------|------|
| Peace | 12h | ~12 分钟 | ~2.4 分钟 | 不暂停 |
| Normal | 6h | ~6 分钟 | ~1.2 分钟 | 不暂停 |
| Busy | 2h | ~2 分钟 | ~24 秒 | 不暂停 |
| HighPressure | 30min | ~30 秒 | ~10 秒 | 不暂停 |
| Crisis | 实时 | 暂停 | 暂停 | 暂停 |

## 三层架构

```
┌──────────────────────────────────────────┐
│          RimWorld Process (C#)           │
│                                          │
│  ┌────────────────────────────────┐      │
│  │     Agent Runtime (NEW)        │      │
│  │  Scheduler                     │      │
│  │  ContextBuilder                │      │
│  │  TaskBoard                     │      │
│  │  MemoryManager                 │      │
│  │  AgentOrchestrator             │      │
│  └──────┬──────────────┬─────────┘      │
│         │ HTTP         │ WebSocket      │
│         ▼              ▼                │
│  ┌──────────┐  ┌───────────────┐        │
│  │MCP Server│  │ MessageBus    │        │
│  │  :9877   │  │ (事件推送)    │        │
│  └──────────┘  └───────────────┘        │
└───────────────┬─────────────┬───────────┘
                │             │
┌───────────────┴─────────────┴───────────┐
│          CCB (Node.js)                  │
│          WebSocket Server               │
│          SDK Session Manager            │
│          数据显示 (Web UI + 游戏内UI)    │
│          ❌ 不包含 Agent 业务逻辑        │
└───────────────┬─────────────────────────┘
                │
                ▼
          Claude API
```

- Agent Runtime 通过 HTTP 调 MCP Server
- Agent Runtime 通过 WebSocket 调 CCB 的 SDK Session
- CCB 是纯桥接 + 数据显示，不含 Agent 编排、Scheduler、TaskBoard
- MCP Server 不知道 Agent 的存在

## Agent 运行模型

每个 SubAgent 独立工作目录（`sessions/{sessionId}/{agentName}/`），每次用不同的 cwd（`{timestamp}/`），CC 自己管理历史，Runtime 不碰。

### Reflection Loop

SDK session 每次重置，Agent 结束前把"经验教训"写入记忆文件，下轮注入 Prompt。

```
                      ┌─────────────────┐
                      │   记忆文件        │ ← JSON 持久化，固定长度
                      │   上次的教训      │
                      └────────┬────────┘
                               │ 读取注入 Prompt
                               ▼
Scheduler → Context Builder → API query → 产出
                               │
                               ▼
                      ┌─────────────────┐
                      │   Reflection     │ ← Agent 结束前自己总结
                      │   写入记忆文件   │
                      └─────────────────┘
```

### 三个持久化层次

| 层 | 存什么 | 由谁写 | 由谁读 |
|-----|--------|--------|--------|
| 记忆文件 | Agent 的经验教训 | Agent 自己总结 | 同一 Agent 下轮 |
| TaskBoard | 跨 Agent 任务状态 | 所有 Agent | 所有 Agent |
| RimWorld 存档 | 游戏世界状态 | 游戏本体 + C# Mod | MCP Tool |

## Agent 生命周期

### Overseer / Economy / Medic（定时触发）

```
Scheduler 到点 → 读记忆文件 → 构建 Prompt → query → 产出 → 写记忆文件 → 下次用新 cwd
```

### Combat Agent（事件驱动 + 自终止循环）

4 阶段工作流，AI 自主调用 `exit_combat_role` 退出。

```
Phase 1 — 暂停分析:
  Raid → 暂停 → find_enemies + get_colonists + get_tile_grid/detail

Phase 2 — 部署:
  敌人路上 → draft_pawn + move_pawn + equip_pawn → 等敌人接近

Phase 3 — 接敌 (1x):
  attack_pawn / force_attack → tend_now (我方) → 实时观察

Phase 4 — 收尾:
  敌人全灭/逃跑 → capture_pawn + strip_pawn + haul_item + tend_now → 解除征召 → exit_combat_role
```

兜底：Round N+1 提示 "调用 exit_combat_role" → Round N+3 仍不退出 → 强制退出

### Economy Agent（生产建造 + 军械后勤）

```
Economy 每轮:
  1. 读 TaskBoard (Overseer 优先级 + Combat 装备请求 + Medic 药品请求)
  2. 盘点库存 (get_resources, find_equipment)
  3. get_recommended_weapon + get_recommended_apparel
  4. get_colonists → 检查装备 → create_production_bill / equip_pawn
  5. 处理 Medic 的仿生体/药品请求
```

### Medic Agent（医疗健康）

触发: L3 医疗事件立即唤醒 / L2 医疗事件排队 / Combat 结束立即唤醒 / 每天定时

## Prompt 设计

### 结构（固定顺序）

```
[System Prompt]        ← 每个 Agent 不同
[Memory]               ← 从记忆文件读取, cache_control breakpoint
[World Summary]        ← cache_control breakpoint
[Active Alerts]
[TaskBoard]
[Relevant Chunks]      ← 每轮不同
[上轮命令执行反馈]     ← 每轮不同
```

前三层命中 Prompt Cache → Token 消耗集中在 Chunks + Feedback。

### 每个 Agent 的 System Prompt

| Agent | 视角 | 禁止 |
|-------|------|------|
| Overseer | 策略级，只看全局摘要 | 不操作具体单位/格子 |
| Economy | 生产建造 + 军械后勤 | 不制定长期策略 |
| Combat | 战斗级，看敌我位置 Chunk | 不管生产和建造 |
| Medic | 医疗健康 + 仿生体管理 | 不管打仗和建造 |

## TaskBoard

跨 Agent 通信的唯一媒介。Agent 之间不直接对话。

| ID | 目标 | Agent | 状态 | 进度 |
|----|------|-------|------|------|
| 1 | 研发地热发电 | overseer | queued | - |
| 2 | 扩建粮食储备 | overseer | queued | - |
| 3 | 建造太阳能板×3 | economy | running | 67% |
| 4 | 生产步枪×2 | economy | blocked(缺钢) | 0% |

## 事件路由

L3 → 立即唤醒；L1-L2 → 排队等 Agent 自然醒来。

| 事件 | 级别 | 路由 | 触发 |
|------|------|------|------|
| Raid / 虫灾 / 围攻 / 猎杀人类 | L3 | Combat | 立即 |
| 火灾 | L3 | Combat + Economy | 立即 |
| 食物/药品不足 | L2 | Economy + Overseer | 排队 |
| 研究完成 | L1 | Overseer | 排队 |
| 建造/制作完成 | L1 | Economy | 排队 |
| 技能升级/操作被拒 | L0 | 丢弃 | - |

## Tool 过滤

`ITool` 加 `AgentAffinity` 属性（`[Flags] enum`），`tools/list` 传 `agent` 参数过滤。

## 工作预估

所有写操作 Tool 返回时必须带材料和工时预估。

```
⏱ 纯工时: 420 ticks, 预估实际: ~2-3x (打断)
⚠ 库存木材×3, 缺口×2
```

## 错误恢复

| 场景 | 处理 |
|------|------|
| Agent 崩溃 | ERROR 日志 + 回调通知玩家 |
| Tool 超时 | AI 自己决定重试或换方案 |
| 不可执行计划 | Tool 参数校验 + 游戏条件检查返回 Error, AI 修正 |
| 退主菜单 | ExposeData 保存状态, 读档恢复 |
| 存档/读档 | 检测 ProgramState, 非 Playing 时暂停 |

## 实施路径

1. Agent Runtime Foundation — Scheduler + TaskBoard + MemoryManager + AgentOrchestrator
2. 新增 Tool — `get_world_summary` + `exit_combat_role` + Tool 过滤 + chunk_id
3. Agent 实现 — Overseer + Economy + Combat + Medic
4. 事件路由 + 游戏速度
5. CCB 重构 — 删除 Agent 业务，保留桥接 + 数据显示
6. 优化 — Prompt Cache 调优 + Token 预算联动 + Daily Report

## C# 改动范围

| 模块 | 改动 | 量 |
|------|------|---|
| AgentRuntime/ (新) | 新增 | 大 |
| BridgeLifecycle / CCClient | 删除重写 | 大 |
| McpServiceManager / McpServer | 不变 | 零 |
| McpCommandQueue | 不变 | 零 |
| ToolRegistry + 99 Tools | 不变 | 零 |
| 新增 Tool | ~5 个文件 | 小 |
| NotificationBus | 加路由 | 小 |
| McpModSettings | 配置调整 | 小 |
| Chunk 系统 | 不变 | 零 |

## 硬约束

1. 游戏主线程单线程 — 所有状态读写必须经 `McpCommandQueue.DispatchAsync()`
2. `Find.CurrentMap` 可能为 null — 返回主菜单时优雅降级
3. HttpListener 端口占用 — 每台机器单实例
4. 存档由游戏主线程触发 — 外部无法直接读写存档
