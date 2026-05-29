using System;
using System.Collections.Generic;
using System.Text.Json;

namespace RimWorldMCP
{
    public enum ChatRole { User, Assistant }
    public enum ChatState { Streaming, Done, Error }

    public enum ToolStatus { Running, Completed, Failed }
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
        public string ThinkingText = "";  // 当前思考文本（与 Text 分开，互不覆盖）
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

    /// <summary>线程安全的聊天状态管理器，接收 Gateway 的 "chat" / "agent" 事件</summary>
    public static class ChatDisplayState
    {
        private const int MaxEntries = 100;
        private static readonly List<ChatEntry> _entries = new();
        private static readonly List<ToolCallInfo> _toolCalls = new();
        private static readonly object _lock = new();

        // 预算状态（由 BridgeLifecycle 更新，UI 持久渲染）
        public static BudgetStatus CurrentBudgetStatus = BudgetStatus.Ok;
        public static double CurrentBudgetPercent;
        public static string CurrentBudgetText = "";

        /// <summary>是否有活跃对话（流式输出或工具执行中），用于抑制空闲推送</summary>
        public static bool IsBusy
        {
            get
            {
                lock (_lock)
                {
                    if (_entries.Count > 0 && _entries[_entries.Count - 1].State == ChatState.Streaming)
                        return true;
                    foreach (var tc in _toolCalls)
                        if (tc.Status == ToolStatus.Running)
                            return true;
                    return false;
                }
            }
        }

        /// <summary>新消息事件（在主线程订阅）</summary>
        public static event Action? OnChanged;

        /// <summary>线程安全消息快照</summary>
        public static List<ChatEntry> Snapshot
        {
            get { lock (_lock) return new List<ChatEntry>(_entries); }
        }

        /// <summary>线程安全工具调用快照</summary>
        public static List<ToolCallInfo> ToolCallsSnapshot
        {
            get { lock (_lock) return new List<ToolCallInfo>(_toolCalls); }
        }

        /// <summary>限制条目上限，移除最旧的</summary>
        private static void TrimEntries()
        {
            while (_entries.Count > MaxEntries)
                _entries.RemoveAt(0);
        }

        /// <summary>事件系统上下文消息（空闲兜底/早报/弹框等），插入到流式条目之前避免打断 AI 回复</summary>
        public static void AddSystemMessage(string text)
        {
            lock (_lock)
            {
                var entry = new ChatEntry
                {
                    Role = ChatRole.Assistant,
                    Text = text,
                    State = ChatState.Done,
                    IsContext = true,
                };
                // 插入到流式条目之前，避免系统消息"插队"到最底部
                if (_streamingEntry != null)
                {
                    var idx = _entries.IndexOf(_streamingEntry);
                    if (idx >= 0)
                        _entries.Insert(idx, entry);
                    else
                        _entries.Add(entry);
                }
                else
                {
                    _entries.Add(entry);
                }
                TrimEntries();
            }
            OnChanged?.Invoke();
        }

        /// <summary>用户发送消息时记录（从主线程调用）</summary>
        public static void OnUserMessage(string text)
        {
            lock (_lock)
            {
                _toolCalls.Clear(); // 新消息 → 清理上轮工具卡片
                // 结束上一轮 AI 流式条目，确保每轮对话独立记录
                if (_streamingEntry != null)
                {
                    // 合并思考文本到正文（避免历史残留"AI 思考中"空标签）
                    if (!string.IsNullOrEmpty(_streamingEntry.ThinkingText))
                    {
                        _streamingEntry.Text = (_streamingEntry.ThinkingText ?? "")
                            + (string.IsNullOrEmpty(_streamingEntry.Text) ? "" : "\n" + _streamingEntry.Text);
                        _streamingEntry.ThinkingText = "";
                    }
                    _streamingEntry.State = ChatState.Done;
                    _streamingEntry.CachedHeight = 0f;
                    _streamingEntry = null;
                }
                _entries.Add(new ChatEntry
                {
                    Role = ChatRole.User,
                    Text = text,
                    State = ChatState.Done
                });
                TrimEntries();
            }
            _deltaAccum = "";
            OnChanged?.Invoke();
        }

        public static void Clear()
        {
            lock (_lock)
            {
                _entries.Clear();
                _toolCalls.Clear();
                _streamingEntry = null;
            }
            _deltaAccum = "";
            OnChanged?.Invoke();
        }

        public static void ClearToolCalls()
        {
            lock (_lock) { _toolCalls.Clear(); }
            OnChanged?.Invoke();
        }

