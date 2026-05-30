# Tool Result Suffix — 双工通知机制

## 概述

Agent 通过 MCP 工具设置一段 suffix 文本（一次性），MCP Server 在下一次工具返回结果后自动追加并清空。AI 在工具结果中即时看到通知，无需等待 session 结束。

## 动机

Agent 侧需要在 AI 执行工具期间注入实时信息（事件通知、弹框提示等），但原有路径有延迟：

| 路径 | 时机 | 缺点 |
|------|------|------|
| Companion WebSocket 消息 | session 结束后送达 | AI 忙碌时看不到 |
| ContextBuilder Prompt 层 | session 开始时注入一次 | session 中途的新事件丢失 |
| **Tool Result Suffix** | **下一次工具调用即可见** | 拉取模式，需 AI 调用工具 |

Suffix 是"拉取"机制 — AI 只在调用工具时才看到通知。对于真正紧急的事件（L3 袭击），仍需 Companion WebSocket 直接注入。

## 架构

```
Agent 侧                          MCP 侧
┌─────────────────────┐          ┌─────────────────────────┐
│ AgentOrchestrator    │          │ ToolRegistry            │
│  NotisAgent() ────────── HTTP ──→│  ToolResultSuffix 字段   │
│    ↓ 运行中          │          │  set_tool_result_suffix  │
│    set_tool_result_  │          │                         │
│      suffix          │          │ ExecuteAsync:           │
│    ↓ 休眠中          │          │  result.Text += suffix  │
│    CcbWs 直接发送     │          │  suffix = "" (一次性)    │
└─────────────────────┘          └─────────────────────────┘
```

## 关键文件

| 文件 | 职责 |
|------|------|
| `Tools/ToolRegistry.cs` | `volatile string ToolResultSuffix` 字段 + `ExecuteAsync` 一次性追加 |
| `Tools/Tool_SetToolResultSuffix.cs` | MCP 工具：设置 suffix |

Agent 侧：`AgentOrchestrator.NotisAgent()` 封装统一入口。

## 实现细节

### ToolRegistry 字段

```csharp
public static volatile string ToolResultSuffix = "";
```

`volatile` 保证多线程可见性 — HTTP 线程读取，Agent 线程写入。

### ExecuteAsync 一次性追加

在 `tool.ExecuteAsync(args)` 返回后、包装 `ToolCallResult` 前：

```csharp
var suffix = ToolResultSuffix;
if (!string.IsNullOrEmpty(suffix))
{
    ToolResultSuffix = "";  // 一次性：追加后立即清空
    result.Text = result.Text + "\n\n" + suffix;
}
```

**设计理由**：
- 一次性机制：每次通知只影响一次工具调用，避免过期信息残留
- 先读 suffix 到局部变量，再清空，保证原子性
- `\n\n` 分隔确保 suffix 与工具结果视觉分离

### 线程安全

- `volatile` 保证单次读写的可见性
- suffix 写入和读取是独立操作，无需锁
- 极端情况下可能丢失一次 suffix 更新（两个 HTTP 请求并发），可接受

## NotisAgent 入口

`AgentOrchestrator.NotisAgent(notification)` 封装统一通知逻辑：

- Agent 运行中 + SessionMcp 可用 → `set_tool_result_suffix`（AI 下次工具调用时看到）
- Agent 休眠或 MCP 不可用 → CcbWs 直接发送到 Companion（AI 立即收到）

## 使用场景

| 场景 | suffix 内容 |
|------|------------|
| 弹框检测 | `## ⚠️ 弹框提示\n有 1 个弹框需要选择。使用 get_open_dialogs 查看。` |
| L3 事件 | `## 🔴 紧急事件\n大型突袭来袭！请优先处理。` |
| Agent 建议 | `## 💡 建议\n来自总督：优先建造防御工事。` |
