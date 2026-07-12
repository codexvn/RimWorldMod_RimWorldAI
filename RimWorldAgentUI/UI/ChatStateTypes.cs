using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using UnityEngine;
using Verse;

namespace RimWorldAgent
{
    public enum ChatRole { User, Assistant }
    public enum ChatState { Streaming, Done, Error }
    public enum ToolStatus { Running, Completed, Failed }
    public enum BudgetStatus { Ok, Warning, Critical, Exceeded }

    public class ToolCallInfo
    {
        public string ItemId = "";
        public string Name = "";
        public string Title = "";
        public string ToolKind = "";
        public string Content = "";
        public string Meta = "";
        public string Result = "";
        public ToolStatus Status;
        public DateTime StartTime = DateTime.UtcNow;
        public double DurationMs;
    }

    public class ChatEntry
    {
        public ChatRole Role;
        public string Text = "";
        public string ThinkingText = "";
        public ChatState State;
        public string RunId = "";
        public string AgentId = "";
        public string AgentType = "";
        public bool IsContext;
        public int LastChunkLen;
        public float CachedHeight;
        public int CachedTextLen;
        public int CachedThinkingLen;
        public float CachedContentWidth;
        public string CachedText = "";
        public string CachedThinking = "";
    }

    /// <summary>UI 绘制使用的只读快照；仅在对应集合发生变化时重建。</summary>
    public sealed class ChatDisplaySnapshot
    {
        public IReadOnlyList<ChatEntry> Entries { get; internal set; } = Array.Empty<ChatEntry>();
        public IReadOnlyList<ToolCallInfo> ToolCalls { get; internal set; } = Array.Empty<ToolCallInfo>();
        public IReadOnlyList<ChatDisplayState.SdkTaskItem> Tasks { get; internal set; } = Array.Empty<ChatDisplayState.SdkTaskItem>();
        public int EntriesRevision { get; internal set; }
        public int ToolCallsRevision { get; internal set; }
        public int TasksRevision { get; internal set; }
    }

    /// <summary>由 UIMessageBus UiMessage 协议驱动，和 WebUI 共享同一数据源</summary>
    public static class ChatDisplayState
    {
        public static event Action? OnChanged;
        public static BudgetStatus CurrentBudgetStatus = BudgetStatus.Ok;
        public static float CurrentBudgetPercent;
        public static string CurrentBudgetText = "";
        public static string CurrentSessionConfigSummary = "";
        public static string SessionId = "";
        public static string AgentStatus = "";
        public static bool CompactionActive;
        private static readonly List<ChatEntry> _entries = new();
        private static readonly List<ToolCallInfo> _toolCalls = new();
        private static readonly List<SdkTaskItem> _sdkTasks = new();
        private static readonly object _lock = new();
        private static readonly ChatDisplaySnapshot _snapshot = new();
        private static bool _entriesSnapshotDirty = true;
        private static bool _toolCallsSnapshotDirty = true;
        private static bool _tasksSnapshotDirty = true;

        // 事件队列：BridgeClient 后台线程入队，Dialog_AiChat UI 线程消费
        private static readonly Queue<Action> _pendingEvents = new();
        private static readonly object _eventLock = new();
        private const float UiRefreshIntervalSeconds = 1f / 15f;
        private static float _nextDrainTime;
        private static bool _forceDrain;
        private static bool _isDrainingEvents;
        private static bool _changedDuringDrain;

        // 流式累积器 — REPLACE 语义
        private static string _deltaAccum = "";
        private static bool _deltaIsThinking;
        private static ChatEntry? _streamingEntry;

        public class SdkTaskItem
        {
            public string Id = "";
            public string Subject = "";
            public string Status = "pending";
        }

        // ===== 线程安全事件队列 =====

        /// <summary>WS 后台线程安全入队，由 Dialog_AiChat 在 UI 线程 DrainEvents</summary>
        public static void EnqueueUiEvent(Action action, bool forceNextDrain = false)
        {
            lock (_eventLock)
            {
                _pendingEvents.Enqueue(action);
                if (forceNextDrain) _forceDrain = true;
            }
        }

