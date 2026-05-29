using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SimpleMspServer.Mcp;

namespace SimpleMspServer
{
    /// <summary>MCP 服务主机 — HttpListener + SDK StreamableHttpServerTransport + Tool 调度 + 游戏事件 SSE</summary>
    public class McpServiceHost : IDisposable
    {
        private readonly int _port;
        private readonly string _host;
        private readonly string _prefixHost;
        private HttpListener? _listener;
        private StreamableHttpServerTransport? _sdkTransport;
        private McpServer? _sdkServer;
        private CancellationTokenSource? _cts;
        private readonly List<IToolProvider> _providers = new();
        private readonly IMspLog _log;
        private readonly ConcurrentDictionary<string, SseSession> _eventSessions = new();

        public int Port => _port;
        public string Host => _host;
        public bool IsRunning => _listener != null;

        /// <summary>全局单例，供 NotificationBus 推送游戏事件</summary>
        public static McpServiceHost? Instance { get; private set; }

        public McpServiceHost(int port = 9877, string host = "0.0.0.0", IMspLog? log = null)
        {
            _port = port;
            _host = host;
            _prefixHost = host == "0.0.0.0" ? "+" : host;
            _log = log ?? NullMspLog.Instance;
        }

        // ===== 公开 API =====

        public void RegisterProvider(IToolProvider provider) => _providers.Add(provider);

        /// <summary>推送游戏事件到 /events SSE 订阅</summary>
        public void PostEvent(string jsonData)
        {
            foreach (var kv in _eventSessions)
                _ = kv.Value.SendAsync(jsonData);
        }

        public void Start()
        {
            if (IsRunning) return;
            Instance = this;

            try
            {
                // ── SDK transport + server ──
                _sdkTransport = new StreamableHttpServerTransport(NullLoggerFactory.Instance);
                _cts = new CancellationTokenSource();

                var options = new McpServerOptions
                {
                    ServerInfo = new Implementation { Name = "RimWorldMCP", Version = "1.0" },
                    Handlers = new McpServerHandlers
                    {
                        ListToolsHandler = (req, ct) => new ValueTask<ListToolsResult>(new ListToolsResult { Tools = BuildToolList() }),
                        CallToolHandler = async (req, ct) =>
                        {
                            var d = req.Params!.Arguments;
                            var je = d != null && d.Count > 0
                                ? System.Text.Json.JsonSerializer.SerializeToElement(d)
                                : (System.Text.Json.JsonElement?)null;
                            var r = await ExecuteAsync(req.Params!.Name, je);
                            return new CallToolResult
                            {
                                Content = r.Content.Select(c => (ContentBlock)new TextContentBlock { Text = c.Text }).ToList(),
                                IsError = r.IsError
                            };
                        }
                    }
                };

                _sdkServer = McpServer.Create(_sdkTransport, options, NullLoggerFactory.Instance);
                _ = _sdkServer.RunAsync(_cts.Token);

                // ── HttpListener ──
                _listener = new HttpListener();
                _listener.Prefixes.Add($"http://{_prefixHost}:{_port}/");
                _listener.Start();

                _log.Info($"MCP 服务已启动: http://{_host}:{_port}");
                Task.Run(() => AcceptLoop(_cts.Token), _cts.Token);
            }
            catch (Exception ex)
            {
                _log.Error($"MCP 服务启动失败: {ex.Message}");
                Cleanup();
            }
        }

        public void Stop()
        {
            _log.Info("MCP 服务已停止");
            Cleanup();
        }

        public void Dispose() => Stop();

        private void Cleanup()
        {
            foreach (var kv in _eventSessions) kv.Value.Dispose();
            _eventSessions.Clear();
            _cts?.Cancel();
            try { _listener?.Stop(); _listener?.Close(); } catch { }
            _listener = null;
            _cts?.Dispose();
            _cts = null;
            Instance = null;
        }

        // ===== HTTP 请求循环 =====

