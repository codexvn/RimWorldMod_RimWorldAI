using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RimWorldAgent.Core.Mcp;

namespace RimWorldAgent.Core.AgentRuntime
{
    /// <summary>游戏暂停/恢复控制器 — Plan/Act 阶段切换时控制游戏速度</summary>
    public class GamePaceController : IDisposable
    {
        private readonly SemaphoreSlim _opLock = new(1, 1);

        /// <summary>Plan 阶段游戏速度，默认 paused，可选 normal/fast/superfast/ultrafast</summary>
        public static string PlanSpeed { get; set; } = "paused";

        /// <summary>宿主可设置的跳过恢复判断</summary>
        public static Func<bool>? ShouldSkipResume { get; set; }

        /// <summary>进入 Plan 阶段，设置游戏速度（toggle_pause 幂等）</summary>
        public async Task PauseForPlanning(McpClient mcp, string speed = "paused")
        {
            await _opLock.WaitAsync();
            try
            {
                await CallTogglePause(mcp, speed);
                CoreLog.Info($"[GamePace] Plan 阶段速度: {speed}");
            }
            finally { _opLock.Release(); }
        }

        /// <summary>进入 Act 阶段，恢复游戏（toggle_pause 幂等）</summary>
        public async Task ResumeForAction(McpClient mcp, string speed = "superfast")
        {
            await _opLock.WaitAsync();
            try
            {
                await CallTogglePause(mcp, speed);
                CoreLog.Info($"[GamePace] 已恢复游戏 (Act 阶段, 速度: {speed})");
            }
            finally { _opLock.Release(); }
        }

        /// <summary>确保游戏已恢复（finally 中调用）</summary>
        public async Task EnsureResumed(McpClient mcp)
        {
            if (ShouldSkipResume != null && ShouldSkipResume())
            {
                CoreLog.Info("[GamePace] 跳过恢复（ShouldSkipResume=true）");
                return;
            }
            await _opLock.WaitAsync();
            try
            {
                await CallTogglePause(mcp, "superfast");
                CoreLog.Info("[GamePace] 已恢复游戏 (EnsureResumed)");
            }
            finally { _opLock.Release(); }
        }

        private static async Task CallTogglePause(McpClient mcp, string speed)
        {
            try
            {
                var speedElement = JsonSerializer.SerializeToElement(speed);
                var args = new Dictionary<string, JsonElement> { ["speed"] = speedElement };
                await mcp.CallTool("toggle_pause", args);
            }
            catch (Exception ex)
            {
                CoreLog.Error($"[GamePace] toggle_pause({speed}) 失败: {ex.Message}");
                throw;
            }
        }

        public void Dispose()
        {
            _opLock.Dispose();
        }
    }
}