        /// <summary>UI 线程调用，按刷新节奏合并消费积压事件；事件不会丢弃。</summary>
        public static void DrainEvents()
        {
            List<Action> batch;
            var now = Time.realtimeSinceStartup;
            lock (_eventLock)
            {
                if (_pendingEvents.Count == 0) return;
                if (!_forceDrain && now < _nextDrainTime) return;
                batch = new List<Action>(_pendingEvents);
                _pendingEvents.Clear();
                _forceDrain = false;
            }
            _isDrainingEvents = true;
            try
            {
                foreach (var act in batch)
                {
                    try { act(); }
                    catch (Exception ex)
                    {
                        Log.Warning($"[ChatDisplayState] 事件处理异常: {ex}");
                    }
                }
            }
            finally
            {
                _isDrainingEvents = false;
                _nextDrainTime = now + UiRefreshIntervalSeconds;
            }
            if (!_changedDuringDrain) return;
            _changedDuringDrain = false;
            OnChanged?.Invoke();
        }

        public static ChatDisplaySnapshot Snapshot
        {
            get
            {
                lock (_lock)
                {
                    if (_entriesSnapshotDirty)
                    {
                        _snapshot.Entries = _entries.ToArray();
                        _entriesSnapshotDirty = false;
                    }
                    if (_toolCallsSnapshotDirty)
                    {
                        _snapshot.ToolCalls = _toolCalls.ToArray();
                        _toolCallsSnapshotDirty = false;
                    }
                    if (_tasksSnapshotDirty)
                    {
                        _snapshot.Tasks = _sdkTasks.ToArray();
                        _tasksSnapshotDirty = false;
                    }
                    return _snapshot;
                }
            }
        }

        public static IReadOnlyList<ToolCallInfo> ToolCallsSnapshot => Snapshot.ToolCalls;

        public static IReadOnlyList<SdkTaskItem> SdkTasksSnapshot => Snapshot.Tasks;

        private static void MarkEntriesChangedLocked()
        {
            _entriesSnapshotDirty = true;
            _snapshot.EntriesRevision++;
        }

        private static void MarkToolCallsChangedLocked()
        {
            _toolCallsSnapshotDirty = true;
            _snapshot.ToolCallsRevision++;
        }

        private static void MarkTasksChangedLocked()
        {
            _tasksSnapshotDirty = true;
            _snapshot.TasksRevision++;
        }

        private static void NotifyChanged()
        {
            if (_isDrainingEvents)
            {
                _changedDuringDrain = true;
                return;
            }
            OnChanged?.Invoke();
        }

        // ===== 用户消息 =====

        /// <summary>用户发送消息时记录，结束上一轮 AI 流式条目</summary>
        public static void OnUserMessage(string text)
        {
            lock (_lock)
            {
                FinalizeStreamingLocked();
                _entries.Add(new ChatEntry { Role = ChatRole.User, Text = text, State = ChatState.Done });
                MarkEntriesChangedLocked();
            }
            _deltaAccum = "";
            NotifyChanged();
        }

        public static void AddSystemMessage(string text)
        {
            lock (_lock)
            {
                _entries.Add(new ChatEntry { Role = ChatRole.Assistant, Text = text, State = ChatState.Done, IsContext = true });
                MarkEntriesChangedLocked();
            }
            NotifyChanged();
        }

        // ===== 流式文本 REPLACE 语义 =====

