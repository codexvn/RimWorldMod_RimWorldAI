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
    }

    public class ChatEntry
    {
        public ChatRole Role;
        public string Text = "";
        public ChatState State;
        public string RunId = "";
        // 流式：记录上一个 delta chunk 的长度，用于 replace 场景
        public int LastChunkLen;
        // 由 UI 线程每帧写入，避免重复 Text.CalcHeight
        public float CachedHeight;
        public int CachedTextLen;
    }

    /// <summary>线程安全的聊天状态管理器，接收 Gateway 的 "chat" / "agent" 事件</summary>
    public static class ChatDisplayState
    {
        private const int MaxEntries = 100;
        private static readonly List<ChatEntry> _entries = new();
        private static readonly List<ToolCallInfo> _toolCalls = new();
        private static readonly object _lock = new();

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

        /// <summary>从 WebSocket 线程调用：处理 Gateway 广播的 "chat" 事件</summary>
        public static void OnChatEvent(JsonElement root)
        {
            var payload = root.TryGetProperty("payload", out var pl) ? pl : root;

            if (!payload.TryGetProperty("state", out var st)) return;
            var state = st.GetString();
            var runId = payload.TryGetProperty("runId", out var rid) ? rid.GetString() ?? "" : "";

            string? sessionKey = null;
            if (payload.TryGetProperty("sessionKey", out var sk))
                sessionKey = sk.GetString();

            // CC 模式单 session，无需过滤

            string? text = ExtractMessageText(payload);

            lock (_lock)
            {
                ChatEntry? entry = null;
                if (!string.IsNullOrEmpty(runId))
                {
                    for (int i = _entries.Count - 1; i >= 0; i--)
                    {
                        if (_entries[i].RunId == runId && _entries[i].Role == ChatRole.Assistant)
                        {
                            entry = _entries[i];
                            break;
                        }
                    }
                }

                switch (state)
                {
                    case "delta":
                    {
                        if (entry == null)
                        {
                            entry = new ChatEntry
                            {
                                Role = ChatRole.Assistant,
                                RunId = runId,
                                State = ChatState.Streaming
                            };
                            _entries.Add(entry);
                        }

                        // 提取增量 deltaText（非全文），实现流式逐字追加
                        var deltaText = payload.TryGetProperty("deltaText", out var dt)
                            ? dt.GetString() : null;
                        bool replace = payload.TryGetProperty("replace", out var rp)
                            && rp.GetBoolean();

                        if (!string.IsNullOrEmpty(deltaText))
                        {
                            if (replace)
                            {
                                // 替换上一段 chunk：回退后再追加
                                if (entry.LastChunkLen > 0)
                                    entry.Text = entry.Text.Substring(0,
                                        entry.Text.Length - entry.LastChunkLen);
                                entry.Text += deltaText!;
                                entry.LastChunkLen = deltaText!.Length;
                            }
                            else
                            {
                                entry.Text += deltaText!;
                                entry.LastChunkLen = deltaText!.Length;
                            }
                        }
                        // 如果 deltaText 为空，回退到全文覆盖（兼容旧 Gateway）
                        else if (!string.IsNullOrEmpty(text))
                        {
                            entry.Text = text!;
                            entry.LastChunkLen = 0;
                        }
                        entry.State = ChatState.Streaming;
                        break;
                    }

                    case "final":
                        // 用消息全文覆盖，确保最终文本准确
                        if (entry != null)
                        {
                            if (!string.IsNullOrEmpty(text))
                                entry.Text = text!;
                            entry.LastChunkLen = 0;
                            entry.State = ChatState.Done;
                        }
                        else if (!string.IsNullOrEmpty(text))
                        {
                            _entries.Add(new ChatEntry
                            {
                                Role = ChatRole.Assistant,
                                RunId = runId,
                                Text = text!,
                                State = ChatState.Done
                            });
                        }
                        break;

                    case "aborted":
                        if (entry != null)
                        {
                            if (payload.TryGetProperty("message", out var abortMsg)
                                && abortMsg.TryGetProperty("content", out var ac)
                                && ac.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var item in ac.EnumerateArray())
                                {
                                    if (item.TryGetProperty("text", out var at))
                                    {
                                        entry.Text = at.GetString() ?? entry.Text;
                                        break;
                                    }
                                }
                            }
                            if (string.IsNullOrEmpty(entry.Text))
                                entry.Text = "（已中断）";
                            entry.State = ChatState.Done;
                        }
                        else if (!string.IsNullOrEmpty(text))
                        {
                            _entries.Add(new ChatEntry
                            {
                                Role = ChatRole.Assistant,
                                RunId = runId,
                                Text = text!,
                                State = ChatState.Done
                            });
                        }
                        break;

                    case "error":
                        if (entry != null)
                        {
                            var errMsg = payload.TryGetProperty("errorMessage", out var em)
                                ? em.GetString() : null;
                            entry.Text = errMsg ?? "AI 回复出错";
                            entry.State = ChatState.Error;
                        }
                        else
                        {
                            var errMsg = payload.TryGetProperty("errorMessage", out var em2)
                                ? em2.GetString() : null;
                            _entries.Add(new ChatEntry
                            {
                                Role = ChatRole.Assistant,
                                RunId = runId,
                                Text = errMsg ?? "AI 回复出错",
                                State = ChatState.Error
                            });
                        }
                        break;
                }
                TrimEntries();
            }

            OnChanged?.Invoke();
        }

        /// <summary>限制条目上限，移除最旧的</summary>
        private static void TrimEntries()
        {
            while (_entries.Count > MaxEntries)
                _entries.RemoveAt(0);
        }

        /// <summary>用户发送消息时记录（从主线程调用）</summary>
        public static void OnUserMessage(string text)
        {
            lock (_lock)
            {
                _entries.Add(new ChatEntry
                {
                    Role = ChatRole.User,
                    Text = text,
                    State = ChatState.Done
                });
                TrimEntries();
            }
            OnChanged?.Invoke();
        }

        /// <summary>从 WebSocket 线程调用：处理 Gateway 广播的 "agent" 事件（工具调用/生命周期等）</summary>
        public static void OnAgentEvent(JsonElement root)
        {
            var payload = root.TryGetProperty("payload", out var pl) ? pl : root;

            if (!payload.TryGetProperty("stream", out var st)) return;
            var stream = st.GetString();

            // 上下文压缩事件 → 在聊天中显示提示
            if (stream == "compaction")
            {
                HandleCompactionUI(payload);
                return;
            }

            if (stream != "tool") return;

            string? sessionKey = null;
            if (payload.TryGetProperty("sessionKey", out var sk))
                sessionKey = sk.GetString();
            // CC 模式单 session，无需过滤

            if (!payload.TryGetProperty("data", out var data)) return;
            var itemId = data.TryGetProperty("itemId", out var iid) ? iid.GetString() ?? "" : "";
            var name = data.TryGetProperty("name", out var nm) ? nm.GetString() ?? "" : "";
            var title = data.TryGetProperty("title", out var ttl) ? ttl.GetString() ?? name : name;
            var meta = data.TryGetProperty("meta", out var mt) ? mt.GetString() ?? "" : "";
            var phase = data.TryGetProperty("phase", out var ph) ? ph.GetString() ?? "" : "";
            var status = data.TryGetProperty("status", out var stt) ? stt.GetString() ?? "" : "";

            lock (_lock)
            {
                ToolCallInfo? existing = null;
                for (int i = 0; i < _toolCalls.Count; i++)
                {
                    if (_toolCalls[i].ItemId == itemId)
                    { existing = _toolCalls[i]; break; }
                }

                if (phase == "start" || (existing == null && !string.IsNullOrEmpty(name)))
                {
                    if (existing == null)
                    {
                        existing = new ToolCallInfo { ItemId = itemId };
                        _toolCalls.Add(existing);
                    }
                    existing.Name = name;
                    existing.Title = title;
                    existing.Meta = meta;
                    existing.Status = ToolStatus.Running;
                }
                else if (existing != null)
                {
                    if (phase == "end" || status == "completed")
                        existing.Status = ToolStatus.Completed;
                    else if (status == "failed")
                        existing.Status = ToolStatus.Failed;
                    if (!string.IsNullOrEmpty(title))
                        existing.Title = title;
                    if (!string.IsNullOrEmpty(meta))
                        existing.Meta = meta;
                }

                // 保留最近 20 个工具调用
                while (_toolCalls.Count > 20)
                    _toolCalls.RemoveAt(0);
            }

            OnChanged?.Invoke();
        }

        public static void Clear()
        {
            lock (_lock)
            {
                _entries.Clear();
                _toolCalls.Clear();
            }
            OnChanged?.Invoke();
        }

        /// <summary>从 WebSocket 线程调用：处理 Gateway 广播的 "session.operation" 事件（压缩事件）</summary>
        public static void OnSessionOperationEvent(JsonElement root)
        {
            if (!root.TryGetProperty("payload", out var payload)) return;
            if (!payload.TryGetProperty("operation", out var op) || op.GetString() != "compact") return;

            if (!payload.TryGetProperty("phase", out var ph)) return;
            var phase = ph.GetString();

            bool? completed = payload.TryGetProperty("completed", out var cp) ? cp.GetBoolean() : null;

            string text = phase switch
            {
                "start" => "上下文压缩中...",
                "end" => completed == true ? "上下文压缩完成" : "上下文压缩结束",
                _ => ""
            };
            if (string.IsNullOrEmpty(text)) return;

            lock (_lock)
            {
                _entries.Add(new ChatEntry
                {
                    Role = ChatRole.Assistant,
                    Text = text,
                    State = ChatState.Done
                });
                TrimEntries();
            }
            OnChanged?.Invoke();
        }

        /// <summary>上下文压缩事件 → 聊天内显示提示</summary>
        private static void HandleCompactionUI(JsonElement payload)
        {
            if (!payload.TryGetProperty("data", out var data)) return;
            if (!data.TryGetProperty("phase", out var ph)) return;
            var phase = ph.GetString();

            string text = phase switch
            {
                "start" => "上下文压缩中...",
                "end" => data.TryGetProperty("completed", out var cp) && cp.GetBoolean()
                    ? "上下文压缩完成"
                    : "上下文压缩结束",
                _ => ""
            };
            if (string.IsNullOrEmpty(text)) return;

            lock (_lock)
            {
                _entries.Add(new ChatEntry
                {
                    Role = ChatRole.Assistant,
                    Text = text,
                    State = ChatState.Done
                });
                TrimEntries();
            }
            OnChanged?.Invoke();
        }

        private static string? ExtractMessageText(JsonElement payload)
        {
            if (payload.TryGetProperty("message", out var msg)
                && msg.TryGetProperty("content", out var content)
                && content.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in content.EnumerateArray())
                {
                    if (item.TryGetProperty("text", out var text))
                        return text.GetString();
                }
            }
            return null;
        }
    }
}