        /// <summary>将最后一个流式输出的助理条目标记为"（已中断）"，用于中断按钮</summary>
        public static void MarkLastAborted()
        {
            lock (_lock)
            {
                if (_streamingEntry != null)
                {
                    _streamingEntry.State = ChatState.Done;
                    // 合并思考文本到正文（避免中断时思考内容丢失）
                    if (!string.IsNullOrEmpty(_streamingEntry.ThinkingText))
                    {
                        _streamingEntry.Text = "思考中: " + _streamingEntry.ThinkingText
                            + (string.IsNullOrEmpty(_streamingEntry.Text) ? "" : "\n" + _streamingEntry.Text);
                        _streamingEntry.ThinkingText = "";
                    }
                    if (string.IsNullOrEmpty(_streamingEntry.Text))
                        _streamingEntry.Text = "（已中断）";
                    else
                        _streamingEntry.Text += "（已中断）";
                    _streamingEntry.CachedHeight = 0f;
                    _streamingEntry = null;
                }
                _toolCalls.Clear(); // 清理残留工具调用，允许 IsBusy 恢复为 false
            }
            _deltaAccum = "";
            OnChanged?.Invoke();
        }

        /// <summary>处理 Companion 广播的 SDK 消息（assistant/user 类型）</summary>
        public static void OnSdkMessage(JsonElement sdkMsg)
        {
            var message = sdkMsg.TryGetProperty("message", out var msg) ? msg : sdkMsg;
            var role = message.TryGetProperty("role", out var r) ? r.GetString() : "assistant";
            var isUser = role == "user";

            // SDK 回显 user 消息 → 结束上一轮流式条目
            if (isUser) FinalizeStreaming();

            // 提取子代理信息
            var agentId = sdkMsg.TryGetProperty("agent_id", out var aid) ? aid.GetString() ?? "" : "";
            var agentType = sdkMsg.TryGetProperty("agent_type", out var at) ? at.GetString() ?? "" : "";

            // content 可能是数组或纯文本字符串
            if (!message.TryGetProperty("content", out var content)) return;

            string? streamingText = null;

            if (content.ValueKind == JsonValueKind.String)
            {
                if (!isUser)
                {
                    streamingText = content.GetString() ?? "";
                    _deltaAccum = streamingText;
                    _deltaIsThinking = false;
                }
            }
            else if (content.ValueKind == JsonValueKind.Array)
            {
                foreach (var block in content.EnumerateArray())
                {
                    var blockType = block.TryGetProperty("type", out var bt) ? bt.GetString() : "";
                    if (blockType == "text" && !isUser)
                    {
                        streamingText = block.TryGetProperty("text", out var tt) ? tt.GetString() ?? "" : "";
                        _deltaAccum = streamingText;
                        _deltaIsThinking = false;
                    }
                    else if (blockType == "thinking" && !isUser)
                    {
                        var thinking = block.TryGetProperty("thinking", out var th) ? th.GetString() ?? "" : "";
                        if (!string.IsNullOrEmpty(thinking))
                        {
                            streamingText = thinking;
                            _deltaAccum = thinking;
                            _deltaIsThinking = true;
                        }
                    }
                    else if (blockType == "tool_use")
                    {
                        var name = block.TryGetProperty("name", out var tn) ? tn.GetString() ?? "" : "";
                        var itemId = block.TryGetProperty("id", out var iid) ? iid.GetString() ?? "" : "";
                        lock (_lock)
                        {
                            bool exists = false;
                            for (int i = 0; i < _toolCalls.Count; i++)
                            {
                                if (_toolCalls[i].ItemId == itemId) { exists = true; break; }
                            }
                            if (!exists)
                            {
                                _toolCalls.Add(new ToolCallInfo
                                {
                                    ItemId = itemId, Name = name, Title = name,
                                    Status = ToolStatus.Running
                                });
                                while (_toolCalls.Count > 20) _toolCalls.RemoveAt(0);
                            }
                        }
                    }
                    else if (blockType == "tool_result")
                    {
                        var itemId = block.TryGetProperty("tool_use_id", out var tuid) ? tuid.GetString() ?? "" : "";
                        var isError = block.TryGetProperty("is_error", out var ie) && ie.GetBoolean();
                        lock (_lock)
                        {
                            for (int i = 0; i < _toolCalls.Count; i++)
                            {
                                if (_toolCalls[i].ItemId == itemId)
                                {
                                    _toolCalls[i].Status = isError ? ToolStatus.Failed : ToolStatus.Completed;
                                    _toolCalls[i].DurationMs = (DateTime.UtcNow - _toolCalls[i].StartTime).TotalMilliseconds;
                                    if (isError)
                                    {
                                        var errContent = block.TryGetProperty("content", out var ec) ? ec : default;
                                        _toolCalls[i].Meta = ExtractToolError(errContent);
                                    }
                                    break;
                                }
                            }
                        }
                    }
                }
            }

            if (!string.IsNullOrEmpty(streamingText))
            {
                UpsertStreaming(streamingText!, _deltaIsThinking, agentId, agentType);
            }

            // tool_use / tool_result 变更也需要刷新 UI
            if (streamingText == null)
                OnChanged?.Invoke();
        }

