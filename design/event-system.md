# 事件系统

## 概述

拦截 RimWorld 游戏事件（袭击、死亡、负面事件等），推送到 AI 聊天，并按严重程度分级。高危事件自动暂停游戏并催促 AI 收尾。

## 关键文件

| 文件 | 职责 |
|------|------|
| `Bridge/BridgeLifecycle.cs` | `CCEventTick()` 每帧事件处理、暂停管理、定期轮询 |
| `Bridge/NotificationBus.cs` | InterceptedEvent 数据类、事件级别枚举 |
| `Bridge/ChatDisplayState.cs` | `IsBusy`, `DangerPaused`, `DangerSummary`, `CurrentBudgetStatus` 等静态字段 |

## 中断来源

6 个 Harmony Patch 拦截以下事件源：

| 事件源 | 拦截点 |
|--------|--------|
| Letter | 信件到达（大威胁、小威胁、死亡、负面、正面、事件等） |
| Message | 实时消息（大威胁、小威胁、死亡、健康事件、负面、游戏减速等） |
| Alert | 警报触发（饥饿、崩溃风险等） |

## 事件分级

事件分为 4 级，按严重程度差异化响应：

| 级别 | 来源条件 | 行为 |
|------|---------|------|
| **L3 Critical** | 大威胁、小威胁、死亡、负面事件、Boss 组、游戏减速、全部 Alert | **暂停游戏**（AI 忙时）+ DangerSummary 催促 |
| **L2 Warning** | 仪式失败、警告输入 | 不暂停，注入工具返回值末尾提醒 |
| **L1 Info** | 正面事件、来人、成长、任务完成、状态解除 | 不暂停，注入工具返回值末尾提醒 |
| **L0 Silent** | 选择角色、游戏结束、拒绝输入、SilentInput、捆绑信件 | 不推送也不注入 |

**设计理由**：不分级会导致每种弹框都触发暂停，骚扰玩家。L3 仅对真正需要立即处理的威胁暂停。L1/L2 仅在 Tool 返回末尾注入一条短提醒（如 `📋 新事件: ⚠x1 ℹ️x3`），不打断 AI 当前推理。

## 事件驱动暂停流程

`BridgeLifecycle.CCEventTick()` 每帧执行 4 层处理：

### 第 1 层：事件分级响应

```
Drain 通知队列 → GetEventLevel() 分级 → 分流处理
```

- **L3 Critical**：AI 忙时暂停游戏 → 构建 DangerSummary（≤60 字符）→ 注入 Tool 返回值
- **L2/L1**：累加到 `_pendingLevel12Count`，Tool 返回末尾注入 `📋 新事件: ⚠x1 ℹ️x3 | 如需处理请用 toggle_pause 暂停`

### 第 2 层：定期轮询

120 tick + wall clock 兜底：殖民者数量变化、空闲兜底、弹框检测。

### 第 3 层：空闲兜底

120 秒无交互 → 推送殖民地概览。

### 第 4 层：暂停过久提醒

30s 首次，之后每 60s 重复。AI 工作中抑制计时，空闲后触发。

## 暂停守卫

`AutoPauseGuard` 模式：仅当我们自己暂停的游戏才在 AI 完成后自动恢复。

```
事件触发 → DangerPauseIfBusy() → DangerPaused=true
  → AI 收到催促，收尾
  → IsBusy=false 检测 → DangerPaused=true → 恢复游戏
```

**设计理由**：区分"AI 暂停的"和"玩家手动暂停的"。只恢复我们暂停的，不干扰玩家的手动暂停。

## 缓存与效率

- `DangerSummary` ≤60 字符，用 emoji 编码等级（🔴🟡ℹ️）
- 事件详情不重复注入 prompt（已在聊天消息中），避免缓存污染
- 重复同类事件合并计数，不逐条推送
