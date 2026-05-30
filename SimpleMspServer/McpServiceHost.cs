using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SimpleMspServer.Mcp;

namespace SimpleMspServer
{
    /// <summary>MCP 服务主机 — HttpListener + SDK per-session transport</summary>
    public class McpServiceHost : IDisposable
    {
        private readonly int _port;
        private readonly string _host;
        private readonly string _prefixHost;
        private HttpListener? _listener;
        private CancellationTokenSource? _cts;
        private readonly List<IToolProvider> _providers = new();
        private readonly IMspLog _log;
        private readonly ConcurrentDictionary<string, McpSession> _sessions = new();

        public int Port => _port;
        public string Host => _host;
        public bool IsRunning => _listener != null;
        public static McpServiceHost? Instance { get; private set; }

        public McpServiceHost(int port = 9877, string host = "localhost", IMspLog? log = null)
        {
            _port = port;
            _host = host;
            _prefixHost = host == "0.0.0.0" ? "+" : host;
            _log = log ?? NullMspLog.Instance;
        }

        public void RegisterProvider(IToolProvider provider) => _providers.Add(provider);

        public void SendEvent(string method, string jsonData)
        {
            foreach (var kv in _sessions)
                _ = kv.Value.SendNotificationAsync(method, jsonData);
        }

        public void Start()
        {
            if (IsRunning) return;
            Instance = this;
            try
            {
                _cts = new CancellationTokenSource();
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

        public void Stop() { _log.Info("MCP 服务已停止"); Cleanup(); }
        public void Dispose() => Stop();

        private void Cleanup()
        {
            _cts?.Cancel();
            try { _listener?.Stop(); _listener?.Close(); } catch (Exception ex) { _log.Info($"清理 HttpListener 异常: {ex.Message}"); }
            _listener = null;
            foreach (var kv in _sessions)
            {
                try { kv.Value.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch (Exception ex) { _log.Info($"清理 Session 异常: {ex.Message}"); }
            }
            _sessions.Clear();
            _cts?.Dispose();
            _cts = null;
            Instance = null;
        }

        // ===== HTTP =====

        private async Task AcceptLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && _listener?.IsListening == true)
            {
                try
                {
                    var ctx = await _listener.GetContextAsync().ConfigureAwait(false);
                    _ = Task.Run(() => HandleAsync(ctx), ct);
                }
                catch (OperationCanceledException) { break; }
                catch (HttpListenerException) { break; }
                catch (Exception ex) { _log.Error($"HTTP 接受错误: {ex}"); }
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
                else if ((isMcp || path == "/sse") && req.HttpMethod == "GET") await HandleMcpSse(ctx);
                else if (isMcp && req.HttpMethod == "DELETE") await HandleMcpDelete(ctx);
                else if (path == "/health" && req.HttpMethod == "GET") Write(res, "OK", "text/plain");
                else if (req.HttpMethod == "GET") Write(res, "{\"status\":\"ok\",\"server\":\"RimWorldMCP\",\"transport\":\"http+sse\"}", "application/json");
                else { res.StatusCode = 404; res.Close(); }
            }
            catch (Exception ex)
            {
                _log.Error($"HTTP 处理错误: {ex.GetType().Name}: {ex.Message}");
                try { res.StatusCode = 500; res.Close(); } catch (Exception closeEx) { _log.Info($"关闭 HTTP 响应异常: {closeEx.Message}"); }
            }
        }

        // ===== POST =====

        private async Task HandleMcpPost(HttpListenerContext ctx)
        {
            var req = ctx.Request;
            var res = ctx.Response;
            using var reader = new StreamReader(req.InputStream, Encoding.UTF8);
            var body = await reader.ReadToEndAsync();

            var msg = JsonSerializer.Deserialize<JsonRpcMessage>(body,
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            if (msg == null) { Write(res, "{\"jsonrpc\":\"2.0\",\"id\":null,\"error\":{\"code\":-32700,\"message\":\"Parse error\"}}", "application/json"); return; }

            var existingSid = req.Headers.Get("Mcp-Session-Id");
            McpSession session;
            string? newSid = null;

            if (!string.IsNullOrEmpty(existingSid) && _sessions.TryGetValue(existingSid, out var s))
                session = s;
            else
            {
                session = CreateSession();
                _sessions[session.SessionId] = session;
                newSid = session.SessionId;
                if (!string.IsNullOrEmpty(existingSid)) _log.Info($"会话已迁移: {existingSid} → {newSid}");
                else _log.Info($"新会话: {newSid}");
            }

            using var ms = new MemoryStream();
            var wroteResponse = await session.Transport.HandlePostRequestAsync(msg, ms, _cts?.Token ?? CancellationToken.None);

            var respSid = newSid ?? existingSid;
            if (!string.IsNullOrEmpty(respSid))
                res.Headers.Add("Mcp-Session-Id", respSid);

            if (wroteResponse)
            {
                ms.Position = 0;
                using var sr = new StreamReader(ms);
                var raw = await sr.ReadToEndAsync();
                Write(res, raw, "text/event-stream");
            }
            else { res.StatusCode = 202; res.Close(); }
        }

        // ===== GET SSE =====

        private async Task HandleMcpSse(HttpListenerContext ctx)
        {
            var res = ctx.Response;
            res.ContentType = "text/event-stream";
            res.Headers.Add("Cache-Control", "no-cache");
            res.Headers.Add("Connection", "keep-alive");

            var sid = ctx.Request.Headers.Get("Mcp-Session-Id");
            if (string.IsNullOrEmpty(sid) || !_sessions.TryGetValue(sid, out var session))
            {
                var err = Encoding.UTF8.GetBytes("event: error\ndata: Invalid or missing session\n\n");
                await res.OutputStream.WriteAsync(err, 0, err.Length);
                res.Close(); return;
            }

            res.Headers.Add("Mcp-Session-Id", sid);
            try { await session.Transport.HandleGetRequestAsync(res.OutputStream, _cts?.Token ?? CancellationToken.None); }
            catch (Exception ex) when (ex.Message.Contains("Session resumption")) { }
            catch (Exception ex) { _log.Warn($"SSE 断开 ({sid}): {ex.Message}"); }
        }

        // ===== DELETE =====

        private Task HandleMcpDelete(HttpListenerContext ctx)
        {
            var sid = ctx.Request.Headers.Get("Mcp-Session-Id");
            if (!string.IsNullOrEmpty(sid) && _sessions.TryRemove(sid, out var session))
            {
                _ = DisposeSessionAsync(session, sid);
            }
            ctx.Response.StatusCode = 200; ctx.Response.Close();
            return Task.CompletedTask;
        }

        private async Task DisposeSessionAsync(McpSession session, string sid)
        {
            try { await session.DisposeAsync(); _log.Info($"会话已释放: {sid}"); }
            catch (Exception ex) { _log.Warn($"释放异常 ({sid}): {ex.Message}"); }
        }

        // ===== Session =====

        private McpSession CreateSession()
        {
            var sessionId = Guid.NewGuid().ToString("N");
            var transport = new StreamableHttpServerTransport(NullLoggerFactory.Instance) { SessionId = sessionId };
            var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts?.Token ?? CancellationToken.None);

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
                            ? JsonSerializer.SerializeToElement(d)
                            : (JsonElement?)null;
                        var r = await ExecuteAsync(req.Params!.Name, je);
                        return new CallToolResult
                        {
                            Content = r.Content.Select(c => (ContentBlock)new TextContentBlock { Text = c.Text }).ToList(),
                            IsError = r.IsError
                        };
                    }
                }
            };

