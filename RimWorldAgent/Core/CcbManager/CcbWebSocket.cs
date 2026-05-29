using System;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RimWorldAgent.Core.AgentRuntime;

namespace RimWorldAgent.Core.CcbManager
{
    /// <summary>CCB WebSocket 客户端 — 连接 CC Companion，发送 Claude query，接收响应。</summary>
    public class CcbWebSocket : IDisposable
    {
        private ClientWebSocket? _ws;
        private CancellationTokenSource? _cts;
        private readonly string _url;
        private readonly string _token;
        private TaskCompletionSource<bool>? _helloOk;
        private bool _ready;

        public bool IsReady => _ready;

        /// <summary>收到 Claude 文本回复时触发</summary>
        public event Action<string>? OnAssistantText;
        /// <summary>收到 tool_use 请求时触发（agent 需调用 MCP 执行并返回 tool_result）</summary>
        public event Action<string, string, JsonElement?>? OnToolUse;
        /// <summary>回合结束（result 消息）</summary>
        public event Action<string, string?>? OnResult;

        public CcbWebSocket(string url = "ws://localhost:19999", string token = "")
        {
            _url = url;
            _token = token;
        }

        public async Task<bool> ConnectAsync(int timeoutMs = 10000)
        {
            try
            {
                _ws = new ClientWebSocket();
                _ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
                _cts = new CancellationTokenSource();
                _helloOk = new TaskCompletionSource<bool>();

                await _ws.ConnectAsync(new Uri(_url), _cts.Token);
                _ = ReceiveLoop();
                await SendHello();

                var timeout = Task.Delay(timeoutMs);
                if (await Task.WhenAny(_helloOk.Task, timeout) == _helloOk.Task)
                {
                    _ready = true;
                    return true;
                }
                CoreLog.Error("[CcbWS] hello-ok 超时");
                return false;
            }
            catch (Exception ex)
            {
                CoreLog.Error($"[CcbWS] 连接失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>发送聊天消息（用户 prompt）</summary>
        public async Task SendChat(string text, string? thinkingMode = null)
        {
            var msg = new { type = "event", @event = "chat", payload = new { text }, thinking = new { mode = thinkingMode ?? "default" } };
            await SendJson(msg);
        }

        /// <summary>发送 tool_result（agent 执行完 MCP 工具后回传给 Claude）</summary>
        public async Task SendToolResult(string toolUseId, string content, bool isError = false)
        {
            var msg = new
            {
                type = "event",
                @event = "tool_result",
                payload = new { tool_use_id = toolUseId, content = new[] { new { type = "text", text = content } }, is_error = isError }
            };
            await SendJson(msg);
        }

        public void Disconnect()
        {
            _ready = false;
            _cts?.Cancel();
            _ws?.Dispose();
            _ws = null;
        }

        public void Dispose() => Disconnect();

        // ---- 内部 ----

        private async Task SendHello()
        {
            await SendJson(new
            {
                type = "hello",
                client = new { name = "RimWorldAgent", version = "1.0" },
                auth = new { token = _token }
            });
        }

        private async Task SendJson(object obj)
        {
            if (_ws?.State != WebSocketState.Open) return;
            var json = JsonSerializer.Serialize(obj, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            var bytes = Encoding.UTF8.GetBytes(json);
            await _ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, _cts?.Token ?? CancellationToken.None);
        }

        private async Task ReceiveLoop()
        {
            var buffer = new byte[65536];
            try
            {
                while (_ws?.State == WebSocketState.Open && (_cts == null || !_cts.IsCancellationRequested))
                {
                    var result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), _cts?.Token ?? CancellationToken.None);
                    if (result.MessageType == WebSocketMessageType.Close) break;

                    var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    ProcessMessage(json);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { CoreLog.Error($"[CcbWS] 接收异常: {ex.Message}"); }
        }

        private void ProcessMessage(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (!root.TryGetProperty("type", out var typeEl)) return;
                var type = typeEl.GetString();

                switch (type)
                {
                    case "hello-ok":
                        _helloOk?.TrySetResult(true);
                        break;

                    case "assistant":
                        if (root.TryGetProperty("message", out var msg) &&
                            msg.TryGetProperty("content", out var content))
                        {
                            if (content.ValueKind == JsonValueKind.String)
                                OnAssistantText?.Invoke(content.GetString() ?? "");
                            else if (content.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var block in content.EnumerateArray())
                                {
                                    if (block.TryGetProperty("type", out var bt) && bt.GetString() == "text")
                                        OnAssistantText?.Invoke(block.GetProperty("text").GetString() ?? "");
                                    else if (bt.GetString() == "tool_use")
                                        OnToolUse?.Invoke(
                                            block.GetProperty("id").GetString() ?? "",
                                            block.GetProperty("name").GetString() ?? "",
                                            block.TryGetProperty("input", out var input) ? input : (JsonElement?)null
                                        );
                                }
                            }
                        }
                        break;

                    case "result":
                        var subtype = root.TryGetProperty("subtype", out var st) ? st.GetString() : null;
                        var stopReason = root.TryGetProperty("stop_reason", out var sr) ? sr.GetString() : null;
                        OnResult?.Invoke(subtype ?? "unknown", stopReason);
                        break;

                    case "user":
                    case "system":
                    case "stream_event":
                        // Agent 不需要处理这些消息类型
                        break;
                }
            }
            catch { /* 解析失败忽略 */ }
        }
    }
}
