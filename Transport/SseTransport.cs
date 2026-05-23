using System;
using System.Collections.Concurrent;
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

        public string Name => "sse";
        public event Action<string>? OnMessage;

        public SseTransport(int port = 9877)
        {
            _port = port;
        }

        public async Task StartAsync(CancellationToken ct)
        {
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://localhost:{_port}/");
            _listener.Start();
            Log($"SSE 服务器已启动: http://localhost:{_port}");

            _ = Task.Run(() => AcceptLoop(ct), ct);
            await Task.CompletedTask;
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

            // CORS
            if (request.Headers.Get("Origin") != null)
            {
                response.Headers.Add("Access-Control-Allow-Origin", "*");
                response.Headers.Add("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
                response.Headers.Add("Access-Control-Allow-Headers", "Content-Type");
            }

            try
            {
                if (request.HttpMethod == "OPTIONS")
                {
                    response.StatusCode = 204;
                    response.Close();
                    return;
                }

                if (request.Url?.AbsolutePath == "/sse" && request.HttpMethod == "GET")
                {
                    await HandleSseConnect(context);
                }
                else if (request.Url?.AbsolutePath == "/message" && request.HttpMethod == "POST")
                {
                    await HandlePostMessage(context);
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
                    var json = "{\"status\":\"ok\",\"server\":\"RimWorldMCP\",\"transport\":\"sse\",\"endpoints\":[\"/sse\",\"/message\"]}";
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

            if (context.Request.Headers.Get("Origin") != null)
            {
                response.Headers.Add("Access-Control-Allow-Origin", "*");
            }

            var session = new SseSession(sessionId, response);
            _sessions[sessionId] = session;
            Log($"SSE 客户端连接: {sessionId}");

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
