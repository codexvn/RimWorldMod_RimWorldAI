using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using RimWorldAgent.Core.AgentRuntime;

namespace RimWorldAgent.Core.Mcp
{
    /// <summary>MCP 客户端 — SDK HttpClientTransport + 自定义 SSE 游戏事件订阅</summary>
    public class McpClient : IDisposable
    {
        private ModelContextProtocol.Client.McpClient? _sdkClient;
        private readonly string _baseUrl;
        private readonly HttpClient _http;
        private CancellationTokenSource? _sseCts;
        private Task<ModelContextProtocol.Client.McpClient>? _connectTask;

        public event Action<ColonyEvent>? OnGameEvent;
        public event Action<int>? OnGameTick;
        public event Action<SchedulerInput>? OnWorldState;

        public McpClient(string baseUrl = "http://localhost:9877")
        {
            if (string.IsNullOrWhiteSpace(baseUrl))
                baseUrl = "http://localhost:9877";
            if (!baseUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !baseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                baseUrl = "http://" + baseUrl;
            _baseUrl = baseUrl.TrimEnd('/');
            _http = new HttpClient { BaseAddress = new Uri(_baseUrl + "/"), Timeout = TimeSpan.FromSeconds(120) };
            _connectTask = ConnectAsync();
            CoreLog.Info($"[McpClient] 正在连接 MCP: {_baseUrl}");
        }

        private async Task<ModelContextProtocol.Client.McpClient> ConnectAsync()
        {
            var transport = new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = new Uri(_baseUrl),
            }, NullLoggerFactory.Instance);

            var client = await ModelContextProtocol.Client.McpClient.CreateAsync(transport, loggerFactory: NullLoggerFactory.Instance);
            _sdkClient = client;
            return client;
        }

        private async Task<ModelContextProtocol.Client.McpClient> GetClientAsync()
        {
            if (_sdkClient != null) return _sdkClient;
            if (_connectTask != null) return await _connectTask;
            throw new ObjectDisposedException(nameof(McpClient));
        }

        public async Task<List<Tool>> ListToolsAsync()
        {
            var client = await GetClientAsync();
            var result = await client.ListToolsAsync();
            return result.Select(t => t.ProtocolTool).ToList();
        }

        public async Task<string> CallToolAsync(string name, Dictionary<string, JsonElement>? args = null)
        {
            var client = await GetClientAsync();
            IReadOnlyDictionary<string, object?>? sdkArgs = null;
            if (args != null)
            {
                var dict = new Dictionary<string, object?>();
                foreach (var kv in args) dict[kv.Key] = kv.Value;
                sdkArgs = dict;
            }
            var result = await client.CallToolAsync(name, sdkArgs);
            var sb = new StringBuilder();
            foreach (var c in result.Content)
            {
                if (c is TextContentBlock text)
                    sb.AppendLine(text.Text);
            }
            return sb.ToString().TrimEnd();
        }

        /// <summary>同步封装供旧代码使用</summary>
        public async Task<string> CallTool(string name, Dictionary<string, JsonElement>? args = null)
            => await CallToolAsync(name, args);

        // ===== SSE 游戏事件订阅（自定义 /events 端点） =====

        public void StartSse()
        {
            _sseCts?.Cancel();
            _sseCts = new CancellationTokenSource();
            _ = Task.Run(() => SseLoop(_sseCts.Token));
        }

        public void StopSse() => _sseCts?.Cancel();

        private async Task SseLoop(CancellationToken ct)
        {
            var url = $"{_baseUrl}/events";
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, new Uri(url));
                    req.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("text/event-stream"));
                    using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                    using var stream = await resp.Content.ReadAsStreamAsync();
                    using var reader = new StreamReader(stream);

                    string lastMethod = "";
                    while (!reader.EndOfStream && !ct.IsCancellationRequested)
                    {
                        var line = await reader.ReadLineAsync();
                        if (string.IsNullOrEmpty(line)) continue;

                        // 跟踪 SSE event 行（method 名称）
                        if (line.StartsWith("event:"))
                        {
                            lastMethod = line.Substring(6).Trim();
                            continue;
                        }
                        if (!line.StartsWith("data:")) continue;

                        var json = line.Substring(5).Trim();
                        try
                        {
                            var doc = JsonDocument.Parse(json);
                            var root = doc.RootElement;

                            if (lastMethod == "game/tick")
                            {
                                var gameTick = root.TryGetProperty("tick", out var tk) && tk.TryGetInt32(out var tv) ? tv : 0;
                                if (gameTick > 0) OnGameTick?.Invoke(gameTick);
                            }
                            else if (lastMethod == "game/world-state")
                            {
                                OnWorldState?.Invoke(new SchedulerInput
                                {
                                    ColonistCount = root.TryGetProperty("colonists", out var co) ? co.GetInt32() : 0,
                                    IdleCount = root.TryGetProperty("idle", out var id) ? id.GetInt32() : 0,
                                    EnemyCount = root.TryGetProperty("enemies", out var en) ? en.GetInt32() : 0,
                                    DownedEnemyCount = root.TryGetProperty("downed", out var dn) ? dn.GetInt32() : 0,
                                    FoodDays = root.TryGetProperty("foodDays", out var fd) ? fd.GetSingle() : 0f,
                                    MedicineCount = root.TryGetProperty("medicine", out var md) ? md.GetInt32() : 0,
                                    CurrentTick = AgentOrchestrator.GameTick
                                });
                            }
                            else
                            {
                                // game/notification, game/deterioration 等
                                OnGameEvent?.Invoke(new ColonyEvent
                                {
                                    Category = root.TryGetProperty("Category", out var c) ? c.GetString() ?? "" : "",
                                    Severity = root.TryGetProperty("Severity", out var s) ? s.GetString() ?? "" : "",
                                    Summary = root.TryGetProperty("Summary", out var sm) ? sm.GetString() ?? "" : "",
                                    Tick = root.TryGetProperty("Tick", out var t) && t.TryGetInt32(out var ti) ? ti : 0,
                                    Method = lastMethod
                                });
                            }
                        }
                        catch (Exception ex)
                        {
                            CoreLog.Warn($"[McpClient] SSE 消息解析失败 ({lastMethod}): {ex.Message}");
                        }
                    }
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    var detail = UnwrapException(ex);
                    CoreLog.Error($"[McpClient] SSE 断开 ({_baseUrl}/events): {detail}，3s 后重连");
                    try { await Task.Delay(3000, ct); } catch (OperationCanceledException) { break; }
                }
            }
        }

        public void Dispose()
        {
            StopSse();
            if (_sdkClient is IDisposable d) d.Dispose();
            _http.Dispose();
        }

        private static string UnwrapException(Exception ex)
        {
            var sb = new StringBuilder();
            while (ex != null)
            {
                if (sb.Length > 0) sb.Append(" → ");
                sb.Append($"{ex.GetType().Name}: {ex.Message}");
                ex = ex.InnerException;
            }
            return sb.ToString();
        }
    }
}
