using System;
using System.Threading.Tasks;
using RimworkAgent.Core.AgentRuntime;
using SimpleMspServer.Transport;

namespace RimworkAgent.Core.Mcp.Server
{
    public class AgentMcpServer : IDisposable
    {
        private readonly SseTransport _transport;
        private readonly AgentToolDispatcher _dispatcher;
        public const int DefaultPort = 9878;

        public AgentMcpServer(int port = DefaultPort)
        {
            _transport = new SseTransport(port);
            _dispatcher = new AgentToolDispatcher();
            _transport.SetMcpHandler(ProcessJsonRpc);
        }

        public Task StartAsync() => _transport.StartAsync(System.Threading.CancellationToken.None);

        public void Stop() => _transport.StopAsync().GetAwaiter().GetResult();
        public void Dispose() => Stop();

        private string ProcessJsonRpc(string body)
        {
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            var root = doc.RootElement;
            var method = root.GetProperty("method").GetString();
            var id = root.TryGetProperty("id", out var idEl) ? idEl : (System.Text.Json.JsonElement?)null;

            return method switch
            {
                "tools/list" => ToolsList(id),
                "tools/call" => ToolsCall(id, root),
                _ => Error(id, -32601, $"Unknown method: {method}")
            };
        }

        private string ToolsList(System.Text.Json.JsonElement? id)
        {
            var json = System.Text.Json.JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id,
                result = new { tools = _dispatcher.GetToolDefinitions() }
            });
            return json;
        }

        private string ToolsCall(System.Text.Json.JsonElement? id, System.Text.Json.JsonElement request)
        {
            if (!request.TryGetProperty("params", out var prms)) return Error(id, -32602, "Missing params");
            if (!prms.TryGetProperty("name", out var nameEl)) return Error(id, -32602, "Missing tool name");
            var name = nameEl.GetString() ?? "";

            System.Text.Json.JsonElement? args = null;
            if (prms.TryGetProperty("arguments", out var a)) args = a;

            var (text, _) = _dispatcher.ExecuteAsync(name, args).GetAwaiter().GetResult();
            var json = System.Text.Json.JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id,
                result = new { content = new[] { new { type = "text", text } }, isError = false }
            });
            return json;
        }

        private static string Error(System.Text.Json.JsonElement? id, int code, string message)
        {
            return System.Text.Json.JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id,
                error = new { code, message }
            });
        }
    }
}
