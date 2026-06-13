using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SimpleMspServer.Mcp
{
    // ===== Tool 定义（IToolProvider 接口使用）=====

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
        [JsonPropertyName("readOnlyHint")]
        public bool? ReadOnlyHint { get; set; }

        [JsonPropertyName("destructiveHint")]
        public bool? DestructiveHint { get; set; }

        [JsonPropertyName("requiresAdvanceHint")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool RequiresAdvanceHint { get; set; }
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

    // ===== Resource（IToolProvider 接口使用）=====

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