        /// <summary>流式文本 delta — 累积后替换。空串信号 = 新 text block 开始，创建新条目</summary>
        public static void OnAssistantText(string text)
        {
            lock (_lock)
            {
                if (string.IsNullOrEmpty(text))
                {
                    // content_block_start{text} → 结束上一条流式，新建条目
                    _deltaIsThinking = false;
                    _deltaAccum = "";
                    FinalizeStreamingLocked();
                    _streamingEntry = new ChatEntry { Role = ChatRole.Assistant, State = ChatState.Streaming };
                    _entries.Add(_streamingEntry);
                    MarkEntriesChangedLocked();
                }
                else
                {
                    if (_deltaIsThinking)
                    {
                        // thinking -> text 必须结束旧块，不能复用同一个 entry 并清空 ThinkingText。
                        FinalizeStreamingLocked();
                        _deltaIsThinking = false;
                        _deltaAccum = "";
                    }
                    _deltaAccum += text;
                    if (_streamingEntry == null || _streamingEntry.State != ChatState.Streaming)
                    {
                        _streamingEntry = new ChatEntry { Role = ChatRole.Assistant, State = ChatState.Streaming };
                        _entries.Add(_streamingEntry);
                    }
                    _streamingEntry.Text = _deltaAccum;       // REPLACE 语义
                    _streamingEntry.ThinkingText = "";
                    _streamingEntry.CachedHeight = 0f;
                    MarkEntriesChangedLocked();
                }
            }
            NotifyChanged();
        }

        /// <summary>流式思考 delta — 累积后替换。空串信号 = 新 thinking block 开始，创建新条目</summary>
        public static void OnAssistantThinking(string thinking)
        {
            lock (_lock)
            {
                if (string.IsNullOrEmpty(thinking))
                {
                    // content_block_start{thinking} → 结束上一条流式，新建条目
                    _deltaIsThinking = true;
                    _deltaAccum = "";
                    FinalizeStreamingLocked();
                    _streamingEntry = new ChatEntry { Role = ChatRole.Assistant, State = ChatState.Streaming };
                    _entries.Add(_streamingEntry);
                    MarkEntriesChangedLocked();
                }
                else
                {
                    if (!_deltaIsThinking)
                    {
                        // text -> thinking 开始新的块，避免把新的思考内容追加到旧文本块。
                        FinalizeStreamingLocked();
                        _deltaIsThinking = true;
                        _deltaAccum = "";
                    }
                    _deltaAccum += thinking;
                    if (_streamingEntry == null || _streamingEntry.State != ChatState.Streaming)
                    {
                        _streamingEntry = new ChatEntry { Role = ChatRole.Assistant, State = ChatState.Streaming };
                        _entries.Add(_streamingEntry);
                    }
                    _streamingEntry.ThinkingText = _deltaAccum;  // REPLACE 语义
                    _streamingEntry.CachedHeight = 0f;
                    MarkEntriesChangedLocked();
                }
            }
            NotifyChanged();
        }

        private static void FinalizeStreamingLocked()
        {
            if (_streamingEntry != null)
            {
                _streamingEntry.State = ChatState.Done;
                _streamingEntry.CachedHeight = 0f;
                _streamingEntry = null;
                MarkEntriesChangedLocked();
            }
        }

        public static void FinishStreaming()
        {
            lock (_lock) { FinalizeStreamingLocked(); }
            _deltaAccum = "";
            _deltaIsThinking = false;
            NotifyChanged();
        }

        public static void MarkLastAborted()
        {
            lock (_lock)
            {
                if (_streamingEntry != null)
                {
                    _streamingEntry.State = ChatState.Done;
                    if (string.IsNullOrEmpty(_streamingEntry.Text))
                        _streamingEntry.Text = "（已中断）";
                    _streamingEntry.CachedHeight = 0f;
                    _streamingEntry = null;
                    MarkEntriesChangedLocked();
                }
            }
            _deltaAccum = "";
            _deltaIsThinking = false;
            NotifyChanged();
        }

        // ===== 工具调用 =====