        private static string ExtractToolError(JsonElement content)
        {
            if (content.ValueKind == JsonValueKind.String)
                return Truncate(content.GetString() ?? "", 80);
            if (content.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in content.EnumerateArray())
                {
                    if (item.TryGetProperty("text", out var tt))
                        return Truncate(tt.GetString() ?? "", 80);
                }
            }
            return "执行失败";
        }

        private static string Truncate(string s, int max) =>
            s.Length <= max ? s : s.Substring(0, max - 3) + "...";

        // ========== 流式输出 ==========

        private static ChatEntry? _streamingEntry;

        private static void UpsertStreaming(string text, bool isThinking, string agentId, string agentType)
        {
            lock (_lock)
            {
                if (_streamingEntry == null)
                {
                    _streamingEntry = new ChatEntry
                    {
                        Role = ChatRole.Assistant,
                        State = ChatState.Streaming,
                        AgentId = agentId,
                        AgentType = agentType,
                    };
                    _entries.Add(_streamingEntry);
                }
                else
                {
                    _streamingEntry.AgentId = agentId;
                    _streamingEntry.AgentType = agentType;
                    _streamingEntry.CachedHeight = 0f;
                }

                if (isThinking)
                    _streamingEntry.ThinkingText = text;
                else
                {
                    _streamingEntry.Text = text;
                    _streamingEntry.ThinkingText = ""; // 正文开始，清除思考
                }

                TrimEntries();
            }
            OnChanged?.Invoke();
        }

        // ========== stream_event 增量流式 ==========

        private static string _deltaAccum = "";
        private static bool _deltaIsThinking;

        /// <summary>处理 stream_event 消息（content_block_delta 增量更新）</summary>
        public static void OnStreamEvent(JsonElement sdkMsg)
        {
            if (!sdkMsg.TryGetProperty("event", out var evt)) return;
            var eventType = evt.TryGetProperty("type", out var et) ? et.GetString() : "";

            if (eventType == "content_block_start")
            {
                var block = evt.TryGetProperty("content_block", out var cb) ? cb : default;
                var blockType = block.TryGetProperty("type", out var bt) ? bt.GetString() : "";
                _deltaIsThinking = blockType == "thinking";
                _deltaAccum = "";
                // 立即显示空条目 + 光标，UI 可见 AI 正在响应
                if (_streamingEntry == null)
                    UpsertStreaming("", _deltaIsThinking, "", "");
            }
            else if (eventType == "content_block_delta")
            {
                if (!evt.TryGetProperty("delta", out var delta)) return;
                var deltaType = delta.TryGetProperty("type", out var dt) ? dt.GetString() : "";

                if (deltaType == "text_delta")
                {
                    var text = delta.TryGetProperty("text", out var tt) ? tt.GetString() ?? "" : "";
                    if (_deltaIsThinking) { _deltaIsThinking = false; _deltaAccum = ""; }
                    _deltaAccum += text;
                    UpsertStreaming(_deltaAccum, false, "", "");
                }
                else if (deltaType == "thinking_delta")
                {
                    var thinking = delta.TryGetProperty("thinking", out var th) ? th.GetString() ?? "" : "";
                    if (!_deltaIsThinking) { _deltaIsThinking = true; _deltaAccum = ""; }
                    _deltaAccum += thinking;
                    UpsertStreaming(_deltaAccum, true, "", "");
                }
                else if (deltaType == "input_json_delta")
                {
                    // tool_use 参数增量 — tool_use block 单独处理
                }
            }
        }

        /// <summary>结束当前流式条目（result / 新用户消息时调用）</summary>
        public static void FinalizeStreaming()
        {
            lock (_lock)
            {
                if (_streamingEntry != null)
                {
                    _streamingEntry.State = ChatState.Done;
                    _streamingEntry.CachedHeight = 0f;
                    _streamingEntry = null;
                }
            }
            _deltaAccum = "";
            OnChanged?.Invoke();
        }

    }
}
