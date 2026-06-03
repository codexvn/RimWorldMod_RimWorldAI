using System;
using Verse;

namespace RimWorldMCP.Harmony
{
    /// <summary>MCP 服务不再在此启动，改为 GameComponent.StartedNewGame/LoadedGame 延迟启动，避免加载期间工具调用卡死</summary>
    public static class Hook_McpBootstrap
    {
        static Hook_McpBootstrap()
        {
            try
            {
                McpLog.Info("[RimWorldMCP] 正在启动 MCP 服务...");
                McpServiceManager.Start();
                McpLog.Info("MCP 服务启动成功");
            }
            catch (Exception ex)
            {
                McpLog.Error($"MCP 服务启动失败: {ex.Message}\n{ex.StackTrace}");
            }
        }
    }
}
