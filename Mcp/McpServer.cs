using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using RimWorldMCP.Tools;
using RimWorldMCP.Transport;

namespace RimWorldMCP.Mcp
{
    public class McpServer
    {
        private readonly ITransport _transport;
        private readonly ToolRegistry _toolRegistry;

        public McpServer(ITransport transport, ToolRegistry toolRegistry)
        {
            _transport = transport;
            _toolRegistry = toolRegistry;
            _transport.OnMessage += OnMessageReceived;
        }

        private void OnMessageReceived(string rawJson)
        {
            _ = HandleMessageAsync(rawJson);
        }

        private async Task HandleMessageAsync(string rawJson)
        {
            JsonRpcRequest? request = null;

            try
            {
                request = JsonSerializer.Deserialize<JsonRpcRequest>(rawJson, McpJson.Options);
                if (request == null)
                {
                    await SendError(null, -32700, "Parse error: 无法解析 JSON-RPC 请求");
                    return;
                }
            }
            catch
            {
                await SendError(null, -32700, "Parse error: JSON 格式无效");
                return;
            }

            try
            {
                var response = await DispatchAsync(request);
                if (response != null)
                {
                    await _transport.SendAsync(response.ToJson());
                }
            }
            catch (Exception ex)
            {
                Log($"Dispatch 异常: {ex}");
                await SendError(request.Id, -32603, $"Internal error: {ex.Message}");
            }
        }

        private async Task<JsonRpcResponse?> DispatchAsync(JsonRpcRequest request)
        {
            var id = request.Id;

            switch (request.Method)
            {
                case "initialize":
                    return HandleInitialize(id);

                case "notifications/initialized":
                    Log("MCP 初始化完成");
                    return null;

                case "tools/list":
                    return HandleToolsList(id);

                case "tools/call":
                    return await HandleToolsCallAsync(id, request.Params);

                case "resources/list":
                    return HandleResourcesList(id);

                case "resources/read":
                    return HandleResourcesRead(id, request.Params);

                case "ping":
                    return JsonRpcResponse.Success(id!.Value, new { });

                default:
                    return JsonRpcResponse.Fail(id!.Value, -32601,
                        $"Method not found: {request.Method}");
            }
        }

        private JsonRpcResponse HandleInitialize(JsonElement? id)
        {
            var result = new InitializeResult();
            if (id != null)
            {
                return JsonRpcResponse.Success(id.Value, result);
            }
            return new JsonRpcResponse
            {
                Id = null,
                Result = JsonSerializer.SerializeToElement(result, McpJson.Options)
            };
        }

        private JsonRpcResponse HandleToolsList(JsonElement? id)
        {
            var tools = _toolRegistry.GetDefinitions();
            return JsonRpcResponse.Success(id!.Value, new { tools });
        }

        private async Task<JsonRpcResponse> HandleToolsCallAsync(JsonElement? id, JsonElement? @params)
        {
            if (@params == null)
            {
                return JsonRpcResponse.Fail(id!.Value, -32602, "缺少 params");
            }

            var callParams = JsonSerializer.Deserialize<ToolCallParams>(
                @params.Value.GetRawText(), McpJson.Options);
            if (callParams == null || string.IsNullOrEmpty(callParams.Name))
            {
                return JsonRpcResponse.Fail(id!.Value, -32602, "缺少 tool name");
            }

            var result = await _toolRegistry.ExecuteAsync(callParams.Name, callParams.Arguments);
            return JsonRpcResponse.Success(id!.Value, result);
        }

        private JsonRpcResponse HandleResourcesList(JsonElement? id)
        {
            var resources = _toolRegistry.GetResources();
            return JsonRpcResponse.Success(id!.Value, new { resources });
        }

        private JsonRpcResponse HandleResourcesRead(JsonElement? id, JsonElement? @params)
        {
            if (@params == null || !@params.Value.TryGetProperty("uri", out var uri))
            {
                return JsonRpcResponse.Fail(id!.Value, -32602, "缺少 uri 参数");
            }

            var content = _toolRegistry.ReadResource(uri.GetString() ?? "");
            if (content == null)
            {
                return JsonRpcResponse.Fail(id!.Value, -32000,
                    $"Resource not found: {uri}");
            }

            return JsonRpcResponse.Success(id!.Value, new
            {
                contents = new[]
                {
                    new { uri = uri.GetString(), mimeType = "text/plain", text = content }
                }
            });
        }

        private async Task SendError(JsonElement? id, int code, string message)
        {
            if (id != null)
            {
                var resp = JsonRpcResponse.Fail(id.Value, code, message);
                await _transport.SendAsync(resp.ToJson());
            }
        }

        private static void Log(string msg) => McpLog.Info(msg);
    }
}
