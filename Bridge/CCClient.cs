using System;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RimWorldMCP.Mcp;
using RimWorldMCP.Tools;

namespace RimWorldMCP
{
    public enum CCClientState { Disconnected, Connecting, Connected, Ready }

    /// <summary>CC Companion WebSocket 客户端 — 连接本地 CC Companion，推送游戏事件</summary>
    public static class CCClient
    {
        private static ClientWebSocket? _ws;
        private static CancellationTokenSource? _cts;
        private static string _url = "";
        private static string _token = "";
        private static CCClientState _state = CCClientState.Disconnected;

        private static TaskCompletionSource<bool>? _helloOk;
        private static DateTime _lastPong = DateTime.MinValue;
        private const int PingIntervalMs = 30000;
        private const int PongTimeoutMs = 60000;
        private static DateTime _lastPing = DateTime.MinValue;

        private static int _reconnectDelayMs = 5000;
        private static int _reconnectAttempts;
        private static bool _reconnecting;
        private static volatile bool _shuttingDown;
        private const int MaxReconnectDelayMs = 60000;

        private static readonly SemaphoreSlim _sendLock = new(1, 1);
        private static readonly SemaphoreSlim _eventLock = new(1, 1);

        public static CCClientState State => _state;
        public static bool IsConnected => _state >= CCClientState.Connected;
        public static bool IsReady => _state == CCClientState.Ready;
        public static bool IsSendingMessage { get; private set; }

        // ========== 连接管理 ==========

        public static async Task Connect(string wsUrl, string token = "")
        {
            _url = wsUrl;
            _token = token ?? "";
            _shuttingDown = false;
            Disconnect();

            try
            {
                _ws = new ClientWebSocket();
                _ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
                _cts = new CancellationTokenSource();
                _helloOk = new TaskCompletionSource<bool>();
                _state = CCClientState.Connecting;

                McpLog.Info($"[cc] 正在连接 Claude Code: {wsUrl}");
                await _ws.ConnectAsync(new Uri(wsUrl), _cts.Token);

                _state = CCClientState.Connected;
                McpLog.Info($"[cc] WebSocket 已连接");

                // 发送 hello
                await SendHello();
                // 启动接收循环
                _ = ReceiveLoop(_cts.Token);

                // 等待 hello-ok
                var timeout = Task.Delay(10000);
                var completed = await Task.WhenAny(_helloOk.Task, timeout);
                if (completed == _helloOk.Task && _helloOk.Task.Result)
                {
                    _state = CCClientState.Ready;
                    _reconnectAttempts = 0;
                    _reconnectDelayMs = 5000;
                    McpLog.Info("[cc] 握手完成，Claude Code 就绪");
                }
                else
                {
                    McpLog.Error("[cc] 握手超时(10s)");
                }
            }
            catch (Exception ex)
            {
                _state = CCClientState.Disconnected;
                McpLog.Error($"[cc] 连接失败: {ex.Message}");
                _ = ScheduleReconnect();
            }
        }

        public static void Disconnect()
        {
            _shuttingDown = true;
            _cts?.Cancel();
            _state = CCClientState.Disconnected;
            try { _ws?.Dispose(); } catch { }
            _ws = null;
            _lastPing = DateTime.MinValue;
            _lastPong = DateTime.MinValue;
        }

        // ========== 事件发送 ==========

        /// <summary>发送游戏事件到 CC Companion，由 Companion 转发给 Claude SDK</summary>
        public static async Task SendEvent(string eventName, object payload)
        {
            if (!IsReady) return;

            await _eventLock.WaitAsync();
            try
            {
                IsSendingMessage = true;
                await SendJson(new
                {
                    type = "event",
                    @event = eventName,
                    payload
                });
            }
            finally
            {
                IsSendingMessage = false;
                _eventLock.Release();
            }
        }

        /// <summary>发送游戏事件（文本格式）</summary>
        public static async Task SendEventText(string eventName, string category, string text, object? colonyStats = null)
        {
            await SendEvent(eventName, new
            {
                category,
                text,
                severity = category is "RaidStart" or "PawnDeath" ? "high"
                    : category is "AlertStart" or "NegativeEvent" ? "medium" : "low",
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                colonyStats
            });
        }

        /// <summary>发送中断请求到 Companion，中止当前 AI 回复</summary>
        internal static async Task SendAbort()
        {
            await _eventLock.WaitAsync();
            try
            {
                await SendJson(new { type = "abort" });
                McpLog.Info("[cc] 已发送中断请求");
            }
            finally
            {
                _eventLock.Release();
            }
        }

        // ========== WebSocket IO ==========

