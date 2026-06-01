using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.Json;
using Fleck;
using RimWorldAgent.Core.AgentRuntime;
using RimWorldAgent.Core.Data;

namespace RimWorldAgent.Core
{
    /// <summary>
    /// UI 总线 — Fleck WS :19999
    /// 只负责 UiMessage 广播 + 客户端消息接收，不感知 SDK 原始格式。
    /// SDK → UiMessage 转换由 SdkMessageParser (AgentCore) 负责。
    /// </summary>
    public static class UIMessageBus
    {
        private static WebSocketServer? _server;
        private static readonly ConcurrentDictionary<Guid, IWebSocketConnection> _clients = new();

        public static bool IsRunning => _server != null;
        public static bool IsReady { get; set; }

        // ===== 上游：AgentCore → UIMessageBus → UI =====

        /// <summary>推送 UiMessage 列表 — 序列化 + WS 广播 + 本地回调</summary>
        public static void PushUiMessages(List<UiMessage> messages)
        {
            CoreLog.Info($"[CCGUI_DEBUG] UIMessageBus.PushUiMessages count={messages.Count} _clients={_clients.Count}");
            foreach (var msg in messages)
            {
                var json = msg.ToJson();
                foreach (var kv in _clients)
                {
                    try { kv.Value.Send(json); }
                    catch (Exception ex) { CoreLog.Info($"[UIMessageBus] 发送失败: {ex.Message}"); _clients.TryRemove(kv.Key, out _); }
                }
                OnDisplayMessage?.Invoke(json);
            }
        }

        /// <summary>UiMessage 本地回调 (供 ChatDisplayState)</summary>
        public static event Action<string>? OnDisplayMessage;

        /// <summary>推送单个 UiMessage — 序列化 + WS 广播 + 本地回调</summary>
        public static void PushUiMessage(UiMessage msg)
        {
            var json = msg.ToJson();
            CoreLog.Info($"[CCGUI_DEBUG] UIMessageBus.PushUiMessage _clients={_clients.Count} preview={json.Substring(0, Math.Min(json.Length, 120))}");
            foreach (var kv in _clients)
            {
                try { kv.Value.Send(json); }
                catch (Exception ex) { CoreLog.Info($"[UIMessageBus] 发送失败: {ex.Message}"); _clients.TryRemove(kv.Key, out _); }
            }
            OnDisplayMessage?.Invoke(json);
        }

        // ===== 下游：客户端 → UIMessageBus → AgentCore =====

        public class ChatThinking
        {
            public string Mode = "default";
            public string Effort = "medium";
            public int Tokens;
        }

        public static event Action<string, ChatThinking?>? OnChat;
        public static event Action? OnAbort;

        /// <summary>客户端请求历史记录，(socket, 请求条数 n)</summary>
        public static event Action<IWebSocketConnection, int>? OnHistory;

        /// <summary>客户端请求更早的消息，(socket, beforeId, n)</summary>
        public static event Action<IWebSocketConnection, long, int>? OnHistoryBefore;

        /// <summary>SDK assistant 消息完整内容，(text, thinking, runId, agentType)</summary>
        public static event Action<string, string, string, string>? OnAssistantContent;

        /// <summary>SDK 工具调用，(toolId, name, input)</summary>
        public static event Action<string, string, string>? OnToolCallRecorded;

        /// <summary>SDK 工具结果，(toolId, isError, content)</summary>
        public static event Action<string, bool, string>? OnToolResultRecorded;

        /// <summary>新客户端连接，(socket)</summary>
        public static event Action<IWebSocketConnection>? OnClientConnected;

        public static void RaiseChat(string text, ChatThinking? thinking = null) => OnChat?.Invoke(text, thinking);
        public static void RaiseAbort() => OnAbort?.Invoke();
        public static void RaiseAssistantContent(string text, string thinking, string runId, string agentType)
            => OnAssistantContent?.Invoke(text, thinking, runId, agentType);
        public static void RaiseToolCallRecorded(string toolId, string name, string input)
            => OnToolCallRecorded?.Invoke(toolId, name, input);
        public static void RaiseToolResultRecorded(string toolId, bool isError, string content)
            => OnToolResultRecorded?.Invoke(toolId, isError, content);

