using System;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using System.Security.Cryptography;
using Verse;
using RimWorld;
using RimWorldMCP.Mcp;

namespace RimWorldMCP
{
    public enum ClientState { Disconnected, Connecting, Handshake, Ready }

    /// <summary>Gateway WebSocket 客户端 — 对齐 Vantix/OpenClaw GatewayClient</summary>
    public static class GatewayClient
    {
        private static ClientWebSocket? _ws;
        private static CancellationTokenSource? _cts;
        private static string _url = "";
        private static string _token = "";
        private static string _password = "";

        // 通用请求追踪（Vantix 的 pending Map 模式）
        private class PendingRequest
        {
            public TaskCompletionSource<JsonElement> Tcs = new();
            public bool ExpectFinal;
            /// <summary>持有克隆的 JsonDocument 引用，防止 ReceiveLoop dispose 后元素失效</summary>
            public JsonDocument? ClonedDocument;
        }
        private static readonly ConcurrentDictionary<string, PendingRequest> _pending = new();

        // 设备身份（ED25519）
        private static Ed25519PrivateKeyParameters? _deviceKey;
        private static string? _deviceId;
        private static string? _devicePublicKeyBase64Url;
        private static byte[]? _devicePublicKeyRaw;
        private static ClientState _state = ClientState.Disconnected;
        private static TaskCompletionSource<bool>? _helloOk;
        private static int _tickIntervalMs = 30000;
        private static DateTime _lastTick = DateTime.MinValue;
        private static int _reconnectDelayMs = 5000;
        private static int _reconnectAttempts = 0;
        private static bool _reconnecting = false;
        private static bool _shuttingDown = false;
        private const int MaxReconnectDelayMs = 60000;

        public static ClientState State => _state;
        public static bool IsConnected => _state >= ClientState.Handshake;
        public static bool IsReady => _state == ClientState.Ready;

        /// <summary>SendMessage 是否正在等待 agent 响应</summary>
        public static bool IsSendingMessage { get; private set; }

        public static readonly ConcurrentQueue<string> Incoming = new();
        private static readonly SemaphoreSlim _sendLock = new(1, 1);
        private static readonly SemaphoreSlim _messageLock = new(1, 1);
        private static readonly SemaphoreSlim _firstSendLock = new(1, 1); // FirstSend 串行化，防 Phase 2 竞争

        /// <summary>AbortAgent 等待 lifecycle 事件完成的信号</summary>
        private static TaskCompletionSource<bool>? _abortCompleteTcs;

        /// <summary>当前 session 是否已初始化（首条消息已发送）</summary>
        private static volatile bool _sessionInitialized;

        /// <summary>当前存档的会话 ID</summary>
        public static string SessionId { get; set; } = "rimworld";

        /// <summary>持久化会话路由键，格式 agent:&lt;id&gt;:&lt;name&gt;</summary>
        public static string SessionKey
        {
            get => _sessionKey;
            set
            {
                if (_sessionKey != value)
                {
                    _sessionInitialized = false;
                    McpLog.Info($"[ws] SessionKey 变更 -> {value}");
                }
                _sessionKey = value;
            }
        }
        private static string _sessionKey = "agent:main:main";

        // ========== 通用 RPC 调用（Vantix request() 模式） ==========

        /// <summary>发送请求并等待响应（expectFinal=true 时跳过中间 accepted 状态）</summary>
        private static async Task<JsonElement> Request(string method, object? @params = null, bool expectFinal = false)
        {
            if (!IsReady) throw new InvalidOperationException("gateway not connected");
            var id = Guid.NewGuid().ToString("N").Substring(0, 8);
            var pr = new PendingRequest { ExpectFinal = expectFinal };
            _pending[id] = pr;
            await SendJson(new { type = "req", id, method, @params });
            return await pr.Tcs.Task;
        }

        // ========== 业务 API ==========

