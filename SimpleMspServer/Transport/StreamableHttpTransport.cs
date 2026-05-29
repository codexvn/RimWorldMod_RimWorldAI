using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SimpleMspServer.Transport
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

        public Task StartAsync(CancellationToken ct)
        {
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://localhost:{_port}/");

            try
            {
                _listener.Start();
            }
            catch (HttpListenerException ex)
            {
                var diagnostic = ex.ErrorCode switch
                {
                    5  => $"拒绝访问 — 端口 {_port} 需要管理员权限或 URL ACL 注册。使用管理员运行或执行: netsh http add urlacl url=http://localhost:{_port}/ user=Everyone",
                    183 => $"端口 {_port} 已被占用 (Address Already In Use)。请关闭占用该端口的程序，或执行: netsh http delete urlacl url=http://localhost:{_port}/",
                    32  => $"端口 {_port} 共享冲突 — 正被其他进程使用",
                    87  => "URL 前缀格式无效",
                    _   => $"http.sys 错误 {ex.ErrorCode}"
                };
                SimpleLog.Error($"[http] 启动失败 [{ex.ErrorCode}]: {diagnostic}。原始错误: {ex.Message}");
                throw;
            }

            Log($"Streamable HTTP 服务器已启动: http://localhost:{_port}");

            Task.Run(() => AcceptLoop(ct), ct);
            return Task.CompletedTask;
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

        private static void Log(string msg) => SimpleLog.Info($"[http] {msg}");

        private class PendingResponse
        {
            public HttpListenerResponse Response { get; }
            public PendingResponse(HttpListenerResponse response) => Response = response;
        }
    }
}
