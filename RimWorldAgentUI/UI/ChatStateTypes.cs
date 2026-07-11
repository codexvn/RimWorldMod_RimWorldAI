using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
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
    }

    /// <summary>由 UIMessageBus UiMessage 协议驱动，和 WebUI 共享同一数据源</summary>
    public static class ChatDisplayState
    {
        public static event Action? OnChanged;
        public static BudgetStatus CurrentBudgetStatus = BudgetStatus.Ok;
        public static float CurrentBudgetPercent;
        public static string CurrentBudgetText = "";
        public static string CurrentModel = "";
        public static string SessionId = "";
        public static string AgentStatus = "";
        public static bool CompactionActive;
        private static readonly List<ChatEntry> _entries = new();
        private static readonly List<ToolCallInfo> _toolCalls = new();
        private static readonly List<SdkTaskItem> _sdkTasks = new();
        private static readonly object _lock = new();

        // 事件队列：BridgeClient 后台线程入队，Dialog_AiChat UI 线程消费
        private static readonly Queue<Action> _pendingEvents = new();
        private static readonly object _eventLock = new();

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
        public static void EnqueueUiEvent(Action action)
        {
            lock (_eventLock) { _pendingEvents.Enqueue(action); }
        }

        /// <summary>UI 线程调用，消费所有积压事件</summary>
        public static void DrainEvents()
        {
            List<Action> batch;
            lock (_eventLock)
            {
                if (_pendingEvents.Count == 0) return;
                batch = new List<Action>(_pendingEvents);
                _pendingEvents.Clear();
            }
            foreach (var act in batch)
            {
                try { act(); }
                catch (Exception ex)
                {
                    Log.Warning($"[ChatDisplayState] 事件处理异常: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        public static List<ChatEntry> Snapshot
        { get { lock (_lock) return _entries.ToList(); } }

        public static List<ToolCallInfo> ToolCallsSnapshot
        { get { lock (_lock) return _toolCalls.ToList(); } }

        public static List<SdkTaskItem> SdkTasksSnapshot
        { get { lock (_lock) return _sdkTasks.ToList(); } }

        // ===== 用户消息 =====

        /// <summary>用户发送消息时记录，结束上一轮 AI 流式条目</summary>
        public static void OnUserMessage(string text)
        {
            lock (_lock)
            {
                FinalizeStreamingLocked();
                _entries.Add(new ChatEntry { Role = ChatRole.User, Text = text, State = ChatState.Done });
            }
            _deltaAccum = "";
            OnChanged?.Invoke();
        }

        public static void AddSystemMessage(string text)
        {
            lock (_lock)
            {
                _entries.Add(new ChatEntry { Role = ChatRole.Assistant, Text = text, State = ChatState.Done, IsContext = true });
            }
            OnChanged?.Invoke();
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
                }
                else
                {
                    if (_deltaIsThinking) { _deltaIsThinking = false; _deltaAccum = ""; }
                    _deltaAccum += text;
                    if (_streamingEntry == null || _streamingEntry.State != ChatState.Streaming)
                    {
                        _streamingEntry = new ChatEntry { Role = ChatRole.Assistant, State = ChatState.Streaming };
                        _entries.Add(_streamingEntry);
                    }
                    _streamingEntry.Text = _deltaAccum;       // REPLACE 语义
                    _streamingEntry.ThinkingText = "";
                    _streamingEntry.CachedHeight = 0f;
                }
            }
            OnChanged?.Invoke();
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
                }
                else
                {
                    if (!_deltaIsThinking) { _deltaIsThinking = true; _deltaAccum = ""; }
                    _deltaAccum += thinking;
                    if (_streamingEntry == null || _streamingEntry.State != ChatState.Streaming)
                    {
                        _streamingEntry = new ChatEntry { Role = ChatRole.Assistant, State = ChatState.Streaming };
                        _entries.Add(_streamingEntry);
                    }
                    _streamingEntry.ThinkingText = _deltaAccum;  // REPLACE 语义
                    _streamingEntry.CachedHeight = 0f;
                }
            }
            OnChanged?.Invoke();
        }

        private static void FinalizeStreamingLocked()
        {
            if (_streamingEntry != null)
            {
                _streamingEntry.State = ChatState.Done;
                _streamingEntry.CachedHeight = 0f;
                _streamingEntry = null;
            }
        }

        public static void FinishStreaming()
        {
            lock (_lock) { FinalizeStreamingLocked(); }
            _deltaAccum = "";
            OnChanged?.Invoke();
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
                }
            }
            _deltaAccum = "";
            OnChanged?.Invoke();
        }

        // ===== 工具调用 =====

        public static void AddToolCall(string toolId, string toolName, string meta, string title = "", string toolKind = "")
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
                }
                else
                {
                    _toolCalls.Add(new ToolCallInfo
                    {
                        ItemId = toolId,
                        Name = toolName,
                        Title = title,
                        ToolKind = toolKind,
                        Meta = meta,
                        Status = ToolStatus.Running,
                    });
                }
            }
            OnChanged?.Invoke();
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
                        break;
                    }
                }
            }
            OnChanged?.Invoke();
        }

        public static void Clear()
        {
            lock (_lock)
            {
                _entries.Clear();
                _toolCalls.Clear();
                _sdkTasks.Clear();
                _streamingEntry = null;
            }
            _deltaAccum = "";
            OnChanged?.Invoke();
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
                        var toolInput = root.TryGetProperty("input", out var inp)
                            ? (inp.ValueKind == JsonValueKind.String ? inp.GetString() ?? "{}" : inp.GetRawText())
                            : "{}";
                        EnqueueUiEvent(() => AddToolCall(toolId, toolName, toolInput, toolTitle, toolKind));
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
                        EnqueueUiEvent(() => FinishToolCall(trId, isErr, durMs, result));
                        break;
                    }
                    case "result":
                        // 会话结束的 result 不含 per-turn 缓存字段，仅需 finish streaming
                        EnqueueUiEvent(() => FinishStreaming());
                        break;
                    case "aborted":
                        EnqueueUiEvent(() => MarkLastAborted());
                        break;
                    case "system_init":
                    {
                        var m = root.TryGetProperty("model", out var mm) ? mm.GetString() : null;
                        if (m != null) CurrentModel = m;
                        SessionId = root.TryGetProperty("session_id", out var sid) ? sid.GetString() ?? "" : "";
                        break;
                    }
                    case "system":
                    {
                        var sysText = root.TryGetProperty("text", out var st) ? st.GetString() ?? "" : "";
                        if (!string.IsNullOrEmpty(sysText))
                            EnqueueUiEvent(() => AddSystemMessage(sysText));
                        break;
                    }
                    case "error":
                    {
                        var errText = root.TryGetProperty("error", out var et) ? et.GetString() ?? "" : "";
                        if (!string.IsNullOrEmpty(errText))
                            EnqueueUiEvent(() => AddSystemMessage(errText));
                        break;
                    }
                    case "user":
                    {
                        var txt = root.TryGetProperty("text", out var ut) ? ut.GetString() ?? "" : "";
                        if (!string.IsNullOrEmpty(txt))
                        {
                            Verse.Log.Message($"[ChatDisplayState] user echo text=\"{txt.Substring(0, Math.Min(txt.Length, 60))}\"");
                            OnUserMessage(txt);
                        }
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
                        EnqueueUiEvent(() => UpdateBudget(used, limit, inTok, curCacheR, curCacheC, totalIn));
                        break;
                    }
                    case "agent-status":
                        AgentStatus = root.TryGetProperty("role", out var ar) ? ar.GetString() ?? "" : "";
                        break;
                    case "compaction-status":
                        CompactionActive = root.TryGetProperty("active", out var ca) && ca.GetBoolean();
                        break;
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
                            }
                            OnChanged?.Invoke();
                        });
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"[ChatDisplayState] 解析消息失败: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void UpdateBudget(long used, long limit, long inputTokens, long currentCacheRead, long currentCacheCreate, long totalInput)
        {
            static string Fmt(long v) => v >= 1_000_000 ? $"{v / 1_000_000f:F1}M" : v >= 1000 ? $"{v / 1000f:F0}K" : v.ToString();

            var parts = new System.Text.StringBuilder();

            // 入 12K/13K(35%) — 非缓存 / 总输入(缓存命中率)
            var totalPerTurn = inputTokens + currentCacheRead;
            if (totalPerTurn > 0)
            {
                var hitRate = (double)currentCacheRead / totalPerTurn * 100.0;
                parts.Append($"入 {Fmt(inputTokens)}/{Fmt(totalPerTurn)}({hitRate:F0}%)");
            }

            // Tok 43K/200K 22% ██░░
            if (parts.Length > 0) parts.Append("    ");
            parts.Append("Tok ");
            parts.Append(Fmt(used));
            parts.Append("/");
            parts.Append(limit > 0 ? Fmt(limit) : "--");
            if (limit > 0 && used > 0)
            {
                CurrentBudgetPercent = (float)((double)used / limit * 100.0);
                var pct = CurrentBudgetPercent;
                var blocks = (int)(pct / 10.0);
                if (blocks > 10) blocks = 10;
                var bar = new string('█', blocks) + new string('░', 10 - blocks);
                parts.Append($" {pct:F0}% {bar}");
                CurrentBudgetStatus = used >= limit ? BudgetStatus.Exceeded
                    : pct >= 95f ? BudgetStatus.Critical
                    : pct >= 80f ? BudgetStatus.Warning
                    : BudgetStatus.Ok;
            }

            CurrentBudgetText = parts.ToString();
        }
    }
}
