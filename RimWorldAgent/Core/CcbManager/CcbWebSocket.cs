using System;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RimWorldAgent.Core.AgentRuntime;

namespace RimWorldAgent.Core.CcbManager
{
    public enum CcbClientState { Disconnected, Connecting, Connected, Ready }

    /// <summary>CC Companion WebSocket 客户端 — 心跳/重连/Token提取/hello/abort（旧 CCClient 逻辑）</summary>
    public class CcbWebSocket : IDisposable
    {
        private ClientWebSocket? _ws;
        private CancellationTokenSource? _cts;
        private string _url = "";
        private string _token = "";
        private CcbClientState _state = CcbClientState.Disconnected;

        private TaskCompletionSource<bool>? _helloOk;
        private DateTime _lastPong = DateTime.MinValue;
        private const int PingIntervalMs = 30000;
        private const int PongTimeoutMs = 45000;
        private DateTime _lastPing = DateTime.MinValue;
        private System.Threading.Timer? _heartbeatTimer;

        private int _reconnectDelayMs = 5000;
        private int _reconnectAttempts;
        private bool _reconnecting;
        private volatile bool _shuttingDown;
        private const int MaxReconnectDelayMs = 60000;

        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private readonly SemaphoreSlim _eventLock = new(1, 1);

        /// <summary>Token 预算上限（0 = 无限制）</summary>
        public long BudgetLimit { get; set; }
        /// <summary>思考力度：low / medium / high</summary>
        public string ThinkingEffort { get; set; } = "medium";
        /// <summary>最大思考 Token 数（0 = 默认）</summary>
        public int MaxThinkingTokens { get; set; }

        public CcbClientState State => _state;
        public bool IsConnected => _state >= CcbClientState.Connected;
        public bool IsReady => _state == CcbClientState.Ready;
        public bool IsSendingMessage { get; private set; }

        /// <summary>收到 Claude 文本回复时触发</summary>
        public event Action<string>? OnAssistantText;
        /// <summary>收到 tool_use 请求时触发</summary>
        public event Action<string, string, JsonElement?>? OnToolUse;
        /// <summary>回合结束</summary>
        public event Action<string, string?>? OnResult;
        /// <summary>收到中断确认</summary>
        public event Action? OnAborted;

        public CcbWebSocket(string url = "ws://localhost:19999", string token = "")
        {
            _url = url;
            _token = token;
        }

        // ========== 连接管理 ==========

        public async Task<bool> ConnectAsync(int timeoutMs = 10000)
        {
            _shuttingDown = false;
            Disconnect();

            try
            {
                _ws = new ClientWebSocket();
                _ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
                _cts = new CancellationTokenSource();
                _helloOk = new TaskCompletionSource<bool>();
                _state = CcbClientState.Connecting;

                await _ws.ConnectAsync(new Uri(_url), _cts.Token);
                _state = CcbClientState.Connected;

                await SendHello();
                _ = ReceiveLoop(_cts.Token);

                var timeout = Task.Delay(timeoutMs);
                if (await Task.WhenAny(_helloOk.Task, timeout) == _helloOk.Task)
                {
                    _state = CcbClientState.Ready;
                    _reconnectAttempts = 0;
                    _reconnectDelayMs = 5000;
                    _heartbeatTimer?.Dispose();
                    _heartbeatTimer = new System.Threading.Timer(_ => Heartbeat(), null, 5000, 5000);
                    return true;
                }
                CoreLog.Error("[CcbWS] hello-ok 超时");
                return false;
            }
            catch (Exception ex)
            {
                _state = CcbClientState.Disconnected;
                CoreLog.Error($"[CcbWS] 连接失败: {ex.Message}");
                _ = ScheduleReconnect();
                return false;
            }
        }

