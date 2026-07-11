using System;
using System.Text.Json;
using System.Threading.Tasks;
using SimpleMspServer.Mcp;

namespace RimWorldMCP.Tools
{
    /// <summary>
    /// 获取当前 Agent session ID（持久化到存档的后端不透明标识）。
    /// Agent 侧用于跨存档数据隔离。
    /// </summary>
    public class Tool_GetSessionId : ITool, INoMapRequired
    {
        public string Name => "get_session_id";
        public string Description => "获取当前游戏的 MCP 会话 ID（持久化到存档的唯一标识）。可用于跨存档的数据隔离。";

        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new { }
        });

        public async Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            var sessionId = GameComponent_McpServer.CurrentSessionId;
            if (string.IsNullOrEmpty(sessionId))
                return ToolResult.Error("会话 ID 不可用（当前可能尚未加载存档）。请先开始新游戏或加载存档。");
            return ToolResult.Success(sessionId);
        }

        public (int minX, int minZ, int maxX, int maxZ)? GetTargetRange(JsonElement? args) => null;
    }
}
