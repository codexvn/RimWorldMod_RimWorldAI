using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using RimWorldAgent.Core.AgentRuntime;

namespace RimWorldAgent.Core.Mcp
{
    /// <summary>MCP 客户端 — SDK HttpClientTransport + Notification 拦截游戏事件</summary>
    public class McpClient : IDisposable
    {
        private ModelContextProtocol.Client.McpClient? _sdkClient;
        private readonly string _baseUrl;
        private Task<ModelContextProtocol.Client.McpClient>? _connectTask;

        public event Action<ColonyEvent>? OnGameEvent;
        public event Action<int>? OnGameTick;

        public McpClient(string baseUrl = "http://localhost:9877")
        {
            if (string.IsNullOrWhiteSpace(baseUrl))
                baseUrl = "http://localhost:9877";
            if (!baseUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !baseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                baseUrl = "http://" + baseUrl;
            _baseUrl = baseUrl.TrimEnd('/');
            _connectTask = ConnectAsync();
            CoreLog.Info($"[McpClient] 正在连接 MCP: {_baseUrl}");
        }

        private async Task<ModelContextProtocol.Client.McpClient> ConnectAsync()
        {
            var transport = new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = new Uri(_baseUrl),
            }, NullLoggerFactory.Instance);

            var interceptTransport = new EventInterceptTransport(transport, HandleNotification);
            var client = await ModelContextProtocol.Client.McpClient.CreateAsync(interceptTransport, loggerFactory: NullLoggerFactory.Instance);
            _sdkClient = client;
            return client;
        }

        private async Task<ModelContextProtocol.Client.McpClient> GetClientAsync()
        {
            if (_sdkClient != null) return _sdkClient;
            if (_connectTask != null)
            {
                var client = await _connectTask;
                if (client != null) return client;
            }
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

        public async Task<string> CallTool(string name, Dictionary<string, JsonElement>? args = null)
            => await CallToolAsync(name, args);

        public void Dispose()
        {
            if (_sdkClient is IDisposable d) d.Dispose();
        }

        // ===== Notification 拦截 =====

        private static readonly JsonSerializerOptions _readableJson = new()
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        private int _notificationSeq;

        private void HandleNotification(JsonRpcNotification notif)
        {
            try
            {
                switch (notif.Method)
                {
                    case "game/tick":
                        var tick = notif.Params?["tick"]?.GetValue<int>() ?? 0;
                        if (tick > 0) OnGameTick?.Invoke(tick);
                        break;

                    default:
                        var seq = Interlocked.Increment(ref _notificationSeq);
                        var cat = notif.Params?["Category"]?.GetValue<string>() ?? "";
                        var sev = notif.Params?["Severity"]?.GetValue<string>() ?? "";
                        var summary = notif.Params?["Summary"]?.GetValue<string>() ?? "";
                        CoreLog.Info($"[McpClient] 收到通知 #{seq}: {notif.Method} {sev}/{cat} summary={summary?.Substring(0, Math.Min(summary?.Length ?? 0, 80))}");
                        OnGameEvent?.Invoke(new ColonyEvent
                        {
                            Category = notif.Params?["Category"]?.GetValue<string>() ?? "",
                            Severity = notif.Params?["Severity"]?.GetValue<string>() ?? "",
                            Summary = notif.Params?["Summary"]?.GetValue<string>() ?? "",
                            Tick = notif.Params?["Tick"]?.GetValue<int>() ?? 0,
                            Method = notif.Method,
                            Payload = notif.Params?.ToJsonString(_readableJson),
                            Level = (EventLevel)(notif.Params?["level"]?.GetValue<int>() ?? 2) // 缺省 Warning
                        });
                        break;
                }
            }
            catch (Exception ex)
            {
                CoreLog.Warn($"[McpClient] 通知解析失败 ({notif.Method}): {ex.Message}");
            }
        }

        // ===== Transport Wrapper =====

        /// <summary>包装 HttpClientTransport，拦截 MCP notification 消息</summary>
        private sealed class EventInterceptTransport : IClientTransport
        {
            private readonly IClientTransport _inner;
            private readonly Action<JsonRpcNotification> _onNotification;

            public string Name => _inner.Name;

            public EventInterceptTransport(IClientTransport inner, Action<JsonRpcNotification> onNotification)
            {
                _inner = inner;
                _onNotification = onNotification;
            }

            public async Task<ITransport> ConnectAsync(CancellationToken cancellationToken)
            {
                var innerTransport = await _inner.ConnectAsync(cancellationToken);
                return new InterceptingTransport(innerTransport, _onNotification);
            }
        }

        /// <summary>从 inner MessageReader 转发消息，拦截 notification 触发事件</summary>
        private sealed class InterceptingTransport : ITransport, IAsyncDisposable
        {
            private readonly ITransport _inner;
            private readonly Channel<JsonRpcMessage> _channel;
            private readonly CancellationTokenSource _cts;

            public string? SessionId => _inner.SessionId;
            public ChannelReader<JsonRpcMessage> MessageReader => _channel.Reader;

            public InterceptingTransport(ITransport inner, Action<JsonRpcNotification> onNotification)
            {
                _inner = inner;
                _channel = Channel.CreateUnbounded<JsonRpcMessage>();
                _cts = new CancellationTokenSource();
                _ = ForwardAsync(onNotification, _cts.Token);
            }

            private async Task ForwardAsync(Action<JsonRpcNotification> onNotification, CancellationToken ct)
            {
                var reader = _inner.MessageReader;
                var writer = _channel.Writer;
                try
                {
                    while (await reader.WaitToReadAsync(ct))
                    {
                        while (reader.TryRead(out var msg))
                        {
                            if (msg is JsonRpcNotification notif)
                            {
                                onNotification(notif);
                            }
                            await writer.WriteAsync(msg, ct);
                        }
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { CoreLog.Warn($"[McpClient] 消息转发异常: {ex.Message}"); }
                finally { writer.TryComplete(); }
            }

            public Task SendMessageAsync(JsonRpcMessage message, CancellationToken cancellationToken = default)
                => _inner.SendMessageAsync(message, cancellationToken);

            public async ValueTask DisposeAsync()
            {
                _cts.Cancel();
                _channel.Writer.TryComplete();
                if (_inner is IAsyncDisposable ad)
                    await ad.DisposeAsync();
                else if (_inner is IDisposable d)
                    d.Dispose();
                _cts.Dispose();
            }
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