        // ===== 生命周期 =====

        public static void Start(int port = 19999)
        {
            if (_server != null) return;
            _server = new WebSocketServer($"ws://0.0.0.0:{port}");
            _server.Start(socket =>
            {
                var id = socket.ConnectionInfo.Id;
                socket.OnOpen = () =>
                {
                    _clients[id] = socket;
                    CoreLog.Info($"[UIMessageBus] 客户端已连接: {socket.ConnectionInfo.ClientIpAddress} id={id} 总数={_clients.Count}");
                    OnClientConnected?.Invoke(socket);
                };
                socket.OnClose = () =>
                {
                    _clients.TryRemove(id, out _);
                    CoreLog.Info($"[UIMessageBus] 客户端已断开: {socket.ConnectionInfo.ClientIpAddress} id={id} 剩余={_clients.Count}");
                };
                socket.OnMessage = msg =>
                {
                    CoreLog.Info($"[CCGUI_DEBUG] UIMessageBus 收到客户端消息 len={msg.Length} preview={msg.Substring(0, Math.Min(msg.Length, 200))}");
                    try
                    {
                        using var doc = JsonDocument.Parse(msg);
                        var root = doc.RootElement;
                        var type = root.TryGetProperty("type", out var t) ? t.GetString() : "";
                        CoreLog.Info($"[CCGUI_DEBUG] UIMessageBus 解析客户端消息 type={type}");
                        switch (type)
                        {
                            case "chat":
                                var text = root.TryGetProperty("text", out var txt) ? txt.GetString() ?? "" : "";
                                if (!string.IsNullOrEmpty(text))
                                {
                                    var think = ParseThinking(root);
                                    CoreLog.Info($"[CCGUI_DEBUG] UIMessageBus chat thinking mode={think?.Mode} effort={think?.Effort} tokens={think?.Tokens}");
                                    OnChat?.Invoke(text, think);
                                }
                                break;
                            case "abort":
                                OnAbort?.Invoke();
                                break;
                            case "history":
                            {
                                var n = root.TryGetProperty("n", out var nEl) && nEl.TryGetInt32(out var nv) ? Math.Min(nv, 200) : 30;
                                OnHistory?.Invoke(socket, n);
                                break;
                            }
                            case "history_before":
                            {
                                var beforeId = root.TryGetProperty("before_id", out var bid) && bid.TryGetInt64(out var bv) ? bv : 0L;
                                var nb = root.TryGetProperty("n", out var nEl2) && nEl2.TryGetInt32(out var nv2) ? Math.Min(nv2, 200) : 30;
                                OnHistoryBefore?.Invoke(socket, beforeId, nb);
                                break;
                            }
                        }
                    }
                    catch (Exception ex) { CoreLog.Info($"[UIMessageBus] 消息解析失败: {ex.Message}"); }
                };
            });
            CoreLog.Info($"[UIMessageBus] 已启动 ws://0.0.0.0:{port}");
        }

        public static void Stop()
        {
            OnChat = null;
            OnAbort = null;
            OnDisplayMessage = null;
            OnHistory = null;
            OnAssistantContent = null;
            OnHistoryBefore = null;
            OnToolCallRecorded = null;
            OnToolResultRecorded = null;
            OnClientConnected = null;
            if (_server == null) return;
            foreach (var kv in _clients) { try { kv.Value.Close(); } catch { } }
            _clients.Clear();
            _server.Dispose();
            _server = null;
            CoreLog.Info("[UIMessageBus] 已停止");
        }

        private static ChatThinking? ParseThinking(JsonElement root)
        {
            if (!root.TryGetProperty("thinking", out var th)) return null;
            return new ChatThinking
            {
                Mode = th.TryGetProperty("mode", out var m) ? m.GetString() ?? "default" : "default",
                Effort = th.TryGetProperty("effort", out var e) ? e.GetString() ?? "medium" : "medium",
                Tokens = th.TryGetProperty("tokens", out var t) && t.TryGetInt32(out var n) ? n : 0,
            };
        }
    }
}
