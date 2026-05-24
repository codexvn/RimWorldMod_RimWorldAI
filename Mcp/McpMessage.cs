using System.Collections.Generic;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;

namespace RimWorldMCP.Mcp
{
    public static class McpJson
    {
        public static readonly JsonSerializerOptions Options = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false,
            Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
        };
    }

    // ---- JSON-RPC 2.0 ----

    public class JsonRpcRequest
    {
        [JsonPropertyName("jsonrpc")]
        public string Jsonrpc { get; set; } = "2.0";

        [JsonPropertyName("id")]
        public JsonElement? Id { get; set; }

        [JsonPropertyName("method")]
        public string Method { get; set; } = "";

        [JsonPropertyName("params")]
        public JsonElement? Params { get; set; }

        public bool IsNotification => Id == null || Id.Value.ValueKind == JsonValueKind.Null;
    }

    public class JsonRpcResponse
    {
        [JsonPropertyName("jsonrpc")]
        public string Jsonrpc { get; set; } = "2.0";

        [JsonPropertyName("id")]
        public JsonElement? Id { get; set; }

        [JsonPropertyName("result")]
        public JsonElement? Result { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        [JsonPropertyName("error")]
        public JsonRpcError? Error { get; set; }

        public static JsonRpcResponse Success(JsonElement id, object result)
        {
            return new JsonRpcResponse
            {
                Id = id,
                Result = JsonSerializer.SerializeToElement(result, McpJson.Options)
            };
        }

        public static JsonRpcResponse Fail(JsonElement id, int code, string message)
        {
            return new JsonRpcResponse
            {
                Id = id,
                Error = new JsonRpcError { Code = code, Message = message }
            };
        }

        public string ToJson()
        {
            return JsonSerializer.Serialize(this, McpJson.Options);
        }
    }

    public class JsonRpcError
    {
        [JsonPropertyName("code")]
        public int Code { get; set; }

        [JsonPropertyName("message")]
        public string Message { get; set; } = "";
    }

    // ---- MCP Initialize ----

    public class InitializeParams
    {
        [JsonPropertyName("protocolVersion")]
        public string ProtocolVersion { get; set; } = "";

        [JsonPropertyName("clientInfo")]
        public ClientInfo? ClientInfo { get; set; }
    }

    public class ClientInfo
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("version")]
        public string Version { get; set; } = "";
    }

    public class InitializeResult
    {
        [JsonPropertyName("protocolVersion")]
        public string ProtocolVersion { get; set; } = "2024-11-05";

        [JsonPropertyName("capabilities")]
        public ServerCapabilities Capabilities { get; set; } = new();

        [JsonPropertyName("serverInfo")]
        public ServerInfo ServerInfo { get; set; } = new();
    }

    public class ServerCapabilities
    {
        [JsonPropertyName("tools")]
        public ToolCapability Tools { get; set; } = new();

        [JsonPropertyName("resources")]
        public ResourceCapability Resources { get; set; } = new();
    }

    public class ToolCapability
    {
        [JsonPropertyName("listChanged")]
        public bool ListChanged { get; set; } = false;
    }

    public class ResourceCapability
    {
        [JsonPropertyName("subscribe")]
        public bool Subscribe { get; set; } = false;

        [JsonPropertyName("listChanged")]
        public bool ListChanged { get; set; } = false;
    }

    public class ServerInfo
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "RimWorld MCP";

        [JsonPropertyName("version")]
        public string Version { get; set; } = "1.0.0";
    }

    // ---- Tool ----

    public class ToolDefinition
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("description")]
        public string Description { get; set; } = "";

        [JsonPropertyName("inputSchema")]
        public JsonElement InputSchema { get; set; }

        [JsonPropertyName("annotations")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public ToolAnnotations? Annotations { get; set; }
    }

    public class ToolAnnotations
    {
        [JsonPropertyName("title")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Title { get; set; }

        [JsonPropertyName("readOnlyHint")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? ReadOnlyHint { get; set; }

        [JsonPropertyName("destructiveHint")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? DestructiveHint { get; set; }

        [JsonPropertyName("idempotentHint")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? IdempotentHint { get; set; }

        [JsonPropertyName("openWorldHint")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? OpenWorldHint { get; set; }
    }

    public class ToolCallParams
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("arguments")]
        public JsonElement? Arguments { get; set; }
    }

    public class ToolCallResult
    {
        [JsonPropertyName("content")]
        public List<ContentItem> Content { get; set; } = new();

        [JsonPropertyName("isError")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool IsError { get; set; }
    }

    public class ContentItem
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "text";

        [JsonPropertyName("text")]
        public string Text { get; set; } = "";
    }

    // ---- Resource ----

    public class ResourceDefinition
    {
        [JsonPropertyName("uri")]
        public string Uri { get; set; } = "";

        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("description")]
        public string Description { get; set; } = "";

        [JsonPropertyName("mimeType")]
        public string MimeType { get; set; } = "text/plain";
    }
}
