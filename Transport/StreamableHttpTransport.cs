using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RimWorldMCP.Transport
{
    public class StreamableHttpTransport : ITransport
    {
        private readonly int _port;
        private HttpListener? _listener;
        private readonly Queue<PendingResponse> _pendingResponses = new();
        private readonly object _lock = new();

        public event Action<string>? OnMessage;
        public string Name => "http";

        public StreamableHttpTransport(int port = 9877)
        {
            _port = port;
        }

        public async Task StartAsync(CancellationToken ct)
        {
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://localhost:{_port}/");
            _listener.Start();
            Log($"Streamable HTTP 服务器已启动: http://localhost:{_port}");

            _ = Task.Run(() => AcceptLoop(ct), ct);
            await Task.CompletedTask;
        }

        public async Task SendAsync(string message)
        {
            PendingResponse? pending = null;
            lock (_lock)
            {
                if (_pendingResponses.Count > 0)
                {
                    pending = _pendingResponses.Dequeue();
                }
            }

            if (pending != null)
            {
                var bytes = Encoding.UTF8.GetBytes(message);
                pending.Response.ContentType = "application/json";
                pending.Response.ContentLength64 = bytes.Length;
                await pending.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
                pending.Response.Close();
            }
            else
            {
                Log("警告: 没有等待中的 HTTP 请求来接收响应");
            }
        }

        public async Task StopAsync()
        {
            lock (_lock)
            {
                foreach (var pending in _pendingResponses)
                {
                    try { pending.Response.StatusCode = 503; pending.Response.Close(); } catch { }
                }
                _pendingResponses.Clear();
            }
            _listener?.Stop();
            _listener?.Close();
            Log("Streamable HTTP 服务器已停止");
            await Task.CompletedTask;
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
                response.Headers.Add("Access-Control-Allow-Methods", "POST, GET, OPTIONS");
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

                if (request.Url?.AbsolutePath == "/mcp" && request.HttpMethod == "POST")
                {
                    using var reader = new StreamReader(request.InputStream, request.ContentEncoding);
                    var body = await reader.ReadToEndAsync();
                    Log($"POST /mcp: {body.Substring(0, Math.Min(body.Length, 200))}");

                    lock (_lock)
                    {
                        _pendingResponses.Enqueue(new PendingResponse(response));
                    }

                    OnMessage?.Invoke(body);
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
                    var json = "{\"status\":\"ok\",\"server\":\"RimWorldMCP\",\"transport\":\"http\",\"endpoints\":[\"/mcp\"]}";
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

        private static void Log(string msg) => McpLog.Info($"[http] {msg}");

        private class PendingResponse
        {
            public HttpListenerResponse Response { get; }
            public PendingResponse(HttpListenerResponse response) => Response = response;
        }
    }
}
