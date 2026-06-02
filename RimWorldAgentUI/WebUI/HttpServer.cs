using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Verse;

namespace RimWorldAgent
{
    /// <summary>
    /// 内嵌 HTTP 静态文件服务 — 提供 WebUI (index.html)。
    /// 浏览器打开 http://localhost:19997 连接 UIMessageBus WS。
    /// </summary>
    public class WebUIHttpServer : IDisposable
    {
        private HttpListener? _listener;
        private CancellationTokenSource? _cts;
        private readonly int _port;
        private readonly int _bridgePort;
        private readonly string _bridgeHost;
        private readonly string _webRoot;

        public int Port => _port;
        public bool IsRunning => _listener != null;

        /// <param name="bridgePort">UIMessageBus WS 端口，用于 index.html 模板替换</param>
        /// <param name="bridgeHost">UIMessageBus WS 主机，用于 index.html 模板替换</param>
        /// <param name="webRoot">index.html 所在目录</param>
        public WebUIHttpServer(int port = 19997, int bridgePort = 19999, string bridgeHost = "127.0.0.1", string? webRoot = null)
        {
            _port = port;
            _bridgePort = bridgePort;
            _bridgeHost = bridgeHost;
            _webRoot = webRoot ?? FindWebRoot();
        }

        public void Start()
        {
            if (_listener != null) return;
            try
            {
                _cts = new CancellationTokenSource();
                _listener = new HttpListener();
                _listener.Prefixes.Add($"http://+:{_port}/");
                _listener.Start();
                Task.Run(() => AcceptLoop(_cts.Token), _cts.Token);
                Log.Message($"[WebUI] HTTP 服务已启动: http://localhost:{_port}");
            }
            catch (Exception ex) { Log.Warning($"[WebUI] HTTP 启动失败: {ex.Message}"); }
        }

        public void Stop()
        {
            _cts?.Cancel();
            try { _listener?.Stop(); _listener?.Close(); } catch { }
            _listener = null;
        }

        private async Task AcceptLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && _listener?.IsListening == true)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); }
                catch (OperationCanceledException) { break; }
                catch (HttpListenerException) { break; }
                catch (Exception ex) { Log.Warning($"[WebUI] HTTP 接受错误: {ex.Message}"); continue; }

                _ = Task.Run(() => HandleRequest(ctx), ct);
            }
        }

        private void HandleRequest(HttpListenerContext ctx)
        {
            try
            {
                var path = ctx.Request.Url?.AbsolutePath ?? "/";
                if (path == "/" || path == "") path = "/index.html";

                var filePath = Path.Combine(_webRoot, path.TrimStart('/'));
                // 安全：禁止目录穿越
                if (!Path.GetFullPath(filePath).StartsWith(Path.GetFullPath(_webRoot), StringComparison.OrdinalIgnoreCase))
                { ctx.Response.StatusCode = 403; ctx.Response.Close(); return; }

                if (File.Exists(filePath))
                {
                    var bytes = File.ReadAllBytes(filePath);
                    // HTML 文件模板替换 → JS 中 {{BRIDGE_PORT}} 替换为实际端口
                    if (path.EndsWith(".html"))
                    {
                        var html = Encoding.UTF8.GetString(bytes);
                        html = html.Replace("{{BRIDGE_HOST}}", _bridgeHost);
                        html = html.Replace("{{BRIDGE_PORT}}", _bridgePort.ToString());
                        bytes = Encoding.UTF8.GetBytes(html);
                    }
                    var mime = path.EndsWith(".html") ? "text/html; charset=utf-8"
                             : path.EndsWith(".css") ? "text/css"
                             : path.EndsWith(".js") ? "application/javascript"
                             : "application/octet-stream";
                    ctx.Response.ContentType = mime;
                    ctx.Response.ContentLength64 = bytes.Length;
                    ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
                }
                else { ctx.Response.StatusCode = 404; }
                ctx.Response.Close();
            }
            catch (HttpListenerException) { Log.Warning("[WebUI] 客户端已断开连接（正常网络行为）"); }
            catch (Exception ex) { Log.Warning($"[WebUI] 请求处理失败: {ex.Message}"); }
        }

        private static string FindWebRoot()
        {
            // 优先 publish 路径，回退源码路径
            var asmDir = Path.GetDirectoryName(typeof(WebUIHttpServer).Assembly.Location) ?? ".";
            var publish = Path.Combine(asmDir, "WebUI");
            if (Directory.Exists(publish)) return publish;
            var src = Path.GetFullPath(Path.Combine(asmDir, "..", "..", "..", "resource", "WebUI"));
            if (Directory.Exists(src)) return src;
            return asmDir;
        }

        public void Dispose() => Stop();
    }
}