        /// <summary>
        /// 发送消息到 Agent。_messageLock 只覆盖 abort 阶段，send 阶段无锁。
        /// 先阻塞 abort 清理活跃 run（~1-2s），再 sessions.send（不 interrupt，等完整响应）。
        /// </summary>
        public static async Task SendMessage(string text)
        {
            if (!IsReady) return;

            // 压缩期间暂存消息，压缩完成后自动发送
            if (_isCompacting)
            {
                _pendingCompactionMessage = text;
                McpLog.Info("[compaction] 压缩中，消息已暂存，压缩完成后自动发送...");
                return;
            }

            // Phase 1: abort（持有 _messageLock，阻止并发 abort/send）
            await _messageLock.WaitAsync();
            try
            {
                IsSendingMessage = true;
                ChatDisplayState.OnUserMessage(text);
                await AbortAgentInternal();
            }
            finally { _messageLock.Release(); }

            // Phase 2: send（无锁，不阻塞其他 abort；IsSendingMessage 保持 true 阻止队列并发）
            try
            {
                if (_sessionInitialized)
                {
                    await Request("sessions.send", new
                    {
                        key = SessionKey,
                        message = text,
                        idempotencyKey = Guid.NewGuid().ToString("N")
                    });
                }
                else
                {
                    // 锁 + 双重检查：Phase 2 无锁，两个并发 SendMessage 可能同时进入 else
                    await _firstSendLock.WaitAsync();
                    try
                    {
                        if (!_sessionInitialized)
                        {
                            _sessionInitialized = true;
                            await FirstSend(text);
                        }
                        else
                        {
                            await Request("sessions.send", new
                            {
                                key = SessionKey,
                                message = text,
                                idempotencyKey = Guid.NewGuid().ToString("N")
                            });
                        }
                    }
                    finally { _firstSendLock.Release(); }
                }
            }
            finally { IsSendingMessage = false; }
        }

        /// <summary>首条消息：agent 发送（自动创建 session）</summary>
        private static async Task FirstSend(string text)
        {
            await Request("agent", new
            {
                message = text,
                sessionKey = SessionKey,
                idempotencyKey = Guid.NewGuid().ToString("N")
            }, expectFinal: true);

            McpLog.Info("[ws] FirstSend 完成，session 已初始化");
        }

        /// <summary>发送 RPC，不等待最终结果</summary>
        public static void SendRpc(string method, object? payload = null)
        {
            if (!IsReady) return;
            _ = Request(method, payload);
        }

        public static async Task Ping()
        {
            if (_ws?.State == WebSocketState.Open)
                await SendJson(new { type = "ping" });
        }

        /// <summary>内层 abort：chat.abort + lifecycle 等待，不加锁（调用方持有 _messageLock）</summary>
        private static async Task AbortAgentInternal()
        {
            if (!IsReady) return;

            // abort 耗时较长，暂停游戏防意外事件
            await McpCommandQueue.DispatchAsync<bool>(() =>
            {
                Find.TickManager?.Pause();
                return true;
            });

            var tcs = new TaskCompletionSource<bool>();
            _abortCompleteTcs = tcs;

            try
            {
                await Task.Delay(3000);
                var resp = await Request("chat.abort", new { sessionKey = SessionKey });
                McpLog.Info("[ws] ← chat.abort 已确认");

                var aborted = resp.TryGetProperty("payload", out var pl)
                    && pl.TryGetProperty("aborted", out var a)
                    && a.GetBoolean();

                if (!aborted)
                {
                    McpLog.Info("[ws] 无活跃 run，跳过 lifecycle 等待");
                    return;
                }

                McpLog.Info("[ws] 等待 abort lifecycle 确认...");
                var completed = await Task.WhenAny(tcs.Task, Task.Delay(15000));
                if (completed == tcs.Task)
                    McpLog.Info("[ws] abort lifecycle 已确认");
                else
                    McpLog.Warn("[ws] abort lifecycle 等待超时");
            }
            catch (Exception ex)
            {
                McpLog.Warn($"[ws] chat.abort 失败: {ex.Message}");
            }
            finally
            {
                await McpCommandQueue.DispatchAsync<bool>(() =>
                {
                    Find.TickManager?.TogglePaused();
                    return true;
                });
                _abortCompleteTcs = null;
            }
        }

