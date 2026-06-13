using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RimWorldAgent.Core.Mcp;

namespace RimWorldAgent.Core.AgentRuntime
{
    /// <summary>游戏节奏控制器 — Plan/Act 阶段强制暂停，仅 Advance 阶段允许推进</summary>
    public class GamePaceController : IDisposable
    {
        private readonly SemaphoreSlim _opLock = new(1, 1);

        /// <summary>进入 Plan 阶段，强制暂停游戏</summary>
        public async Task PauseForPlanning(McpClient mcp)
        {
            await _opLock.WaitAsync();
            try
            {
                await CallTogglePause(mcp, "paused");
                CoreLog.Info("[GamePace] Plan 阶段：强制暂停");
            }
            finally { _opLock.Release(); }
        }

        /// <summary>进入 Act 阶段，同样强制暂停游戏（仅 Advance 可推进）</summary>
        public async Task PauseForAction(McpClient mcp)
        {
            await _opLock.WaitAsync();
            try
            {
                await CallTogglePause(mcp, "paused");
                CoreLog.Info("[GamePace] Act 阶段：强制暂停");
            }
            finally { _opLock.Release(); }
        }

        /// <summary>确保游戏已暂停（finally / 会话结束时调用）</summary>
        public async Task EnsurePaused(McpClient mcp)
        {
            await _opLock.WaitAsync();
            try
            {
                await CallTogglePause(mcp, "paused");
                CoreLog.Info("[GamePace] 已恢复强制暂停 (EnsurePaused)");
            }
            finally { _opLock.Release(); }
        }

        private static async Task CallTogglePause(McpClient mcp, string speed)
        {
            try
            {
                CoreLog.Debug($"[GamePace] toggle_pause({speed}) 开始");
                var speedElement = JsonSerializer.SerializeToElement(speed);
                var args = new Dictionary<string, JsonElement> { ["speed"] = speedElement };
                await mcp.CallTool("toggle_pause", args);
                CoreLog.Debug($"[GamePace] toggle_pause({speed}) 完成");
            }
            catch (Exception ex)
            {
                var innerChain = "";
                for (var e = ex.InnerException; e != null; e = e.InnerException)
                    innerChain += $" ← {e.GetType().Name}: {e.Message}";
                CoreLog.Error($"[GamePace] toggle_pause({speed}) 失败: {ex.GetType().Name}: {ex.Message}{innerChain}\n{ex.StackTrace}");
                throw;
            }
        }

        public void Dispose()
        {
            _opLock.Dispose();
        }
    }
}