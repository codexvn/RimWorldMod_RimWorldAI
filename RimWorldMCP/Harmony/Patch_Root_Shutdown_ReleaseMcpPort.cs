using System;
using HarmonyLib;
using Verse;

namespace RimWorldMCP.Harmony
{
    /// <summary>
    /// 游戏正常退出 / 重启路径强制释放 MCP HttpListener 端口。
    /// Root.Shutdown 会调用 Application.Quit；ProcessExit/Application.quitting 作为兜底。
    /// </summary>
    [HarmonyPatch(typeof(Root), nameof(Root.Shutdown))]
    public static class Patch_Root_Shutdown_ReleaseMcpPort
    {
        static void Prefix()
        {
            try
            {
                McpLog.Info("Root.Shutdown: 释放 MCP 端口...");
                McpServiceManager.Stop();
            }
            catch (Exception ex)
            {
                // 退出路径禁止抛异常，避免挡住游戏关闭
                McpLog.Warn($"Root.Shutdown 释放 MCP 端口失败: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}
