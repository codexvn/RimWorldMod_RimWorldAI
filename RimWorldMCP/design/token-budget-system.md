# Token 预算系统

## 概述

按存档限制 LLM token 消耗，超出后阻止（暂停游戏 + 拒绝消息）或警告（Webhook 通知 + 继续）。防止 AI 无节制消耗 API 费用。

## 关键文件

| 文件 | 职责 |
|------|------|
| `McpModSettings.cs` | 预算上限、行为模式、Webhook URL 设置持久化 |
| `Bridge/GlobalModelUsageStore.cs` | 全局模型用量汇总，独立 JSON 文件持久化 |
| `Bridge/TokenUsageTracker.cs` | 按模型追踪、预算检查、`GetCompactDisplay()` 格式化 |
| `Bridge/BridgeLifecycle.cs` | Enforcement 入口 `SendCCMessage()` |
| `Bridge/ChatDisplayState.cs` | `CurrentBudgetStatus/Percent/Text` 静态字段供 UI 读取 |
| `Bridge/CCClient.cs` | 从 SDK 消息提取模型名，hello 附加 budget 字段 |
| `UI/Dialog_AiChat.cs` | 顶部横幅 + 底栏进度条 |
| `RimWorldMCPMod.cs` | 设置窗口：预算配置 + 全局用量汇总表 + 清空按钮 |

## 架构

```
McpModSettings (全局设置)
├── TokenBudgetLimit          — token 预算上限（0=无限制）
├── TokenBudgetExceedAction   — Block / Warn
├── TokenBudgetWebhookUrl     — Warn 模式回调 URL
└── GlobalModelUsageStore     — 全局模型用量汇总

TokenUsageTracker (per-save，持久化到存档)
├── PerModelUsages            — 按模型分列
├── CheckBudget(limit)        — 80%/95%/100% 三档检查
└── Record(model, ...)        — 同步写 GlobalModelUsageStore
```

## 数据流

```
SDK result.usage → session.ts (附加 model 名)
    → WS broadcast
    → CCClient.ReceiveLoop()
    → ExtractUsageFromMessage() 提取 model + usage
    → TokenUsageTracker.Record(model, ...)
        ├── 更新 PerModelUsages (per-save)
        └── GlobalModelUsageStore.Contribute(model, ...) → Save() (全局 JSON)
    → UI 读取 ChatDisplayState.CurrentBudget* 渲染横幅+进度条
```

**设计理由**：Token 数据从 SDK 的 `result.usage` 提取——SDK 返回实际 API 消耗的 input/output token。Companion 侧（session.ts）附加模型名，C# 侧（CCClient）提取后记录。

## 预算检查三档

`CheckBudget(limit)` 返回三档状态：

| 档位 | 阈值 | 状态 |
|------|------|------|
| 正常 | <80% | `Normal` |
| 警告 | 80%-95% | `Warning` → UI 黄色 |
| 临界 | 95%-100% | `Critical` → UI 红色 |
| 超限 | >100% | `Exceeded` → Block 或 Warn |

**设计理由**：80% 提前警告让用户有心理准备，95% 临界提醒即将耗尽，100% 执行管控动作。分档避免突然中断。

## 超限行为

| 模式 | 行为 | 适用场景 |
|------|------|---------|
| **Block** | `Find.TickManager.Pause()` 暂停游戏 + 拒绝发送消息 | 严格预算控制 |
| **Warn** | POST Webhook 通知 + 继续允许对话 | 监控但不阻断 |

## UI 展示

**底栏**（始终显示）：
```
Token: 1.2M/2M (60%) ████████░░░░ | 缓存 800K(40%)
```
颜色：<80% 绿 → 80-95% 黄 → ≥95% 红

**顶部横幅**（Warning/Critical/Exceeded）：醒目半透明色条

**设置窗口**：预算配置 + 全局模型用量汇总表 + 清空按钮（需确认）

## 设置项

| 设置 | 默认值 | 说明 |
|------|--------|------|
| `TokenBudgetLimit` | `0` | 0=无限制 |
| `TokenBudgetExceedAction` | `Block` | Block / Warn |
| `TokenBudgetWebhookUrl` | `""` | 空则不回调 |

## Webhook 格式

超预算时 POST JSON：
```json
{
  "event": "budget_exceeded",
  "save_name": "殖民地名",
  "session_id": "a1b2c3d4e5f6",
  "model": "claude-sonnet-4-6",
  "current_tokens": 2100000,
  "budget_limit": 2000000,
  "usage_percent": 105.0,
  "timestamp": "2026-05-27T12:00:00Z"
}
```