        public static void AddToolCall(string toolId, string toolName, string meta, string title = "", string toolKind = "", string content = "")
        {
            lock (_lock)
            {
                // 去重：stream_event 和 assistant 各发一次同 ID 的 tool_call，只保留一条
                var existing = _toolCalls.FirstOrDefault(t => t.ItemId == toolId);
                if (existing != null)
                {
                    if (!string.IsNullOrEmpty(meta) && (string.IsNullOrEmpty(existing.Meta) || existing.Meta == "{}"))
                        existing.Meta = meta;
                    if (!string.IsNullOrEmpty(title)) existing.Title = title;
                    if (!string.IsNullOrEmpty(toolKind)) existing.ToolKind = toolKind;
                    if (!string.IsNullOrEmpty(content)) existing.Content = content;
                }
                else
                {
                    _toolCalls.Add(new ToolCallInfo
                    {
                        ItemId = toolId,
                        Name = toolName,
                        Title = title,
                        ToolKind = toolKind,
                        Content = content,
                        Meta = meta,
                        Status = ToolStatus.Running,
                    });
                }
                MarkToolCallsChangedLocked();
            }
            NotifyChanged();
        }

        public static void FinishToolCall(string toolId, bool isError, double durationMs, string result = "")
        {
            lock (_lock)
            {
                for (int i = _toolCalls.Count - 1; i >= 0; i--)
                {
                    if (_toolCalls[i].ItemId == toolId && _toolCalls[i].Status == ToolStatus.Running)
                    {
                        _toolCalls[i].Status = isError ? ToolStatus.Failed : ToolStatus.Completed;
                        _toolCalls[i].DurationMs = durationMs;
                        _toolCalls[i].Result = result;
                        MarkToolCallsChangedLocked();
                        break;
                    }
                }
            }
            NotifyChanged();
        }

        public static void Clear()
        {
            lock (_lock)
            {
                _entries.Clear();
                _toolCalls.Clear();
                _sdkTasks.Clear();
                _streamingEntry = null;
                MarkEntriesChangedLocked();
                MarkToolCallsChangedLocked();
                MarkTasksChangedLocked();
            }
            _deltaAccum = "";
            _deltaIsThinking = false;
            NotifyChanged();
        }

        // ===== UIMessageBus UiMessage JSON 解析（由 BridgeClient.OnMessage → 直接调用） =====

