# 历史 TODO：CCB WebSocket → stdin/stdout SDK 桥接

> 本文为已废弃的 CCB 迁移草稿，仅保留历史背景。当前实施计划与状态以 `docs/plans/acp-agent-refactor.md` 为准；当前运行链路使用 ACP stdio + `claude-agent-acp`，不再继续此处的 CCB 实现。

## 目标

companion 精简为纯 SDK 桥接，只做两件事：
1. stdin 接收 C# 指令（chat / abort / set-thinking）
2. stdout 输出 SDK 流式消息（JSON line）

所有业务逻辑（WS server、HTTP server、MessageBus、Game Bus、预算检查、断线超时）移回 C#。

## 通信协议

```
C# (RimWorld)                       companion (Node.js)
    │                                     │
    │ env: CCB_PARENT_PID                 │
    │                                     │
    │──  stdin ──→ {"type":"chat","text":"..."}       → inputStream.enqueue() → SDK query()
    │──  stdin ──→ {"type":"abort"}                   → inputStream.done() + queryIterator.return?()
    │──  stdin ──→ {"type":"set-thinking","mode":"x","effort":"y","tokens":0}
    │                                     │
    │← stdout ── {"type":"sdk","message":{...}}       ← SDK AsyncIterator for await
    │← stdout ── {"type":"log","text":"..."}          ← companion 自身日志
    │← stderr ── SDK 原生错误                          ← SDK 内部 stderr
    └─────────────────────────────────────┘
```

## 实施步骤

### Phase 1: companion 精简（~80 行）
- [ ] 删 `ws-server.ts`、`message-bus.ts`、`chat-page.ts`、`chat-http.ts`、`companion.ts` 中 onEvent/onStatusChange/onAbort/onSetThinking 回调
- [ ] 新 `companion.ts`：
  - `process.stdin.on('data')` → split `\n` → JSON.parse → dispatch: `chat`/`abort`/`set-thinking`
  - `for await (const msg of queryIterator)` → `stdout.write(JSON.stringify({type:'sdk',...}) + '\n')`
  - `console.log` 自动走 stdout，带 `[cc-companion]` 前缀 → C# 识别为 `type:log`
  - `setInterval(checkParent, 2000)` + `process.stdin.on('end')` → `process.exit(0)`
  - 启动时不再监听端口、不写 PID 文件、不 serve HTTP
- [ ] `session.ts` 保留（`createSession` + `createResponseProcessor` 核心不变）
- [ ] 删 `config.ts` 中 `chatPageEnabled`、`colonyStats`、`lastInitData` 等不用字段

### Phase 2: C# 侧清理
- [ ] 删 `CcbWebSocket.cs` (~400行)
- [ ] 删 `Hook_GameDispose.cs`
- [ ] 删 `CcbManager.cs` 中 `KillStaleByProcessScan()`、JobObject P/Invoke、`AttachToJobObject()`
- [ ] 新 `CcbManager.Start()`: 传 `CCB_PARENT_PID` env var，不再传 CCB_HOST/CCB_PORT/CCB_AUTH_TOKEN
- [ ] 新 `CompanionStdio.cs`: 封装 `Process.StandardInput`/`OutputDataReceived`
  - `SendChat(text)` → `stdin.WriteLine(JSON({type:'chat', text}))`
  - `SendAbort()` → `stdin.WriteLine(JSON({type:'abort'}))`
  - `SetThinking(mode,effort,tokens)` → `stdin.WriteLine(JSON({type:'set-thinking',...}))`
  - `OutputDataReceived` → JSON line parse → `type:sdk` → 回调 `OnSdkMessage`; `type:log` → `CoreLog.Info`
- [ ] `AgentEngine` 重构：
  - 不再 await WS 握手，等 companion stdout 输出 `"就绪"` 信号即可（已有 `_ready` 逻辑）
  - `Tick()` 不再检查 WS 重连，改检查 `_process.HasExited` → `TickAndRestart`
  - `SendEvent` / `SendChat` / `SendAbort` 全走 `CompanionStdio`

### Phase 3: Web 前端（如需要）
- [ ] C# 引入 Fleck NuGet (net472 兼容)
- [ ] Fleck WS server + 静态文件 handler serve chat-page.html
- [ ] SDK 消息通过 Fleck broadcast 给 web 页面
- [ ] web 页面发消息 → Fleck OnMessage → `CompanionStdio.SendChat()` → companion → SDK

### Phase 4: 清理
- [ ] 删 `RimWorldAgent.csproj` 中多余的 NuGet 引用（如有）
- [ ] 删 `KillStaleByPidFile()` / `WritePidFile()` / `DeletePidFile()`（不再需要 PID 文件）
- [ ] 删 `cc-companion/.pid` 相关逻辑
- [ ] 清理 CLAUDE.md 中相关文档

## 改动量估算

| | 删除 | 新增 |
|---|---|---|
| companion | ~1800行 (ws-server, message-bus, chat-page, chat-http, companion 大半) | ~80行 (stdin/stdout dispatch) |
| C# | ~500行 (CcbWebSocket, Hook_GameDispose, JobObject, 进程扫描) | ~120行 (CompanionStdio, Fleck server) |
| **净减少** | ~2100行 | |


# UI BUS内包含了业务逻辑

# BUG：
1. echo依然没审报销
2. Dialog中只有token使用 没有命名缓存和额度
3. 打断后依然没有发送继续
   [Core] [INFO] [event] game/notification: Combat/Critical: 大威胁 | 袭击: 德奥加 — 来自<color=#FF3333FF>德奥加</color>的多队炎魔种到达附近。

他们会立即开始进攻。

注意：他们似乎异常聪明地采取了战术，会尝试避开炮塔和陷阱。
[Core] [INFO] [AgentOrchestrator] 中断请求: [Critical/Combat] 大威胁 | 袭击: 德奥加 — 来自德奥加的多队炎魔种到达附近。

他们会立即开始进攻。

注意：他们似乎异常聪明地采取了战术，会尝试避开炮塔和陷阱。
[Core] [INFO] [ccb] [bridge] [CCGUI_DEBUG] 收到消息 type=abort token=(none)
[Core] [INFO] [ccb] [bridge] 收到 abort
[Core] [INFO] [CcbWS] 已发送中断请求
[Core] [INFO] [ccb] [cc-companion] 加载 Prompt: F:\RiderProjects\RimWorldMCP\RimWorldAgent\bin\Release\cc-companion\Prompt.md
[Core] [INFO] [ccb] [bridge] 新会话已创建
[Core] [INFO] [NotisAgent] suffix 注入 (93 字符)



打断了依然会注入suffix


RequestInterrupt → InterruptSummary	下次 prompt 顶部	SDK 内部
NotisAgent → set_tool_result_suffix	下一个 tool_result 末尾	需等工具调用

可以不要
