using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.Json;
using Fleck;
using RimWorldAgent.Core.AgentRuntime;

namespace RimWorldAgent.Core
{
    /// <summary>
    /// 桥接总线 — Hook WS :19999
    /// SDK 原始消息 → ParseSdkToUiMessages → UiMessage → WS广播 + 本地回调
    /// 客户端消息 (chat/abort) → OnChat/OnAbort → 预算检查 + echo → CCB
    /// 所有 UI 消费同一数据源 (UiMessage 协议)，保证显示一致。
    /// </summary>
    public static class BridgeBus
    {
        private static WebSocketServer? _server;
        private static readonly ConcurrentDictionary<Guid, IWebSocketConnection> _clients = new();

        public static bool IsRunning => _server != null;
        public static bool IsReady { get; set; }

        // ===== 上游：CCB → BridgeBus =====

        /// <summary>SDK 原始消息 → 转换为 UiMessage → WS 广播 + 本地回调</summary>
        public static void PushSdkMessage(string rawJson)
        {
            CoreLog.Info($"[CCGUI_DEBUG] BridgeBus.PushSdkMessage 入站 len={rawJson.Length} _clients={_clients.Count} OnDisplayMessage={(OnDisplayMessage != null ? OnDisplayMessage.GetInvocationList().Length : 0)}");
            // 解析 → 一组 UiMessage
            var messages = ParseSdkToUiMessages(rawJson);
            CoreLog.Info($"[CCGUI_DEBUG] BridgeBus.PushSdkMessage 解析出 {messages.Count} 条 UiMessage");
            if (messages.Count == 0) return;

            foreach (var msg in messages)
            {
                // WS 广播给所有外部客户端 (Web UI)
                foreach (var kv in _clients)
                {
                    try { kv.Value.Send(msg); }
                    catch (Exception ex) { CoreLog.Info($"[BridgeBus] 发送失败: {ex.Message}"); _clients.TryRemove(kv.Key, out _); }
                }
                // 本地回调：ChatDisplayState 同步消费
                OnDisplayMessage?.Invoke(msg);
            }
        }

        /// <summary>SDK 消息本地回调 (供 ChatDisplayState)</summary>
        public static event Action<string>? OnDisplayMessage;

        /// <summary>Game/System 事件 → 直接广播 (已是 UiMessage 格式)</summary>
        public static void PushGameEvent(string uiJson)
        {
            CoreLog.Info($"[CCGUI_DEBUG] BridgeBus.PushGameEvent _clients={_clients.Count} preview={uiJson.Substring(0, Math.Min(uiJson.Length, 120))}");
            foreach (var kv in _clients)
            {
                try { kv.Value.Send(uiJson); }
                catch (Exception ex) { CoreLog.Info($"[BridgeBus] 发送失败: {ex.Message}"); _clients.TryRemove(kv.Key, out _); }
            }
            OnDisplayMessage?.Invoke(uiJson);
        }

        // ===== 下游：Web UI / Dialog → CCB =====

        public class ChatThinking
        {
            public string Mode = "default";
            public string Effort = "medium";
            public int Tokens;
        }

        public static event Action<string, ChatThinking?>? OnChat;
        public static event Action? OnAbort;

        public static void RaiseChat(string text, ChatThinking? thinking = null) => OnChat?.Invoke(text, thinking);
        public static void RaiseAbort() => OnAbort?.Invoke();

        // ===== SDK → UiMessage 转换 =====

        private static List<string> ParseSdkToUiMessages(string rawJson)
        {
            var result = new List<string>();
            try
            {
                using var doc = JsonDocument.Parse(rawJson);
                var root = doc.RootElement;
                if (!root.TryGetProperty("type", out var typeProp)) return result;
                var type = typeProp.GetString();

                switch (type)
                {
                    case "assistant":
                        ParseAssistant(doc, result);
                        break;
                    case "stream_event":
                        ParseStreamEvent(doc, result);
                        break;
                    case "result":
                    {
                        var subtype = root.TryGetProperty("subtype", out var st) ? st.GetString() : "";
                        var sr = root.TryGetProperty("stop_reason", out var stop) ? stop.GetString() : null;
                        result.Add(UiMessage.Result(subtype ?? "", sr));
                        break;
                    }
                    case "system":
                    {
                        var sub = root.TryGetProperty("subtype", out var s) ? s.GetString() : "";
                        if (sub == "init")
                        {
                            var model = root.TryGetProperty("model", out var m) ? m.GetString() : null;
                            var sid = root.TryGetProperty("session_id", out var sidE) ? sidE.GetString() : null;
                            result.Add(UiMessage.SystemInit(model, sid));
                        }
                        break;
                    }
                    case "aborted":
                        result.Add(UiMessage.Aborted());
                        break;
                }
            }
            catch (Exception ex) { CoreLog.Warn($"[BridgeBus] SDK 消息解析失败: {ex.Message}"); }
            return result;
        }