        public static void ProcessMessage(string uiJson)
        {
            try
            {
                using var doc = JsonDocument.Parse(uiJson);
                var root = doc.RootElement;
                var type = root.TryGetProperty("type", out var t) ? t.GetString() : "";
                switch (type)
                {
                    case "text_delta":
                    {
                        var text = root.TryGetProperty("text", out var td) ? td.GetString() ?? "" : "";
                        EnqueueUiEvent(() => OnAssistantText(text));
                        break;
                    }
                    case "thinking_delta":
                    {
                        var thinking = root.TryGetProperty("thinking", out var th) ? th.GetString() ?? "" : "";
                        EnqueueUiEvent(() => OnAssistantThinking(thinking));
                        break;
                    }
                    case "text_block":
                    {
                        var text = root.TryGetProperty("text", out var tb) ? tb.GetString() ?? "" : "";
                        EnqueueUiEvent(() => OnAssistantText(text));
                        break;
                    }
                    case "tool_call":
                    {
                        var toolId = root.TryGetProperty("id", out var ti) ? ti.GetString() ?? "" : "";
                        var toolName = root.TryGetProperty("name", out var tn) ? tn.GetString() ?? "" : "";
                        var toolTitle = root.TryGetProperty("title", out var tt) ? tt.GetString() ?? "" : "";
                        var toolKind = root.TryGetProperty("tool_kind", out var tk) ? tk.GetString() ?? "" : "";
                        var toolContent = root.TryGetProperty("content", out var cnt)
                            ? (cnt.ValueKind == JsonValueKind.String ? cnt.GetString() ?? "" : cnt.GetRawText())
                            : "";
                        var toolInput = root.TryGetProperty("input", out var inp)
                            ? (inp.ValueKind == JsonValueKind.String ? inp.GetString() ?? "{}" : inp.GetRawText())
                            : "{}";
                        EnqueueUiEvent(() => AddToolCall(toolId, toolName, toolInput, toolTitle, toolKind, toolContent), forceNextDrain: true);
                        break;
                    }
                    case "tool_result":
                    {
                        var trId = root.TryGetProperty("id", out var tri) ? tri.GetString() ?? "" : "";
                        var isErr = root.TryGetProperty("isError", out var ie) && ie.GetBoolean();
                        var durMs = root.TryGetProperty("durationMs", out var dm) ? dm.GetDouble() : 0;
                        var result = root.TryGetProperty("content", out var co)
                            ? (co.ValueKind == JsonValueKind.String ? co.GetString() ?? "" : co.GetRawText())
                            : "";
                        EnqueueUiEvent(() => FinishToolCall(trId, isErr, durMs, result), forceNextDrain: true);
                        break;
                    }
                    case "result":
                        // 会话结束的 result 不含 per-turn 缓存字段，仅需 finish streaming
                        EnqueueUiEvent(() => FinishStreaming(), forceNextDrain: true);
                        break;
                    case "aborted":
                        EnqueueUiEvent(() => MarkLastAborted(), forceNextDrain: true);
                        break;
                    case "session_init":
                    {
                        var sessionId = root.TryGetProperty("session_id", out var sid) ? sid.GetString() ?? "" : "";
                        var sessionConfigSummary = "";
                        if (root.TryGetProperty("session_config_summary", out var configSummary) &&
                            configSummary.ValueKind == JsonValueKind.Array)
                        {
                            var values = configSummary.EnumerateArray()
                                .Where(value => value.ValueKind == JsonValueKind.String)
                                .Select(value => value.GetString() ?? "")
                                .Where(value => !string.IsNullOrWhiteSpace(value));
                            sessionConfigSummary = string.Join(" · ", values);
                        }
                        EnqueueUiEvent(() =>
                        {
                            SessionId = sessionId;
                            CurrentSessionConfigSummary = sessionConfigSummary;
                            NotifyChanged();
                        }, forceNextDrain: true);
                        break;
                    }
                    case "system":
                    {
                        var sysText = root.TryGetProperty("text", out var st) ? st.GetString() ?? "" : "";
                        if (!string.IsNullOrEmpty(sysText))
                            EnqueueUiEvent(() => AddSystemMessage(sysText), forceNextDrain: true);
                        break;
                    }
                    case "error":
                    {
                        var errText = root.TryGetProperty("error", out var et) ? et.GetString() ?? "" : "";
                        if (!string.IsNullOrEmpty(errText))
                            EnqueueUiEvent(() => AddSystemMessage(errText), forceNextDrain: true);
                        break;
                    }
                    case "user":
                    {
                        var txt = root.TryGetProperty("text", out var ut) ? ut.GetString() ?? "" : "";
                        if (!string.IsNullOrEmpty(txt))
                            EnqueueUiEvent(() => OnUserMessage(txt), forceNextDrain: true);
                        break;
                    }
                    case "budget_status":
                    {
                        var used = root.TryGetProperty("used", out var bu) ? bu.GetInt64() : 0;
                        var limit = root.TryGetProperty("limit", out var bl) ? bl.GetInt64() : 0;
                        var inTok = root.TryGetProperty("inputTokens", out var it) ? it.GetInt64() : 0;
                        var curCacheR = root.TryGetProperty("currentCacheRead", out var ccr) ? ccr.GetInt64() : 0;
                        var curCacheC = root.TryGetProperty("currentCacheCreate", out var ccc) ? ccc.GetInt64() : 0;
                        var totalIn = root.TryGetProperty("totalInput", out var ti) ? ti.GetInt64() : 0;
                        var contextWindow = root.TryGetProperty("contextWindow", out var cw) ? cw.GetInt64() : 0;
                        var contextUsed = root.TryGetProperty("contextUsed", out var cu) ? cu.GetInt64() : 0;
                        EnqueueUiEvent(() => UpdateBudget(used, limit, inTok, curCacheR, curCacheC, totalIn, contextWindow, contextUsed));
                        break;
                    }
                    case "agent-status":
                    {
                        var status = root.TryGetProperty("role", out var ar) ? ar.GetString() ?? "" : "";
                        EnqueueUiEvent(() =>
                        {
                            AgentStatus = status;
                            NotifyChanged();
                        });
                        break;
                    }
                    case "compaction-status":
                    {
                        var compactionActive = root.TryGetProperty("active", out var ca) && ca.GetBoolean();
                        EnqueueUiEvent(() =>
                        {
                            CompactionActive = compactionActive;
                            NotifyChanged();
                        });
                        break;
                    }
                    case "sdk-tasks":
                    {
                        // 在 using scope 内提取数据，避免 lambda 引用已释放的 JsonDocument
                        var tasks = new List<SdkTaskItem>();
                        if (root.TryGetProperty("tasks", out var ta) && ta.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var te in ta.EnumerateArray())
                            {
                                tasks.Add(new SdkTaskItem
                                {
                                    Id = te.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "",
                                    Subject = te.TryGetProperty("subject", out var sjEl) ? sjEl.GetString() ?? "" : "",
                                    Status = te.TryGetProperty("status", out var stEl) ? stEl.GetString() ?? "pending" : "pending"
                                });
                            }
                        }
                        EnqueueUiEvent(() =>
                        {
                            lock (_lock)
                            {
                                _sdkTasks.Clear();
                                _sdkTasks.AddRange(tasks);
                                MarkTasksChangedLocked();
                            }
                            NotifyChanged();
                        });
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"[ChatDisplayState] 解析消息失败: {ex}");
            }
        }