        public void Disconnect()
        {
            _shuttingDown = true;
            _heartbeatTimer?.Dispose();
            _heartbeatTimer = null;
            _cts?.Cancel();
            _state = CcbClientState.Disconnected;
            try { _ws?.Dispose(); } catch (Exception ex) { CoreLog.Info($"[CcbWS] Dispose WebSocket 异常: {ex.Message}"); }
            _ws = null;
            _lastPing = DateTime.MinValue;
            _lastPong = DateTime.MinValue;
        }

        // ========== 消息发送 ==========

        /// <summary>发送聊天消息（用户 prompt）</summary>
        public async Task SendChat(string text)
        {
            await SendEvent("chat", new { text });
        }

        /// <summary>发送工具执行结果回 Claude</summary>
        public async Task SendToolResult(string toolUseId, string content, bool isError = false)
        {
            await SendEvent("tool_result", new
            {
                tool_use_id = toolUseId,
                content = new[] { new { type = "text", text = content } },
                is_error = isError
            });
        }

        /// <summary>发送游戏事件到 CC Companion</summary>
        public async Task SendEvent(string eventName, object payload)
        {
            if (!IsReady) return;
            await _eventLock.WaitAsync();
            try
            {
                IsSendingMessage = true;
                await SendJson(new { type = "event", @event = eventName, payload });
            }
            finally
            {
                IsSendingMessage = false;
                _eventLock.Release();
            }
        }

        /// <summary>发送中断请求，中止当前 AI 回复</summary>
        public async Task SendAbort()
        {
            await _eventLock.WaitAsync();
            try
            {
                await SendJson(new { type = "abort" });
                CoreLog.Info("[CcbWS] 已发送中断请求");
            }
            finally { _eventLock.Release(); }
        }

        // ========== 内部 ==========

        private async Task SendHello()
        {
            await SendJson(new
            {
                type = "hello",
                client = new { name = "RimWorldAgent", version = "1.0" },
                auth = new { token = _token },
                budget = new
                {
                    limit = BudgetLimit,
                    used = 0L,
                    action = "Block"
                },
                thinking = new
                {
                    mode = "default",
                    effort = ThinkingEffort,
                    tokens = MaxThinkingTokens
                }
            });
        }

        private async Task SendJson(object obj)
        {
            await _sendLock.WaitAsync();
            try
            {
                var ws = _ws;
                if (ws?.State != WebSocketState.Open) return;
                var json = JsonSerializer.Serialize(obj, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
                var bytes = Encoding.UTF8.GetBytes(json);
                await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, _cts?.Token ?? CancellationToken.None);
            }
            finally { _sendLock.Release(); }
        }

        private async Task ReceiveLoop(CancellationToken ct)
        {
            var buf = new byte[8192];
            try
            {
                while (!ct.IsCancellationRequested && _ws?.State == WebSocketState.Open)
                {
                    var result = await _ws.ReceiveAsync(new ArraySegment<byte>(buf), ct);
                    if (result.MessageType == WebSocketMessageType.Close) break;
                    var text = Encoding.UTF8.GetString(buf, 0, result.Count);
                    ProcessMessage(text);
                }
            }
            catch (OperationCanceledException) { CoreLog.Info("[CcbWS] 接收循环已取消"); }
            catch (WebSocketException ex) { CoreLog.Info($"[CcbWS] WS 断开: {ex.Message}"); }
            catch (Exception ex) { CoreLog.Error($"[CcbWS] 接收异常: {ex.Message}"); }

            _state = CcbClientState.Disconnected;
            if (!ct.IsCancellationRequested) _ = ScheduleReconnect();
        }

