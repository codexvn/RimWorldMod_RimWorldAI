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
        public string Meta = "";
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
        public static long ContextWindow;
        public static long CurrentInputTokens;

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

        private static string GetDisplayToolName(string toolName, string meta)
        {
            if (!string.IsNullOrEmpty(meta) && toolName.EndsWith("game_cmd"))
            {
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(meta);
                    if (doc.RootElement.TryGetProperty("action", out var a))
                        return a.GetString() ?? toolName;
                }
                catch { }
            }
            return toolName;
        }

        // ===== 工具调用 =====

        public static void AddToolCall(string toolId, string toolName, string meta)
        {
            lock (_lock)
            {
                // 去重：stream_event 和 assistant 各发一次同 ID 的 tool_call，只保留一条
                var existing = _toolCalls.FirstOrDefault(t => t.ItemId == toolId);
                if (existing != null)
                {
                    if (!string.IsNullOrEmpty(meta) && (string.IsNullOrEmpty(existing.Meta) || existing.Meta == "{}"))
                    {
                        existing.Meta = meta;
                        existing.Name = GetDisplayToolName(toolName, meta);
                    }
                }
                else
                {
                    _toolCalls.Add(new ToolCallInfo
                    {
                        ItemId = toolId,
                        Name = GetDisplayToolName(toolName, meta),
                        Meta = meta,
                        Status = ToolStatus.Running,
                    });
                }
            }
            OnChanged?.Invoke();
        }

        public static void FinishToolCall(string toolId, bool isError, double durationMs)
        {
            lock (_lock)
            {
                for (int i = _toolCalls.Count - 1; i >= 0; i--)
                {
                    if (_toolCalls[i].ItemId == toolId && _toolCalls[i].Status == ToolStatus.Running)
                    {
                        _toolCalls[i].Status = isError ? ToolStatus.Failed : ToolStatus.Completed;
                        _toolCalls[i].DurationMs = durationMs;
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
                        var toolInput = root.TryGetProperty("input", out var inp)
                            ? (inp.ValueKind == JsonValueKind.String ? inp.GetString() ?? "{}" : inp.GetRawText())
                            : "{}";
                        EnqueueUiEvent(() => AddToolCall(toolId, toolName, toolInput));
                        break;
                    }
                    case "tool_result":
                    {
                        var trId = root.TryGetProperty("id", out var tri) ? tri.GetString() ?? "" : "";
                        var isErr = root.TryGetProperty("isError", out var ie) && ie.GetBoolean();
                        var durMs = root.TryGetProperty("durationMs", out var dm) ? dm.GetDouble() : 0;
                        EnqueueUiEvent(() => FinishToolCall(trId, isErr, durMs));
                        break;
                    }
                    case "result":
                    {
                        var subtype = root.TryGetProperty("subtype", out var srs) ? srs.GetString() ?? "" : "";
                        var used = root.TryGetProperty("used", out var u) ? u.GetInt64() : 0;
                        var limit = root.TryGetProperty("limit", out var l) ? l.GetInt64() : 0;
                        var cacheR = root.TryGetProperty("cacheRead", out var cr) ? cr.GetInt64() : 0;
                        var totalIn = root.TryGetProperty("totalInput", out var ti) ? ti.GetInt64() : 0;
                        var cacheC = root.TryGetProperty("cacheCreate", out var cc) ? cc.GetInt64() : 0;
                        EnqueueUiEvent(() => { FinishStreaming(); UpdateBudget(subtype, used, limit, cacheR, totalIn, cacheC); });
                        break;
                    }
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
                        var cacheR = root.TryGetProperty("cacheRead", out var cr) ? cr.GetInt64() : 0;
                        var totalIn = root.TryGetProperty("totalInput", out var ti) ? ti.GetInt64() : 0;
                        var cacheC = root.TryGetProperty("cacheCreate", out var cc) ? cc.GetInt64() : 0;
                        var ctxWin = root.TryGetProperty("contextWindow", out var cw) ? cw.GetInt64() : 0;
                        var inTok = root.TryGetProperty("inputTokens", out var it) ? it.GetInt64() : 0;
                        EnqueueUiEvent(() =>
                        {
                            ContextWindow = ctxWin;
                            CurrentInputTokens = inTok;
                            UpdateBudget("", used, limit, cacheR, totalIn, cacheC, inTok);
                        });
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

        private static void UpdateBudget(string ubtype, long used, long limit, long cacheRead = 0, long totalInput = 0, long cacheCreate = 0, long inputTokens = 0)
        {
            static string Fmt(long v) => v >= 1_000_000 ? $"{v / 1_000_000f:F1}M" : v >= 1000 ? $"{v / 1000f:F0}K" : v.ToString();

            var parts = new System.Text.StringBuilder();

            // ① 输入用量: 入 12K 或 入 12K/200K 6%
            if (inputTokens > 0)
            {
                long ctxWin = ContextWindow;
                if (ctxWin > 0)
                {
                    double ctxPct = (double)inputTokens / ctxWin * 100.0;
                    parts.Append($"入 {Fmt(inputTokens)}/{Fmt(ctxWin)} {ctxPct:F0}%");
                }
                else
                {
                    parts.Append($"入 {Fmt(inputTokens)}");
                }
            }

            // ② Token 预算: Tok 43K/200K 85%
            if (parts.Length > 0) parts.Append("  │  ");
            parts.Append("Tok ");
            parts.Append(Fmt(used));
            parts.Append("/");
            parts.Append(limit > 0 ? Fmt(limit) : "--");
            if (limit > 0 && used > 0)
            {
                CurrentBudgetPercent = (float)((double)used / limit * 100.0);
                parts.Append($" {CurrentBudgetPercent:F0}%");
                CurrentBudgetStatus = used >= limit ? BudgetStatus.Exceeded
                    : CurrentBudgetPercent >= 95f ? BudgetStatus.Critical
                    : CurrentBudgetPercent >= 80f ? BudgetStatus.Warning
                    : BudgetStatus.Ok;
            }

            // ③ 缓存命中: 缓存 12K 35%
            long totalInWithCache = totalInput + cacheRead + cacheCreate;
            if (totalInWithCache > 0)
            {
                double cachePct = (double)cacheRead / totalInWithCache * 100.0;
                if (parts.Length > 0) parts.Append("  │  ");
                parts.Append($"缓存 {Fmt(cacheRead)} {cachePct:F0}%");
            }

            CurrentBudgetText = parts.ToString();
        }
    }
}
