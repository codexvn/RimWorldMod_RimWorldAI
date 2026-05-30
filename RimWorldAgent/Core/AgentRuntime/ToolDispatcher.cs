using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Tasks;
using RimWorldAgent.Core.CcbManager;
using RimWorldAgent.Core.Mcp;

namespace RimWorldAgent.Core.AgentRuntime
{
    /// <summary>Tool 调度：内部 Tool → 本地处理，外部 Tool → 转发 MCP。</summary>
    public static class ToolDispatcher
    {
        public static async Task HandleAsync(
            CcbWebSocket ccbWs, McpClient mcp,
            string toolId, string toolName, JsonElement? input,
            Action<string> log)
        {
            var sw = Stopwatch.StartNew();

            // 内部 Tool → 直接本地处理
            if (InternalToolRegistry.Instance.IsInternal(toolName))
            {
                log($"工具调用: {toolName}");
                var (result, shouldExit) = await InternalToolRegistry.Instance.ExecuteInternalAsync(toolName, input);
                sw.Stop();
                log($"工具完成: {toolName} 用时 {sw.ElapsedMilliseconds}ms");
                await ccbWs.SendToolResult(toolId, result);
                return;
            }

            // 外部 Tool → 转发 MCP
            try
            {
                log($"工具调用: {toolName}");
                var args = input != null
                    ? JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(input.Value.GetRawText())
                    : null;
                var result = await mcp.CallTool(toolName, args);
                sw.Stop();
                log($"工具完成: {toolName} 用时 {sw.ElapsedMilliseconds}ms");
                await ccbWs.SendToolResult(toolId, result);
            }
            catch (Exception ex)
            {
                sw.Stop();
                log($"工具失败: {toolName} 用时 {sw.ElapsedMilliseconds}ms — {ex.Message}");
                await ccbWs.SendToolResult(toolId, $"Error: {ex.Message}", true);
            }
        }
    }
}