        private static async Task SendHello()
        {
            var settings = RimWorldMCPMod.Instance.Settings;
            await SendJson(new
            {
                type = "hello",
                client = new
                {
                    name = "RimWorldMCP",
                    version = "1.0"
                },
                auth = new
                {
                    token = _token
                },
                budget = new
                {
                    limit = settings.TokenBudgetLimit,
                    used = TokenUsageTracker.TotalAllTokens,
                    action = settings.TokenBudgetExceedAction == TokenBudgetExceedAction.Block ? "Block" : "Warn"
                },
                thinking = new
                {
                    mode = settings.CCBThinkingMode.ToString(),
                    effort = settings.CCBThinkingEffort,
                    tokens = settings.CCBMaxThinkingTokens
                }
            });
        }

        private static async Task SendJson(object obj)
        {
            await _sendLock.WaitAsync();
            try
            {
                var ws = _ws;
                if (ws?.State != WebSocketState.Open) return;
                var json = JsonSerializer.Serialize(obj, McpJson.Options);
                McpLog.Debug($"[cc] → {Truncate(json)}");
                var bytes = Encoding.UTF8.GetBytes(json);
                await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true,
                    _cts?.Token ?? CancellationToken.None);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        private static string Truncate(string s) => s.Length <= 300 ? s : s.Substring(0, 297) + "...";

        private static async Task ReceiveLoop(CancellationToken ct)
        {
            var buf = new byte[8192];
            try
            {
                while (!ct.IsCancellationRequested && _ws?.State == WebSocketState.Open)
                {
                    var result = await _ws.ReceiveAsync(new ArraySegment<byte>(buf), ct);
                    if (result.MessageType == WebSocketMessageType.Close) break;

                    var text = Encoding.UTF8.GetString(buf, 0, result.Count);
                    McpLog.Debug($"[cc] ← {text}");

                    try
                    {
                        using var doc = JsonDocument.Parse(text);
                        var root = doc.RootElement;
                        if (!root.TryGetProperty("type", out var t)) continue;
                        var type = t.GetString();

                        switch (type)
                        {
                            case "hello-ok":
                                _helloOk?.TrySetResult(true);
                                break;

                            case "system":
                                // 从 system.init 提取模型名
                                if (root.TryGetProperty("subtype", out var sub) && sub.GetString() == "init"
                                    && root.TryGetProperty("model", out var modelEl))
                                {
                                    TokenUsageTracker.CurrentModel = modelEl.GetString() ?? "";
                                }
                                break;

                            case "assistant":
                            case "user":
                                ChatDisplayState.OnSdkMessage(root);
                                CountToolResults(root);
                                ExtractUsageFromMessage(root);
                                break;

                            case "stream_event":
                                ChatDisplayState.OnStreamEvent(root);
                                break;

                            case "result":
                                // 结束流式，工具卡片保留以显示耗时
                                ChatDisplayState.FinalizeStreaming();
                                break;

                            case "aborted":
                                // 中断确认 → 标记最后一个流式条目 + 取消 advance_tick
                                ChatDisplayState.MarkLastAborted();
                                Tool_AdvanceTick.CancelAll();
                                break;

                            case "error":
                                var err = root.TryGetProperty("error", out var e) ? e.GetString() : "unknown";
                                McpLog.Error($"[cc] 服务器错误: {err}");
                                break;

                            case "pong":
                                _lastPong = DateTime.UtcNow;
                                break;
                        }
                    }
                    catch { }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { McpLog.Error($"[cc] 接收异常: {ex.Message}"); }

            _state = CCClientState.Disconnected;

            if (!ct.IsCancellationRequested)
                _ = ScheduleReconnect();
        }

        // ========== 心跳 ==========

        /// <summary>每帧调用，负责心跳发送和接收检查</summary>
        private static void CountToolResults(JsonElement root)
        {
            var message = root.TryGetProperty("message", out var msg) ? msg : root;
            if (!message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
                return;

            foreach (var block in content.EnumerateArray())
            {
                if (block.TryGetProperty("type", out var bt) && bt.GetString() == "tool_result")
                {
                    bool isError = block.TryGetProperty("is_error", out var ie) && ie.GetBoolean();
                    TokenUsageTracker.RecordToolResult(isError);
                }
            }
        }

        /// <summary>从 assistant 消息中提取 Token 用量与模型名</summary>
        private static void ExtractUsageFromMessage(JsonElement root)
        {
            if (!root.TryGetProperty("message", out var msgEl)) return;

            // 尝试从消息中提取模型名
            if (msgEl.TryGetProperty("model", out var modelEl))
            {
                var m = modelEl.GetString();
                if (!string.IsNullOrEmpty(m)) TokenUsageTracker.CurrentModel = m!;
            }

            if (!msgEl.TryGetProperty("usage", out var usageEl) || usageEl.ValueKind != JsonValueKind.Object) return;

            long inputTok = 0, outputTok = 0, cacheRead = 0, cacheCreate = 0;
            if (usageEl.TryGetProperty("input_tokens", out var it)) inputTok = it.GetInt64();
            if (usageEl.TryGetProperty("output_tokens", out var ot)) outputTok = ot.GetInt64();
            if (usageEl.TryGetProperty("cache_read_input_tokens", out var cr)) cacheRead = cr.GetInt64();
            if (usageEl.TryGetProperty("cache_creation_input_tokens", out var cc)) cacheCreate = cc.GetInt64();

            if (inputTok > 0 || outputTok > 0)
            {
                McpLog.Info($"[cc] Token: in={inputTok} out={outputTok} cacheR={cacheRead} cacheW={cacheCreate}");
                TokenUsageTracker.Record(TokenUsageTracker.CurrentModel!, inputTok, outputTok, cacheRead, cacheCreate, 0);
            }
        }

        /// <summary>从 stream_event 中提取增量 usage（message_start 或 message_delta）</summary>
        private static void ExtractUsageFromStreamEvent(JsonElement root)
        {
            if (!root.TryGetProperty("event", out var evt)) return;
            if (!evt.TryGetProperty("type", out var et)) return;
            var eventType = et.GetString();

            if (eventType == "message_start" && evt.TryGetProperty("message", out var msgEl))
            {
                // message_start: usage 在 event.message.usage 中
                if (msgEl.TryGetProperty("usage", out var usageEl) && usageEl.ValueKind == JsonValueKind.Object)
                {
                    long inputTok = 0, outputTok = 0, cacheRead = 0, cacheCreate = 0;
                    if (usageEl.TryGetProperty("input_tokens", out var it)) inputTok = it.GetInt64();
                    if (usageEl.TryGetProperty("output_tokens", out var ot)) outputTok = ot.GetInt64();
                    if (usageEl.TryGetProperty("cache_read_input_tokens", out var cr)) cacheRead = cr.GetInt64();
                    if (usageEl.TryGetProperty("cache_creation_input_tokens", out var cc)) cacheCreate = cc.GetInt64();
                    if (inputTok > 0 || outputTok > 0)
                        TokenUsageTracker.Record(TokenUsageTracker.CurrentModel!, inputTok, outputTok, cacheRead, cacheCreate, 0);
                }
            }
            else if (eventType == "message_delta" && evt.TryGetProperty("usage", out var deltaUsage) && deltaUsage.ValueKind == JsonValueKind.Object)
            {
                long inputTok = 0, outputTok = 0, cacheRead = 0, cacheCreate = 0;
                if (deltaUsage.TryGetProperty("input_tokens", out var it)) inputTok = it.GetInt64();
                if (deltaUsage.TryGetProperty("output_tokens", out var ot)) outputTok = ot.GetInt64();
                if (deltaUsage.TryGetProperty("cache_read_input_tokens", out var cr)) cacheRead = cr.GetInt64();
                if (deltaUsage.TryGetProperty("cache_creation_input_tokens", out var cc)) cacheCreate = cc.GetInt64();
                if (outputTok > 0)
                    TokenUsageTracker.Record(TokenUsageTracker.CurrentModel!, inputTok, outputTok, cacheRead, cacheCreate, 0);
            }
        }

        public static void Tick()
        {
            if (!IsReady) return;

            var now = DateTime.UtcNow;

            // 发送 ping（实际发送成功后才更新 _lastPing，失败可立即重试）
            if ((now - _lastPing).TotalMilliseconds > PingIntervalMs)
            {
                _ = SendPing();
            }

            // 检查 pong 超时
            if (_lastPong != DateTime.MinValue && (now - _lastPong).TotalMilliseconds > PongTimeoutMs)
            {
                McpLog.Error("[cc] pong 超时，断开连接（将自动重连）");
                _state = CCClientState.Disconnected;
                try { _ws?.CloseAsync(WebSocketCloseStatus.ProtocolError, "pong timeout", CancellationToken.None); } catch { }
            }
        }

        private static async Task SendPing()
        {
            try
            {
                await SendJson(new { type = "ping" });
                _lastPing = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                McpLog.Warn($"[cc] ping 失败: {ex.Message}");
            }
        }

        // ========== 自动重连 ==========

        private static async Task ScheduleReconnect()
        {
            if (_reconnecting || _shuttingDown) return;
            _reconnecting = true;
            try
            {
                while (true)
                {
                    _reconnectAttempts++;
                    var delay = Math.Min(_reconnectDelayMs * _reconnectAttempts, MaxReconnectDelayMs);
                    McpLog.Info($"[cc] {delay / 1000}s 后重连 (第 {_reconnectAttempts} 次)...");
                    await Task.Delay(delay);
                    if (_shuttingDown) break;
                    if (string.IsNullOrEmpty(_url)) break;
                    try { await Connect(_url, _token); }
                    catch { }
                    if (_state != CCClientState.Disconnected) break;
                }
            }
            finally { _reconnecting = false; }
        }
    }
}
