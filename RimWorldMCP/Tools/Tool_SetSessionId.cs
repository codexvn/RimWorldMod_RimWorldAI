using System.Text.Json;
using System.Threading.Tasks;
using SimpleMspServer.Mcp;

namespace RimWorldMCP.Tools
{
    /// <summary>
    /// 设置当前 MCP 会话 ID，由 Agent 调用以同步 SDK 会话 UUID 到 Scribe 持久化存档。
    /// </summary>
    public class Tool_SetSessionId : ITool, INoMapRequired
    {
        public string Name => "set_session_id";
        public string Description => "设置当前游戏的 MCP 会话 ID（由 Agent 同步 SDK 会话 UUID 到存档持久化）。";

        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                id = new
                {
                    type = "string",
                    description = "新的会话 ID"
                }
            },
            required = new[] { "id" }
        });

        public async Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            var id = args?.TryGetProperty("id", out var p) == true ? p.GetString() ?? "" : "";
            GameComponent_McpServer.SetSessionId(id);
            return ToolResult.Success(id);
        }

        public (int minX, int minZ, int maxX, int maxZ)? GetTargetRange(JsonElement? args) => null;
    }
}
