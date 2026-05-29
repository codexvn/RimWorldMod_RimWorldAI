using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using RimworkAgent.Core.AgentRuntime;

namespace RimworkAgent.Core.Mcp.Server
{
    /// <summary>将 InternalToolRegistry 适配为 MCP Tool 调度器。</summary>
    public class AgentToolDispatcher
    {
        public List<object> GetToolDefinitions()
        {
            var list = new List<object>();
            foreach (var tool in InternalToolRegistry.All)
            {
                list.Add(new { name = tool.Name, description = tool.Description, inputSchema = tool.InputSchema });
            }
            return list;
        }

        public async Task<(string result, bool exit)> ExecuteAsync(string name, JsonElement? args)
        {
            if (InternalToolRegistry.IsInternal(name))
                return await InternalToolRegistry.ExecuteAsync(name, args);
            return ($"未知 Tool: {name}", false);
        }
    }
}
