using System;
using System.Collections.Generic;
using System.Text.Json;

namespace RimWorldMCP
{
    public enum ChatRole { User, Assistant }
    public enum ChatState { Streaming, Done, Error }

    public class ChatEntry
    {
        public ChatRole Role;
        public string Text = "";
        public ChatState State;
        public string RunId = "";
    }

    /// <summary>线程安全的聊天状态管理器，接收 Gateway 的 "chat" 事件并累积对话历史</summary>
    public static class ChatDisplayState
    {
        private static readonly List<ChatEntry> _entries = new();
        private static readonly object _lock = new();

        /// <summary>新消息事件（在主线程订阅，调用 Window.Close() 强制刷新）</summary>
        public static event Action? OnChanged;

        /// <summary>线程安全快照</summary>
        public static List<ChatEntry> Snapshot
        {
            get { lock (_lock) return new List<ChatEntry>(_entries); }
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

            // 只接收我们自己 session 的消息
            if (!string.IsNullOrEmpty(sessionKey) && sessionKey != GatewayClient.SessionKey)
                return;

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
                        // delta 的 message.content[0].text 是合并后的全文，直接覆盖
                        if (!string.IsNullOrEmpty(text))
                            entry.Text = text!;
                        entry.State = ChatState.Streaming;
                        break;

                    case "final":
                        if (entry != null)
                        {
                            if (!string.IsNullOrEmpty(text))
                                entry.Text = text!;
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
            }

            OnChanged?.Invoke();
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
            }
            OnChanged?.Invoke();
        }

        public static void Clear()
        {
            lock (_lock) _entries.Clear();
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