        private static void UpdateBudget(long used, long limit, long inputTokens, long currentCacheRead, long currentCacheCreate, long totalInput, long contextWindow, long contextUsed)
        {
            static string Fmt(long v) => v >= 1_000_000 ? $"{v / 1_000_000f:F1}M" : v >= 1000 ? $"{v / 1000f:F0}K" : v.ToString();
            static string ProgressBar(double percent)
            {
                var blocks = (int)(percent / 10.0);
                if (blocks < 0) blocks = 0;
                if (blocks > 10) blocks = 10;
                return new string('█', blocks) + new string('░', 10 - blocks);
            }

            var parts = new System.Text.StringBuilder();

            if (contextWindow > 0)
            {
                var contextPercent = (double)contextUsed / contextWindow * 100.0;
                parts.Append($"上下文 {Fmt(contextUsed)}/{Fmt(contextWindow)} {contextPercent:F0}% {ProgressBar(contextPercent)}");
            }

            // 入 12K/13K(35%) — 非缓存 / 总输入(缓存命中率)
            var totalPerTurn = inputTokens + currentCacheRead;
            if (totalPerTurn > 0)
            {
                if (parts.Length > 0) parts.Append("    ");
                var hitRate = (double)currentCacheRead / totalPerTurn * 100.0;
                parts.Append($"入 {Fmt(inputTokens)}/{Fmt(totalPerTurn)}({hitRate:F0}%)");
            }

            CurrentBudgetPercent = limit > 0 ? (float)((double)used / limit * 100.0) : 0f;
            CurrentBudgetStatus = limit <= 0 ? BudgetStatus.Ok
                : used >= limit ? BudgetStatus.Exceeded
                : CurrentBudgetPercent >= 95f ? BudgetStatus.Critical
                : CurrentBudgetPercent >= 80f ? BudgetStatus.Warning
                : BudgetStatus.Ok;

            // ACP usage_update 标准只保证上下文 used/size，并不提供累计 token。
            // 只有累计账本有实际值（或缺少上下文信息）时才显示 Tok，避免稳定显示误导性的 0。
            if (used > 0 || contextWindow <= 0)
            {
                if (parts.Length > 0) parts.Append("    ");
                parts.Append("Tok ");
                parts.Append(Fmt(used));
                parts.Append("/");
                parts.Append(limit > 0 ? Fmt(limit) : "--");
                if (limit > 0)
                    parts.Append($" {CurrentBudgetPercent:F0}% {ProgressBar(CurrentBudgetPercent)}");
            }

            CurrentBudgetText = parts.ToString();
        }
    }
}