        /// <summary>中止当前 agent 运行，阻塞等待 lifecycle 确认。等待期间阻止新消息发送</summary>
        public static async Task AbortAgent()
        {
            if (!IsReady) return;

            var wasSending = IsSendingMessage;
            await _messageLock.WaitAsync();
            try
            {
                IsSendingMessage = true;
                await AbortAgentInternal();
            }
            finally
            {
                IsSendingMessage = wasSending;
                _messageLock.Release();
            }
        }

        // ========== 连接管理 ==========

        public static async Task Connect(string wsUrl, string token, string password)
        {
            _url = wsUrl;
            _token = token ?? "";
            _password = password ?? "";
            _shuttingDown = false;
            Disconnect();

            try
            {
                _ws = new ClientWebSocket();
                _cts = new CancellationTokenSource();
                _helloOk = new TaskCompletionSource<bool>();
                _state = ClientState.Connecting;

                await _ws.ConnectAsync(new Uri(wsUrl), _cts.Token);
                _state = ClientState.Handshake;
                McpLog.Info($"[ws] 已连接: {wsUrl}");

                _ = ReceiveLoop(_cts.Token);

                var timeout = Task.Delay(15000);
                var completed = await Task.WhenAny(_helloOk.Task, timeout);
                if (completed == _helloOk.Task && _helloOk.Task.Result)
                {
                    _state = ClientState.Ready;
                    _reconnectAttempts = 0;
                    _reconnectDelayMs = 5000;
                    McpLog.Info("[ws] 握手完成");
                }
                else
                {
                    McpLog.Warn("[ws] 握手超时");
                }
            }
            catch (Exception ex)
            {
                _state = ClientState.Disconnected;
                McpLog.Warn($"[ws] 连接失败: {ex.Message}");
            }
        }

        public static void Disconnect()
        {
            _shuttingDown = true;
            _cts?.Cancel();
            _state = ClientState.Disconnected;
            FlushPending(new Exception("disconnected"));
            try { _ws?.Dispose(); } catch { }
            _ws = null;
        }

        // ========== WebSocket IO ==========

        private static async Task SendJson(object obj)
        {
            await _sendLock.WaitAsync();
            try
            {
                var ws = _ws;
                if (ws?.State != WebSocketState.Open) return;
                var json = JsonSerializer.Serialize(obj, McpJson.Options);
                var bytes = Encoding.UTF8.GetBytes(json);
                McpLog.Debug($"[ws] → {Truncate(json)}");
                await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true,
                    _cts?.Token ?? CancellationToken.None);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        private static string Truncate(string s) => s.Length <= 200 ? s : s.Substring(0, 197) + "...";

        /// <summary>断开时拒绝所有等待中的请求</summary>
        private static void FlushPending(Exception err)
        {
            foreach (var kv in _pending)
            {
                kv.Value.Tcs.TrySetException(err);
            }
            _pending.Clear();
        }

        // ========== 接收循环 ==========

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
                    while (!result.EndOfMessage && !ct.IsCancellationRequested)
                    {
                        result = await _ws.ReceiveAsync(new ArraySegment<byte>(buf, result.Count, buf.Length - result.Count), ct);
                        text += Encoding.UTF8.GetString(buf, 0, result.Count);
                    }

                    Incoming.Enqueue(text);
                    McpLog.Debug($"[ws] ← {text}");

                    try
                    {
                        using var doc = JsonDocument.Parse(text);
                        var root = doc.RootElement;
                        if (!root.TryGetProperty("type", out var t)) continue;
                        var type = t.GetString();

                        switch (type)
                        {
                            case "event":
                                HandleEventFrame(root);
                                break;

                            case "res":
                                HandleResponseFrame(root);
                                break;

                            case "ping":
                                await SendJson(new { type = "pong" });
                                break;
                        }

                        // tick 超时检查
                        if (_state == ClientState.Ready
                            && (DateTime.UtcNow - _lastTick).TotalMilliseconds > _tickIntervalMs * 2)
                        {
                            McpLog.Warn("[ws] tick 超时，连接可能已断开");
                            _state = ClientState.Disconnected;
                            try { _ws?.CloseAsync(WebSocketCloseStatus.ProtocolError, "tick timeout", CancellationToken.None); } catch { }
                            break;
                        }
                    }
                    catch { }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { McpLog.Warn($"[ws] 接收异常: {ex.Message}"); }

            _state = ClientState.Disconnected;
            FlushPending(new Exception("disconnected"));

            if (!ct.IsCancellationRequested)
                _ = ScheduleReconnect();
        }