            var server = McpServer.Create(transport, options, NullLoggerFactory.Instance);
            var runTask = Task.Run(() => server.RunAsync(cts.Token), cts.Token);
            return new McpSession(transport, server, runTask, cts);
        }

        // ===== Tool 路由 =====

        private List<Tool> BuildToolList() =>
            _providers.SelectMany(p => p.GetDefinitions()).Select(def => new Tool
            { Name = def.Name, Description = def.Description, InputSchema = def.InputSchema }).ToList();

        private async Task<ToolCallResult> ExecuteAsync(string name, JsonElement? args)
        {
            foreach (var p in _providers)
                if (p.GetDefinitions().Any(d => d.Name == name))
                    return await p.ExecuteAsync(name, args);
            return new ToolCallResult { IsError = true, Content = new List<ContentItem> { new() { Text = $"Unknown tool: {name}" } } };
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

        // ===== McpSession =====

        private class McpSession : IAsyncDisposable
        {
            public StreamableHttpServerTransport Transport { get; }
            public string SessionId => Transport.SessionId ?? "";
            private readonly McpServer _server;
            private readonly Task _runTask;
            private readonly CancellationTokenSource _cts;

            public McpSession(StreamableHttpServerTransport transport, McpServer server, Task runTask, CancellationTokenSource cts)
            { Transport = transport; _server = server; _runTask = runTask; _cts = cts; }

            public async Task SendNotificationAsync(string method, string jsonParams)
            {
                try
                {
                    var notification = new JsonRpcNotification
                    { Method = method, Params = System.Text.Json.Nodes.JsonNode.Parse(jsonParams) };
                    await Transport.SendMessageAsync(notification, _cts.Token);
                }
                catch (OperationCanceledException) { Console.Error.WriteLine("[McpSession] 发送通知已取消"); }
                catch (Exception ex) { Console.Error.WriteLine($"[McpSession] 发送通知失败: {ex.Message}"); }
            }

            public async ValueTask DisposeAsync()
            {
                _cts.Cancel();
                try { await _runTask; } catch (Exception ex) { Console.Error.WriteLine($"[McpSession] 等待运行任务异常: {ex.Message}"); }
                if (_server is IDisposable d) d.Dispose();
                _cts.Dispose();
            }
        }
    }
}
