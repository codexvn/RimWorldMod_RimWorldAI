using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RimWorldMCP.Transport
{
    public class SseTransport : ITransport
    {
        private readonly int _port;
        private HttpListener? _listener;
        private readonly ConcurrentDictionary<string, SseSession> _sessions = new();
        // /mcp 端点同步请求响应队列
        private readonly Queue<PendingMcpResponse> _mcpResponses = new();
        private readonly object _mcpLock = new();

        public string Name => "sse";
        public event Action<string>? OnMessage;

        public SseTransport(int port = 9877)
        {
            _port = port;
        }

        public Task StartAsync(CancellationToken ct)
        {
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://localhost:{_port}/");
            _listener.Start();
            Log($"SSE 服务器已启动: http://localhost:{_port}");

            Task.Run(() => AcceptLoop(ct), ct);
            return Task.CompletedTask;
        }

        public async Task SendAsync(string message)
        {
            // 优先检查 /mcp 同步响应，跳过已超时的
            PendingMcpResponse? mcp = null;
            lock (_mcpLock)
            {
                while (_mcpResponses.Count > 0)
                {
                    var item = _mcpResponses.Dequeue();
                    if (!item.Cancelled) { mcp = item; break; }
                }
            }
            if (mcp != null)
            {
                try
                {
                    var bytes = Encoding.UTF8.GetBytes(message);
                    mcp.Response.ContentType = "application/json";
                    mcp.Response.ContentLength64 = bytes.Length;
                    await mcp.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
                    mcp.Response.Close();
                }
                catch (Exception ex)
                {
                    Log($"SendAsync /mcp: {ex.Message}");
                }
                mcp.Completion.TrySetResult(true);
                return;
            }

            // SSE 广播
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
                catch (HttpListenerException) { break; }
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

            response.ContentType = "text/event-stream";
            response.Headers.Add("Cache-Control", "no-cache");
            response.Headers.Add("Connection", "keep-alive");

            var session = new SseSession(sessionId, response);
            _sessions[sessionId] = session;
            Log($"SSE 客户端连接: {sessionId}");

            // MCP SSE 规范: 第一个事件必须是 endpoint，告知客户端 POST 地址
            await session.SendEventAsync("endpoint", "/message");
            await session.SendEventAsync("connected", $"{{\"sessionId\":\"{sessionId}\"}}");

            try
            {
                await session.WaitForDisconnectAsync();
            }
            finally
            {
                _sessions.TryRemove(sessionId, out _);
                session.Dispose();
                Log($"SSE 客户端断开: {sessionId}");
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

            var pending = new PendingMcpResponse(response);
            lock (_mcpLock)
            {
                _mcpResponses.Enqueue(pending);
            }

            OnMessage?.Invoke(body);

            // 等待 McpServer 处理并调用 SendAsync 返回响应（超时 30s）
            await Task.WhenAny(pending.Completion.Task, Task.Delay(30000));
            if (!pending.Completion.Task.IsCompleted)
            {
                pending.Cancelled = true;
                response.StatusCode = 504;
                response.Close();
            }
        }

        private class PendingMcpResponse
        {
            public HttpListenerResponse Response { get; }
            public TaskCompletionSource<bool> Completion { get; } = new();
            public bool Cancelled { get; set; }
            public PendingMcpResponse(HttpListenerResponse response) => Response = response;
        }

        private static void Log(string msg) => McpLog.Info($"[sse] {msg}");

        private class SseSession : IDisposable
        {
            public string SessionId { get; }
            private readonly HttpListenerResponse _response;
            private readonly Stream _outputStream;
            private readonly TaskCompletionSource<bool> _disconnectTcs = new();

            public SseSession(string sessionId, HttpListenerResponse response)
            {
                SessionId = sessionId;
                _response = response;
                _outputStream = response.OutputStream;
            }

            public async Task SendEventAsync(string eventType, string data)
            {
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
