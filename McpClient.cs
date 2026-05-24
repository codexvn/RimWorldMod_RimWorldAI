using System;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace RimWorldMCP
{
    public enum ClientState { Disconnected, Connecting, Handshake, Ready }

    public static class McpClient
    {
        private static ClientWebSocket? _ws;
        private static CancellationTokenSource? _cts;
        private static string _url = "";
        private static string _token = "";
        private static int _rpcSeq;
        private static ClientState _state = ClientState.Disconnected;

        public static ClientState State => _state;
        public static bool IsConnected => _state >= ClientState.Handshake;
        public static bool IsReady => _state == ClientState.Ready;

        // 收到的消息队列 — UI 从中消费
        public static readonly ConcurrentQueue<string> Incoming = new();

        /// <summary>连接 Gateway 并完成 connect→auth→ready 握手</summary>
        public static async Task Connect(string wsUrl, string token, string password)
        {
            _url = wsUrl;
            _token = !string.IsNullOrEmpty(token) ? token : password;
            Disconnect();

            try
            {
                _ws = new ClientWebSocket();
                _cts = new CancellationTokenSource();
                _state = ClientState.Connecting;

                await _ws.ConnectAsync(new Uri(wsUrl), _cts.Token);
                McpLog.Info($"[ws] 已连接: {wsUrl}");

                // Step 1: 发送 connect handshake
                await SendJson(new { type = "connect", role = "client", client = "csharp" });
                _state = ClientState.Handshake;

                // Step 2: 如果配置了 token，发送 auth
                if (!string.IsNullOrEmpty(_token))
                    await SendJson(new { type = "auth", token = _token });

                // Step 3: 启动接收循环（等待事件流）
                _ = ReceiveLoop(_cts.Token);

                // Step 4: 握手完成，直接进入 Ready
                _state = ClientState.Ready;
                McpLog.Info("[ws] 握手完成");
            }
            catch (Exception ex)
            {
                _state = ClientState.Disconnected;
                McpLog.Warn($"[ws] 连接失败: {ex.Message}");
            }
        }

        /// <summary>发送 RPC 请求</summary>
        public static async Task<string?> SendRpc(string method, object? payload = null)
        {
            if (!IsReady) return null;
            var id = (++_rpcSeq).ToString();
            await SendJson(new { type = "req", id, method, @params = payload });
            // 响应由 ReceiveLoop 处理并放入 Incoming 队列
            return id;
        }

        /// <summary>发送文本消息（兼容旧 SendMessage）</summary>
        public static async Task SendMessage(string text)
        {
            if (!IsReady) return;
            var id = (++_rpcSeq).ToString();
            await SendJson(new { type = "req", id, method = "agent.send", @params = new { text } });
        }

        /// <summary>发送心跳</summary>
        public static async Task Ping()
        {
            if (_ws?.State == WebSocketState.Open)
                await SendJson(new { type = "ping" });
        }

        public static void Disconnect()
        {
            _cts?.Cancel();
            _state = ClientState.Disconnected;
            try { _ws?.Dispose(); } catch { }
            _ws = null;
        }

        private static async Task SendJson(object obj)
        {
            if (_ws?.State != WebSocketState.Open) return;
            var json = JsonSerializer.Serialize(obj, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
            });
            var bytes = Encoding.UTF8.GetBytes(json);
            await _ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, _cts?.Token ?? CancellationToken.None);
        }

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

                    // 解析消息类型
                    try
                    {
                        using var doc = JsonDocument.Parse(text);
                        var root = doc.RootElement;
                        if (root.TryGetProperty("type", out var t))
                        {
                            switch (t.GetString())
                            {
                                case "res":
                                    if (root.TryGetProperty("result", out var r))
                                        text = $"← {r}";
                                    break;
                                case "event":
                                    if (root.TryGetProperty("event", out var ev) && root.TryGetProperty("payload", out var pl))
                                        text = $"⚡ {ev.GetString()}: {pl}";
                                    break;
                            }
                        }
                    }
                    catch { }

                    Incoming.Enqueue(text);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { McpLog.Warn($"[ws] 接收异常: {ex.Message}"); }

            _state = ClientState.Disconnected;
        }
    }
}