        /// <summary>检查 agent lifecycle 事件，确认 abort 完成</summary>
        private static void TryHandleAbortLifecycle(JsonElement root)
        {
            var tcs = _abortCompleteTcs;
            if (tcs == null) return;

            // lifecycle 事件结构: payload.stream="lifecycle", payload.data.{ phase, status }
            // 必须匹配自己的 sessionKey，避免其他 agent 的事件误触发
            if (root.TryGetProperty("payload", out var payload)
                && payload.TryGetProperty("stream", out var stream)
                && stream.GetString() == "lifecycle"
                && payload.TryGetProperty("sessionKey", out var sk)
                && sk.GetString() == SessionKey
                && payload.TryGetProperty("data", out var data)
                && data.TryGetProperty("phase", out var phase)
                && phase.GetString() == "end"
                && data.TryGetProperty("status", out var status)
                && status.GetString() == "cancelled")
            {
                tcs.TrySetResult(true);
            }
        }

        private static void HandleEventFrame(JsonElement root)
        {
            if (!root.TryGetProperty("event", out var ev)) return;
            var evt = ev.GetString();

            if (evt == "connect.challenge"
                && root.TryGetProperty("payload", out var pl)
                && pl.TryGetProperty("nonce", out var nonce))
            {
                _ = SendChallengeResponse(nonce.GetString() ?? "");
            }
            else if (evt == "tick")
            {
                _lastTick = DateTime.UtcNow;
            }
            else if (evt == "chat")
            {
                ChatDisplayState.OnChatEvent(root);
            }
            else if (evt == "agent")
            {
                TryHandleAbortLifecycle(root);
                ChatDisplayState.OnAgentEvent(root);
                HandleCompactionEvent(root);
            }
            else if (evt == "session.operation")
            {
                HandleCompactionEvent(root);
                ChatDisplayState.OnSessionOperationEvent(root);
            }
        }

        /// <summary>上下文压缩导致暂停游戏的原因</summary>
        public static string? CompactionPauseReason;

        /// <summary>是否正在压缩上下文（压缩期间阻塞消息发送）</summary>
        public static bool IsCompacting => _isCompacting;
        private static volatile bool _isCompacting;

        /// <summary>压缩期间暂存的消息，压缩完成后自动发送</summary>
        private static string? _pendingCompactionMessage;