        private async Task AcceptLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && _listener?.IsListening == true)
            {
                try { var ctx = await _listener.GetContextAsync(); _ = Task.Run(() => HandleAsync(ctx), ct); }
                catch (OperationCanceledException) { break; }
                catch (HttpListenerException) { break; }
                catch (Exception ex) { _log.Error($"HTTP 接受错误: {ex.Message}"); }
            }
        }

        private async Task HandleAsync(HttpListenerContext ctx)
        {
            var req = ctx.Request;
            var res = ctx.Response;

            if (req.Headers.Get("Origin") != null)
            {
                res.Headers.Add("Access-Control-Allow-Origin", "*");
                res.Headers.Add("Access-Control-Allow-Methods", "POST, GET, OPTIONS, DELETE");
                res.Headers.Add("Access-Control-Allow-Headers", "Content-Type, Mcp-Session-Id");
            }

            try
            {
                if (req.HttpMethod == "OPTIONS") { res.StatusCode = 204; res.Close(); return; }

                var path = req.Url?.AbsolutePath ?? "/";
                var isMcp = path == "/mcp" || path == "/" || path == "";

                if (isMcp && req.HttpMethod == "POST") await HandleMcpPost(ctx);
                else if (isMcp && req.HttpMethod == "GET") await HandleMcpSse(ctx);
                else if (isMcp && req.HttpMethod == "DELETE") { res.StatusCode = 200; res.Close(); }
                else if (path == "/events" && req.HttpMethod == "GET") await HandleEventsSse(ctx);
                else if (path == "/health" && req.HttpMethod == "GET") Write(res, "OK", "text/plain");
                else if (req.HttpMethod == "GET") Write(res, "{\"status\":\"ok\",\"server\":\"RimWorldMCP\",\"transport\":\"http+sse\"}", "application/json");
                else { res.StatusCode = 404; res.Close(); }
            }
            catch (Exception ex)
            {
                _log.Error($"HTTP 处理错误: {ex.Message}");
                try { res.StatusCode = 500; res.Close(); } catch { }
            }
        }

        // ===== /mcp POST — JSON-RPC → SDK transport =====

        private async Task HandleMcpPost(HttpListenerContext ctx)
        {
            var req = ctx.Request;
            var res = ctx.Response;
            using var reader = new StreamReader(req.InputStream, req.ContentEncoding);
            var body = await reader.ReadToEndAsync();

            var sid = req.Headers.Get("Mcp-Session-Id");
            if (!string.IsNullOrEmpty(sid)) res.Headers.Add("Mcp-Session-Id", sid);

            var msg = System.Text.Json.JsonSerializer.Deserialize<JsonRpcMessage>(body,
                new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });

            if (msg == null)
            {
                Write(res, "{\"jsonrpc\":\"2.0\",\"id\":null,\"error\":{\"code\":-32700,\"message\":\"Parse error\"}}", "application/json");
                return;
            }

            using var ms = new MemoryStream();
            var wroteResponse = _sdkTransport!.HandlePostRequestAsync(msg, ms, _cts?.Token ?? CancellationToken.None).GetAwaiter().GetResult();

            if (wroteResponse)
            {
                ms.Position = 0;
                using var outReader = new StreamReader(ms);
                Write(res, outReader.ReadToEnd(), "application/json");
            }
            else
            {
                res.StatusCode = 202; // 响应通过 SSE 推送
                res.Close();
            }
        }

        // ===== /mcp GET — SSE Streamable HTTP（SDK 管理）=====

        private async Task HandleMcpSse(HttpListenerContext ctx)
        {
            var res = ctx.Response;
            res.ContentType = "text/event-stream";
            res.Headers.Add("Cache-Control", "no-cache");
            res.Headers.Add("Connection", "keep-alive");

            var sid = ctx.Request.Headers.Get("Mcp-Session-Id");
            if (!string.IsNullOrEmpty(sid)) res.Headers.Add("Mcp-Session-Id", sid);

            try { await _sdkTransport!.HandleGetRequestAsync(res.OutputStream, CancellationToken.None); }
            catch (Exception ex) when (ex.Message.Contains("Session resumption")) { _log.Info($"SSE 新会话: {ex.Message}"); }
            catch (Exception ex) { _log.Warn($"MCP SSE 断开: {ex.Message}"); }
        }

        // ===== /events GET — 游戏事件 SSE =====

        private async Task HandleEventsSse(HttpListenerContext ctx)
        {
            var res = ctx.Response;
            var sessionId = Guid.NewGuid().ToString("N");
            res.ContentType = "text/event-stream";
            res.Headers.Add("Cache-Control", "no-cache");
            res.Headers.Add("Connection", "keep-alive");

            var session = new SseSession(res);
            _eventSessions[sessionId] = session;

            await session.Send("connected", $"{{\"sessionId\":\"{sessionId}\"}}");
            try { await session.WaitForDisconnectAsync(); }
            finally { _eventSessions.TryRemove(sessionId, out _); session.Dispose(); }
        }

        // ===== Tool 路由 =====

        private List<Tool> BuildToolList()
        {
            return _providers.SelectMany(p => p.GetDefinitions()).Select(def => new Tool
            {
                Name = def.Name, Description = def.Description, InputSchema = def.InputSchema
            }).ToList();
        }

        private async Task<ToolCallResult> ExecuteAsync(string name, System.Text.Json.JsonElement? args)
        {
            foreach (var p in _providers)
            {
                if (p.GetDefinitions().Any(d => d.Name == name))
                    return await p.ExecuteAsync(name, args);
            }
            return new ToolCallResult
            {
                IsError = true,
                Content = new List<ContentItem> { new ContentItem { Text = $"Unknown tool: {name}" } }
            };
        }

        // ===== 辅助 =====

        private static void Write(HttpListenerResponse res, string text, string contentType)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            res.ContentType = contentType;
            res.ContentLength64 = bytes.Length;
            res.OutputStream.Write(bytes, 0, bytes.Length);
            res.Close();
        }

        // ===== SSE Session（游戏事件）=====

        private class SseSession : IDisposable
        {
            private readonly HttpListenerResponse _response;
            private readonly Stream _stream;
            private readonly TaskCompletionSource<bool> _disconnect = new();
            private readonly SemaphoreSlim _lock = new(1, 1);

            public SseSession(HttpListenerResponse response) { _response = response; _stream = response.OutputStream; }

            public async Task SendAsync(string data) => await Send("gameEvent", data);

            public async Task Send(string eventType, string data)
            {
                await _lock.WaitAsync();
                try
                {
                    var sb = new StringBuilder().AppendLine($"event: {eventType}");
                    foreach (var line in data.Split('\n')) sb.AppendLine($"data: {line}");
                    sb.AppendLine();
                    var bytes = Encoding.UTF8.GetBytes(sb.ToString());
                    await _stream.WriteAsync(bytes, 0, bytes.Length);
                    await _stream.FlushAsync();
                }
                catch { _disconnect.TrySetResult(true); }
                finally { _lock.Release(); }
            }

            public Task WaitForDisconnectAsync() => _disconnect.Task;
            public void Dispose() { _disconnect.TrySetResult(true); try { _response.Close(); } catch { } }
        }
    }
}