        private void ProcessMessage(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (!root.TryGetProperty("type", out var t)) return;
                var type = t.GetString();

                switch (type)
                {
                    case "hello-ok":
                        _helloOk?.TrySetResult(true);
                        break;

                    case "assistant":
                    case "user":
                        ParseAssistantMessage(root);
                        CountToolResults(root);
                        ExtractUsageFromMessage(root);
                        break;

                    case "stream_event":
                        ParseStreamEvent(root);
                        ExtractUsageFromStreamEvent(root);
                        break;

                    case "result":
                        var subtype = root.TryGetProperty("subtype", out var st) ? st.GetString() : null;
                        var stopReason = root.TryGetProperty("stop_reason", out var sr) ? sr.GetString() : null;
                        OnResult?.Invoke(subtype ?? "unknown", stopReason);
                        break;

                    case "aborted":
                        OnAborted?.Invoke();
                        break;

                    case "system":
                        if (root.TryGetProperty("subtype", out var sub) && sub.GetString() == "init"
                            && root.TryGetProperty("model", out var modelEl))
                            TokenUsageTracker.CurrentModel = modelEl.GetString() ?? "";
                        break;

                    case "error":
                        var err = root.TryGetProperty("error", out var e) ? e.GetString() : "unknown";
                        CoreLog.Error($"[CcbWS] 服务器错误: {err}");
                        break;

                    case "pong":
                        _lastPong = DateTime.UtcNow;
                        break;
                }
            }
            catch (Exception ex) { CoreLog.Warn($"[CcbWS] 消息解析失败: {json.Substring(0, Math.Min(200, json.Length))} — {ex.Message}"); }
        }

        private void ParseAssistantMessage(JsonElement root)
        {
            if (!root.TryGetProperty("message", out var msg)) return;
            if (!msg.TryGetProperty("content", out var content)) return;

            if (content.ValueKind == JsonValueKind.Array)
            {
                foreach (var block in content.EnumerateArray())
                {
                    if (!block.TryGetProperty("type", out var bt)) continue;
                    var blockType = bt.GetString();
                    if (blockType == "text")
                        OnAssistantText?.Invoke(block.GetProperty("text").GetString() ?? "");
                    else if (blockType == "tool_use")
                        OnToolUse?.Invoke(
                            block.GetProperty("id").GetString() ?? "",
                            block.GetProperty("name").GetString() ?? "",
                            block.TryGetProperty("input", out var input) ? input : (JsonElement?)null
                        );
                }
            }
        }

        private void ParseStreamEvent(JsonElement root)
        {
            if (!root.TryGetProperty("event", out var evt)) return;
            if (!evt.TryGetProperty("type", out var et)) return;
            var eventType = et.GetString();

            // stream_event 中提取 assistant delta text 和 tool_use
            if (eventType == "content_block_delta" && evt.TryGetProperty("delta", out var delta))
            {
                if (delta.TryGetProperty("type", out var dt) && dt.GetString() == "text_delta"
                    && delta.TryGetProperty("text", out var txt))
                    OnAssistantText?.Invoke(txt.GetString() ?? "");
            }
            else if (eventType == "content_block_start" && evt.TryGetProperty("content_block", out var cb))
            {
                if (cb.TryGetProperty("type", out var cbt) && cbt.GetString() == "tool_use"
                    && evt.TryGetProperty("index", out var idx) && cb.TryGetProperty("id", out var tid))
                {
                    OnToolUse?.Invoke(tid.GetString() ?? "", cb.GetProperty("name").GetString() ?? "",
                        cb.TryGetProperty("input", out var inp) ? inp : (JsonElement?)null);
                }
            }
        }

        // ========== 心跳 ==========

        private void Heartbeat()
        {
            if (!IsReady) return;
            var now = DateTime.UtcNow;

            if ((now - _lastPing).TotalMilliseconds > PingIntervalMs)
                _ = SendPing();

            if (_lastPong != DateTime.MinValue && (now - _lastPong).TotalMilliseconds > PongTimeoutMs)
            {
                CoreLog.Error("[CcbWS] pong 超时，断开连接（将自动重连）");
                _state = CcbClientState.Disconnected;
                try { _ws?.CloseAsync(WebSocketCloseStatus.ProtocolError, "pong timeout", CancellationToken.None); } catch (Exception ex) { CoreLog.Info($"[CcbWS] 关闭 WS 异常: {ex.Message}"); }
            }
        }