        /// <summary>处理 OpenClaw 上下文压缩事件</summary>
        private static int _lastCompactionNotifyTick;
        private static void HandleCompactionEvent(JsonElement root)
        {
            if (!root.TryGetProperty("payload", out var payload)) return;

            string phase;
            bool? completed = null;

            // agent 事件: payload.stream == "compaction"
            if (payload.TryGetProperty("stream", out var st) && st.GetString() == "compaction"
                && payload.TryGetProperty("data", out var data))
            {
                if (data.TryGetProperty("phase", out var ph)) phase = ph.GetString() ?? "";
                else return;
                if (data.TryGetProperty("completed", out var cp)) completed = cp.GetBoolean();
            }
            // session.operation 事件: payload.operation == "compact"
            else if (payload.TryGetProperty("operation", out var op) && op.GetString() == "compact")
            {
                if (payload.TryGetProperty("phase", out var ph)) phase = ph.GetString() ?? "";
                else return;
                if (payload.TryGetProperty("completed", out var cp)) completed = cp.GetBoolean();
            }
            else return;

            int now = Environment.TickCount;
            if (now - _lastCompactionNotifyTick < 2000) return;

            if (phase == "start")
            {
                _lastCompactionNotifyTick = now;
                _isCompacting = true;
                CompactionPauseReason = "上下文压缩中，消息队列将暂停以等待压缩完成...";
                McpLog.Info("[compaction] 上下文压缩开始，暂停游戏 + 阻塞消息...");

                // 主线程暂停 + 通知补推
                _ = McpCommandQueue.DispatchAsync<bool>(() =>
                {
                    Find.TickManager?.Pause();
                    var sb = new System.Text.StringBuilder();
                    sb.AppendLine("## 上下文压缩");
                    sb.AppendLine("OpenClaw 正在压缩对话上下文以管理 Token 用量。");
                    sb.AppendLine("游戏已暂停，压缩完成后自动恢复。");
                    GatewayMessageQueue.Enqueue(MessageCategory.Alert, sb.ToString().TrimEnd());
                    return true;
                });
            }
            else if (phase == "end")
            {
                _lastCompactionNotifyTick = now;
                _isCompacting = false;
                CompactionPauseReason = null;
                string status = completed == true ? "完成" : (completed == false ? "失败" : "");
                McpLog.Info($"[compaction] 上下文压缩{status}，恢复消息 + 触发 Agent...");

                // 压缩完成后发送暂存的消息
                var pending = Interlocked.Exchange(ref _pendingCompactionMessage, null);
                if (pending != null)
                {
                    McpLog.Info($"[compaction] 发送暂存的消息: {pending}");
                    _ = SendMessage(pending);
                }

                // 压缩完成后取消暂停 + 触发 Agent（复用"继续"按钮逻辑）
                _ = McpCommandQueue.DispatchAsync<bool>(() =>
                {
                    Find.TickManager?.TogglePaused();
                    var map = Find.CurrentMap;
                    if (map != null)
                    {
                        var colonists = PawnsFinder.AllMaps_FreeColonistsSpawned;
                        var msg = GatewayEventMonitor.BuildColonyOverview(map, colonists, colonists.Count);
                        GatewayMessageQueue.Enqueue(MessageCategory.Alert, msg);
                    }
                    return true;
                });
            }
        }

        private static void HandleResponseFrame(JsonElement root)
        {
            // hello-ok 检测（无 id 字段或是 connect 的响应）
            if (root.TryGetProperty("ok", out var okElem) && okElem.GetBoolean()
                && root.TryGetProperty("payload", out var payload)
                && payload.TryGetProperty("type", out var pt) && pt.GetString() == "hello-ok")
            {
                if (payload.TryGetProperty("policy", out var policy)
                    && policy.TryGetProperty("tickIntervalMs", out var tiv) && tiv.TryGetInt32(out var iv))
                    _tickIntervalMs = iv;
                _lastTick = DateTime.UtcNow;
                _helloOk?.TrySetResult(true);
                return;
            }

            // 通用响应匹配
            if (!root.TryGetProperty("id", out var idElem)) return;
            var id = idElem.GetString();
            if (id == null || !_pending.TryGetValue(id, out var pr)) return;

            // expectFinal: 跳过中间 accepted 状态
            var hasOk = root.TryGetProperty("ok", out var resOkElem);
            var resOk = hasOk && resOkElem.GetBoolean();
            var status = "ok";
            if (root.TryGetProperty("payload", out var resPl)
                && resPl.TryGetProperty("status", out var stElem))
                status = stElem.GetString() ?? "ok";

            if (pr.ExpectFinal && status == "accepted")
                return; // 跳过中间 ack，等最终响应

            _pending.TryRemove(id, out _);

            if (resOk)
            {
                // 克隆元素避免 ReceiveLoop 的 using JsonDocument dispose 后失效
                var cloneDoc = JsonDocument.Parse(root.GetRawText());
                pr.ClonedDocument = cloneDoc;
                pr.Tcs.TrySetResult(cloneDoc.RootElement);
            }
            else
                pr.Tcs.TrySetException(new Exception(
                    root.TryGetProperty("error", out var err) && err.TryGetProperty("message", out var msg)
                    ? msg.GetString() ?? "rpc error" : "rpc error"));
        }

