using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SimpleMspServer.Transport
{
    public class SseTransport : ITransport
    {
        private readonly int _port;
        private readonly string _host;       // 显示用
        private readonly string _prefixHost; // http.sys 实际绑定的 host
        private HttpListener? _listener;
        private readonly ConcurrentDictionary<string, SseSession> _sessions = new();
        // Agent SSE 会话（区分普通 MCP 客户端）
        private readonly ConcurrentDictionary<string, SseSession> _agentSessions = new();
        /// <summary>全局单例，供 NotificationBus 推送事件到 Agent SSE 订阅者</summary>
        public static SseTransport? Instance { get; private set; }
        // /mcp 端点专用同步处理器（绕过 OnMessage→SendAsync 事件通道，避免 SSE 串台）
        private Func<string, string>? _mcpHandler;

        public string Name => "sse";
        public event Action<string>? OnMessage;

        /// <summary>为 /mcp 端点设置同步请求处理器</summary>
        public void SetMcpHandler(Func<string, string> handler)
        {
            _mcpHandler = handler;
        }

        /// <summary>推送游戏事件到所有 Agent SSE 订阅者（fire-and-forget）</summary>
        public void PushToAgents(string jsonData)
        {
            foreach (var kv in _agentSessions)
            {
                _ = kv.Value.SendEventAsync("gameEvent", jsonData);
            }
        }

        public SseTransport(int port = 9877, string host = "0.0.0.0")
        {
            _port = port;
            _host = host;
            _prefixHost = host == "0.0.0.0" ? "+" : host;
            Instance = this;
        }

        public Task StartAsync(CancellationToken ct)
        {
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://{_prefixHost}:{_port}/");

            try
            {
                _listener.Start();
            }
            catch (HttpListenerException ex)
            {
                var diagnostic = ex.ErrorCode switch
                {
                    5  => $"拒绝访问 — 端口 {_port} 需要管理员权限",
                    183 => $"端口 {_port} 已被占用 (Address Already In Use)",
                    32  => $"端口 {_port} 共享冲突",
                    87  => "URL 前缀格式无效",
                    _   => $"http.sys 错误 {ex.ErrorCode}"
                };
                SimpleLog.Error($"[sse] 启动失败 [{ex.ErrorCode}]: {diagnostic}。{ex.Message}");
                throw;
            }

            Log($"SSE 服务器已启动: http://{_host}:{_port}");

            Task.Run(() => AcceptLoop(ct), ct);
            return Task.CompletedTask;
        }

        public async Task SendAsync(string message)
        {
            foreach (var kvp in _sessions)
            {
                var session = kvp.Value;
                await session.SendEventAsync("message", message);
            }
        }

        public Task StopAsync()
        {
            foreach (var kvp in _sessions)
            {
                var session = kvp.Value;
                session.Dispose();
            }
            _sessions.Clear();
            _listener?.Stop();
            _listener?.Close();
            Log("SSE 服务器已停止");
            return Task.CompletedTask;
        }

        private async Task AcceptLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && _listener?.IsListening == true)
            {
                try
                {
                    var context = await _listener.GetContextAsync();
                    _ = Task.Run(() => HandleRequestAsync(context), ct);
                }
                catch (OperationCanceledException) { break; }
                catch (HttpListenerException ex)
                {
                    Log($"HttpListener 接受循环已停止 [{ex.ErrorCode}]: {ex.Message}");
                    break;
                }
                catch (Exception ex)
                {
                    Log($"接受连接错误: {ex.Message}");
                }
            }
        }

        private async Task HandleRequestAsync(HttpListenerContext context)
        {
            var request = context.Request;
            var response = context.Response;

            try
            {
                if (request.Url?.AbsolutePath == "/sse" && request.HttpMethod == "GET")
                {
                    await HandleSseConnect(context);
                }
                else if (request.Url?.AbsolutePath == "/message" && request.HttpMethod == "POST")
                {
                    await HandlePostMessage(context);
                }
                else if (request.Url?.AbsolutePath == "/mcp" && request.HttpMethod == "POST")
                {
                    await HandleMcpPost(context);
                }
                else if (request.Url?.AbsolutePath == "/mcp" && request.HttpMethod == "DELETE")
                {
                    response.StatusCode = 204;
                    response.Close();
                }
                else if (request.Url?.AbsolutePath == "/health" && request.HttpMethod == "GET")
                {
                    var bytes = Encoding.UTF8.GetBytes("OK");
                    response.ContentType = "text/plain";
                    response.ContentLength64 = bytes.Length;
                    await response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
                    response.Close();
                }
                else if (request.HttpMethod == "GET")
                {
                    // 根路径状态页 — Claude Desktop 激活时首先检查
                    var json = "{\"status\":\"ok\",\"server\":\"RimWorldMCP\",\"transport\":\"sse+http\",\"endpoints\":[\"/sse\",\"/message\",\"/mcp\"]}";
                    var bytes = Encoding.UTF8.GetBytes(json);
                    response.ContentType = "application/json";
                    response.ContentLength64 = bytes.Length;
                    await response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
                    response.Close();
                }
                else
                {
                    response.StatusCode = 404;
                    response.Close();
                }
            }
            catch (Exception ex)
            {
                Log($"处理请求错误: {ex.Message}");
                try { response.StatusCode = 500; response.Close(); } catch { }
            }
        }

        private async Task HandleSseConnect(HttpListenerContext context)
        {
            var response = context.Response;
            var sessionId = Guid.NewGuid().ToString("N");

            // 检测 Agent SSE 订阅（?agent=overseer）
            var isAgent = false;
            var query = context.Request.Url?.Query ?? "";
            if (query.Contains("agent="))
            {
                isAgent = true;
                var pi = query.IndexOf("agent=") + 6;
                var end = query.IndexOf('&', pi);
                var agent = end > 0 ? query.Substring(pi, end - pi) : query.Substring(pi);
                sessionId = $"agent-{agent}-{sessionId}";
            }

            response.ContentType = "text/event-stream";
            response.Headers.Add("Cache-Control", "no-cache");
            response.Headers.Add("Connection", "keep-alive");

            var session = new SseSession(sessionId, response);
            if (isAgent)
            {
                _agentSessions[sessionId] = session;
                Log($"Agent SSE 连接: {sessionId}");
                await session.SendEventAsync("connected", $"{{\"sessionId\":\"{sessionId}\",\"agent\":true}}");
            }
            else
            {
                _sessions[sessionId] = session;
                Log($"SSE 客户端连接: {sessionId}");
                await session.SendEventAsync("endpoint", "/message");
                await session.SendEventAsync("connected", $"{{\"sessionId\":\"{sessionId}\"}}");
            }

            try
            {
                await session.WaitForDisconnectAsync();
            }
            finally
            {
                if (isAgent) _agentSessions.TryRemove(sessionId, out _);
                else _sessions.TryRemove(sessionId, out _);
                session.Dispose();
                Log($"SSE {(isAgent ? "Agent " : "")}断开: {sessionId}");
            }
        }

        private async Task HandlePostMessage(HttpListenerContext context)
        {
            var request = context.Request;
            var response = context.Response;

            using var reader = new StreamReader(request.InputStream, request.ContentEncoding);
            var body = await reader.ReadToEndAsync();
            Log($"POST /message: {body.Substring(0, Math.Min(body.Length, 200))}");

            OnMessage?.Invoke(body);

            response.StatusCode = 202;
            response.Close();
        }

        private async Task HandleMcpPost(HttpListenerContext context)
        {
            var request = context.Request;
            var response = context.Response;

            using var reader = new StreamReader(request.InputStream, request.ContentEncoding);
            var body = await reader.ReadToEndAsync();
            Log($"POST /mcp: {body.Substring(0, Math.Min(body.Length, 200))}");

            if (_mcpHandler != null)
            {
                // Task.Run 避免阻塞 AcceptLoop，支持多个并发 /mcp 请求
                var result = await Task.Run(() => _mcpHandler(body));
                var bytes = Encoding.UTF8.GetBytes(result);
                response.ContentType = "application/json";
                response.ContentLength64 = bytes.Length;
                await response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
            }
            else
            {
                response.StatusCode = 503;
            }
            response.Close();
        }

        private static void Log(string msg) => SimpleLog.Info($"[sse] {msg}");

        private class SseSession : IDisposable
        {
            public string SessionId { get; }
            private readonly HttpListenerResponse _response;
            private readonly Stream _outputStream;
            private readonly TaskCompletionSource<bool> _disconnectTcs = new();
            private readonly SemaphoreSlim _writeLock = new(1, 1);

            public SseSession(string sessionId, HttpListenerResponse response)
            {
                SessionId = sessionId;
                _response = response;
                _outputStream = response.OutputStream;
            }

            public async Task SendEventAsync(string eventType, string data)
            {
                await _writeLock.WaitAsync();
                try
                {
                    var sb = new StringBuilder();
                    sb.AppendLine($"event: {eventType}");
                    foreach (var line in data.Split('\n'))
                    {
                        sb.AppendLine($"data: {line}");
                    }
                    sb.AppendLine();

                    var bytes = Encoding.UTF8.GetBytes(sb.ToString());
                    await _outputStream.WriteAsync(bytes, 0, bytes.Length);
                    await _outputStream.FlushAsync();
                }
                catch
                {
                    _disconnectTcs.TrySetResult(true);
                }
                finally
                {
                    _writeLock.Release();
                }
            }

            public async Task WaitForDisconnectAsync()
            {
                await _disconnectTcs.Task;
            }

            public void Dispose()
            {
                _disconnectTcs.TrySetResult(true);
                try { _response.Close(); } catch { }
            }
        }
    }
}