        private static void ParseAssistant(JsonDocument doc, List<string> outList)
        {
            var root = doc.RootElement;
            // usage: 仅从 assistant 消息提取 (完整 input/output/cache)
            ExtractUsage(doc);
            // content blocks
            if (!root.TryGetProperty("message", out var msg)) return;
            if (!msg.TryGetProperty("content", out var content)) return;
            foreach (var block in content.EnumerateArray())
            {
                var bt = block.GetProperty("type").GetString();
                if (bt == "text")
                {
                    var text = block.GetProperty("text").GetString() ?? "";
                    outList.Add(UiMessage.TextBlock(text));
                }
                else if (bt == "tool_use")
                {
                    var id = block.GetProperty("id").GetString() ?? "";
                    var name = block.GetProperty("name").GetString() ?? "";
                    var input = block.TryGetProperty("input", out var inp) ? inp.GetRawText() : "{}";
                    outList.Add(UiMessage.ToolCall(id, name, input));
                }
            }
        }

        private static void ParseStreamEvent(JsonDocument doc, List<string> outList)
        {
            var root = doc.RootElement;
            if (!root.TryGetProperty("event", out var evt)) return;
            var et = evt.GetProperty("type").GetString();

            if (et == "content_block_start")
            {
                var cb = evt.GetProperty("content_block");
                var cbt = cb.GetProperty("type").GetString();
                if (cbt == "text")
                    outList.Add(UiMessage.TextDelta(""));    // 空串 = 新 text block 开始
                else if (cbt == "thinking")
                    outList.Add(UiMessage.ThinkingDelta("")); // 空串 = 新 thinking block 开始
            }
            else if (et == "content_block_delta")
            {
                var delta = evt.GetProperty("delta");
                var dt = delta.GetProperty("type").GetString();
                if (dt == "text_delta")
                    outList.Add(UiMessage.TextDelta(delta.GetProperty("text").GetString() ?? ""));
                else if (dt == "thinking_delta")
                    outList.Add(UiMessage.ThinkingDelta(delta.GetProperty("thinking").GetString() ?? ""));
            }
        }

        private static void ExtractUsage(JsonDocument doc)
        {
            try
            {
                var root = doc.RootElement;
                if (!root.TryGetProperty("message", out var msg)) return;
                if (!msg.TryGetProperty("usage", out var usage)) return;
                var inp = TryGetLong(usage, "input_tokens");
                var outp = TryGetLong(usage, "output_tokens");
                var cr = TryGetLong(usage, "cache_read_input_tokens");
                var cc = TryGetLong(usage, "cache_creation_input_tokens");
                if (inp > 0 || outp > 0)
                    TokenUsageTracker.Record(inp, outp, cr, cc, 0);
            }
            catch (Exception ex) { CoreLog.Warn($"[BridgeBus] Usage 提取失败: {ex.Message}"); }
        }

        private static long TryGetLong(JsonElement el, string key)
            => el.TryGetProperty(key, out var v) && v.TryGetInt64(out var n) ? n : 0;

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
                    CoreLog.Info($"[BridgeBus] 客户端已连接: {socket.ConnectionInfo.ClientIpAddress} id={id} 总数={_clients.Count}");
                };
                socket.OnClose = () =>
                {
                    _clients.TryRemove(id, out _);
                    CoreLog.Info($"[BridgeBus] 客户端已断开: {socket.ConnectionInfo.ClientIpAddress} id={id} 剩余={_clients.Count}");
                };
                socket.OnMessage = msg =>
                {
                    CoreLog.Info($"[CCGUI_DEBUG] BridgeBus 收到客户端消息 len={msg.Length} preview={msg.Substring(0, Math.Min(msg.Length, 200))}");
                    try
                    {
                        using var doc = JsonDocument.Parse(msg);
                        var root = doc.RootElement;
                        var type = root.TryGetProperty("type", out var t) ? t.GetString() : "";
                        CoreLog.Info($"[CCGUI_DEBUG] BridgeBus 解析客户端消息 type={type}");
                        switch (type)
                        {
                            case "chat":
                                var text = root.TryGetProperty("text", out var txt) ? txt.GetString() ?? "" : "";
                                CoreLog.Info($"[CCGUI_DEBUG] BridgeBus chat text=\"{text}\" OnChat订阅者={(OnChat != null ? OnChat.GetInvocationList().Length : 0)}");
                                if (!string.IsNullOrEmpty(text))
                                {
                                    var think = ParseThinking(root);
                                    CoreLog.Info($"[CCGUI_DEBUG] BridgeBus chat thinking mode={think?.Mode} effort={think?.Effort} tokens={think?.Tokens}");
                                    OnChat?.Invoke(text, think);
                                }
                                else
                                    CoreLog.Info($"[CCGUI_DEBUG] BridgeBus chat text 为空，跳过");
                                break;
                            case "abort":
                                CoreLog.Info($"[CCGUI_DEBUG] BridgeBus abort OnAbort订阅者={(OnAbort != null ? OnAbort.GetInvocationList().Length : 0)}");
                                OnAbort?.Invoke();
                                break;
                            default:
                                CoreLog.Info($"[CCGUI_DEBUG] BridgeBus 未知客户端消息类型: {type}");
                                break;
                        }
                    }
                    catch (Exception ex) { CoreLog.Info($"[BridgeBus] 消息解析失败: {ex.Message}"); }
                };
            });
            CoreLog.Info($"[BridgeBus] 已启动 ws://0.0.0.0:{port} IsRunning={IsRunning}");
        }

        public static void Stop()
        {
            OnChat = null;
            OnAbort = null;
            OnDisplayMessage = null;
            if (_server == null) return;
            foreach (var kv in _clients) { try { kv.Value.Close(); } catch { } }
            _clients.Clear();
            _server.Dispose();
            _server = null;
            CoreLog.Info("[BridgeBus] 已停止");
        }

    }
}