        // ========== 握手 ==========

        private static void EnsureDeviceIdentity()
        {
            if (_deviceKey != null) return;
            var seed = new byte[32];
            RandomNumberGenerator.Create().GetBytes(seed);
            _deviceKey = new Ed25519PrivateKeyParameters(seed, 0);
            var pubKey = _deviceKey.GeneratePublicKey();
            _devicePublicKeyRaw = pubKey.GetEncoded();
            _devicePublicKeyBase64Url = Base64UrlEncode(_devicePublicKeyRaw);
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(_devicePublicKeyRaw);
            _deviceId = BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
        }

        private static string SignDevicePayload(string nonce, long signedAtMs, string platform)
        {
            var scopes = "operator.read,operator.write,operator.admin";
            var payload = $"v3|{_deviceId}|gateway-client|backend|operator|{scopes}|{signedAtMs}|{_token}|{nonce}|{platform}|";
            var data = Encoding.UTF8.GetBytes(payload);
            var signer = new Ed25519Signer();
            signer.Init(true, _deviceKey);
            signer.BlockUpdate(data, 0, data.Length);
            return Base64UrlEncode(signer.GenerateSignature());
        }

        private static string Base64UrlEncode(byte[] data)
        {
            return Convert.ToBase64String(data).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        }

        private static async Task SendChallengeResponse(string nonce)
        {
            EnsureDeviceIdentity();
            var platform = Environment.OSVersion.Platform.ToString().Contains("Win") ? "windows"
                : Environment.OSVersion.Platform.ToString().Contains("Mac") ? "macos" : "linux";
            var signedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            await SendJson(new
            {
                type = "req",
                id = Guid.NewGuid().ToString("N").Substring(0, 8),
                method = "connect",
                @params = new
                {
                    minProtocol = 3,
                    maxProtocol = 4,
                    client = new { id = "gateway-client", displayName = "RimWorldMCP", version = "1.0", platform, mode = "backend" },
                    role = "operator",
                    scopes = new[] { "operator.read", "operator.write", "operator.admin" },
                    caps = new[] { "tool-events" },
                    locale = "zh-CN",
                    userAgent = "RimWorldMCP/1.0",
                    auth = new
                    {
                        token = !string.IsNullOrEmpty(_token) ? _token : !string.IsNullOrEmpty(_password) ? _password : null,
                        password = (string?)null,
                        deviceToken = (string?)null
                    },
                    device = new { id = _deviceId, publicKey = _devicePublicKeyBase64Url, signature = SignDevicePayload(nonce, signedAt, platform), signedAt, nonce }
                }
            });
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
                    McpLog.Info($"[ws] {delay / 1000}s 后自动重连 (第 {_reconnectAttempts} 次)...");
                    await Task.Delay(delay);
                    if (_shuttingDown) break;
                    if (_state != ClientState.Disconnected) break;
                    if (string.IsNullOrEmpty(_url)) break;
                    try { await Connect(_url, _token, _password); }
                    catch (Exception ex) { McpLog.Warn($"[ws] 重连失败: {ex.Message}"); }
                    if (_state != ClientState.Disconnected) break;
                }
            }
            finally { _reconnecting = false; }
        }
    }
}
