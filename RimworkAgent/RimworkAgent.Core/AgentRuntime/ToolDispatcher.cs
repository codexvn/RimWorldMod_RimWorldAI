using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using RimworkAgent.Core.CcbManager;
using RimworkAgent.Core.Mcp;

namespace RimworkAgent.Core.AgentRuntime
{
    /// <summary>Tool 调度：内部 Tool → 本地处理，外部 Tool → 转发 MCP。</summary>
    public static class ToolDispatcher
    {
        public static async Task HandleAsync(
            CcbWebSocket ccbWs, McpClient mcp,
            string toolId, string toolName, JsonElement? input,
            Action<string> log)
        {
            // 内部 Tool → 直接本地处理
            if (InternalToolRegistry.IsInternal(toolName))
            {
                var (result, shouldExit) = await InternalToolRegistry.ExecuteAsync(toolName, input);
                log($"[internal] {toolName}: {result}");
                await ccbWs.SendToolResult(toolId, result);

                if (shouldExit && toolName == "exit_combat_role")
                {
                    AgentOrchestrator.EndAgent("combat");
                    log("Combat Agent 退出");
                }
                return;
            }

            // 外部 Tool → 转发 MCP
            try
            {
                var args = input != null
                    ? JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(input.Value.GetRawText())
                    : null;
                var result = await mcp.CallTool(toolName, args);
                await ccbWs.SendToolResult(toolId, result);
            }
            catch (Exception ex)
            {
                await ccbWs.SendToolResult(toolId, $"Error: {ex.Message}", true);
            }
        }
    }
}
