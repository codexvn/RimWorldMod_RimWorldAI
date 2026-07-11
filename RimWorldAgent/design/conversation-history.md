# Conversation History — 会话持久化

## 架构

```
WebUI ──WS {type:"history",n}──→ UIMessageBus ──OnHistory──→ AgentLoop
                                       ↑                       │socket.Send()
                                  IConversationStore.GetRecent(n)
                                 (SQLite / 内存)
```

## 录制点

| 消息类型 | 触发点 | 录制方法 |
|---------|-------|---------|
| 用户消息 | `AgentLoop.OnChat` | `RecordUserMessage(text)` |
| AI 回复 | `NodeRuntimeEventProjector` → `UIMessageBus.OnAssistantContent` | `RecordAssistantMessage(text, thinking, runId, agentType)` |
| 系统/错误 | `AgentLoop` 静态构造 `OnDisplayMessage` → 解析 `system`/`error` 类型 | `RecordSystemMessage(text)` |

## 存储抽象

```
IConversationStore (Core/Data/)
├── MemoryConversationStore   — MOD 模式，List+lock 内存
└── SqliteConversationStore   — EXE 模式，SQLite WAL 持久化
```

## SQLite 表结构

```sql
CREATE TABLE conversation (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    role        TEXT    NOT NULL,          -- user / assistant / system
    text        TEXT    NOT NULL DEFAULT '',
    thinking    TEXT    NOT NULL DEFAULT '',
    run_id      TEXT    NOT NULL DEFAULT '',
    agent_type  TEXT    NOT NULL DEFAULT '',
    timestamp   TEXT    NOT NULL           -- ISO 8601 UTC
);
CREATE INDEX idx_timestamp ON conversation(timestamp);
```

## WAL 模式

`PRAGMA journal_mode=WAL` — 写不阻塞读，崩溃自动恢复。

## 线程安全

| 场景 | 措施 |
|------|------|
| Fleck WS 线程 → `OnHistory` → `socket.Send()` | Fleck Send 线程安全 |
| ACP notification callback → `OnAssistantContent` | SQLite WAL 多读 + `_writeLock` 系列写 |
| 多客户端并发读 | WAL 并发读 |
| `UIMessageBus.Stop()` → events=null | 飞行中调用 check null |

## 前端协议

### 请求
```json
{ "type": "history", "n": 30 }
```

### 响应
```json
{
  "type": "history_response",
  "messages": [
    {
      "type": "assistant",
      "uuid": "msg_1",
      "agent_type": "",
      "message": {
        "content": [
          { "type": "thinking", "thinking": "..." },
          { "type": "text", "text": "..." }
        ]
      }
    }
  ]
}
```

### 加载流程
1. `ws.onopen` → `loadHistory()` → 发送 `{type:"history",n:30}`
2. `loadingHistory = true`，后续 WS 消息入 `pendingWs` 缓冲
3. `history_response` 到达 → 在 `loadingHistory` 守卫前处理
4. 渲染历史，释放 `loadingHistory`，flush `pendingWs`
5. 5 秒超时 → 自动释放锁，防止永久卡死

## 可视化

```bash
sqlite3 conversation.db "SELECT id, role, substr(text,1,60) FROM conversation ORDER BY id DESC LIMIT 10"
```
