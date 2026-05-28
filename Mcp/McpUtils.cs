using System.Collections.Generic;
using System.Text.Json;

namespace RimWorldMCP
{
    /// <summary>MCP 工具函数</summary>
    public static class McpUtils
    {
        /// <summary>生成默认的 .mcp.json 项目配置内容</summary>
        public static string BuildMcpJson(int mcpPort)
        {
            var obj = new Dictionary<string, object?>
            {
                ["mcpServers"] = new Dictionary<string, object>
                {
                    ["rimworld"] = new Dictionary<string, string>
                    {
                        ["type"] = "http",
                        ["url"] = $"http://localhost:{mcpPort}/mcp"
                    }
                }
            };
            return JsonSerializer.Serialize(obj, new JsonSerializerOptions { WriteIndented = true });
        }
    }
}
