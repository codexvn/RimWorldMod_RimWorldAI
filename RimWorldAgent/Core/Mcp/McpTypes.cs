using System.Collections.Generic;
using System.Text.Json;

namespace RimWorldAgent.Core.Mcp
{
    /// <summary>MCP JSON-RPC 2.0 请求</summary>
    public class JsonRpcRequest
    {
        public string Jsonrpc { get; set; } = "2.0";
        public string Method { get; set; } = "";
        public Dictionary<string, JsonElement>? Params { get; set; }
        public long Id { get; set; }
    }

    /// <summary>MCP JSON-RPC 2.0 响应</summary>
    public class JsonRpcResponse
    {
        public string Jsonrpc { get; set; } = "2.0";
        public long? Id { get; set; }
        public JsonElement? Result { get; set; }
        public JsonRpcError? Error { get; set; }
    }

    public class JsonRpcError
    {
        public int Code { get; set; }
        public string Message { get; set; } = "";
    }

    /// <summary>tools/call 调用参数</summary>
    public class ToolCallParams
    {
        public string Name { get; set; } = "";
        public Dictionary<string, JsonElement>? Arguments { get; set; }
    }

    /// <summary>tools/list 响应</summary>
    public class ToolsListResult
    {
        public List<ToolDefinition> Tools { get; set; } = new();
    }

    public class ToolDefinition
    {
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public JsonElement? InputSchema { get; set; }
    }

    /// <summary>tools/call 响应内容</summary>
    public class ToolCallResult
    {
        public List<TextContent> Content { get; set; } = new();
        public bool IsError { get; set; }
    }

    public class TextContent
    {
        public string Type { get; set; } = "text";
        public string Text { get; set; } = "";
    }

    /// <summary>SSE 下行事件（NotificationBus 推送）</summary>
    public class SseEvent
    {
        public string? Event { get; set; }
        public string? Data { get; set; }
        public string? Id { get; set; }
    }
}
