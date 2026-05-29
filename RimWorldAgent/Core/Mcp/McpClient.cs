using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RimWorldAgent.Core.AgentRuntime;

namespace RimWorldAgent.Core.Mcp
{
    public class McpClient : IDisposable
    {
        private readonly HttpClient _http;
        private readonly string _baseUrl;
        private long _nextId = 1;
        private CancellationTokenSource? _sseCts;

        /// <summary>收到 SSE 游戏事件时触发</summary>
        public event Action<ColonyEvent>? OnGameEvent;

        public McpClient(string baseUrl = "http://localhost:9877")
        {
            _baseUrl = baseUrl.TrimEnd('/');
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
        }

        /// <summary>tools/list — 获取可用 Tool 列表</summary>
        public async Task<List<ToolDefinition>> ListTools()
        {
            var result = await CallAsync("tools/list", null);
            var tools = JsonSerializer.Deserialize<ToolsListResult>(result.GetRawText());
            return tools?.Tools ?? new List<ToolDefinition>();
        }

        /// <summary>tools/call — 调用 MCP Tool，返回文本结果</summary>
        public async Task<string> CallTool(string name, Dictionary<string, JsonElement>? args = null)
        {
            var prms = new Dictionary<string, JsonElement>
            {
                ["name"] = JsonSerializer.SerializeToElement(name),
                ["arguments"] = args != null
                    ? JsonSerializer.SerializeToElement(args)
                    : JsonSerializer.SerializeToElement(new Dictionary<string, JsonElement>())
            };

            var result = await CallAsync("tools/call", prms);
            var tc = JsonSerializer.Deserialize<ToolCallResult>(result.GetRawText());
            if (tc == null || tc.Content.Count == 0) return "";
            var sb = new StringBuilder();
            foreach (var c in tc.Content) sb.AppendLine(c.Text);
            return sb.ToString().TrimEnd();
        }

        // ===== SSE 事件订阅 =====

        /// <summary>开始 SSE 订阅（后台长连接），事件通过 OnGameEvent 回调</summary>
        public void StartSse()
        {
            _sseCts?.Cancel();
            _sseCts = new CancellationTokenSource();
            _ = Task.Run(() => SseLoop(_sseCts.Token));
        }

        public void StopSse() => _sseCts?.Cancel();

        private async Task SseLoop(CancellationToken ct)
        {
            var url = $"{_baseUrl}/sse";
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, url);
                    req.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("text/event-stream"));
                    using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                    using var stream = await resp.Content.ReadAsStreamAsync();
                    using var reader = new StreamReader(stream);

                    while (!reader.EndOfStream && !ct.IsCancellationRequested)
                    {
                        var line = await reader.ReadLineAsync();
                        if (string.IsNullOrEmpty(line)) continue;
                        if (line.StartsWith("data:"))
                        {
                            var json = line.Substring(5).Trim();
                            try
                            {
                                var doc = JsonDocument.Parse(json);
                                var root = doc.RootElement;
                                var evt = new ColonyEvent
                                {
                                    Category = root.TryGetProperty("Category", out var c) ? c.GetString() ?? "" : "",
                                    Severity = root.TryGetProperty("Severity", out var s) ? s.GetString() ?? "" : "",
                                    Summary = root.TryGetProperty("Summary", out var sm) ? sm.GetString() ?? "" : "",
                                    Tick = root.TryGetProperty("Tick", out var t) && t.TryGetInt32(out var tv) ? tv : 0
                                };
                                OnGameEvent?.Invoke(evt);
                            }
                            catch { /* 忽略解析失败 */ }
                        }
                    }
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    CoreLog.Error($"[McpClient] SSE 断开: {ex.Message}，3s 后重连");
                    try { await Task.Delay(3000, ct); } catch { break; }
                }
            }
        }

        // ===== 内部 =====

        private async Task<JsonElement> CallAsync(string method, Dictionary<string, JsonElement>? prms)
        {
            var request = new JsonRpcRequest
            {
                Method = method,
                Params = prms,
                Id = Interlocked.Increment(ref _nextId)
            };

            var json = JsonSerializer.Serialize(request, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var httpResp = await _http.PostAsync($"{_baseUrl}/mcp", content);
            var respJson = await httpResp.Content.ReadAsStringAsync();
            var resp = JsonSerializer.Deserialize<JsonRpcResponse>(respJson, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

            if (resp?.Error != null)
                throw new Exception($"MCP Error [{resp.Error.Code}]: {resp.Error.Message}");
            if (resp?.Result == null)
                throw new Exception("MCP 响应无 result");

            return resp.Result.Value;
        }

        public void Dispose() { StopSse(); _http.Dispose(); }
    }
}
