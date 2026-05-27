# CC Companion 桥接生命周期

## 概述

RimWorld (C#) 通过 WebSocket 连接本地 Node.js 进程（CC Companion），Companion 使用 Claude Agent SDK 与 Claude API 通信。游戏事件推送到 Companion，AI 响应广播回游戏内聊天窗。

## 架构

```
RimWorld (C#)                  CC Companion (Node.js)       Claude API
    │                                │                         │
    │ 游戏事件(WS)                     │  SDK query()            │
    │──────────────────────────────▶ │────────────────────────▶│
    │                                │                         │
    │ 聊天窗 ◀─ WS broadcast ────────│  ◀── assistant/tool ────│
    │                                │                         │
    │  MCP Server :9877 ◀────────────│──── tools/call ─────────│
```

## 关键文件

| 文件 | 职责 |
|------|------|
| `Bridge/BridgeLifecycle.cs` | CC 连接生命周期、事件转发、子进程 spawn/kill |
| `Bridge/CCClient.cs` | WebSocket 客户端（心跳 + 重连） |
| `Bridge/ChatDisplayState.cs` | 线程安全聊天显示状态 |
| `Bridge/GameContextProvider.cs` | 游戏上下文文本构建 |
| `cc-companion/bridge/ws-server.ts` | WebSocket Server（Companion 侧） |
| `cc-companion/bridge/session.ts` | SDK 会话管理 |
| `cc-companion/bridge/message-bus.ts` | MessageBus 双总线机制 |

## 连接流程

`BridgeLifecycle.StartAsync(sessionId)` 在加载存档时执行：

```
StopCompanionProcess()         → 停止旧进程
KillStaleByPidFile()           → 清理 .pid 残留文件
StartCompanionProcess()        → spawn Node.js 子进程
CCClient.Connect()             → WebSocket 握手 (hello/hello-ok)
```

**Session 目录**：`claude-sessions/rimworld-<sessionId>/`，确保不同存档的 checkpoint 隔离。

**sessionId 生成与持久化**：新游戏随机生成 12 位 hex，`ExposeData()` 通过 `Scribe_Values` 写入存档。读档时恢复；兼容旧存档（无 sessionId 时自动补生成）。

## 进程清理：三层保障

Companion 进程必须在 RimWorld 退出/崩溃/重进入时可靠清理。单靠正常退出路径不够——游戏可能被强杀。

| 层 | 机制 | 平台 | 覆盖场景 |
|----|------|------|---------|
| 1 | Harmony `Game.Dispose()` postfix → `BridgeLifecycle.Stop()` | 跨平台 | 正常返回主菜单 / 退出游戏 |
| 2 | `StopMcpService()` → `BridgeLifecycle.Stop()` | 跨平台 | 读档 / 新档（先杀旧进程再启动新） |
| 3 | Windows Job Object `KILL_ON_JOB_CLOSE` | Windows | RimWorld 强杀 → OS 立即杀 companion |
| 兜底 | `--idle-timeout 30000`（WS 断开计时） | 跨平台 | WS 断开 30s 无重连自动退出 |

**设计理由**：RimWorld 作为 Unity 游戏，可能被任务管理器强杀或被 mod 冲突崩溃。仅靠正常退出路径无法保证子进程清理。Windows Job Object 将子进程绑定到父进程生命周期——OS 级别保证，无需任何代码执行。Linux/Mac 依赖 idle-timeout 兜底。

## 子进程 Spawn

C# spawn 的实际命令：
```bash
node --import tsx/esm companion/companion.ts
```

**配置传递**：全部通过环境变量，不写配置文件。
- `RIMWORLD_PROJECT_PATH` → SDK cwd，确定 `.claude/settings.json` 位置
- `CCB_HOST`, `CCB_PORT` → Companion WebSocket 监听地址
- `CCB_AUTH_TOKEN` → WS 握手认证（可选）

**设计理由**：环境变量避免文件 I/O 竞态，进程隔离天然保证不同存档配置独立。

## MessageBus 双总线

Companion 通过 `message-bus.ts` 管理两条独立的广播总线，走同一条 WebSocket 连接：

| Bus | 数据来源 | 消息类型 | 消费者 |
|-----|---------|---------|--------|
| **Game Bus** | C# 游戏事件 | `colony-stats`, `todo-state`, `budget-status`, `user`, `error` | Web 页面, 游戏内 UI |
| **Agent Bus** | SDK query() 响应 | `assistant`, `stream_event`, `result`, `system/init`, `aborted` | Web 页面, C# CCClient |

**关键设计**：
- Game Bus 消息不经 SDK（零延迟、不消耗 Token），直接在 Companion 侧广播
- Agent Bus 消息由 `createResponseProcessor` 遍历 SDK AsyncIterator，逐条广播
- Companion 是两股流的**多路复用器**——接收端通过 `msg.type` 路由

## 参数覆盖顺序

```
用户 .claude/settings.json   ← 低优先级（API Key、Base URL、MCP 等）
        ↓
SDK Options (session.ts)     ← 中优先级（model、settingSources、cwd）
        ↓
环境变量 (ProcessStartInfo)   ← 高优先级（RIMWORLD_PROJECT_PATH、CCB_HOST/CCB_PORT/AUTH_TOKEN）
```

**设计理由**：用户本地 settings.json 保持用户偏好，SDK Options 由 Companion 控制，环境变量由 C# 强制覆盖（连接参数必须一致）。