        private async Task SendPing()
        {
            try { await SendJson(new { type = "keepalive" }); _lastPing = DateTime.UtcNow; _lastPong = DateTime.UtcNow; }
            catch (Exception ex) { CoreLog.Error($"[CcbWS] keepalive 失败: {ex.Message}"); }
        }

        // ========== 自动重连 ==========

        private async Task ScheduleReconnect()
        {
            if (_reconnecting || _shuttingDown) return;
            _reconnecting = true;
            try
            {
                while (true)
                {
                    _reconnectAttempts++;
                    var delay = Math.Min(_reconnectDelayMs * _reconnectAttempts, MaxReconnectDelayMs);
                    CoreLog.Info($"[CcbWS] {delay / 1000}s 后重连 (第 {_reconnectAttempts} 次)...");
                    await Task.Delay(delay);
                    if (_shuttingDown) break;
                    if (string.IsNullOrEmpty(_url)) break;
                    try { await ConnectAsync(); } catch (Exception ex) { CoreLog.Info($"[CcbWS] 重连尝试异常: {ex.Message}"); }
                    if (_state != CcbClientState.Disconnected) break;
                }
            }
            finally { _reconnecting = false; }
        }

        // ========== Token 提取（旧 CCClient 逻辑） ==========

        private static void CountToolResults(JsonElement root)
        {
            var message = root.TryGetProperty("message", out var msg) ? msg : root;
            if (!message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) return;
            foreach (var block in content.EnumerateArray())
                if (block.TryGetProperty("type", out var bt) && bt.GetString() == "tool_result")
                    TokenUsageTracker.RecordToolResult(block.TryGetProperty("is_error", out var ie) && ie.GetBoolean());
        }

        private static void ExtractUsageFromMessage(JsonElement root)
        {
            if (!root.TryGetProperty("message", out var msgEl)) return;
            if (msgEl.TryGetProperty("model", out var modelEl) && !string.IsNullOrEmpty(modelEl.GetString()))
                TokenUsageTracker.CurrentModel = modelEl.GetString()!;
            if (!msgEl.TryGetProperty("usage", out var u) || u.ValueKind != JsonValueKind.Object) return;
            long inp = u.TryGetProperty("input_tokens", out var it) ? it.GetInt64() : 0;
            long outp = u.TryGetProperty("output_tokens", out var ot) ? ot.GetInt64() : 0;
            long cr = u.TryGetProperty("cache_read_input_tokens", out var c) ? c.GetInt64() : 0;
            long cc = u.TryGetProperty("cache_creation_input_tokens", out var ct) ? ct.GetInt64() : 0;
            if (inp > 0 || outp > 0) TokenUsageTracker.Record(TokenUsageTracker.CurrentModel, inp, outp, cr, cc, 0);
        }

        private static void ExtractUsageFromStreamEvent(JsonElement root)
        {
            if (!root.TryGetProperty("event", out var evt)) return;
            if (!evt.TryGetProperty("type", out var et)) return;
            var eventType = et.GetString();
            JsonElement src = default;
            if (eventType == "message_start" && evt.TryGetProperty("message", out src)) { }
            else if (eventType == "message_delta" && evt.TryGetProperty("usage", out src)) { }
            else return;
            if (src.ValueKind != JsonValueKind.Object) return;
            long inp = src.TryGetProperty("input_tokens", out var it) ? it.GetInt64() : 0;
            long outp = src.TryGetProperty("output_tokens", out var ot) ? ot.GetInt64() : 0;
            long cr = src.TryGetProperty("cache_read_input_tokens", out var c) ? c.GetInt64() : 0;
            long cc = src.TryGetProperty("cache_creation_input_tokens", out var ct) ? ct.GetInt64() : 0;
            if (inp > 0 || outp > 0) TokenUsageTracker.Record(TokenUsageTracker.CurrentModel, inp, outp, cr, cc, 0);
        }

        public void Dispose() => Disconnect();
    }
}
